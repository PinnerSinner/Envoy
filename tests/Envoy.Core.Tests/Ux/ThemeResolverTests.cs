using Envoy.Core.Settings;
using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

public class ThemeResolverTests
{
    private static readonly string[] Known = ["imperial", "parchment", "frontier", "olympian", "classic"];

    [Theory]
    [InlineData("parchment")]
    [InlineData("match-game")]
    public void Follow_windows_uses_high_contrast_when_system_on(string theme)
    {
        Assert.Equal(ThemeResolver.HighContrast, ThemeResolver.Resolve(theme, "olympian", true, HighContrastMode.FollowWindows, Known));
        Assert.Equal(theme == "match-game" ? "olympian" : theme,
            ThemeResolver.Resolve(theme, "olympian", false, HighContrastMode.FollowWindows, Known));
    }

    [Fact]
    public void Never_ignores_system() =>
        Assert.Equal("parchment", ThemeResolver.Resolve("parchment", null, true, HighContrastMode.Never, Known));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Always_forces(bool system) =>
        Assert.Equal(ThemeResolver.HighContrast, ThemeResolver.Resolve("frontier", "olympian", system, HighContrastMode.Always, Known));

    [Fact]
    public void Match_game_uses_game_theme_else_default()
    {
        Assert.Equal("olympian", ThemeResolver.Resolve(ThemeResolver.MatchGame, "olympian", false, HighContrastMode.Never, Known));
        Assert.Equal(ThemeResolver.Default, ThemeResolver.Resolve(ThemeResolver.MatchGame, null, false, HighContrastMode.Never, Known));
    }

    [Theory]
    [InlineData("neon")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("highcontrast")] // only the resolver picks it, never the theme setting
    public void Unknown_theme_falls_back_to_default(string? theme) =>
        Assert.Equal(ThemeResolver.Default, ThemeResolver.Resolve(theme, null, false, HighContrastMode.FollowWindows, Known));

    [Fact]
    public void Unknown_game_theme_falls_back_to_default() =>
        Assert.Equal(ThemeResolver.Default, ThemeResolver.Resolve(ThemeResolver.MatchGame, "neon", false, HighContrastMode.FollowWindows, Known));

    [Fact]
    public void Default_is_follow_windows() =>
        Assert.Equal(HighContrastMode.FollowWindows, new AppSettings().Accessibility.HighContrast);
}
