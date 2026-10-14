namespace Envoy.Core.Games;

/// <param name="ProcessNames">Executable names without ".exe", used to auto-detect the running game.</param>
/// <param name="ThemeId">Theme used when the theme is set to "Match the game".</param>
public sealed record GameProfile(string Id, string Name, string ShortName, IReadOnlyList<string> ProcessNames, string ThemeId);

public static class GameCatalog
{
    public const string Any = "any";

    public static IReadOnlyList<GameProfile> All { get; } =
    [
        new("aoe4", "Age of Empires IV", "AoE IV", ["RelicCardinal", "RelicCardinal_ws", "AoE4"], "imperial"),
        new("aoe2de", "Age of Empires II: Definitive Edition", "AoE II: DE", ["AoE2DE_s", "AoE2DE"], "parchment"),
        new("aoe3de", "Age of Empires III: Definitive Edition", "AoE III: DE", ["AoE3DE_s", "AoE3DE"], "frontier"),
        new("aom", "Age of Mythology: Retold", "AoM: Retold", ["AoMRT_s", "AoMRT", "AoMR"], "olympian"),
        new("aoe1de", "Age of Empires: Definitive Edition", "AoE: DE", ["AoEDE_s", "AoEDE"], "classic"),
        new("aoe2hd", "Age of Empires II HD", "AoE II HD", ["AoK HD"], "parchment"),
        new("aoe3", "Age of Empires III (2007)", "AoE III", ["age3y", "age3x", "age3"], "frontier"),
    ];

    public static GameProfile? Find(string? id) => All.FirstOrDefault(g => g.Id == id);

    /// <summary>Returns the first known game among the given running process names.</summary>
    public static GameProfile? Detect(IEnumerable<string> runningProcessNames)
    {
        var running = new HashSet<string>(runningProcessNames, StringComparer.OrdinalIgnoreCase);
        return All.FirstOrDefault(g => g.ProcessNames.Any(running.Contains));
    }
}
