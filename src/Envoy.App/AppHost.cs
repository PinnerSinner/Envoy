using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Envoy.App.Services;
using Envoy.Core.Chat;
using Envoy.Core.Diagnostics;
using Envoy.Core.Games;
using Envoy.Core.Hotkeys;
using Envoy.Core.Notifications;
using Envoy.Core.Phrases;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Updates;

namespace Envoy.App;

/// <summary>
/// What <see cref="AppHost"/> is built with (contract §3). 02 creates it; later areas add their own members in their own
/// step.
/// </summary>
public sealed record AppHostOptions
{
    /// <summary>null → a hub with no sinks, owned and disposed by the host.</summary>
    public LogHub? Log { get; init; }

    /// <summary>The in-memory ring the diagnostics report reads.</summary>
    public MemoryLogSink? RecentLog { get; init; }

    public SafeModeReason? SafeMode { get; init; }

    /// <summary>null → the settings file's folder.</summary>
    public string? DataFolder { get; init; }

    /// <summary>null → <see cref="TimeProvider.System"/>; passed to every timed Core type.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// Owns the app's long-lived services and state. Windows bind to this; it marshals background events
/// (capture, translation) onto the UI thread.
/// </summary>
public sealed class AppHost : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly SettingsStore _store;
    private readonly ILog _log;
    private readonly ILog _captureLog;
    private readonly ILog _translationLog;
    private readonly bool _ownsLog;
    private readonly DateTimeOffset _startedAt;
    private int _saveFailures;
    private readonly HttpClient _http;
    private readonly TranslatorFactory _factory;
    private readonly DispatcherTimer _gameTimer;
    private readonly DispatcherTimer _saveTimer;
    private ITranslator _translator;
    private RunningGame? _game;
    private string _providerStatus = "";
    private UpdateInfo? _update;
    private bool _disposed;

    public AppHost(string settingsPath, Dispatcher ui, AppHostOptions? options = null)
    {
        options ??= new AppHostOptions();
        _ui = ui;
        Time = options.TimeProvider ?? TimeProvider.System;
        _ownsLog = options.Log is null;
        Log = options.Log ?? new LogHub(Time);
        RecentLog = options.RecentLog;
        SafeMode = options.SafeMode;
        DataFolder = options.DataFolder ?? Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        Notifications = new NotificationThrottle(Time);
        _log = Log.For("App");
        _captureLog = Log.For("Capture");
        _translationLog = Log.For("Translation");
        _startedAt = Time.GetUtcNow();

        // Contract §3 constructor order. 1: settings (02).
        _store = new SettingsStore(settingsPath, new DpapiProtector(), Log.For("Settings"), Time);
        SettingsLoad = _store.LoadWithReport();
        Settings = SettingsLoad.Settings;
        // 2: diagnostics (02).
        ApplyDiagnosticsSettings();
        // [03 TranslationRuntime] [03 TranslatorFactory]
        Usage.Restore(Settings.Usage);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Envoy/0.1");
        _factory = new TranslatorFactory(_http, Usage);
        _translator = BuildTranslator();

        Phrases = PhraseBook.LoadWithUserFile(UserPhrasesPath);
        Ocr = new OcrService();
        // [04 ChatFormatCatalog]
        Pipeline = new ChatPipeline(_translator, PipelineOptions());
        Outgoing = new OutgoingTranslator(() => _translator, Phrases);
        // [05 ChatWatcher rewrite]
        Watcher = new ChatWatcher(Ocr, Pipeline, () => Settings, () => CurrentGameId)
        {
            Log = _captureLog,
            ShouldRead = () => !Settings.Capture.OnlyWhileGameRuns || _game is not null || Settings.Game != GameCatalog.Any,
        };
        Transcript = new TranscriptWriter(LogsFolder);
        // [06 UpdateCoordinator] [07a NoticeCenter, skipped in safe mode]

        Pipeline.MessageReceived += OnPipelineMessageReceived;
        Pipeline.MessageUpdated += OnPipelineMessageUpdated;
        Watcher.StateChanged += OnWatcherStateChanged;

        ConfigureOcr();

        _gameTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => RefreshGame(), _ui);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), _ui) { IsEnabled = false };
    }

    /// <summary>The folder holding every Envoy file (REQ-REL-44); see <see cref="DataInventory"/>.</summary>
    public string DataFolder { get; }
    public string LogsFolder => Path.Combine(DataFolder, DataInventory.LogsFolderName);
    public string UserPhrasesPath => Path.Combine(DataFolder, "phrases.json");
    public string CrashMarkerPath => Path.Combine(DataFolder, "last-crash.json");

    public LogHub Log { get; }
    public MemoryLogSink? RecentLog { get; }
    public TimeProvider Time { get; }
    public SafeModeReason? SafeMode { get; }
    public bool IsSafeMode => SafeMode is not null;

    /// <summary>How the settings were loaded (for the startup toast and the diagnostics report).</summary>
    public SettingsLoadResult SettingsLoad { get; }

    /// <summary>The single notification throttle (contract §2.2).</summary>
    public NotificationThrottle Notifications { get; }

    /// <summary>True after a reset or "Delete all Envoy data": nothing may write settings.json again this session.</summary>
    public bool SaveSuppressed => _store.SavesSuppressed;

    public TimeSpan Uptime => Time.GetUtcNow() - _startedAt;

    /// <summary>A problem for the user, raised on the UI thread. App shows it as a toast.</summary>
    public event Action<ProblemReport>? ProblemReported;

    /// <summary>GitHub "owner/repo" checked for new releases.</summary>
    public const string ReleaseRepository = "PinnerSinner/envoy";

    public static Version CurrentVersion => typeof(AppHost).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public AppSettings Settings { get; }
    public UsageTracker Usage { get; } = new();
    public PhraseBook Phrases { get; private set; }
    public OcrService Ocr { get; }
    public ChatPipeline Pipeline { get; }
    public OutgoingTranslator Outgoing { get; }
    public ChatWatcher Watcher { get; }
    public ITranslator Translator => _translator;
    public TranscriptWriter Transcript { get; }

    private SpeechService? _speech;
    private readonly HashSet<Guid> _spoken = [];

    /// <summary>Created on first use so machines without audio never pay for it.</summary>
    public SpeechService Speech => _speech ??= new SpeechService(Log.For("Speech"));
    public UpdateInfo? Update => _update;
    public bool HasUpdate => _update is not null;
    public string UpdateText => _update is null ? "" : $"Envoy {_update.Tag} is available — you have v{CurrentVersion.ToString(3)}.";

    /// <summary>Players seen this match, for the dashboard.</summary>
    public ObservableCollection<PlayerInfo> Players { get; } = [];

    /// <summary>Most recent messages, newest first (for the dashboard log and the reply box).</summary>
    public ObservableCollection<IncomingMessage> History { get; } = [];

    /// <summary>
    /// The outcome of the last hotkey registration pass, one entry per action (contract §2.3). UI thread only; written
    /// only by <c>App.RegisterHotkeys</c>, which replaces the whole list after every pass.
    /// </summary>
    public IReadOnlyList<HotkeyRegistrationResult> HotkeyResults
    {
        get => _hotkeyResults;
        set
        {
            _hotkeyResults = value;
            HotkeyResultsChanged?.Invoke();
        }
    }

    public event Action? HotkeyResultsChanged;
    private IReadOnlyList<HotkeyRegistrationResult> _hotkeyResults = [];

    public event Action<IncomingMessage, bool>? MessageArrived;
    public event Action? SettingsApplied;
    public event PropertyChangedEventHandler? PropertyChanged;

    public RunningGame? Game => _game;
    public string CurrentGameId => Settings.Game != GameCatalog.Any ? Settings.Game : _game?.Profile.Id ?? GameCatalog.Any;
    public string GameStatus => _game is null ? "No Age game running" : $"{_game.Profile.Name} detected";
    public string ProviderStatus => _providerStatus;

    public string CaptureStatus => Watcher.State switch
    {
        WatcherState.Running => "Reading chat",
        WatcherState.NoRegion => "Chat area not set",
        WatcherState.Idle => "Waiting for a game to start",
        WatcherState.Error => "Error: " + Watcher.LastError,
        _ => "Paused",
    };

    /// <summary>Starts the game timer and, unless in safe mode, automatic capture and the update check.</summary>
    public void Start()
    {
        RefreshGame();
        ApplyTheme();
        _gameTimer.Start();
        if (!IsSafeMode && Settings.StartCapturingAutomatically) Watcher.Start();
        Notify(nameof(CaptureStatus));
        if (!IsSafeMode && Settings.CheckForUpdates) CheckForUpdatesAsync().Forget(_log, "update check");
    }

    /// <summary>Stops every later settings save (reset, "Delete all Envoy data"). Irreversible for this host.</summary>
    public void SuppressSave() => _store.SuppressSaves();

    /// <summary>Suppresses saving, keeps a <c>.reset-{time}.bak</c> copy and deletes settings.json (REQ-REL-38).</summary>
    public string? ResetSettings() => _store.ResetToDefaults();

    /// <summary>Logs <paramref name="report"/> at its severity and raises <see cref="ProblemReported"/> on the UI thread. Any thread.</summary>
    public void ReportProblem(ProblemReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Log.For("Problem").Write(report.Severity, "Problem reported", null, LogField.Of("key", report.Key));
        _ui.BeginInvoke(() => ProblemReported?.Invoke(report));
    }

    /// <summary>The redacted "Copy diagnostics" text for <paramref name="snapshot"/>, these settings and the recent log.</summary>
    public string BuildDiagnosticsText(DiagnosticsSnapshot snapshot) =>
        DiagnosticsReport.Render(snapshot, Settings, RecentLog?.Snapshot() ?? [], redactor: Log.Redactor);

    /// <summary>Level, chat-text opt-in and the secrets snapshot for the redactor (contract §3 position 2).</summary>
    public void ApplyDiagnosticsSettings()
    {
        var d = Settings.Diagnostics;
        Log.MinimumLevel = d.VerboseLogging ? LogLevel.Debug : LogLevel.Info;
        Log.IncludeSensitive = d.LogChatText;
        Log.Redactor.SetSecrets(SettingsRedaction.SecretValues(Settings));
    }

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await new UpdateChecker(_http, ReleaseRepository).CheckAsync(CurrentVersion);
            _update = info;
            Notify(nameof(Update));
            Notify(nameof(HasUpdate));
            Notify(nameof(UpdateText));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Offline or GitHub unavailable: try again next start.
            _log.Info("Update check failed", LogField.Of("error", ex.GetType().Name));
        }
    }

    /// <summary>Starts fresh for a new game: forgets players and seen lines, starts a new transcript file.</summary>
    public void NewMatch()
    {
        Pipeline.NewMatch();
        Transcript.Rotate();
        Players.Clear();
        History.Clear();
        Watcher.Invalidate();
    }

    public void ToggleCapture()
    {
        if (Watcher.IsRunning) Watcher.Stop();
        else Watcher.Start();
        Notify(nameof(CaptureStatus));
    }

    /// <summary>Saves the chat region for the current game (and as the default for games without their own).</summary>
    public void SetRegion(ScreenRegion region)
    {
        Settings.Capture.Regions[CurrentGameId] = region;
        if (!Settings.Capture.Regions.ContainsKey(GameCatalog.Any)) Settings.Capture.Regions[GameCatalog.Any] = region;
        Pipeline.Reset();
        Watcher.Invalidate();
        if (!Watcher.IsRunning) Watcher.Start();
        SaveNow();
        Notify(nameof(CaptureStatus));
        SettingsApplied?.Invoke();
    }

    /// <summary>Call after any settings edit: re-applies everything and schedules a save.</summary>
    public void ApplySettings()
    {
        ApplyDiagnosticsSettings(); // contract §3 ApplySettings position 1
        _translator = BuildTranslator();
        Pipeline.Translator = _translator;
        var o = PipelineOptions();
        Pipeline.Options.MyLanguage = o.MyLanguage;
        Pipeline.Options.MyPlayerName = o.MyPlayerName;
        Pipeline.Options.SkipMyLanguage = o.SkipMyLanguage;
        Pipeline.Options.UseSlangGlossary = o.UseSlangGlossary;
        Pipeline.Options.MinCharacters = o.MinCharacters;
        Pipeline.Options.MutedPlayers = o.MutedPlayers;
        Pipeline.Options.Channels = o.Channels;
        ConfigureOcr();
        Watcher.Invalidate();
        ApplyTheme();
        StartupRegistration.Apply(Settings.StartWithWindows);
        ScheduleSave();
        SettingsApplied?.Invoke();
    }

    public void ReloadPhrases() => Phrases = PhraseBook.LoadWithUserFile(UserPhrasesPath);

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Saves now unless saving is suppressed. A failure is logged; the third in a row shows a problem toast.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        if (_store.SavesSuppressed) return;
        Settings.Usage = Usage.Snapshot();
        try
        {
            _store.Save(Settings);
            _saveFailures = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retry on the next change.
            _saveFailures++;
            Log.For("Settings").Warn("Settings save failed", ex, LogField.Of("failures", _saveFailures));
            if (_saveFailures == 3) ReportProblem(ProblemText.SettingsSaveFailed);
        }
    }

    public void ApplyTheme()
    {
        var theme = Settings.Theme == ThemeManager.MatchGame
            ? GameCatalog.Find(CurrentGameId)?.ThemeId ?? "imperial"
            : Settings.Theme;
        ThemeManager.Apply(theme);
    }

    /// <summary>Feeds sample messages through the overlay so the user can see and position it without a game.</summary>
    public void ShowDemoMessages()
    {
        var samples = new (string Sender, string Original, string Translation, string Lang)[]
        {
            ("龙骑士", "你好，玩得开心！", "Hello, have fun!", "zh-CN"),
            ("전략가", "ㅈㅈ", "GG — I give up", "ko"),
            ("Сокол", "хорошая стена, но я иду на таран", "nice wall, but I'm coming with rams", "ru"),
        };
        foreach (var s in samples)
        {
            var msg = IncomingMessage.Preview(s.Sender, s.Original, s.Translation, s.Lang);
            OnMessage(msg, isNew: true);
        }
    }

    // Background events, marshalled to the UI thread. Dispose unsubscribes them; a call already queued checks _disposed.
    private void OnPipelineMessageReceived(IncomingMessage m) => _ui.BeginInvoke(() => OnMessage(m, isNew: true));
    private void OnPipelineMessageUpdated(IncomingMessage m) => _ui.BeginInvoke(() => OnMessage(m, isNew: false));
    private void OnWatcherStateChanged(WatcherState state) => _ui.BeginInvoke(() => OnWatcherState(state));

    private void OnWatcherState(WatcherState state)
    {
        if (_disposed) return;
        Notify(nameof(CaptureStatus));
        if (state != WatcherState.Error) return;
        _captureLog.Warn("Capture stopped with an error", null, LogField.Of("error", Watcher.LastError));
        ReportProblem(ProblemText.CaptureError);
    }

    private void OnMessage(IncomingMessage message, bool isNew)
    {
        // A translation can finish after Dispose (the watcher does not wait for in-flight work) or after a reset or
        // "Delete all Envoy data": nothing may write logs/chat-*.txt or speak then.
        if (_disposed || SaveSuppressed) return;
        if (!isNew && message.Status == IncomingStatus.Failed)
        {
            _translationLog.Warn("Message translation failed", null, LogField.Sensitive("text", message.Chat.Text),
                LogField.Of("error", message.Error));
        }

        if (isNew)
        {
            History.Insert(0, message);
            while (History.Count > 100) History.RemoveAt(History.Count - 1);
        }
        else
        {
            // Re-insert to make the ObservableCollection refresh the updated item.
            var index = History.IndexOf(message);
            if (index >= 0) History[index] = message;
        }
        MessageArrived?.Invoke(message, isNew);

        if (message.Status == IncomingStatus.Translated && Settings.Overlay.SpeakMessages && _spoken.Add(message.Id))
            Say(message);

        if (message.Status != IncomingStatus.Pending && message.ProviderId != "preview")
        {
            if (Settings.SaveTranscripts)
            {
                try { Transcript.Append(message); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.For("Transcript").Warn("Transcript write failed", ex);
                }
            }
            RefreshPlayers();
        }
    }

    private void Say(IncomingMessage message)
    {
        try
        {
            var o = Settings.Overlay;
            Speech.VoiceId = o.SpeechVoiceId;
            Speech.Volume = o.SpeechVolume;
            Speech.Rate = o.SpeechRate;
            var text = o.SpeakSenderName && message.Chat.Sender is { } s ? $"{s}: {message.DisplayText}" : message.DisplayText;
            Speech.Speak(text);
        }
        catch (Exception ex)
        {
            // Speech is a nicety; never let it break message handling.
            Log.For("Speech").Warn("Speech failed", ex);
        }
    }

    public void TestVoice() => Say(IncomingMessage.Preview("Envoy", "", "Your scouts report: the enemy is advancing to the Castle Age.", "en"));

    private void RefreshPlayers()
    {
        Players.Clear();
        foreach (var p in Pipeline.Players.Players) Players.Add(p);
    }

    private ITranslator BuildTranslator()
    {
        var failures = new List<string>();
        var translator = _factory.Build(Settings.Translation, (id, ex) =>
        {
            // 03 replaces this with its TranslationRuntime.Diagnostic subscription.
            _translationLog.Warn("Provider failed", null, LogField.Of("provider", id), LogField.Of("error", ex.GetType().Name + ": " + ex.Message));
            failures.Add($"{ProviderCatalog.Find(id)?.Name ?? id}: {ex.Message}");
            _ui.BeginInvoke(() =>
            {
                _providerStatus = $"{ProviderCatalog.Find(id)?.Name ?? id} failed — {ex.Message}";
                Notify(nameof(ProviderStatus));
            });
        });
        _providerStatus = failures.Count == 0
            ? $"Using {ProviderCatalog.Find(Settings.Translation.Primary)?.Name ?? Settings.Translation.Primary}"
            : string.Join("\n", failures);
        Notify(nameof(ProviderStatus));
        return translator;
    }

    private ChatPipelineOptions PipelineOptions() => new()
    {
        MyLanguage = Settings.MyLanguage,
        MyPlayerName = Settings.MyPlayerName,
        SkipMyLanguage = Settings.Translation.SkipMyLanguage,
        UseSlangGlossary = Settings.Translation.UseSlangGlossary,
        MinCharacters = Settings.Capture.MinCharacters,
        MutedPlayers = Settings.MutedPlayers,
        Channels = Settings.Channels,
    };

    private void ConfigureOcr()
    {
        try
        {
            Ocr.Configure(Settings.MyLanguage, Settings.Capture.OcrLanguages);
        }
        catch (Exception ex)
        {
            // OCR unavailable (very old Windows / stripped-down edition): capture will report it.
            _captureLog.Warn("OCR configuration failed", ex);
        }
    }

    private void RefreshGame()
    {
        var before = _game?.Profile.Id;
        _game = GameDetector.Find();
        if (_game?.Profile.Id == before) return;
        // Contract §3 RefreshGame position 1: log (profile name only, never window titles).
        if (_game is not null) _log.Info("Game detected", LogField.Of("game", _game.Profile.Name));
        else _log.Info("Game closed");
        // [04 format re-resolve] [06 Updates.TickAsync on close] [07a Notices.TakeDeferred]
        if (_game is not null && before is null) NewMatch();
        Notify(nameof(Game));
        Notify(nameof(GameStatus));
        if (Settings.Theme == ThemeManager.MatchGame) ApplyTheme();
        Watcher.Invalidate();
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Stops services and saves (unless suppressed). App owns and disposes a hub it passed in.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pipeline.MessageReceived -= OnPipelineMessageReceived;
        Pipeline.MessageUpdated -= OnPipelineMessageUpdated;
        Watcher.StateChanged -= OnWatcherStateChanged;
        _gameTimer.Stop();
        Watcher.Dispose();
        _speech?.Dispose();
        SaveNow(); // a no-op once saving is suppressed, so a reset or Delete-all is never undone
        _http.Dispose();
        Log.Flush();
        if (_ownsLog) Log.Dispose();
    }
}
