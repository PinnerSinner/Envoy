using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Envoy.App.Services;
using Envoy.Core.Diagnostics;
using Envoy.Core.Games;
using Envoy.Core.Hotkeys;
using Envoy.Core.Languages;
using Envoy.Core.Mvvm;
using Envoy.Core.Settings;

namespace Envoy.App.ViewModels;

/// <summary>
/// The single view model behind MainWindow and every page in <c>Pages/</c>. Split into partial files by page:
/// <c>.Translation</c>, <c>.ReadingChat</c>, <c>.Phrasebook</c>, <c>.Diagnostics</c> (About › Troubleshooting and the
/// safe-mode banner) and <c>.Actions</c> (the commands). App-level actions go
/// through <see cref="IShell"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IShell _shell;
    private readonly SettingsAutoApplier _applier;
    private readonly PropertyChangedEventHandler _hostChanged;
    private bool _disposed;

    internal MainViewModel(AppHost host, IShell shell, SettingsAutoApplier applier)
    {
        Host = host;
        _shell = shell;
        _applier = applier;
        _hostChanged = (_, _) => Notify(nameof(Host));
        host.PropertyChanged += _hostChanged;
        host.HotkeyResultsChanged += OnHotkeyResultsChanged;

        ReplyLanguages = [new Option(LanguageCatalog.Auto, "Auto — whatever they last wrote in"), .. Languages.Select(l => new Option(l.Code, l.DisplayName))];
        Games = [new Option(GameCatalog.Any, "Detect automatically"), .. GameCatalog.All.Select(g => new Option(g.Id, g.Name))];

        Themes =
        [
            new ThemeOption(ThemeManager.MatchGame, "Match the game", "Switches automatically: Imperial for AoE IV, Parchment for AoE II, and so on.",
                [Swatch("#0E1420"), Swatch("#CDB98F"), Swatch("#0D1C1B"), Swatch("#0B1828")], () => Settings.Theme, SetTheme),
            .. ThemeManager.Themes.Select(t => new ThemeOption(t.Id, t.Name, t.Inspiration, SwatchesFor(t.Id), () => Settings.Theme, SetTheme)),
        ];

        OcrLanguages = new(LoadOcrLanguages());
        CapturePreview = new CapturePreviewViewModel(host, System.Windows.Threading.Dispatcher.CurrentDispatcher);

        _phrasePreviewLanguage = LanguageCatalog.Find(host.Pipeline.LastOpponentLanguage) ?? LanguageCatalog.Find("zh-CN");
        RefreshPhrasePreview();

        // Commands (bodies in MainViewModel.Actions.cs).
        SelectRegionCommand = new RelayCommand(_shell.SelectRegion);
        ToggleCaptureCommand = new RelayCommand(Host.ToggleCapture);
        ShowSampleMessagesCommand = new RelayCommand(Host.ShowDemoMessages);
        MoveOverlayCommand = new RelayCommand(_shell.ToggleOverlayEditMode);
        OpenReplyBoxCommand = new RelayCommand(_shell.OpenCompose);
        ScanAndTranslateCommand = new RelayCommand(_shell.ScanAndTranslate);
        NewMatchCommand = new RelayCommand(Host.NewMatch);
        DownloadUpdateCommand = new RelayCommand(DownloadUpdate);
        TestTranslateCommand = new AsyncRelayCommand(TestTranslateAsync, ReportCommandError);
        InstallOcrCommand = new RelayCommand<string>(InstallOcr);
        TestVoiceCommand = new RelayCommand(TestVoice);
        EditPhrasesCommand = new RelayCommand(EditPhrases);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        OpenLinkCommand = new RelayCommand<string>(OpenLink);
        QuitCommand = new RelayCommand(_shell.ExitApp);

        // About › Troubleshooting and the safe-mode banner (bodies in MainViewModel.Diagnostics.cs).
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        RestartSafeModeCommand = new RelayCommand(() => _shell.Restart(safeMode: true));
        RestartNormallyCommand = new RelayCommand(() => _shell.Restart(safeMode: false));
        ResetSettingsCommand = new RelayCommand(_shell.ResetSettingsAndRestart);
        DeleteAllDataCommand = new RelayCommand(_shell.DeleteAllDataAndRestart);
        DismissCrashCommand = new RelayCommand(DismissCrash);
        RefreshTroubleshooting();
    }

    public AppHost Host { get; }
    public AppSettings Settings => Host.Settings;

    public IReadOnlyList<Language> Languages { get; } = LanguageCatalog.All;

    // ---- Replying page ----
    public IReadOnlyList<Option> ReplyLanguages { get; }

    public IReadOnlyList<Option> SendModes { get; } =
    [
        new(SendMode.TypeAndSend, "Type it into game chat and send"),
        new(SendMode.TypeOnly, "Type it into game chat, I'll press Enter"),
        new(SendMode.Clipboard, "Copy to clipboard only"),
    ];

    public IReadOnlyList<Option> TypingMethods { get; } =
    [
        new(TypingMethod.UnicodeKeystrokes, "Unicode keystrokes (recommended)"),
        new(TypingMethod.Paste, "Paste with Ctrl+V"),
    ];

    // ---- Overlay page ----
    public IReadOnlyList<Option> Anchors { get; } =
    [
        new(OverlayAnchor.AboveChat, "Above the chat area"),
        new(OverlayAnchor.BelowChat, "Below the chat area"),
        new(OverlayAnchor.RightOfChat, "Right of the chat area"),
        new(OverlayAnchor.Free, "Wherever I drag it"),
    ];

    public IReadOnlyList<Option> Voices { get; } =
        [new Option("", "Windows default voice"), .. SpeechService.Voices.Select(v => new Option(v.Id, v.Name))];

    // ---- Hotkeys page ----
    /// <summary>
    /// "" when every hotkey registered; otherwise which ones another app took (or that couldn't be parsed), or that
    /// hotkeys are off in safe mode.
    /// </summary>
    public string HotkeyErrors => HotkeyText.StatusLine(Host.HotkeyResults);

    // ---- Appearance page ----
    public IReadOnlyList<ThemeOption> Themes { get; }

    // ---- About page ----
    public string DataFolder => Host.DataFolder;

    public string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public void RefreshStatus()
    {
        Notify(nameof(RegionText));
        Notify(nameof(HasRegion));
        Notify(nameof(UsageText));
        Notify(nameof(OcrSummary));
        Notify(nameof(HotkeyErrors));
        RefreshTroubleshooting();
    }

    /// <summary>
    /// The one error sink for every <see cref="AsyncRelayCommand"/> (contract §8.8): logs at Error under <c>UI</c> and
    /// shows a problem toast. Every async command body catches its own expected exceptions, so this is a last resort.
    /// </summary>
    public void ReportCommandError(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Host.Log.For("UI").Error("Command failed", ex);
        Host.ReportProblem(ProblemText.CommandFailed("command:" + ex.GetType().FullName));
    }

    public void Notify([CallerMemberName] string? name = null) => OnPropertyChanged(name);

    private void OnHotkeyResultsChanged() => Notify(nameof(HotkeyErrors));

    private void SetTheme(string id)
    {
        Settings.Theme = id;
        foreach (var t in Themes) t.Refresh();
        Host.ApplySettings();
    }

    private static Brush Swatch(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static Brush[] SwatchesFor(string id) => id switch
    {
        "imperial" => [Swatch("#0E1420"), Swatch("#1C2638"), Swatch("#C9A35B"), Swatch("#EDE3CF")],
        "parchment" => [Swatch("#CDB98F"), Swatch("#EADCBD"), Swatch("#8E2E1C"), Swatch("#2A1C0F")],
        "frontier" => [Swatch("#0D1C1B"), Swatch("#1A3330"), Swatch("#C38D42"), Swatch("#F0E7D3")],
        "olympian" => [Swatch("#0B1828"), Swatch("#192E46"), Swatch("#D2A65A"), Swatch("#F2EFE8")],
        "classic" => [Swatch("#1E1710"), Swatch("#33271B"), Swatch("#CC8A3C"), Swatch("#F1E3C9")],
        _ => [],
    };

    /// <summary>Unsubscribes from the host and releases the live-view lease. Called by MainWindow on close.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Host.PropertyChanged -= _hostChanged;
        Host.HotkeyResultsChanged -= OnHotkeyResultsChanged;
        CapturePreview.Dispose();
    }
}
