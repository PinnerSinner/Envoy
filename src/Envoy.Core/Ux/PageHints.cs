using Envoy.Core.Capture;
using Envoy.Core.Hotkeys;
using Envoy.Core.Settings;
using Envoy.Core.Translation;

namespace Envoy.Core.Ux;

public enum HintSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>What a callout's button does; the page view model maps each id to an existing command or <c>IShell</c> call.</summary>
public static class HintActions
{
    public const string SelectRegion = "select-region";
    public const string ToggleCapture = "toggle-capture";
    public const string OpenTranslation = "open-translation";
    public const string OpenCapture = "open-capture";
    public const string ShowOverlay = "show-overlay";
    public const string OpenHotkeys = "open-hotkeys";
    public const string EditPhrases = "edit-phrases";
    public const string OpenSetup = "open-setup";
}

/// <summary>One page callout (REQ-OUXA-22): a sentence from <see cref="PageHintText"/> and an optional action.</summary>
public sealed record PageHint(HintSeverity Severity, string Text, string? ActionLabel = null, string? ActionId = null)
{
    public bool HasAction => ActionId is not null;
    public bool IsInfo => Severity == HintSeverity.Info;
}

public enum CaptureConditionKind
{
    Running,
    Stopped,
    NoRegion,
    WaitingForGame,
    AutoPaused,
    Error,
}

/// <summary>The watcher's state as the War Room capture hint sees it (MainViewModel maps <c>WatcherState</c> in one switch).</summary>
/// <param name="Error">The 05 sentence for <see cref="CaptureConditionKind.Error"/>.</param>
public readonly record struct CaptureCondition(CaptureConditionKind Kind, PauseReason Reason = PauseReason.None, string? Error = null)
{
    public static CaptureCondition AutoPaused(PauseReason reason) => new(CaptureConditionKind.AutoPaused, reason);
}

/// <summary>Empty and error states for each page (REQ-OUXA-22, 23, 48). Pure.</summary>
public static class PageHints
{
    /// <summary>
    /// The War Room capture hint (MUST). Stopped → Info + "Start reading". NoRegion → Warning + "Select chat area".
    /// WaitingForGame and Running → null. AutoPaused(r): NoRegion and NoGame map as above; RegionOffScreen → Warning
    /// <see cref="CaptureText.Paused"/> + "Select chat area again"; any other reason → Info <see cref="CaptureText.Paused"/>
    /// with no action (an automatic pause never offers Resume). Error → Error with 05's sentence.
    /// </summary>
    public static PageHint? Capture(CaptureCondition capture) => capture.Kind switch
    {
        CaptureConditionKind.Stopped => new(HintSeverity.Info, PageHintText.CaptureStopped, PageHintText.StartReadingLabel, HintActions.ToggleCapture),
        CaptureConditionKind.NoRegion => NoRegion(),
        CaptureConditionKind.Running or CaptureConditionKind.WaitingForGame => null,
        CaptureConditionKind.AutoPaused => capture.Reason switch
        {
            PauseReason.None or PauseReason.NoGame => null,
            PauseReason.NoRegion => NoRegion(),
            PauseReason.RegionOffScreen => new(HintSeverity.Warning, CaptureText.Paused(capture.Reason), PageHintText.SelectRegionAgainLabel, HintActions.SelectRegion),
            _ => new(HintSeverity.Info, CaptureText.Paused(capture.Reason)),
        },
        CaptureConditionKind.Error => new(HintSeverity.Error, string.IsNullOrWhiteSpace(capture.Error) ? CaptureText.CaptureFailed : capture.Error),
        _ => throw new ArgumentOutOfRangeException(nameof(capture), capture.Kind, null),
    };

    /// <summary>The Phrasebook callout (MUST, REQ-OUXA-23): null without a load error.</summary>
    public static PageHint? Phrasebook(string? loadError) => string.IsNullOrWhiteSpace(loadError)
        ? null
        : new(HintSeverity.Warning, PageHintText.PhrasesNotLoadedFor(loadError), PageHintText.EditPhrasesLabel, HintActions.EditPhrases);

    /// <summary>The War Room "Dispatches" empty state.</summary>
    public static PageHint? Dispatches(int messageCount) => messageCount > 0 ? null : new(HintSeverity.Info, PageHintText.NoDispatches);

    /// <summary>The War Room "Players this match" empty state.</summary>
    public static PageHint? Players(int playerCount) => playerCount > 0 ? null : new(HintSeverity.Info, PageHintText.NoPlayers);

    /// <summary>Reading Chat: no OCR language at all (Error), or wanted packs missing (Warning).</summary>
    public static PageHint? OcrAvailability(int statusCount, int missingWanted) =>
        statusCount <= 0 ? new(HintSeverity.Error, PageHintText.OcrUnavailable)
        : missingWanted > 0 ? new(HintSeverity.Warning, PageHintText.OcrMissingFor(missingWanted))
        : null;

    /// <summary>Translation: a provider in the chain has no key or address.</summary>
    public static PageHint? ProviderCredentials(string providerName, bool hasCredential)
    {
        ArgumentNullException.ThrowIfNull(providerName);
        return hasCredential ? null : new(HintSeverity.Warning, PageHintText.CredentialsMissingFor(providerName));
    }

    /// <summary>Overlay: the overlay is hidden.</summary>
    public static PageHint? OverlayHidden(bool visible, string toggleHotkey) => visible
        ? null
        : new(HintSeverity.Info, string.IsNullOrWhiteSpace(toggleHotkey) ? PageHintText.OverlayHiddenNoHotkey : PageHintText.OverlayHiddenFor(toggleHotkey),
            PageHintText.ShowOverlayLabel, HintActions.ShowOverlay);

    /// <summary>Replying: the reply hotkey is not set, taken or invalid.</summary>
    public static PageHint? ReplyHotkey(HotkeyRegistrationResult? compose) => compose?.Status switch
    {
        HotkeyStatus.NotSet => new(HintSeverity.Warning, PageHintText.ReplyHotkeyNotSet, PageHintText.OpenHotkeysLabel, HintActions.OpenHotkeys),
        HotkeyStatus.Taken => new(HintSeverity.Warning, PageHintText.ReplyHotkeyTakenFor(compose.Combo), PageHintText.OpenHotkeysLabel, HintActions.OpenHotkeys),
        HotkeyStatus.Invalid => new(HintSeverity.Warning, PageHintText.ReplyHotkeyInvalidFor(compose.Combo), PageHintText.OpenHotkeysLabel, HintActions.OpenHotkeys),
        _ => null,
    };

    /// <summary>Hotkeys: taken or invalid combinations (Warning, 01's summary text), else unset ones (Info).</summary>
    public static PageHint? Hotkeys(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var taken = HotkeyText.TakenSummary(results);
        if (taken.Length > 0) return new(HintSeverity.Warning, taken);
        var unset = results.Where(r => r.Status == HotkeyStatus.NotSet).Select(r => r.ActionName).ToList();
        return unset.Count > 0 ? new(HintSeverity.Info, PageHintText.HotkeysNotSetFor(unset)) : null;
    }

    private static PageHint NoRegion() =>
        new(HintSeverity.Warning, PageHintText.CaptureNoRegion, PageHintText.SelectRegionLabel, HintActions.SelectRegion);
}

/// <summary>Whether a provider has what it needs to be built (for the Translation page callout).</summary>
public static class ProviderCredentials
{
    public static bool IsSet(string providerId, TranslationSettings t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return providerId switch
        {
            ProviderCatalog.DeepL => !string.IsNullOrWhiteSpace(t.DeepLKey),
            ProviderCatalog.Claude => !string.IsNullOrWhiteSpace(t.ClaudeKey),
            ProviderCatalog.Aws => !string.IsNullOrWhiteSpace(t.AwsAccessKeyId) && !string.IsNullOrWhiteSpace(t.AwsSecretAccessKey),
            ProviderCatalog.LibreTranslate => !string.IsNullOrWhiteSpace(t.LibreTranslateUrl),
            _ => true,
        };
    }
}

/// <summary>Which callout the War Room hint slot shows (contract §8.6).</summary>
public enum HintSlotKind
{
    None,
    Privacy,
    FinishSetup,
    PageHint,
}

public static class WarRoomSlots
{
    /// <summary>Privacy while unacknowledged; else FinishSetup while the guide is not completed; else the page hint; else None.</summary>
    public static HintSlotKind Choose(bool privacyAcknowledged, bool onboardingCompleted, PageHint? hint) =>
        !privacyAcknowledged ? HintSlotKind.Privacy
        : !onboardingCompleted ? HintSlotKind.FinishSetup
        : hint is not null ? HintSlotKind.PageHint
        : HintSlotKind.None;
}
