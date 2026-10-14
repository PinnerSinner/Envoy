using System.Windows;

namespace Envoy.App.Services;

public sealed record ThemeInfo(string Id, string Name, string Inspiration);

/// <summary>
/// Swaps the colour/typography dictionary at runtime. Every control style uses DynamicResource, so the whole UI
/// (settings, overlay, reply box) re-skins instantly.
/// </summary>
public static class ThemeManager
{
    public const string MatchGame = "match-game";

    public static IReadOnlyList<ThemeInfo> Themes { get; } =
    [
        new("imperial", "Imperial", "Age of Empires IV — midnight blue, burnished gold"),
        new("parchment", "Parchment", "Age of Empires II — illuminated manuscript, oak and vellum"),
        new("frontier", "Frontier", "Age of Empires III — colonial charts, brass and deep teal"),
        new("olympian", "Olympian", "Age of Mythology — marble, bronze and Aegean blue"),
        new("classic", "Classic", "Age of Empires — sandstone and bronze-age earth"),
    ];

    public static string CurrentId { get; private set; } = "";

    public static void Apply(string themeId)
    {
        if (Themes.All(t => t.Id != themeId)) themeId = "imperial";
        if (themeId == CurrentId) return;

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
    }
}
