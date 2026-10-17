using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class ContrastMathTests
{
    private static readonly Rgb[] Backdrops = [Rgb.White, Rgb.MidGrey, Rgb.Black];

    [Fact]
    public void Black_white_is_21() => Assert.Equal(21.0, ContrastMath.Ratio(Rgb.Black, Rgb.White), 6);

    [Fact]
    public void Same_colour_is_1() => Assert.Equal(1.0, ContrastMath.Ratio(Rgb.MidGrey, Rgb.MidGrey), 6);

    [Theory]
    [InlineData("#EDE3CF", "#0E1420")]
    [InlineData("#8A291B", "#CDB98F")]
    [InlineData("#808080", "#FFFFFF")]
    public void Ratio_symmetric(string a, string b) =>
        Assert.Equal(ContrastMath.Ratio(Rgb.Parse(a), Rgb.Parse(b)), ContrastMath.Ratio(Rgb.Parse(b), Rgb.Parse(a)), 12);

    [Fact]
    public void Known_ratio_matches_wcag() =>
        // #777777 on white is the classic 4.48:1 example.
        Assert.Equal(4.48, ContrastMath.Ratio(Rgb.Parse("#777777"), Rgb.White), 2);

    [Fact]
    public void Parse_accepts_argb()
    {
        Assert.Equal(new Rgb(0x12, 0x34, 0x56), Rgb.Parse("#123456"));
        Assert.Equal(new Rgb(0x12, 0x34, 0x56), Rgb.Parse("#FF123456"));
        Assert.Equal(new Rgb(0x12, 0x34, 0x56), Rgb.Parse("#00123456"));
        Assert.Equal("#123456", Rgb.Parse("#99123456").ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GG3456")]
    [InlineData("#1234567")]
    public void Parse_rejects_malformed(string text) => Assert.Throws<FormatException>(() => Rgb.Parse(text));

    [Fact]
    public void Composite_blends_by_alpha()
    {
        Assert.Equal(Rgb.Black, ContrastMath.Composite(Rgb.Black, 1, Rgb.White));
        Assert.Equal(Rgb.White, ContrastMath.Composite(Rgb.Black, 0, Rgb.White));
        Assert.Equal(new Rgb(128, 128, 128), ContrastMath.Composite(Rgb.Black, 0.5, Rgb.White));
    }

    [Theory]
    [InlineData("#F3EAD6", "#0A1019")] // imperial overlay
    [InlineData("#F4E9D1", "#2B1F14")] // parchment overlay
    [InlineData("#FFFFFF", "#000000")]
    public void Minimum_alpha_meets_target_over_white_grey_black(string text, string bg)
    {
        var t = Rgb.Parse(text);
        var b = Rgb.Parse(bg);
        var alpha = ContrastMath.MinimumBackgroundAlpha(t, b, 4.5);
        Assert.InRange(alpha, 0, 1);
        foreach (var d in Backdrops) Assert.True(ContrastMath.Ratio(t, ContrastMath.Composite(b, alpha, d)) >= 4.5, $"{d} at {alpha}");
        // It is the smallest such step: one step lower fails on some backdrop.
        if (alpha > 0)
            Assert.Contains(Backdrops, d => ContrastMath.Ratio(t, ContrastMath.Composite(b, alpha - 0.01, d)) < 4.5);
    }

    [Fact]
    public void Minimum_alpha_is_one_when_unreachable() =>
        Assert.Equal(1.0, ContrastMath.MinimumBackgroundAlpha(Rgb.Parse("#777777"), Rgb.Parse("#888888"), 4.5));

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(0.65)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void Effective_alpha_never_lowers_user_choice(double user)
    {
        var text = Rgb.Parse("#F3EAD6");
        var bg = Rgb.Parse("#0A1019");
        var effective = ContrastMath.EffectiveBackgroundAlpha(user, text, bg, ensureReadable: true);
        Assert.True(effective >= user);
        Assert.True(effective >= ContrastMath.MinimumBackgroundAlpha(text, bg, 4.5));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(0.78)]
    public void Effective_alpha_ignores_floor_when_disabled(double user) =>
        Assert.Equal(user, ContrastMath.EffectiveBackgroundAlpha(user, Rgb.Parse("#F3EAD6"), Rgb.Parse("#0A1019"), ensureReadable: false));

    [Fact]
    public void Effective_alpha_clamps_out_of_range_values()
    {
        Assert.Equal(1.0, ContrastMath.EffectiveBackgroundAlpha(3, Rgb.White, Rgb.Black, ensureReadable: false));
        Assert.Equal(0.0, ContrastMath.EffectiveBackgroundAlpha(-1, Rgb.White, Rgb.Black, ensureReadable: false));
        Assert.Equal(0.0, ContrastMath.EffectiveBackgroundAlpha(double.NaN, Rgb.White, Rgb.Black, ensureReadable: false));
    }
}
