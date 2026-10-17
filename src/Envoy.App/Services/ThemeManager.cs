using System.Windows;
using System.Windows.Media;
using Envoy.Core.Settings;
using Envoy.Core.Ux;

namespace Envoy.App.Services;

public sealed record ThemeInfo(string Id, string Name, string Inspiration);

/// <summary>
/// Swaps the colour/typography dictionary at runtime. Every control style uses DynamicResource, so the whole UI
/// (settings, overlay, reply box) re-skins instantly.
/// </summary>
public static class ThemeManager
{
    public const string MatchGame = ThemeResolver.MatchGame;

    public static IReadOnlyList<ThemeInfo> Themes { get; } =
    [
        new("imperial", "Imperial", "Age of Empires IV — midnight blue, burnished gold"),
        new("parchment", "Parchment", "Age of Empires II — illuminated manuscript, oak and vellum"),
        new("frontier", "Frontier", "Age of Empires III — colonial charts, brass and deep teal"),
        new("olympian", "Olympian", "Age of Mythology — marble, bronze and Aegean blue"),
        new("classic", "Classic", "Age of Empires — sandstone and bronze-age earth"),
    ];

    /// <summary>
    /// Every palette the smoke test renders, in order: the game themes, then high contrast (spec 07b). The theme picker
    /// lists <see cref="Themes"/> only; <see cref="ThemeResolver"/> chooses high contrast.
    /// </summary>
    public static IReadOnlyList<string> PaletteIds { get; } = [.. Themes.Select(t => t.Id), ThemeResolver.HighContrast];

    /// <summary>The game theme ids, for <see cref="ThemeResolver.Resolve"/>.</summary>
    public static IReadOnlyList<string> ThemeIds { get; } = [.. Themes.Select(t => t.Id)];

    public static string CurrentId { get; private set; } = "";

    /// <summary>True while the high-contrast palette is applied.</summary>
    public static bool IsHighContrast => CurrentId == ThemeResolver.HighContrast;

    /// <summary>Raised on the UI thread after a palette (re)load, so code-computed brushes can follow it.</summary>
    public static event Action? Applied;

    /// <summary>
    /// Loads a <see cref="PaletteIds"/> entry (anything else becomes <see cref="ThemeResolver.Default"/>). <paramref name="force"/>
    /// reloads the current palette too: the high-contrast palette reads Windows' system colours when it loads.
    /// </summary>
    public static void Apply(string themeId, bool force = false)
    {
        if (!PaletteIds.Contains(themeId)) themeId = ThemeResolver.Default;
        if (themeId == CurrentId && !force) return;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/Palette.", StringComparison.Ordinal) == true);
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Envoy;component/Themes/Palette.{themeId}.xaml", UriKind.Absolute) };

        // Palette must come before the control styles that reference it.
        if (existing is not null)
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = next;
        }
        else
        {
            dictionaries.Insert(0, next);
        }
        CurrentId = themeId;
        Applied?.Invoke();
    }

    /// <summary>
    /// The overlay card's background alpha for the current palette (REQ-OUXB-36): the user's value, raised to keep
    /// Overlay.Text at 4.5:1 over white, grey and black when <see cref="OverlaySettings.EnsureReadableText"/> is on; 1 in
    /// high contrast. UI thread.
    /// </summary>
    public static double OverlayCardAlpha(OverlaySettings overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (IsHighContrast) return 1.0;
        return ContrastMath.EffectiveBackgroundAlpha(overlay.BackgroundOpacity, PaletteColour("Overlay.Text.Color", Colors.White),
            PaletteColour("Overlay.Bg.Color", Colors.Black), overlay.EnsureReadableText);
    }

    /// <summary>The minimum readable alpha for the current palette's overlay colours (the smoke test compares against it).</summary>
    public static double OverlayMinimumAlpha() => IsHighContrast
        ? 1.0
        : ContrastMath.MinimumBackgroundAlpha(PaletteColour("Overlay.Text.Color", Colors.White), PaletteColour("Overlay.Bg.Color", Colors.Black),
            ContrastMath.TextTarget);

    private static Rgb PaletteColour(string key, Color fallback)
    {
        var c = Application.Current?.TryFindResource(key) is Color found ? found : fallback;
        return new Rgb(c.R, c.G, c.B);
    }
}
