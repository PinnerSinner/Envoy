using System.Collections.ObjectModel;
using Envoy.App.Services;
using Envoy.App.Services.Capture;
using Envoy.Core.Capture;
using Envoy.Core.Chat;
using Envoy.Core.Games;
using Envoy.Core.Languages;

namespace Envoy.App.ViewModels;

/// <summary>Reading Chat page (and the War Room's chat-area and scouts cards).</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<Option> Games { get; }
    public ObservableCollection<OcrLanguageItem> OcrLanguages { get; }

    /// <summary>The live view. ReadingChatPage switches it on while the page is visible.</summary>
    public CapturePreviewViewModel CapturePreview { get; }

    /// <summary>The chat area's size and whether it follows the game window or is a fixed screen position (REQ-CAP-8).</summary>
    public string RegionText => RegionTextFor(Host);

    /// <summary>The region line, shared with the setup guide's Chat area step.</summary>
    internal static string RegionTextFor(AppHost host)
    {
        if (host.Watcher.CurrentRegion is not { IsEmpty: false } r) return CaptureText.RegionNotSet;
        if (host.Watcher.CurrentRegionSource != RegionSource.WindowRelative) return CaptureText.RegionFixed(r);
        var game = GameCatalog.Find(host.CurrentGameId)?.Name ?? host.Game?.Profile.Name ?? host.CurrentGameId;
        return CaptureText.RegionFollows(r.Width, r.Height, game);
    }

    /// <summary>Asks for a re-select when the game's size changed since the chat area was chosen; "" otherwise.</summary>
    public string RegionHint => Host.Watcher.CurrentRegionSource == RegionSource.WindowRelative && Host.Watcher.ClientSizeDiffers
        ? CaptureText.RegionResizedHint
        : "";

    public bool HasRegionHint => RegionHint.Length > 0;

    public bool HasRegion => Host.Watcher.CurrentRegion is { IsEmpty: false };

    /// <summary>The capture methods this machine offers ("Capture method"; collapsed while there is only one).</summary>
    public IReadOnlyList<Option> CaptureBackends { get; } =
        [.. CaptureBackendFactory.Available().Select(b => new Option(b.Id, b.Label))];

    public bool HasCaptureBackendChoice => CaptureBackends.Count > 1;

    /// <summary>Re-reads the region row when the game window moves or the resolved region changes.</summary>
    private void OnRegionMoved()
    {
        Notify(nameof(RegionText));
        Notify(nameof(RegionHint));
        Notify(nameof(HasRegionHint));
        Notify(nameof(HasRegion));
    }

    // ---- Understanding chat (spec 04) ----

    private ChatFormatCatalog? _chatFormatOptionsFrom;
    private IReadOnlyList<Option> _chatFormatOptions = [];

    /// <summary>The notice patterns, one per line, for the "Also treat these lines as notices" box (REQ-CHAT-40).</summary>
    public string SystemLinePatternsText
    {
        get => string.Join("\n", Settings.Chat.SystemLinePatterns);
        set
        {
            Settings.Chat.SystemLinePatterns = SystemLinePatterns.SplitLines(value);
            _applier.ApplyNow();
            Notify();
            RefreshChatUnderstanding();
        }
    }

    /// <summary>Pattern and chat-formats.json problems, one sentence each; "" when there are none.</summary>
    public string ChatUnderstandingStatus => ChatUnderstandingText.Errors(Host.Pipeline.SystemPatternErrors, Host.ChatFormats.Errors);

    public bool HasChatUnderstandingErrors => ChatUnderstandingStatus.Length > 0;

    /// <summary>"Match the game", then every chat format profile (REQ-CHAT-33). Rebuilt only when the catalog changes.</summary>
    public IReadOnlyList<Option> ChatFormatOptions
    {
        get
        {
            if (!ReferenceEquals(_chatFormatOptionsFrom, Host.ChatFormats))
            {
                _chatFormatOptionsFrom = Host.ChatFormats;
                _chatFormatOptions =
                [
                    new Option(ChatFormatCatalog.Auto, ChatUnderstandingText.MatchGameFormat),
                    .. Host.ChatFormats.Profiles.Select(p => new Option(p.Id, p.Name)),
                ];
            }
            return _chatFormatOptions;
        }
    }

    /// <summary>Re-reads the card's error line, and the format list if chat-formats.json was reloaded.</summary>
    private void RefreshChatUnderstanding()
    {
        Notify(nameof(ChatUnderstandingStatus));
        Notify(nameof(HasChatUnderstandingErrors));
        if (!ReferenceEquals(_chatFormatOptionsFrom, Host.ChatFormats)) Notify(nameof(ChatFormatOptions));
    }

    public string OcrSummary => Host.Ocr.ActiveLanguages.Count == 0
        ? "No OCR languages available"
        : "Reading: " + string.Join(", ", Host.Ocr.ActiveLanguages.Select(LanguageCatalog.NameOf));

    private IEnumerable<OcrLanguageItem> LoadOcrLanguages()
    {
        IReadOnlyList<OcrLanguageStatus> statuses;
        try { statuses = OcrService.AllLanguageStatuses(); }
        catch (Exception) { statuses = []; }

        var selected = new HashSet<string>(Settings.Capture.OcrLanguages, StringComparer.OrdinalIgnoreCase);
        // Latin-script languages are all read by any Latin OCR pack (e.g. English), so only list them if installed.
        return statuses
            .Where(s => s.Installed || LanguageCatalog.Find(s.Code) is not { Script: Script.Latin } || s.Code == "vi")
            .OrderByDescending(s => s.Code == Settings.MyLanguage)
            .ThenByDescending(s => s.Installed)
            .Select(s => new OcrLanguageItem(s, selected.Contains(s.Code), s.Code == Settings.MyLanguage, OnOcrSelectionChanged));
    }

    private void OnOcrSelectionChanged()
    {
        // Keep wanted-but-not-installed languages in the list so they activate as soon as they're installed.
        var notInstalled = Settings.Capture.OcrLanguages.Where(c => OcrLanguages.Any(l => l.Code == c && !l.Installed));
        Settings.Capture.OcrLanguages = OcrLanguages.Where(l => l.Installed && !l.IsMine && l.IsSelected).Select(l => l.Code)
            .Concat(notInstalled).Distinct().ToList();
        Host.ApplySettings();
        Notify(nameof(OcrSummary));
    }
}
