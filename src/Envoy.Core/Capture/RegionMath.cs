using Envoy.Core.Games;
using Envoy.Core.Settings;

namespace Envoy.Core.Capture;

/// <summary>Where the resolved chat region came from.</summary>
public enum RegionSource
{
    /// <summary>No region is set for this game or for "any".</summary>
    None,

    /// <summary>Relative to the game window's client area (follows the window).</summary>
    WindowRelative,

    /// <summary>The game's fixed screen rectangle.</summary>
    GameAbsolute,

    /// <summary>The "any game" fixed screen rectangle.</summary>
    AnyAbsolute,
}

/// <summary>The region to capture this tick and where it came from.</summary>
/// <param name="Region">Physical screen pixels; null when none is set.</param>
/// <param name="Source">Which setting supplied it.</param>
/// <param name="ClientSizeDiffers">A window-relative region was chosen at a different client size (it was scaled).</param>
public sealed record RegionResolution(ScreenRegion? Region, RegionSource Source, bool ClientSizeDiffers)
{
    public static readonly RegionResolution None = new(null, RegionSource.None, false);
}

/// <summary>Physical-pixel rectangle maths for the capture engine (REQ-CAP-1, 2, 4, 34). Pure functions.</summary>
public static class RegionMath
{
    /// <summary>How far a selection may overhang the client area and still be stored relative to it.</summary>
    public const int EdgeTolerancePx = 2;

    /// <summary>Inset of the covered-window sample grid from the region's edges.</summary>
    public const int SampleInsetPx = 2;

    /// <summary>
    /// <paramref name="absolute"/> as an offset inside <paramref name="client"/>, clamped to it, with the client size as
    /// the reference. Null when it overhangs the client by more than <see cref="EdgeTolerancePx"/> or is empty.
    /// </summary>
    public static WindowRelativeRegion? ToWindowRelative(ScreenRegion absolute, ScreenRegion client)
    {
        ArgumentNullException.ThrowIfNull(absolute);
        ArgumentNullException.ThrowIfNull(client);
        if (absolute.IsEmpty || client.IsEmpty || !Contains(client, absolute, EdgeTolerancePx)) return null;
        if (Intersect(absolute, client) is not { } inside) return null;
        return new WindowRelativeRegion(inside.X - client.X, inside.Y - client.Y, inside.Width, inside.Height, client.Width, client.Height);
    }

    /// <summary>
    /// The screen rectangle for <paramref name="relative"/> in <paramref name="client"/>: translated when the client size
    /// is unchanged, otherwise scaled per axis (rounded); always clamped to the client. Empty when nothing is left.
    /// </summary>
    public static ScreenRegion Resolve(WindowRelativeRegion relative, ScreenRegion client)
    {
        ArgumentNullException.ThrowIfNull(relative);
        ArgumentNullException.ThrowIfNull(client);
        ScreenRegion moved;
        if (relative.ClientWidth == client.Width && relative.ClientHeight == client.Height)
        {
            moved = new ScreenRegion(client.X + relative.OffsetX, client.Y + relative.OffsetY, relative.Width, relative.Height);
        }
        else
        {
            var sx = relative.ClientWidth > 0 ? (double)client.Width / relative.ClientWidth : 1.0;
            var sy = relative.ClientHeight > 0 ? (double)client.Height / relative.ClientHeight : 1.0;
            moved = new ScreenRegion(
                client.X + Round(relative.OffsetX * sx), client.Y + Round(relative.OffsetY * sy),
                Round(relative.Width * sx), Round(relative.Height * sy));
        }
        return Intersect(moved, client) ?? new ScreenRegion(Math.Clamp(moved.X, client.X, client.X + Math.Max(0, client.Width)),
            Math.Clamp(moved.Y, client.Y, client.Y + Math.Max(0, client.Height)), 0, 0);
    }

    /// <summary>
    /// The region for <paramref name="gameId"/>: <c>WindowRegions[gameId]</c> resolved against
    /// <paramref name="gameClient"/> (only when the client is known), else <c>Regions[gameId]</c>, else
    /// <c>Regions["any"]</c>, exactly as v0.1 (REQ-CAP-4).
    /// </summary>
    public static RegionResolution ResolveForGame(CaptureOptions options, string gameId, ScreenRegion? gameClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gameId);
        if (gameClient is { IsEmpty: false } client && options.WindowRegions.TryGetValue(gameId, out var relative) && !relative.IsEmpty)
        {
            var differs = relative.ClientWidth != client.Width || relative.ClientHeight != client.Height;
            return new RegionResolution(Resolve(relative, client), RegionSource.WindowRelative, differs);
        }
        if (options.Regions.TryGetValue(gameId, out var region))
            return new RegionResolution(region, gameId == GameCatalog.Any ? RegionSource.AnyAbsolute : RegionSource.GameAbsolute, false);
        if (options.Regions.TryGetValue(GameCatalog.Any, out var any))
            return new RegionResolution(any, RegionSource.AnyAbsolute, false);
        return RegionResolution.None;
    }

    /// <summary>The overlap of two rectangles, or null when they don't overlap.</summary>
    public static ScreenRegion? Intersect(ScreenRegion a, ScreenRegion b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min((long)a.X + a.Width, (long)b.X + b.Width);
        var bottom = Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height);
        return right > left && bottom > top ? new ScreenRegion(left, top, (int)(right - left), (int)(bottom - top)) : null;
    }

    /// <summary>True when <paramref name="inner"/> lies inside <paramref name="outer"/>, allowing <paramref name="tolerance"/> px of overhang.</summary>
    public static bool Contains(ScreenRegion outer, ScreenRegion inner, int tolerance = 0)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        return inner.X >= (long)outer.X - tolerance && inner.Y >= (long)outer.Y - tolerance
            && (long)inner.X + inner.Width <= (long)outer.X + outer.Width + tolerance
            && (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height + tolerance;
    }

    /// <summary>
    /// The v0.1 output size: <paramref name="upscale"/> clamped to [1, 4], then limited so neither side exceeds
    /// <paramref name="maxDimension"/>; each side at least 1 px.
    /// </summary>
    public static (int Width, int Height, double Scale) OutputSize(ScreenRegion source, double upscale, uint maxDimension)
    {
        ArgumentNullException.ThrowIfNull(source);
        var w = Math.Max(1, source.Width);
        var h = Math.Max(1, source.Height);
        var scale = Math.Clamp(double.IsNaN(upscale) ? 1.0 : upscale, 1.0, 4.0);
        if (maxDimension > 0) scale = Math.Min(scale, Math.Min((double)maxDimension / w, (double)maxDimension / h));
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)), scale);
    }

    /// <summary>A <paramref name="gridSize"/>×<paramref name="gridSize"/> grid of points inside <paramref name="region"/>, inset 2 px from its edges.</summary>
    public static IReadOnlyList<(int X, int Y)> SamplePoints(ScreenRegion region, int gridSize = 3)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentOutOfRangeException.ThrowIfLessThan(gridSize, 1);
        var xs = Axis(region.X, region.Width, gridSize);
        var ys = Axis(region.Y, region.Height, gridSize);
        var points = new List<(int, int)>(gridSize * gridSize);
        foreach (var y in ys)
        {
            foreach (var x in xs) points.Add((x, y));
        }
        return points;
    }

    private static int[] Axis(int start, int length, int count)
    {
        var first = start + SampleInsetPx;
        var last = start + length - 1 - SampleInsetPx;
        if (last < first) first = last = start + Math.Max(0, length - 1) / 2;
        var result = new int[count];
        for (var i = 0; i < count; i++)
            result[i] = count == 1 ? first + (last - first) / 2 : first + Round((last - first) * (double)i / (count - 1));
        return result;
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
