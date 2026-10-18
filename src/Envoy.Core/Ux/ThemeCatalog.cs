namespace Envoy.Core.Ux;

/// <summary>
/// One livery of the Herald's Writ look (spec 09a, REQ-HRD-2): the same oak, vellum and ink, with its own primary and
/// secondary tinctures and wax. <paramref name="Primary"/> and <paramref name="Secondary"/> are <c>#RRGGBB</c> for the
/// Livery page's shield swatches; the palettes carry the same values as <c>Livery.Primary</c> and <c>Livery.Secondary</c>.
/// </summary>
public sealed record Livery(string Id, string Name, string Description, string Primary, string Secondary);

/// <summary>
/// The themes the Livery page offers, in picker order (spec 09a). Ids never change, so saved settings keep working; the
/// palette file for each is <c>Themes/Palette.{Id}.xaml</c>. High contrast is not a livery: only
/// <see cref="ThemeResolver"/> chooses it.
/// </summary>
public static class ThemeCatalog
{
    public const string Herald = "herald";

    /// <summary>The picker's first card: follow the running game (<see cref="ThemeResolver.MatchGame"/>).</summary>
    public const string MatchGameName = "Follow the game";
    public const string MatchGameDescription = "Each Age game flies its own livery. Envoy's own arms when no game is running.";

    public static IReadOnlyList<Livery> Liveries { get; } =
    [
        new(Herald, "Herald", "Envoy's own arms: gules and or", "#8E2418", "#D8B04A"),
        new("imperial", "Imperial", "Age of Empires IV livery: gules and or", "#8E2418", "#D8B04A"),
        new("parchment", "Parchment", "Age of Empires II livery: azure and or", "#2B4C8C", "#D8B04A"),
        new("frontier", "Frontier", "Age of Empires III livery: vert and argent", "#2F5E35", "#E9E4D6"),
        new("olympian", "Olympian", "Age of Mythology livery: purpure and or", "#5B2D73", "#D8B04A"),
        new("classic", "Classic", "Age of Empires livery: sable and or", "#1C1A18", "#D8B04A"),
    ];

    /// <summary>Every livery id, in picker order.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. Liveries.Select(l => l.Id)];

    /// <summary>The livery with this id, or null.</summary>
    public static Livery? Find(string? id) => Liveries.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal));
}
