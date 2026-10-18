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

    public static TheoryData<string, string, string> HeraldPairs()
    {
        // Spec 09a: text on the oak rail and the leather toast, the rail's focus ring, rubrics (Accent) on vellum, and the
        // glyph on every lettered wax seal and on the livery's primary colour.
        var pairs = new List<(string Fg, string Bg, double Target)>();
        foreach (var bg in new[] { "Oak", "Oak.Deep", "Oak.Tab" })
        {
            pairs.Add(("Leather.Text", bg, ContrastMath.TextTarget));
            pairs.Add(("Leather.Muted", bg, ContrastMath.TextTarget));
            pairs.Add(("Focus.Ring.Oak", bg, ContrastMath.UiTarget));
        }
        // Vellum.Deep is the darkest stop of the charter's Vellum.Gradient; "Wax.X.Mid" is the halfway blend of a seal's
        // radial wax, where its glyph sits.
        foreach (var bg in Backgrounds.Append("Vellum.Deep")) pairs.Add(("Accent", bg, ContrastMath.TextTarget));
        foreach (var wax in new[] { "Gules", "Azure", "Vert", "Or", "Purpure", "Sable", "Livery" })
        {
            pairs.Add(("Wax.Text", $"Wax.{wax}.Dark", ContrastMath.TextTarget));
            pairs.Add(("Wax.Text", $"Wax.{wax}.Mid", ContrastMath.TextTarget));
        }
        foreach (var bg in new[] { "Accent", "Accent.Bright", "Livery.Primary", "Livery.Dark" }) pairs.Add(("Text.OnAccent", bg, ContrastMath.TextTarget));
        var data = new TheoryData<string, string, string>();
        foreach (var id in PaletteIds().Where(id => id != ThemeResolver.HighContrast))
            foreach (var (fg, bg, target) in pairs)
                data.Add(id, fg, bg + (target == ContrastMath.UiTarget ? "|ui" : ""));
        return data;
    }

    [Theory]
    [MemberData(nameof(HeraldPairs))]
    public void Herald_ornament_text_and_rings_meet_their_targets(string palette, string foreground, string background)
    {
        var ui = background.EndsWith("|ui", StringComparison.Ordinal);
        var bg = ui ? background[..^3] : background;
        var colours = Colours(palette);
        var ratio = ContrastMath.Ratio(colours[foreground], colours[bg]);
        var target = ui ? ContrastMath.UiTarget : ContrastMath.TextTarget;
        Assert.True(ratio >= target, $"{palette}: {foreground} {colours[foreground]} on {bg} {colours[bg]} is {ratio:0.00}:1 (target {target}:1)");
    }

    [Fact]
    public void Herald_is_the_default_palette_and_shares_the_keys()
    {
        Assert.Equal("herald", ThemeResolver.Default);
        var herald = Load("herald").Root!.Elements().Select(e => (string?)e.Attribute(X + "Key")).ToHashSet(StringComparer.Ordinal);
        foreach (var key in new[] { "Livery.Primary", "Livery.Secondary", "Wax.Livery", "Oak", "Vellum", "Knot.Horizontal", "Damask.Oak" })
            Assert.Contains(key, herald);
        foreach (var id in PaletteIds())
        {
            var keys = Load(id).Root!.Elements().Select(e => (string?)e.Attribute(X + "Key")).ToHashSet(StringComparer.Ordinal);
            Assert.True(herald.SetEquals(keys), $"{id} keys differ from herald: {string.Join(", ", keys.Except(herald).Concat(herald.Except(keys)))}");
        }
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

    /// <summary>
    /// The palette's literal <c>&lt;Color&gt;</c> elements, keyed without the ".Color" suffix, plus "Wax.X.Mid": the
    /// halfway blend of each wax's light and dark stop.
    /// </summary>
    private static Dictionary<string, Rgb> Colours(string palette)
    {
        var colours = Load(palette).Root!.Elements().Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => ((string)e.Attribute(X + "Key")!).Replace(".Color", "", StringComparison.Ordinal),
                          e => Rgb.Parse(e.Value), StringComparer.Ordinal);
        foreach (var light in colours.Keys.Where(k => k.StartsWith("Wax.", StringComparison.Ordinal) && k.EndsWith(".Light", StringComparison.Ordinal)).ToList())
        {
            var wax = light[..^".Light".Length];
            if (colours.TryGetValue(wax + ".Dark", out var dark))
                colours[wax + ".Mid"] = ContrastMath.Composite(colours[light], 0.5, dark);
        }
        return colours;
    }

    [Fact]
    public void High_contrast_drop_cap_is_window_text_on_window()
    {
        var brushes = Load(ThemeResolver.HighContrast).Root!.Elements().Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => (string)e.Attribute("Color")!, StringComparer.Ordinal);
        var members = Load(ThemeResolver.HighContrast).Root!.Elements().Where(e => e.Name.LocalName == "Static")
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => (string)e.Attribute("Member")!, StringComparer.Ordinal);
        string Member(string brush) => members[brushes[brush].Replace("{StaticResource ", "", StringComparison.Ordinal).TrimEnd('}')];
        Assert.Equal("SystemColors.WindowColor", Member("DropCap.Gradient"));
        Assert.Equal("SystemColors.WindowTextColor", Member("Livery.Primary"));
    }

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
