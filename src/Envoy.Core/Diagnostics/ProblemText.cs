using Envoy.Core.Notifications;
using Envoy.Core.Text;

namespace Envoy.Core.Diagnostics;

/// <summary>
/// The text of every problem toast (contract §8.7): whole sentences, second person, what happened then what to do, no
/// "Error:" prefixes and never raw exception text.
/// </summary>
public static class ProblemText
{
    public const string UnexpectedTitle = "Something went wrong.";
    public const string UnexpectedDetail = "Envoy kept running. Select Details to see what happened.";
    public const string CommandFailedTitle = "That action didn't finish.";
    public const string CaptureErrorTitle = "Envoy stopped reading chat.";
    public const string CaptureErrorDetail = "Check the chat area, then start reading again.";
    public const string SettingsSaveFailedTitle = "Envoy couldn't save your settings.";
    public const string SettingsSaveFailedDetail = "Your changes apply until you close Envoy. Check that the settings folder isn't read-only.";
    public const string SettingsDamagedTitle = "Your settings file was damaged.";
    public const string SettingsRecoveredDetail = "Envoy restored your last working settings. The damaged copy is saved as settings.json.broken.";
    public const string SettingsResetDetail = "Envoy started with default settings. The damaged copy is saved as settings.json.broken.";
    public const string PreviousCrashTitle = "Envoy closed unexpectedly last time.";
    public const string PreviousCrashDetail = "If it happens again, copy diagnostics from About › Troubleshooting into a bug report.";
    public const string ReplySendFailedTitle = "Envoy couldn't type into the game.";
    public const string ReplySendFailedDetail = "Your reply is on the clipboard. Paste it into chat.";

    /// <summary>{reason} = a whole sentence from <c>SecurityText.SendBlocked</c>.</summary>
    public const string ReplyBlockedDetailFormat = "{reason} Your reply is on the clipboard.";
    public const string ExportFailedTitle = "Envoy couldn't save the diagnostics file.";
    public const string ExportFailedDetail = "Choose another folder and try again.";
    public const string ClipboardBusyTitle = "Envoy couldn't use the clipboard.";
    public const string ClipboardBusyDetail = "Close any clipboard tool and try again.";

    public const string CaptureErrorKey = "capture:error";
    public const string SettingsSaveKey = "settings.save";
    public const string StartupSettingsKey = "startup.settings";
    public const string StartupCrashKey = "startup.crash";
    public const string ComposeSendKey = "compose.send";
    public const string ExportKey = "export";
    public const string ClipboardKey = "clipboard";

    /// <summary>An unhandled exception; <paramref name="key"/> is the exception type plus target site.</summary>
    public static ProblemReport Unexpected(string key) => new(key, UnexpectedTitle, UnexpectedDetail);

    /// <summary>An <c>AsyncRelayCommand</c> failed; <paramref name="key"/> is <c>command:</c> plus the exception type.</summary>
    public static ProblemReport CommandFailed(string key) => new(key, CommandFailedTitle, UnexpectedDetail);

    public static ProblemReport CaptureError { get; } = new(CaptureErrorKey, CaptureErrorTitle, CaptureErrorDetail, LogLevel.Warning);
    public static ProblemReport SettingsSaveFailed { get; } = new(SettingsSaveKey, SettingsSaveFailedTitle, SettingsSaveFailedDetail);
    public static ProblemReport SettingsRecovered { get; } = new(StartupSettingsKey, SettingsDamagedTitle, SettingsRecoveredDetail, LogLevel.Warning);
    public static ProblemReport SettingsReset { get; } = new(StartupSettingsKey, SettingsDamagedTitle, SettingsResetDetail, LogLevel.Warning);
    public static ProblemReport PreviousCrash { get; } = new(StartupCrashKey, PreviousCrashTitle, PreviousCrashDetail, LogLevel.Warning);
    public static ProblemReport ReplySendFailed { get; } = new(ComposeSendKey, ReplySendFailedTitle, ReplySendFailedDetail, LogLevel.Warning);
    public static ProblemReport ExportFailed { get; } = new(ExportKey, ExportFailedTitle, ExportFailedDetail, LogLevel.Warning);
    public static ProblemReport ClipboardBusy { get; } = new(ClipboardKey, ClipboardBusyTitle, ClipboardBusyDetail, LogLevel.Warning);

    /// <summary>A reply that the send-target guard stopped; <paramref name="reason"/> is the curated guard sentence.</summary>
    public static ProblemReport ReplyBlocked(string reason) =>
        new(ComposeSendKey, ReplySendFailedTitle, TextTemplate.Format(ReplyBlockedDetailFormat, null, ("reason", reason)), LogLevel.Warning);

    /// <summary>Every fixed report, for the voice-rule test.</summary>
    public static IReadOnlyList<ProblemReport> All =>
    [
        Unexpected("k"), CommandFailed("k"), CaptureError, SettingsSaveFailed, SettingsRecovered, SettingsReset,
        PreviousCrash, ReplySendFailed, ExportFailed, ClipboardBusy,
    ];
}
