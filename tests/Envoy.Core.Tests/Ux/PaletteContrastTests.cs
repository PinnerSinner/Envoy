using System.Xml.Linq;
using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

/// <summary>
/// REQ-OUXB-25, 35: reads src/Envoy.App/Themes/Palette.*.xaml (found by walking up to Envoy.sln; a missing file fails).
/// Game palettes are every palette except high contrast, whose colours come from Windows.
/// </summary>
public class PaletteContrastTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Backgrounds = ["Bg.Window", "Bg.Panel", "Bg.PanelAlt", "Bg.Input"];
    private static readonly string[] TextKeys = ["Text.Primary", "Text.Secondary", "Text.Muted", "Danger"];

    public static TheoryData<string> GamePalettes()
    {
        var data = new TheoryData<string>();
        foreach (var id in PaletteIds().Where(id => id != ThemeResolver.HighContrast)) data.Add(id);
        return data;
    }

    public static TheoryData<string> AllPalettes()
    {
        var data = new TheoryData<string>();
        foreach (var id in PaletteIds()) data.Add(id);
        return data;
    }

    public static TheoryData<string, string, string> TextPairs()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var id in PaletteIds().Where(id => id != ThemeResolver.HighContrast))
            foreach (var text in TextKeys)
                foreach (var bg in Backgrounds)
                    data.Add(id, text, bg);
        return data;
    }

    public static TheoryData<string, string> FocusPairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var id in PaletteIds().Where(id => id != ThemeResolver.HighContrast))
            foreach (var bg in Backgrounds)
                data.Add(id, bg);
        return data;
    }

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void Text_colours_meet_4_5_to_1(string palette, string text, string background)
    {
        var colours = Colours(palette);
        var ratio = ContrastMath.Ratio(colours[text], colours[background]);
        Assert.True(ratio >= ContrastMath.TextTarget, $"{palette}: {text} {colours[text]} on {background} {colours[background]} is {ratio:0.00}:1");
    }

    [Theory]
    [MemberData(nameof(FocusPairs))]
    public void Focus_ring_meets_3_to_1(string palette, string background)
    {
        var colours = Colours(palette);
        var ratio = ContrastMath.Ratio(colours["Focus.Ring"], colours[background]);
        Assert.True(ratio >= ContrastMath.UiTarget, $"{palette}: Focus.Ring on {background} is {ratio:0.00}:1");
    }

    [Theory]
    [MemberData(nameof(GamePalettes))]
    public void Overlay_text_meets_4_5_on_opaque_bg(string palette)
    {
        var colours = Colours(palette);
        foreach (var key in new[] { "Overlay.Text", "Overlay.Sender" })
        {
            var ratio = ContrastMath.Ratio(colours[key], colours["Overlay.Bg"]);
            Assert.True(ratio >= ContrastMath.TextTarget, $"{palette}: {key} on Overlay.Bg is {ratio:0.00}:1");
        }
    }

    [Theory]
    [MemberData(nameof(AllPalettes))]
    public void All_palettes_define_focus_ring(string palette)
    {
        var doc = Load(palette);
        var keys = doc.Root!.Elements().Select(e => (string?)e.Attribute(X + "Key")).ToHashSet(StringComparer.Ordinal);
        foreach (var key in new[] { "Focus.Ring.Color", "Focus.Ring", "Overlay.HitTest", "Overlay.Dim", "Overlay.Bg" })
            Assert.Contains(key, keys);
    }

    [Fact]
    public void Functional_alphas_are_the_same_in_every_palette()
    {
        foreach (var id in PaletteIds())
        {
            var literal = Load(id).Root!.Elements().Where(e => e.Name.LocalName == "Color")
                .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim(), StringComparer.Ordinal);
            Assert.Equal("#01000000", literal["Overlay.HitTest.Color"]);
            Assert.Equal("#99000000", literal["Overlay.Dim.Color"]);
        }
    }

    [Fact]
    public void High_contrast_uses_only_system_colours()
    {
        var literals = Load(ThemeResolver.HighContrast).Root!.Elements().Where(e => e.Name.LocalName == "Color")
            .Select(e => (string)e.Attribute(X + "Key")!).Order(StringComparer.Ordinal);
        Assert.Equal(["Overlay.Dim.Color", "Overlay.HitTest.Color"], literals);
    }

    [Fact]
    public void Palettes_include_high_contrast_and_the_game_palettes()
    {
        var ids = PaletteIds();
        Assert.Contains(ThemeResolver.HighContrast, ids);
        Assert.Contains(ThemeResolver.Default, ids);
        Assert.True(ids.Count >= 6, string.Join(", ", ids));
    }

    private static IReadOnlyList<string> PaletteIds() =>
        [.. Directory.GetFiles(ThemesFolder(), "Palette.*.xaml")
            .Select(f => Path.GetFileNameWithoutExtension(f)["Palette.".Length..])
            .Order(StringComparer.Ordinal)];

    private static XDocument Load(string palette)
    {
        var path = Path.Combine(ThemesFolder(), $"Palette.{palette}.xaml");
        Assert.True(File.Exists(path), $"{path} is missing");
        return XDocument.Load(path);
    }

    /// <summary>The palette's literal <c>&lt;Color&gt;</c> elements, keyed without the ".Color" suffix.</summary>
    private static Dictionary<string, Rgb> Colours(string palette) =>
        Load(palette).Root!.Elements().Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => ((string)e.Attribute(X + "Key")!).Replace(".Color", "", StringComparison.Ordinal),
                          e => Rgb.Parse(e.Value), StringComparer.Ordinal);

    private static string ThemesFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Envoy.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var themes = Path.Combine(dir!.FullName, "src", "Envoy.App", "Themes");
        Assert.True(Directory.Exists(themes), $"{themes} is missing");
        return themes;
    }
}
