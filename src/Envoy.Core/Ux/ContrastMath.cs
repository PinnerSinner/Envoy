using System.Globalization;

namespace Envoy.Core.Ux;

/// <summary>An sRGB colour without alpha (spec 07b). Palette XAML colours are parsed with <see cref="Parse"/>.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb MidGrey = new(0x80, 0x80, 0x80);

    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c>; the alpha byte is ignored.</summary>
    /// <exception cref="FormatException">The text is not one of those two forms.</exception>
    public static Rgb Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 8) s = s[2..];
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"'{hex}' is not a #RRGGBB or #AARRGGBB colour.");
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>WCAG 2.x contrast arithmetic for the palettes and the overlay's readable-background floor (REQ-OUXB-25, 35, 36).</summary>
public static class ContrastMath
{
    /// <summary>The WCAG AA ratio for normal text.</summary>
    public const double TextTarget = 4.5;

    /// <summary>The WCAG ratio for non-text UI such as the focus ring.</summary>
    public const double UiTarget = 3.0;

    private static readonly Rgb[] Backdrops = [Rgb.White, Rgb.MidGrey, Rgb.Black];

    /// <summary>The WCAG 2.x relative luminance of an sRGB colour, 0 (black) to 1 (white).</summary>
    public static double RelativeLuminance(Rgb c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    /// <summary>(L1 + 0.05) / (L2 + 0.05) with L1 the lighter colour: symmetric, from 1 to 21.</summary>
    public static double Ratio(Rgb a, Rgb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary><paramref name="top"/> drawn with <paramref name="alpha"/> (clamped to 0..1) over an opaque backdrop.</summary>
    public static Rgb Composite(Rgb top, double alpha, Rgb backdrop)
    {
        var a = Math.Clamp(alpha, 0, 1);
        return new Rgb(Mix(top.R, backdrop.R, a), Mix(top.G, backdrop.G, a), Mix(top.B, backdrop.B, a));
    }

    /// <summary>
    /// The smallest alpha in [0, 1], in steps of 0.01, such that <paramref name="text"/> reaches <paramref name="target"/>
    /// against <paramref name="bg"/> drawn at that alpha over white, mid-grey and black; 1 if even an opaque
    /// background cannot reach it.
    /// </summary>
    public static double MinimumBackgroundAlpha(Rgb text, Rgb bg, double target)
    {
        for (var step = 0; step <= 100; step++)
        {
            var alpha = step / 100.0;
            if (Backdrops.All(d => Ratio(text, Composite(bg, alpha, d)) >= target)) return alpha;
        }
        return 1.0;
    }

    /// <summary>
    /// The overlay card's background alpha: the user's value, raised to <see cref="MinimumBackgroundAlpha"/> when
    /// <paramref name="ensureReadable"/> is on. Never lower than the user's (clamped) choice.
    /// </summary>
    public static double EffectiveBackgroundAlpha(double userAlpha, Rgb text, Rgb bg, bool ensureReadable, double target = TextTarget)
    {
        var user = double.IsNaN(userAlpha) ? 0 : Math.Clamp(userAlpha, 0, 1);
        return ensureReadable ? Math.Max(user, MinimumBackgroundAlpha(text, bg, target)) : user;
    }

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static byte Mix(byte top, byte back, double alpha) => (byte)Math.Round(top * alpha + back * (1 - alpha));
}
