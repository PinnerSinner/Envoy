using Envoy.Core.Settings;
using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

/// <summary>
/// Spec 09a, REQ-HRD-1, 2: the livery catalog the app's ThemeManager lists. The default is herald, the five game theme ids
/// are unchanged (saved settings keep working), every livery has a palette file, and no description uses a dash.
/// </summary>
public class ThemeManagerTests
{
    private static readonly string[] GameThemeIds = ["imperial", "parchment", "frontier", "olympian", "classic"];

    [Fact]
    public void Default_is_herald()
    {
        Assert.Equal("herald", ThemeCatalog.Herald);
        Assert.Equal(ThemeCatalog.Herald, ThemeResolver.Default);
        Assert.Equal(ThemeCatalog.Herald, new AppSettings().Theme);
        Assert.Equal(ThemeCatalog.Herald, ThemeCatalog.Ids[0]);
    }

    [Fact]
    public void Game_theme_ids_are_unchanged()
    {
        Assert.Equal(GameThemeIds, ThemeCatalog.Ids.Skip(1));
        foreach (var game in Envoy.Core.Games.GameCatalog.All)
            Assert.True(game.ThemeId is null || ThemeCatalog.Find(game.ThemeId) is not null, $"{game.Id} → {game.ThemeId}");
    }

    [Fact]
    public void Saved_settings_keep_their_theme()
    {
        foreach (var id in GameThemeIds.Append(ThemeResolver.MatchGame))
        {
            var json = $$"""{ "Theme": "{{id}}" }""";
            var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
            Assert.Equal(id, loaded.Theme);
            Assert.Equal(id == ThemeResolver.MatchGame ? "olympian" : id,
                ThemeResolver.Resolve(loaded.Theme, "olympian", false, HighContrastMode.Never, ThemeCatalog.Ids));
        }
    }

    [Theory]
    [InlineData("imperial", "#8E2418", "#D8B04A")]
    [InlineData("parchment", "#2B4C8C", "#D8B04A")]
    [InlineData("frontier", "#2A5530", "#E9E4D6")]
    [InlineData("olympian", "#5B2D73", "#D8B04A")]
    [InlineData("classic", "#1C1A18", "#D8B04A")]
    public void Liveries_carry_their_tinctures(string id, string primary, string secondary)
    {
        var livery = ThemeCatalog.Find(id)!;
        Assert.Equal(primary, livery.Primary);
        Assert.Equal(secondary, livery.Secondary);
    }

    [Fact]
    public void Descriptions_are_plain()
    {
        foreach (var text in ThemeCatalog.Liveries.SelectMany(l => new[] { l.Name, l.Description })
                     .Append(ThemeCatalog.MatchGameName).Append(ThemeCatalog.MatchGameDescription))
        {
            Assert.DoesNotContain('—', text);
            Assert.DoesNotContain('–', text);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
        Assert.Equal("Age of Empires IV livery: gules and or", ThemeCatalog.Find("imperial")!.Description);
    }

    [Fact]
    public void Every_livery_and_high_contrast_has_a_palette_file()
    {
        var themes = Path.Combine(RepoRoot(), "src", "Envoy.App", "Themes");
        foreach (var id in ThemeCatalog.Ids.Append(ThemeResolver.HighContrast))
            Assert.True(File.Exists(Path.Combine(themes, $"Palette.{id}.xaml")), $"Palette.{id}.xaml is missing");
        Assert.Null(ThemeCatalog.Find(ThemeResolver.HighContrast)); // never in the picker
        Assert.Null(ThemeCatalog.Find("neon"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Envoy.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
