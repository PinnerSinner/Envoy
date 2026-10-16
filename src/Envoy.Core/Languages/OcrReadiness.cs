namespace Envoy.Core.Languages;

/// <summary>One row of the guide's Text recognition step (REQ-OUXA-7).</summary>
/// <param name="Code">Envoy language code.</param>
/// <param name="Installed">Windows can read it (its pack, or any Latin pack for a Latin-script language other than Vietnamese).</param>
/// <param name="IsMine">Your own language (always read).</param>
/// <param name="Wanted">Listed in the languages Envoy reads (or your own language).</param>
public sealed record OcrRecommendation(string Code, string Name, bool Installed, bool IsMine, bool Wanted);

/// <summary>Which Windows text recognition packs Envoy needs and which are missing (REQ-OUXA-7, 17). Pure.</summary>
public static class OcrReadiness
{
    /// <summary>The scripts opponents write in most often in AoE lobbies (also the default <c>Capture.OcrLanguages</c>).</summary>
    public static IReadOnlyList<string> CommonOpponentLanguages { get; } = ["zh-CN", "ko", "ja", "ru"];

    /// <summary>
    /// Wanted codes (normalised, distinct, plus <paramref name="myLanguage"/> first) whose pack is not installed. An empty
    /// <paramref name="wanted"/> means "every installed pack", so only <paramref name="myLanguage"/> can be missing.
    /// </summary>
    public static IReadOnlyList<string> MissingWanted(IEnumerable<string> wanted, string myLanguage, IEnumerable<string> installedCodes)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(installedCodes);
        var installed = InstalledSet(installedCodes);
        return [.. WantedCodes(wanted, myLanguage).Where(code => !IsCovered(code, installed))];
    }

    /// <summary>Your language first, then the wanted ones, then <see cref="CommonOpponentLanguages"/>; distinct, with status.</summary>
    public static IReadOnlyList<OcrRecommendation> Recommend(string myLanguage, IEnumerable<string> wanted, IEnumerable<string> installedCodes)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(installedCodes);
        var installed = InstalledSet(installedCodes);
        var mine = Normalize(myLanguage);
        var wantedCodes = WantedCodes(wanted, myLanguage);
        var wantedSet = new HashSet<string>(wantedCodes, StringComparer.OrdinalIgnoreCase);
        var order = wantedCodes.Concat(CommonOpponentLanguages.Select(Normalize)).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. order.Select(code => new OcrRecommendation(code, LanguageCatalog.NameOf(code), IsCovered(code, installed),
                string.Equals(code, mine, StringComparison.OrdinalIgnoreCase), wantedSet.Contains(code))),
        ];
    }

    /// <summary>True when <paramref name="code"/>'s pack is installed, or it is a Latin-script language (not "vi") and any Latin pack is.</summary>
    public static bool IsCovered(string code, IReadOnlySet<string> installedNormalised)
    {
        ArgumentNullException.ThrowIfNull(installedNormalised);
        if (installedNormalised.Contains(code)) return true;
        return IsLatinCoverable(code) && installedNormalised.Any(i => LanguageCatalog.Find(i)?.Script == Script.Latin);
    }

    private static bool IsLatinCoverable(string code) => code != "vi" && LanguageCatalog.Find(code)?.Script == Script.Latin;

    private static HashSet<string> InstalledSet(IEnumerable<string> installedCodes) =>
        new(installedCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(Normalize), StringComparer.OrdinalIgnoreCase);

    /// <summary>Your language first, then each wanted code, normalised and distinct.</summary>
    private static List<string> WantedCodes(IEnumerable<string> wanted, string myLanguage)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in new[] { myLanguage }.Concat(wanted))
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            var n = Normalize(code);
            if (seen.Add(n)) list.Add(n);
        }
        return list;
    }

    private static string Normalize(string? code) => string.IsNullOrWhiteSpace(code) ? "" : LanguageCatalog.Normalize(code);
}
