using System.Collections.ObjectModel;
using Envoy.App.Services;
using Envoy.Core.Languages;

namespace Envoy.App.ViewModels;

/// <summary>Reading Chat page (and the War Room's chat-area and scouts cards).</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<Option> Games { get; }
    public ObservableCollection<OcrLanguageItem> OcrLanguages { get; }

    /// <summary>The live view. ReadingChatPage switches it on while the page is visible.</summary>
    public CapturePreviewViewModel CapturePreview { get; }

    public string RegionText => Host.Watcher.CurrentRegion is { IsEmpty: false } r
        ? $"{r.Width} × {r.Height} px at ({r.X}, {r.Y})"
        : "Not set yet — click “Select chat area”";

    public bool HasRegion => Host.Watcher.CurrentRegion is { IsEmpty: false };

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
