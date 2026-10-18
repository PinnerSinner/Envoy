namespace Envoy.Core.Ux;

/// <summary>
/// One settings page's heading on the war table (spec 09a, REQ-HRD-6): the playful name on its banner tab and title, the
/// plain name beside it, the blackletter drop cap, a one-line lede and the caption of its marginal drawing.
/// </summary>
/// <param name="PageId">The app's page id (<c>PageIds</c>), e.g. "Dashboard".</param>
public sealed record PageHeading(string PageId, string Name, string PlainName, string Lede, string FigureCaption)
{
    /// <summary>The drop cap: the first letter of <see cref="Name"/>.</summary>
    public string DropCap => Name[..1];

    /// <summary>The tab's accessible name: the plain name, so nothing is a riddle for a screen reader.</summary>
    public string AccessibleName => PlainName;
}

/// <summary>The nine headings, in sidebar order. Medieval flavour stays in names and headings (REQ-HRD-11).</summary>
public static class PageHeadings
{
    /// <summary>Below this window width the marginal drawing column is hidden (the reference shows it from 1100 px).</summary>
    public const double MarginaliaMinWidth = 1100;

    public static IReadOnlyList<PageHeading> All { get; } =
    [
        new("Dashboard", "War Room", "Home", "Envoy at a glance: is it reading chat, who translates, what was said.", "Fig. I · the keep"),
        new("Translation", "Interpreters", "Translation", "Free interpreters need no account. Paid ones use your own key, so nobody else spends your credit.", "Fig. II · the twin tongues"),
        new("Capture", "Scouts", "Reading chat", "Where the chat is on screen, and how often Envoy reads it.", "Fig. III · the spyglass"),
        new("Overlay", "Heralds", "Overlay", "How translated messages appear over the game.", "Fig. IV · the herald's horn"),
        new("Reply", "Couriers", "Replying", "Write in your language; Envoy types it into game chat in theirs.", "Fig. V · the courier bird"),
        new("Phrases", "Codex", "Phrasebook", "Ready phrases, already translated, one click away.", "Fig. VI · the codex"),
        new("Hotkeys", "Seals", "Hotkeys", "The keys that call Envoy in the middle of a game.", "Fig. VII · seal and signet"),
        new("Appearance", "Livery", "Appearance", "Colours and arms. Each Age game can fly its own.", "Fig. VIII · the arms"),
        new("About", "Chronicle", "About", "Version, updates, logs and your data.", "Fig. IX · the glass of hours"),
    ];

    /// <summary>The heading for a page id; the War Room for an unknown id.</summary>
    public static PageHeading For(string? pageId) =>
        All.FirstOrDefault(h => string.Equals(h.PageId, pageId, StringComparison.Ordinal)) ?? All[0];

    /// <summary>Whether the war table shows the marginal drawing column at this window width.</summary>
    public static bool ShowsMarginalia(double windowWidth) => windowWidth >= MarginaliaMinWidth;
}
