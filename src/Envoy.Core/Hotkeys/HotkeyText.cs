using Envoy.Core.Diagnostics;
using Envoy.Core.Text;

namespace Envoy.Core.Hotkeys;

/// <summary>User-facing hotkey text (contract §8.7).</summary>
public static class HotkeyText
{
    /// <summary>{list} = comma-separated "Name (combo)" list.</summary>
    public const string TakenFormat = "These hotkeys are taken by another app: {list}";

    /// <summary>One entry of {list}: {name} = action display name; {combo} = the combination.</summary>
    public const string TakenItemFormat = "{name} ({combo})";

    /// <summary>Display name of a hotkey action, keyed by its <c>HotkeySettings</c> property name.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is not a known hotkey action.</exception>
    public static string ActionName(string actionId) => actionId switch
    {
        "Compose" => "Reply",
        "ToggleOverlay" => "Show/hide overlay",
        "ToggleCapture" => "Pause/resume",
        "SelectRegion" => "Select chat area",
        "OverlayEditMode" => "Move overlay",
        "ScanAndTranslate" => "Scan & translate",
        _ => throw new ArgumentOutOfRangeException(nameof(actionId), actionId, "Unknown hotkey action."),
    };

    /// <summary>
    /// The Hotkeys page status line: <see cref="SafeModeText.HotkeysOff"/> when any entry was skipped for safe mode,
    /// otherwise <see cref="TakenSummary"/>.
    /// </summary>
    public static string StatusLine(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.Any(r => r.Status == HotkeyStatus.SkippedSafeMode) ? SafeModeText.HotkeysOff : TakenSummary(results);
    }

    /// <summary>
    /// "" when no entry is <see cref="HotkeyStatus.Taken"/> or <see cref="HotkeyStatus.Invalid"/>; otherwise
    /// <see cref="TakenFormat"/> over those entries in list order. Invalid entries are included because the text has
    /// always listed them too.
    /// </summary>
    public static string TakenSummary(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var failed = results.Where(r => r.Status is HotkeyStatus.Taken or HotkeyStatus.Invalid)
            .Select(r => TextTemplate.Format(TakenItemFormat, null, ("name", r.ActionName), ("combo", r.Combo)))
            .ToList();
        return failed.Count == 0 ? "" : TextTemplate.Format(TakenFormat, null, ("list", string.Join(", ", failed)));
    }
}
