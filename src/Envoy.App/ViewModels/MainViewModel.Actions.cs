using Envoy.Core.Mvvm;
using Envoy.Core.Phrases;
using Envoy.Core.Translation;

namespace Envoy.App.ViewModels;

/// <summary>The commands behind every page button and link. Each does what the old click handler did.</summary>
public sealed partial class MainViewModel
{
    public RelayCommand SelectRegionCommand { get; }
    public RelayCommand ToggleCaptureCommand { get; }
    public RelayCommand ShowSampleMessagesCommand { get; }
    public RelayCommand MoveOverlayCommand { get; }
    public RelayCommand OpenReplyBoxCommand { get; }
    public RelayCommand ScanAndTranslateCommand { get; }
    public RelayCommand NewMatchCommand { get; }

    /// <summary>Opens the release page. Always enabled: the banner is only visible when an update exists. 06 replaces it.</summary>
    public RelayCommand DownloadUpdateCommand { get; }

    /// <summary>Applies pending edits, then translates the "Try it" text. Ignores clicks while one is running.</summary>
    public AsyncRelayCommand TestTranslateCommand { get; }

    /// <summary>Parameter: the Envoy language code whose OCR pack to install.</summary>
    public RelayCommand<string> InstallOcrCommand { get; }

    /// <summary>Applies pending edits (so the just-chosen voice is used), then speaks a sample.</summary>
    public RelayCommand TestVoiceCommand { get; }

    public RelayCommand EditPhrasesCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    /// <summary>Parameter: the URL to open.</summary>
    public RelayCommand<string> OpenLinkCommand { get; }

    public RelayCommand QuitCommand { get; }

    private void DownloadUpdate()
    {
        if (Host.Update is { } u) _shell.OpenUrl(u.PageUrl);
    }

    private async Task TestTranslateAsync()
    {
        _applier.ApplyNow();
        TranslationTestOutput = TranslationTestText.Pending;
        try
        {
            var result = await Host.Translator.TranslateAsync(new(TranslationTestInput, Settings.MyLanguage));
            TranslationTestOutput = TranslationTestText.Describe(result, Settings.MyLanguage);
        }
        catch (Exception ex)
        {
            TranslationTestOutput = TranslationTestText.Failure(ex);
        }
    }

    private void InstallOcr(string? code)
    {
        if (code is not null) _shell.InstallOcrPack(code);
    }

    private void TestVoice()
    {
        _applier.ApplyNow();
        Host.TestVoice();
    }

    private void EditPhrases()
    {
        var path = Host.UserPhrasesPath;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PhrasePreview.UserFileTemplate);
        }
        _shell.OpenUrl(path);
        _shell.ShowInfo(PhrasebookText.EditTitle, PhrasebookText.EditInstructions);
        Host.ReloadPhrases();
        RefreshPhrasePreview();
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(Host.LogsFolder);
        _shell.OpenUrl(Host.LogsFolder);
    }

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(Host.DataFolder);
        _shell.OpenUrl(Host.DataFolder);
    }

    private void OpenLink(string? url)
    {
        if (!string.IsNullOrEmpty(url)) _shell.OpenUrl(url);
    }
}
