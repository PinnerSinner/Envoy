using Envoy.Core.Capture;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Capture;

public class RegionMathTests
{
    private static readonly ScreenRegion Client = new(100, 50, 1920, 1080);

    [Fact]
    public void ToWindowRelative_inside_client_stores_offset_and_reference_size() =>
        Assert.Equal(new WindowRelativeRegion(20, 850, 600, 150, 1920, 1080),
            RegionMath.ToWindowRelative(new ScreenRegion(120, 900, 600, 150), Client));

    [Fact]
    public void ToWindowRelative_returns_null_when_region_leaves_client()
    {
        Assert.Null(RegionMath.ToWindowRelative(new ScreenRegion(90, 900, 600, 150), Client)); // 10 px left of the client
        Assert.Null(RegionMath.ToWindowRelative(new ScreenRegion(1500, 1000, 600, 150), Client)); // overhangs right and bottom
        Assert.Null(RegionMath.ToWindowRelative(new ScreenRegion(120, 900, 0, 150), Client));
    }

    [Fact]
    public void ToWindowRelative_tolerates_2px_overhang_and_clamps()
    {
        var relative = RegionMath.ToWindowRelative(new ScreenRegion(98, 48, 600, 150), Client);
        Assert.Equal(new WindowRelativeRegion(0, 0, 598, 148, 1920, 1080), relative);
        Assert.Null(RegionMath.ToWindowRelative(new ScreenRegion(97, 48, 600, 150), Client));
    }

    [Fact]
    public void Resolve_same_client_size_moves_with_window()
    {
        var relative = new WindowRelativeRegion(20, 850, 600, 150, 1920, 1080);
        var moved = Client with { X = Client.X + 500, Y = Client.Y - 200 };
        Assert.Equal(new ScreenRegion(620, 700, 600, 150), RegionMath.Resolve(relative, moved));
        Assert.Equal(new ScreenRegion(120, 900, 600, 150), RegionMath.Resolve(relative, Client));
    }

    [Fact]
    public void Resolve_scales_proportionally_when_client_resized()
    {
        var relative = new WindowRelativeRegion(20, 850, 600, 150, 1920, 1080);
        var bigger = new ScreenRegion(0, 0, 2560, 1440);
        Assert.Equal(new ScreenRegion(27, 1133, 800, 200), RegionMath.Resolve(relative, bigger));
    }

    [Fact]
    public void Resolve_clamps_to_client()
    {
        var relative = new WindowRelativeRegion(1800, 1000, 300, 200, 1920, 1080);
        Assert.Equal(new ScreenRegion(1900, 1050, 120, 80), RegionMath.Resolve(relative, Client));
        var outside = new WindowRelativeRegion(5000, 5000, 300, 200, 1920, 1080);
        Assert.True(RegionMath.Resolve(outside, Client).IsEmpty);
    }

    [Fact]
    public void Resolve_handles_negative_monitor_coordinates()
    {
        var left = new ScreenRegion(-1920, -200, 1920, 1080); // a monitor left of and above the primary
        var relative = RegionMath.ToWindowRelative(new ScreenRegion(-1900, 650, 600, 150), left)!;
        Assert.Equal(new WindowRelativeRegion(20, 850, 600, 150, 1920, 1080), relative);
        Assert.Equal(new ScreenRegion(-1400, 650, 600, 150), RegionMath.Resolve(relative, left with { X = -1420 }));
    }

    public static TheoryData<bool, bool, bool, bool, RegionSource, bool> ResolveOrderCases() => new()
    {
        // client known, relative entry, game entry, any entry → source, client size differs
        { true, true, true, true, RegionSource.WindowRelative, false },
        { false, true, true, true, RegionSource.GameAbsolute, false },
        { true, false, true, true, RegionSource.GameAbsolute, false },
        { true, false, false, true, RegionSource.AnyAbsolute, false },
        { false, true, false, true, RegionSource.AnyAbsolute, false },
        { false, true, false, false, RegionSource.None, false },
        { true, false, false, false, RegionSource.None, false },
    };

    [Theory]
    [MemberData(nameof(ResolveOrderCases))]
    public void ResolveForGame_order(bool clientKnown, bool relative, bool game, bool any, RegionSource expected, bool differs)
    {
        var settings = new AppSettings();
        if (relative) settings.Capture.WindowRegions["aoe2de"] = new WindowRelativeRegion(20, 850, 600, 150, 1920, 1080);
        if (game) settings.Capture.Regions["aoe2de"] = new ScreenRegion(1, 2, 300, 100);
        if (any) settings.Capture.Regions["any"] = new ScreenRegion(3, 4, 200, 80);
        var options = CaptureOptions.From(settings);

        var result = RegionMath.ResolveForGame(options, "aoe2de", clientKnown ? Client : null);

        Assert.Equal(expected, result.Source);
        Assert.Equal(differs, result.ClientSizeDiffers);
        var region = expected switch
        {
            RegionSource.WindowRelative => new ScreenRegion(120, 900, 600, 150),
            RegionSource.GameAbsolute => new ScreenRegion(1, 2, 300, 100),
            RegionSource.AnyAbsolute => new ScreenRegion(3, 4, 200, 80),
            _ => null,
        };
        Assert.Equal(region, result.Region);
    }

    [Fact]
    public void ResolveForGame_reports_a_different_client_size()
    {
        var settings = new AppSettings();
        settings.Capture.WindowRegions["aoe2de"] = new WindowRelativeRegion(20, 850, 600, 150, 1920, 1080);
        var result = RegionMath.ResolveForGame(CaptureOptions.From(settings), "aoe2de", new ScreenRegion(0, 0, 2560, 1440));
        Assert.Equal(RegionSource.WindowRelative, result.Source);
        Assert.True(result.ClientSizeDiffers);
    }

    [Fact]
    public void ResolveForGame_any_game_uses_the_any_region()
    {
        var settings = new AppSettings();
        settings.Capture.Regions["any"] = new ScreenRegion(3, 4, 200, 80);
        var result = RegionMath.ResolveForGame(CaptureOptions.From(settings), "any", null);
        Assert.Equal(RegionSource.AnyAbsolute, result.Source);
    }

    [Theory]
    [InlineData(600, 200, 2.0, 10000u, 1200, 400)]
    [InlineData(600, 200, 0.5, 10000u, 600, 200)]   // upscale below 1 is clamped to 1
    [InlineData(600, 200, 9.0, 10000u, 2400, 800)]  // above 4 is clamped to 4
    [InlineData(3000, 200, 4.0, 10000u, 10000, 667)] // the long side is limited to the OCR maximum
    [InlineData(301, 101, 1.5, 10000u, 452, 152)]   // 451.5 and 151.5 round to even, as v0.1 did
    [InlineData(1, 1, 1.0, 10000u, 1, 1)]
    public void OutputSize_matches_v01_formula(int w, int h, double upscale, uint max, int expectedW, int expectedH)
    {
        var (width, height, _) = RegionMath.OutputSize(new ScreenRegion(0, 0, w, h), upscale, max);
        // v0.1 (ChatWatcher.TickAsync + ScreenCapturer.Capture):
        var scale = Math.Clamp(upscale, 1.0, 4.0);
        scale = Math.Min(scale, Math.Min((double)max / w, (double)max / h));
        Assert.Equal(Math.Max(1, (int)Math.Round(w * scale)), width);
        Assert.Equal(Math.Max(1, (int)Math.Round(h * scale)), height);
        Assert.Equal((expectedW, expectedH), (width, height));
    }

    [Fact]
    public void SamplePoints_returns_3x3_inset_grid_inside_region()
    {
        var region = new ScreenRegion(100, 200, 300, 90);
        var points = RegionMath.SamplePoints(region);
        Assert.Equal(9, points.Count);
        Assert.Equal(9, points.Distinct().Count());
        foreach (var (x, y) in points)
        {
            Assert.InRange(x, region.X + 2, region.X + region.Width - 3);
            Assert.InRange(y, region.Y + 2, region.Y + region.Height - 3);
        }
        Assert.Contains((102, 202), points);
        Assert.Contains((397, 287), points);
        Assert.Contains((250, 245), points);
    }

    [Fact]
    public void Intersect_and_Contains()
    {
        Assert.Equal(new ScreenRegion(50, 50, 50, 50), RegionMath.Intersect(new(0, 0, 100, 100), new(50, 50, 100, 100)));
        Assert.Null(RegionMath.Intersect(new(0, 0, 100, 100), new(100, 0, 100, 100)));
        Assert.True(RegionMath.Contains(new(0, 0, 100, 100), new(0, 0, 100, 100)));
        Assert.False(RegionMath.Contains(new(0, 0, 100, 100), new(0, 0, 101, 100)));
        Assert.True(RegionMath.Contains(new(0, 0, 100, 100), new(-2, 0, 102, 100), 2));
    }
}
