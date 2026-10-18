namespace Envoy.Core.Ux;

/// <summary>
/// Which parts of the war table's banner rail fit its height (spec 09a, REQ-HRD-6). The nine tabs must always be reachable,
/// so on a short window the rail first slims the tabs, then folds away the crest, the motto under the wordmark and the
/// status lines, in that order. The wordmark, the tabs and the version line always stay.
/// </summary>
/// <param name="CompactTabs">The tabs use <see cref="RailLayouts.CompactTabHeight"/> instead of the full height.</param>
/// <param name="Crest">The livery's crest on its rod, with its name, shows at the foot of the rail.</param>
/// <param name="Motto">The motto and flourish under the wordmark show.</param>
/// <param name="Status">The game and chat-reading status lines show above the version.</param>
public readonly record struct RailLayout(bool CompactTabs, bool Crest, bool Motto, bool Status);

/// <summary>
/// The rail's height budget. The heights are the rail's parts as laid out with the embedded fonts, rounded up; the smoke
/// test checks on Windows that the last tab ends above the rail's foot at the default size and at the minimum height.
/// </summary>
public static class RailLayouts
{
    /// <summary>The rail's top and bottom margins.</summary>
    public const double Padding = 42;
    /// <summary>The wordmark and the gap under it.</summary>
    public const double Wordmark = 74;
    /// <summary>The motto line and the flourish.</summary>
    public const double Motto = 44;
    /// <summary>One tab and the gap under it, full and compact.</summary>
    public const double TabHeight = 50, TabGap = 4, CompactTabHeight = 44, CompactTabGap = 2;
    /// <summary>The version line and the gap above the foot.</summary>
    public const double Version = 34;
    /// <summary>The two status lines (they never wrap).</summary>
    public const double Status = 46;
    /// <summary>The crest, its rod and its name.</summary>
    public const double Crest = 125;

    /// <summary>The space the nine tabs take, full or compact, including the gap above the first.</summary>
    public static double Tabs(bool compact) =>
        4 + PageHeadings.All.Count * (compact ? CompactTabHeight + CompactTabGap : TabHeight + TabGap);

    /// <summary>The height a layout needs.</summary>
    public static double Needs(RailLayout layout) =>
        Padding + Wordmark + Tabs(layout.CompactTabs) + Version
        + (layout.Motto ? Motto : 0) + (layout.Status ? Status : 0) + (layout.Crest ? Crest : 0);

    /// <summary>
    /// The fullest layout that fits <paramref name="railHeight"/>, giving things up in a fixed order; the barest one
    /// (compact tabs, wordmark and version only) when nothing fits.
    /// </summary>
    public static RailLayout For(double railHeight)
    {
        RailLayout[] steps =
        [
            new(CompactTabs: false, Crest: true, Motto: true, Status: true),
            new(CompactTabs: true, Crest: true, Motto: true, Status: true),
            new(CompactTabs: true, Crest: false, Motto: true, Status: true),
            new(CompactTabs: true, Crest: false, Motto: false, Status: true),
            new(CompactTabs: true, Crest: false, Motto: false, Status: false),
        ];
        foreach (var step in steps)
        {
            if (Needs(step) <= railHeight)
                return step;
        }
        return steps[^1];
    }
}
