namespace Envoy.App.Pages;

/// <summary>
/// The settings pages' ids, in sidebar order. Each id names the sidebar button (<c>Nav{id}</c>) and the screenshot
/// (<c>{palette}-settings-{id lower-case}.png</c>). New pages are appended to <see cref="All"/>.
/// </summary>
public static class PageIds
{
    public const string Dashboard = "Dashboard", Translation = "Translation", Capture = "Capture", Overlay = "Overlay",
        Reply = "Reply", Phrases = "Phrases", Hotkeys = "Hotkeys", Appearance = "Appearance", About = "About";

    public static IReadOnlyList<string> All { get; } = [Dashboard, Translation, Capture, Overlay, Reply, Phrases, Hotkeys, Appearance, About];
}
