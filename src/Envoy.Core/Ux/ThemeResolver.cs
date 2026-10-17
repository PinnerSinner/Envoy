using Envoy.Core.Settings;

namespace Envoy.Core.Ux;

/// <summary>Which palette Envoy shows (REQ-OUXB-33): high contrast when asked or when Windows uses a contrast theme.</summary>
public static class ThemeResolver
{
    public const string HighContrast = "highcontrast";
    public const string MatchGame = "match-game";
    public const string Default = "imperial";

    /// <summary>
    /// <see cref="HighContrastMode.Always"/> → <see cref="HighContrast"/>; <see cref="HighContrastMode.FollowWindows"/>
    /// with <paramref name="systemHighContrast"/> → <see cref="HighContrast"/>; otherwise the game's theme for
    /// <see cref="MatchGame"/> (or <see cref="Default"/> with no game), else the chosen theme. An id not in
    /// <paramref name="knownThemeIds"/> becomes <see cref="Default"/>.
    /// </summary>
    public static string Resolve(string? themeSetting, string? gameThemeId, bool systemHighContrast, HighContrastMode mode,
                                 IReadOnlyCollection<string> knownThemeIds)
    {
        ArgumentNullException.ThrowIfNull(knownThemeIds);
        if (mode == HighContrastMode.Always || (mode == HighContrastMode.FollowWindows && systemHighContrast)) return HighContrast;
        var id = themeSetting == MatchGame ? gameThemeId ?? Default : themeSetting;
        return id is not null && knownThemeIds.Contains(id) ? id : Default;
    }
}
