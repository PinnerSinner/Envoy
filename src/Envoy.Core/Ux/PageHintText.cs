using Envoy.Core.Text;

namespace Envoy.Core.Ux;

/// <summary>Page callout sentences and their button labels (contract §8.7, REQ-OUXA-22, 23, 48).</summary>
public static class PageHintText
{
    public const string CaptureStopped = "Chat reading is off. Start it before your match so Envoy can translate.";
    public const string CaptureNoRegion = "Envoy doesn't know where the chat is yet. Select the chat area with the game open.";

    /// <summary>{reason} = why the file was not loaded (<c>PhrasebookText.LoadInvalidJson</c>, <c>LoadTooLarge</c> or <c>LoadUnreadable</c>).</summary>
    public const string PhrasesNotLoaded = "Your phrases.json was not loaded because {reason}. Envoy is using the built-in phrases until you fix it.";

    public const string NoDispatches = "No messages yet. Chat Envoy reads appears here, newest first.";
    public const string NoPlayers = "No players yet. Envoy lists who writes in which language as chat arrives.";
    public const string OcrUnavailable = "Windows text recognition isn't available on this PC, so Envoy can't read chat.";

    /// <summary>{count} = languages Envoy should read whose pack is missing.</summary>
    public const string OcrMissing = "Text recognition packs are missing for {count} language(s) you want Envoy to read. Install them below.";

    /// <summary>{provider} = the provider's display name.</summary>
    public const string CredentialsMissing = "{provider} needs its key or address before it can translate. Envoy uses your other providers until then.";

    /// <summary>{hotkey} = the show/hide overlay combination.</summary>
    public const string OverlayHidden = "The overlay is hidden, so translations won't show in game. Press {hotkey} or click Show overlay.";

    public const string OverlayHiddenNoHotkey = "The overlay is hidden, so translations won't show in game. Click Show overlay.";
    public const string ReplyHotkeyNotSet = "No reply hotkey is set, so you can only reply from the tray menu. Set one on the Hotkeys page.";

    /// <summary>{combo} = the reply combination.</summary>
    public const string ReplyHotkeyTaken = "Another app has taken the reply hotkey {combo}. Choose another on the Hotkeys page.";

    /// <summary>{combo} = the reply combination.</summary>
    public const string ReplyHotkeyInvalid = "The reply hotkey {combo} isn't a valid combination. Choose another on the Hotkeys page.";

    /// <summary>{list} = action names.</summary>
    public const string HotkeysNotSet = "These actions have no hotkey: {list}. Click a box below to set one.";

    public const string SelectRegionLabel = "Select chat area";
    public const string SelectRegionAgainLabel = "Select chat area again";
    public const string StartReadingLabel = "Start reading";
    public const string EditPhrasesLabel = "Edit my phrases";
    public const string ShowOverlayLabel = "Show overlay";
    public const string OpenHotkeysLabel = "Open Hotkeys";
    public const string OpenSetupLabel = "Open setup guide";
    public const string TurnOnTranslationLabel = "Turn on translation";

    public static string PhrasesNotLoadedFor(string reason) => TextTemplate.Format(PhrasesNotLoaded, null, ("reason", reason));
    public static string OcrMissingFor(int count) => TextTemplate.Format(OcrMissing, null, ("count", count));
    public static string CredentialsMissingFor(string provider) => TextTemplate.Format(CredentialsMissing, null, ("provider", provider));
    public static string OverlayHiddenFor(string hotkey) => TextTemplate.Format(OverlayHidden, null, ("hotkey", hotkey));
    public static string ReplyHotkeyTakenFor(string combo) => TextTemplate.Format(ReplyHotkeyTaken, null, ("combo", combo));
    public static string ReplyHotkeyInvalidFor(string combo) => TextTemplate.Format(ReplyHotkeyInvalid, null, ("combo", combo));
    public static string HotkeysNotSetFor(IEnumerable<string> names) => TextTemplate.Format(HotkeysNotSet, null, ("list", string.Join(", ", names)));
}
