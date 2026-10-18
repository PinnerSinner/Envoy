using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Envoy.App.Services;
using Envoy.Core.Capture;
using Envoy.Core.Chat;
using Envoy.Core.Diagnostics;
using Envoy.Core.Games;
using Envoy.Core.Hotkeys;
using Envoy.Core.Languages;
using Envoy.Core.Notifications;
using Envoy.Core.Phrases;
using Envoy.Core.Pipeline;
using Envoy.Core.Privacy;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Envoy.Core.Updates;
using Envoy.Core.Ux;

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

    /// <summary>null → <see cref="UpdateBackendFactory.Create"/>: Velopack when installed, else GitHub (notify only). 06.</summary>
    public IUpdateBackend? UpdateBackend { get; init; }

    /// <summary>null → <see cref="TimeProvider.System"/>; passed to every timed Core type.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>Picks the update backend once (contract §5: the network types stay in this file).</summary>
internal static class UpdateBackendFactory
{
    /// <summary>A Velopack-installed copy gets <see cref="VelopackUpdateBackend"/>; anything else (portable exe, dotnet run) GitHub, notify only.</summary>
    public static IUpdateBackend Create(HttpClient http, ReleaseVersion current, ILog log) =>
        (IUpdateBackend?)VelopackUpdateBackend.TryCreate(log)
        ?? new GitHubReleaseBackend(new UpdateChecker(http, UpdateEndpoints.Repository), current);
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
    private readonly ILog _chatLog;
    private readonly bool _ownsLog;
    private readonly DateTimeOffset _startedAt;
    private int _saveFailures;
    private readonly HttpClient _http;
    private readonly TranslatorFactory _factory;
    private readonly IDisposable _translationLogging;
    private readonly DispatcherTimer _gameTimer;
    private readonly DispatcherTimer _windowTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _updateTimer;
    private readonly CancellationTokenSource _updatesCts = new();
    private readonly ILog _updatesLog;
    private ITranslator _translator;
    private ITranslator _composeTranslator;
    private IReadOnlyList<string> _activeProviderIds = [];
    private volatile RunningGame? _game; // written on the UI thread, read by the capture loop
    private CaptureOptions _captureOptions;
    private string _providerStatus = "";
    private bool _gameWasRunning;
    private bool _updatesStarted;
    private bool _disposed;
    // The privacy gate (contract §8.1): published at ApplySettings position 2, read by PrivacyGatedTranslator on pool threads.
    private volatile bool _privacyAcknowledged;
    private readonly DispatcherTimer? _noticeTimer;
    private readonly ILog _noticesLog;

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
        _chatLog = Log.For("Chat");
        _noticesLog = Log.For("Notices");
        _startedAt = Time.GetUtcNow();

        // Contract §3 constructor order. 1: settings (02).
        _store = new SettingsStore(settingsPath, new DpapiProtector(), Log.For("Settings"), Time);
        SettingsLoad = _store.LoadWithReport();
        Settings = SettingsLoad.Settings;
        // 2: diagnostics (02).
        ApplyDiagnosticsSettings();
        // 3: TranslationRuntime (03): breakers, throttles, provider health and the cache, for the life of the host.
        TranslationRuntime = new TranslationRuntime(TranslationCachePath, Time, Log.Redactor, _translationLog);
        TranslationRuntime.Configure(TranslationRuntimeOptions.From(Settings.Translation));
        _translationLogging = TranslationLogging.Attach(TranslationRuntime, _translationLog);
        TranslationRuntime.ProviderHealthChanged += OnProviderHealthChanged;
        // 4: TranslatorFactory (03). Each attempt has its own timeout; HttpClient.Timeout is only a backstop now.
        Usage.Restore(Settings.Usage);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // Browser-like (the keyless providers depend on it); only the Envoy token carries the version (contract §3).
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgents.Default(AppVersion));
        _factory = new TranslatorFactory(_http, Usage, TranslationRuntime);
        _privacyAcknowledged = PrivacyText.IsAcknowledged(Settings.Onboarding.PrivacyAcknowledgedVersion);
        (_translator, _composeTranslator) = BuildTranslator();

        Phrases = PhraseBook.LoadWithUserFile(UserPhrasesPath, out var phrasesError);
        PhrasesLoadError = phrasesError;
        Ocr = new OcrService(Time);
        // 5: ChatFormatCatalog (04): built-in formats plus the user's chat-formats.json, read only.
        ChatFormats = ChatFormatCatalog.LoadWithUserFile(ChatFormatsPath, _chatLog);
        // 6: ChatPipeline, with an immutable options snapshot (contract §7).
        Pipeline = new ChatPipeline(_translator, BuildChatOptions(), Time, _chatLog);
        Outgoing = new OutgoingTranslator(() => _composeTranslator, Phrases);
        // 7: ChatWatcher (05). The loop reads settings only through the CaptureOptions snapshot (contract §7).
        _captureOptions = CaptureOptions.From(Settings);
        Watcher = new ChatWatcher(Ocr, Pipeline, () => CaptureOptions, () => _game, _captureLog, Time, PublishGameWindow);
        Watcher.RegionChanged += OnWatcherRegionChanged;
        Transcript = new TranscriptWriter(LogsFolder);
        // 8: UpdateCoordinator (06). It reads the immutable UpdatePreferences snapshot; its background events and the
        //    last-check callback are marshalled to the UI thread here (contract §2.5, §7).
        _updatesLog = Log.For("Updates");
        UpdatesToken = _updatesCts.Token;
        Updates = new UpdateCoordinator(options.UpdateBackend ?? UpdateBackendFactory.Create(_http, AppVersion, _updatesLog),
            UpdatePreferences.From(Settings, IsSafeMode), OnUpdateCheckCompleted, Time, _updatesLog);
        Updates.Changed += OnUpdatesChanged;
        // 9: NoticeCenter (07a), skipped in safe mode (contract §2.2). Its sources all arrive on the UI thread: provider
        //    health (OnProviderHealthChanged), update state (RefreshUpdateView), game window (PublishGameWindow) and the
        //    OCR check (ApplySettings position 8 and Start); a 5 s timer re-offers deferred balloons.
        if (!IsSafeMode)
        {
            Notices = new NoticeCenter(Settings, Notifications);
            _noticeTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => ReleaseDeferredNotices(), _ui);
            _noticeTimer.Start();
        }

        Pipeline.MessageReceived += OnPipelineMessageReceived;
        Pipeline.MessageUpdated += OnPipelineMessageUpdated;
        Watcher.StateChanged += OnWatcherStateChanged;

        ConfigureOcr();

        _gameTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            RefreshGame();
            RefreshProviderStatus(); // a cooling-down period may have ended
        }, _ui);
        // The game window is published every second even while capture is stopped (contract §2.4).
        _windowTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshGameWindow(), _ui) { IsEnabled = false };
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), _ui) { IsEnabled = false };
        // Periodic update checks (REQ-DIST-21) and deferred downloads; started by StartUpdates, never in safe mode.
        _updateTimer = new DispatcherTimer(TimeSpan.FromMinutes(30), DispatcherPriority.Background,
            (_, _) => Updates.TickAsync(UpdatesToken).Forget(_updatesLog, "update tick"), _ui) { IsEnabled = false };

        // The one-time "Envoy was updated to …" notice (REQ-DIST-23): the version that last ran is older than this one.
        if (ReleaseVersion.TryParse(Settings.Updates.LastRunVersion, out var lastRun) && lastRun < AppVersion) JustUpdatedTo = AppVersion;
        if (Settings.Updates.LastRunVersion != AppVersion.ToString())
        {
            Settings.Updates.LastRunVersion = AppVersion.ToString();
            ScheduleSave();
        }
        _updateView = UpdateViewBuilder.Build(Updates.Snapshot, JustUpdatedTo);
    }

    /// <summary>The folder holding every Envoy file (REQ-REL-44); see <see cref="DataInventory"/>.</summary>
    public string DataFolder { get; }
    public string LogsFolder => Path.Combine(DataFolder, DataInventory.LogsFolderName);
    public string UserPhrasesPath => Path.Combine(DataFolder, "phrases.json");
    public string CrashMarkerPath => Path.Combine(DataFolder, "last-crash.json");

    /// <summary>Saved translations, when "Remember translations" is on (REQ-TRR-20).</summary>
    public string TranslationCachePath => Path.Combine(DataFolder, TranslationCacheStore.FileName);

    /// <summary>The user's own chat format profiles (REQ-CHAT-32). Envoy only reads this file; the user creates it.</summary>
    public string ChatFormatsPath => Path.Combine(DataFolder, "chat-formats.json");

    /// <summary>The chat format profiles in use (built-in plus <see cref="ChatFormatsPath"/>). UI thread.</summary>
    public ChatFormatCatalog ChatFormats { get; private set; }

    public LogHub Log { get; }
    public MemoryLogSink? RecentLog { get; }
    public TimeProvider Time { get; }
    public SafeModeReason? SafeMode { get; }
    public bool IsSafeMode => SafeMode is not null;

    /// <summary>How the settings were loaded (for the startup toast and the diagnostics report).</summary>
    public SettingsLoadResult SettingsLoad { get; }

    /// <summary>The single notification throttle (contract §2.2).</summary>
    public NotificationThrottle Notifications { get; }

    /// <summary>Advisory notices (07a): the War Room list and the tray balloons. Null in safe mode. UI thread only.</summary>
    public NoticeCenter? Notices { get; }

    /// <summary>A notice to show as a tray balloon now; raised on the UI thread (App's <c>TrayNotifier</c> shows it).</summary>
    public event Action<Notice>? NoticeReady;

    /// <summary>Why phrases.json was not loaded the last time it was read; null when it loaded or does not exist (REQ-OUXA-23).</summary>
    public string? PhrasesLoadError { get; private set; }

    /// <summary>
    /// True once the user confirmed "What leaves your PC" for the current <see cref="PrivacyText.Version"/>; until then no
    /// text reaches any provider (contract §8.1). UI thread.
    /// </summary>
    public bool PrivacyAcknowledged => PrivacyText.IsAcknowledged(Settings.Onboarding.PrivacyAcknowledgedVersion);

    /// <summary>The provider chain's display names, in order ("What leaves your PC").</summary>
    public IReadOnlyList<string> ActiveProviderNames => [.. _activeProviderIds.Select(ProviderCatalog.NameOf)];

    /// <summary>True after a reset or "Delete all Envoy data": nothing may write settings.json again this session.</summary>
    public bool SaveSuppressed => _store.SavesSuppressed;

    public TimeSpan Uptime => Time.GetUtcNow() - _startedAt;

    /// <summary>A problem for the user, raised on the UI thread. App shows it as a toast.</summary>
    public event Action<ProblemReport>? ProblemReported;

    /// <summary>GitHub "owner/repo" checked for new releases.</summary>
    public const string ReleaseRepository = UpdateEndpoints.Repository;

    /// <summary>The numeric assembly version (log header, crash marker). User-visible text uses <see cref="AppVersion"/>.</summary>
    public static Version CurrentVersion => typeof(AppHost).Assembly.GetName().Version ?? new Version(0, 1, 0);

    /// <summary>The release version with any prerelease suffix, from the informational version (REQ-DIST-19, contract §3).</summary>
    public static ReleaseVersion AppVersion { get; } = ReleaseVersion.FromInformational(
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(AppHost).Assembly.GetName().Version);

    /// <summary>Update state and policy (contract §2.5). Its <c>Changed</c> event is raised on a background thread; bind <see cref="UpdateView"/>.</summary>
    public UpdateCoordinator Updates { get; }

    /// <summary>Cancelled by <see cref="Dispose"/>: aborts an in-flight check or download (contract §4 exit step 3).</summary>
    public CancellationToken UpdatesToken { get; }

    /// <summary>The War Room banner and About status line; rebuilt on the UI thread after every update-state change.</summary>
    public UpdateView UpdateView => _updateView;
    private UpdateView _updateView;

    /// <summary>This version, when it is newer than the one that ran last (REQ-DIST-23); null once dismissed.</summary>
    public ReleaseVersion? JustUpdatedTo { get; private set; }

    public AppSettings Settings { get; }
    public UsageTracker Usage { get; } = new();
    public PhraseBook Phrases { get; private set; }
    public OcrService Ocr { get; }
    public ChatPipeline Pipeline { get; }
    public OutgoingTranslator Outgoing { get; }
    public ChatWatcher Watcher { get; }
    public ITranslator Translator => _translator;

    /// <summary>The reply box's stack: its own low-priority lane, sharing breakers, health and cache (REQ-TRR-29).</summary>
    public ITranslator ComposeTranslator => _composeTranslator;

    /// <summary>Long-lived translation state: breakers, throttles, health and the cache (spec 03).</summary>
    public TranslationRuntime TranslationRuntime { get; }

    /// <summary>The single provider-health source (contract §2.1).</summary>
    public ProviderHealthRegistry TranslationHealth => TranslationRuntime.Health;

    /// <summary>The provider ids of the chain as built, in order (primary, fallbacks, Google free last). UI thread.</summary>
    public IReadOnlyList<string> ActiveProviderIds => _activeProviderIds;

    /// <summary>A provider's health changed state; raised on the UI thread.</summary>
    public event Action<ProviderHealthSnapshot>? ProviderHealthChanged;

    public TranscriptWriter Transcript { get; }

    private SpeechService? _speech;
    private readonly HashSet<Guid> _spoken = [];

    /// <summary>Created on first use so machines without audio never pay for it.</summary>
    public SpeechService Speech => _speech ??= new SpeechService(Log.For("Speech"));

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

    /// <summary>The capture settings snapshot the watcher reads (contract §7); republished by ApplySettings and SetRegion.</summary>
    public CaptureOptions CaptureOptions => Volatile.Read(ref _captureOptions);

    /// <summary>The latest probe of the game window, or null without one (contract §2.4). UI thread.</summary>
    public GameWindowInfo? GameWindow { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="GameWindow"/> changes; re-read it.</summary>
    public event Action? GameWindowChanged;

    /// <summary>The resolved chat region moved (the game window moved or was resized); raised on the UI thread.</summary>
    public event Action? RegionMoved;
    public string CurrentGameId => Settings.Game != GameCatalog.Any ? Settings.Game : _game?.Profile.Id ?? GameCatalog.Any;
    public string GameStatus => _game is null ? "No Age game running" : $"{_game.Profile.Name} detected";
    public string ProviderStatus => _providerStatus;

    /// <summary>
    /// The War Room status line, from <see cref="CaptureText"/>; a blank-area hint and the DPI notice (REQ-CAP-34) go on
    /// lines of their own.
    /// </summary>
    public string CaptureStatus
    {
        get
        {
            var status = Watcher.State switch
            {
                WatcherState.Running when Watcher.Health == CaptureHealth.Degraded => CaptureText.ReadingSlow,
                WatcherState.Running => CaptureText.Reading,
                WatcherState.Paused => CaptureText.Paused(Watcher.PauseReason),
                WatcherState.Idle => CaptureText.WaitingForGame,
                WatcherState.NoRegion => CaptureText.NoRegion,
                WatcherState.Error => Watcher.LastError ?? CaptureText.CaptureFailed,
                _ => CaptureText.Stopped,
            };
            if (Watcher.Hint is { } hint) status += Environment.NewLine + hint;
            if (Watcher.DpiUnaware && status != CaptureText.DpiUnaware) status += Environment.NewLine + CaptureText.DpiUnaware;
            return status;
        }
    }

    /// <summary>Starts the game timer and, unless in safe mode, automatic capture. Update checks start in <see cref="StartUpdates"/>.</summary>
    public void Start()
    {
        RefreshGame();
        ApplyTheme();
        ApplyMotion();
        _gameTimer.Start();
        _windowTimer.Start();
        RefreshGameWindow();
        if (!IsSafeMode && Settings.StartCapturingAutomatically) Watcher.Start();
        CheckOcrReadiness();
        Notify(nameof(CaptureStatus));
    }

    /// <summary>
    /// Records (or withdraws) the "What leaves your PC" acknowledgement and applies it at once, not through a page's
    /// debounce, so the gate is open before the next translation (REQ-OUXA-47). UI thread.
    /// </summary>
    public void SetPrivacyAcknowledged(bool acknowledged)
    {
        var version = acknowledged ? PrivacyText.Version : 0;
        if (Settings.Onboarding.PrivacyAcknowledgedVersion == version) return;
        Settings.Onboarding.PrivacyAcknowledgedVersion = version;
        _log.Info("Privacy acknowledgement changed", LogField.Of("acknowledged", acknowledged));
        ApplySettings();
    }

    /// <summary>
    /// Shows every deferred advisory that may be shown now (contract §2.2): on game-window changes, on game exit
    /// (RefreshGame position 4), when the guide closes and every 5 s. The throttle's spacing releases at most one per call.
    /// UI thread.
    /// </summary>
    public void ReleaseDeferredNotices()
    {
        if (_disposed || Notices is null) return;
        while (Notices.TakeDeferred(GameWindow?.GameForeground == true) is { } notice)
        {
            LogNotice(notice, NoticeDecision.Show, deferred: true);
            Announce(notice);
        }
    }

    /// <summary>
    /// Start-up slot 9 (never in safe mode): the start-up update check, the 30-minute tick, and (REQ-DIST-26) the "start
    /// at sign-in" value rewritten to this exe, so moving from the portable exe to the installer keeps it working.
    /// </summary>
    public void StartUpdates()
    {
        _updatesStarted = true;
        Updates.StartAsync(UpdatesToken).Forget(_updatesLog, "update start");
        _updateTimer.Start();
        if (Settings.StartWithWindows) StartupRegistration.Apply(true);
    }

    /// <summary>"Skip this version" (REQ-DIST-22): hidden from automatic checks from now on.</summary>
    public void SkipUpdate()
    {
        if (Updates.SkipAvailableVersion() is not { } skipped) return;
        Settings.Updates.SkippedVersion = skipped.ToString();
        ScheduleSave();
    }

    /// <summary>Dismisses the one-time "Envoy was updated" notice.</summary>
    public void DismissJustUpdated()
    {
        JustUpdatedTo = null;
        RefreshUpdateView();
    }

    /// <summary>
    /// Stops every later settings and saved-translations write (reset, "Delete all Envoy data"). Irreversible for this
    /// host.
    /// </summary>
    public void SuppressSave()
    {
        _store.SuppressSaves();
        TranslationRuntime.SuppressPersistence();
    }

    /// <summary>Suppresses saving, keeps a <c>.reset-{time}.bak</c> copy and deletes settings.json (REQ-REL-38).</summary>
    public string? ResetSettings()
    {
        TranslationRuntime.SuppressPersistence();
        return _store.ResetToDefaults();
    }

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

    /// <summary>
    /// Saves the chat region for the current game: relative to the game window when one is known and the region lies in
    /// its client area (REQ-CAP-1), and always as the fixed screen rectangle (and as the default for games without their
    /// own).
    /// </summary>
    public void SetRegion(ScreenRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var gameId = CurrentGameId;
        var window = _game is { } game ? GameWindowTracker.Observe(game, Watcher.UniformFrameStreak) : null;
        if (window is { Minimized: false } && RegionMath.ToWindowRelative(region, window.ClientRect) is { } relative)
            Settings.Capture.WindowRegions[gameId] = relative;
        else
            Settings.Capture.WindowRegions.Remove(gameId); // chosen outside the game window: a fixed position from now on
        Settings.Capture.Regions[gameId] = region;
        if (!Settings.Capture.Regions.ContainsKey(GameCatalog.Any)) Settings.Capture.Regions[GameCatalog.Any] = region;
        Volatile.Write(ref _captureOptions, CaptureOptions.From(Settings));
        _captureLog.Info("Chat area selected", LogField.Of("size", SettingsRedaction.Size(region)),
            LogField.Of("followsWindow", Settings.Capture.WindowRegions.ContainsKey(gameId)));
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
        var translationOptions = TranslationRuntimeOptions.From(Settings.Translation); // 2: publish the snapshots
        Volatile.Write(ref _captureOptions, CaptureOptions.From(Settings));
        _privacyAcknowledged = PrivacyText.IsAcknowledged(Settings.Onboarding.PrivacyAcknowledgedVersion); // the gate (07a)
        TranslationRuntime.Configure(translationOptions); // 3
        (_translator, _composeTranslator) = BuildTranslator(); // 4
        Pipeline.Translator = _translator;
        Pipeline.Options = BuildChatOptions(); // 5: chat options and format re-resolve (04)
        ConfigureOcr();
        Watcher.Invalidate(); // 6 (05)
        Updates.Configure(UpdatePreferences.From(Settings, IsSafeMode), UpdatesToken).Forget(_updatesLog, "update channel check"); // 7 (06)
        CheckOcrReadiness(); // 8 (07a)
        ApplyTheme(); // 9: theme through ThemeResolver (07b)
        ApplyMotion(); // 10: the motion level (09b)
        StartupRegistration.Apply(Settings.StartWithWindows);
        ScheduleSave();
        Notify(nameof(PrivacyAcknowledged));
        SettingsApplied?.Invoke();
    }

    /// <summary>Re-reads phrases.json; <see cref="PhrasesLoadError"/> says why it was not used (REQ-OUXA-23).</summary>
    public void ReloadPhrases()
    {
        Phrases = PhraseBook.LoadWithUserFile(UserPhrasesPath, out var error);
        if (error is not null && error != PhrasesLoadError) Log.For("Phrases").Warn("phrases.json was not loaded", null, LogField.Of("reason", error));
        PhrasesLoadError = error;
        Notify(nameof(PhrasesLoadError));
    }

    /// <summary>
    /// The OCR-readiness notice (REQ-OUXA-17): wanted languages whose Windows pack is missing. A changed set resolves the
    /// old key (so installing a pack withdraws it). OCR is unavailable on some Windows editions, so a failure is logged
    /// and skipped. UI thread; ApplySettings position 8 and <see cref="Start"/>.
    /// </summary>
    private void CheckOcrReadiness()
    {
        if (Notices is null || _disposed) return;
        try
        {
            var installed = OcrService.InstalledLanguages().Select(l => l.Code);
            var missing = OcrReadiness.MissingWanted(Settings.Capture.OcrLanguages, Settings.MyLanguage, installed);
            var notice = NoticeFactory.OcrMissing(missing);
            Notices.ResolveWhere(k => k.StartsWith(NoticeFactory.OcrKeyPrefix, StringComparison.Ordinal) && k != notice?.Key);
            Submit(notice);
        }
        catch (Exception ex)
        {
            _noticesLog.Warn("Text recognition check failed", ex);
        }
    }

    /// <summary>Asks <see cref="Notices"/> about <paramref name="notice"/>; a Show raises <see cref="NoticeReady"/>. UI thread.</summary>
    private void Submit(Notice? notice)
    {
        if (notice is null || Notices is null || _disposed) return;
        var decision = Notices.Submit(notice, GameWindow?.GameForeground == true);
        LogNotice(notice, decision, deferred: false);
        if (decision == NoticeDecision.Show) Announce(notice);
    }

    private void Announce(Notice notice)
    {
        // Showing an update notice recorded its version (Updates.LastAnnouncedVersion), so a restart doesn't repeat it.
        if (notice.Kind == NoticeKind.UpdateAvailable) ScheduleSave();
        NoticeReady?.Invoke(notice);
    }

    // Keys hold language codes, provider ids or versions only (never chat text or keys).
    private void LogNotice(Notice notice, NoticeDecision decision, bool deferred) =>
        _noticesLog.Info("Notice", LogField.Of("kind", notice.Kind), LogField.Of("key", notice.Key), LogField.Of("decision", decision),
            LogField.Of("deferred", deferred));

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

    /// <summary>
    /// The palette through <see cref="ThemeResolver"/> (ApplySettings position 9, Start, and the high-contrast listeners in
    /// App.OnStartup step 8). The high-contrast palette is always reloaded, because it reads Windows' colours at load. UI thread.
    /// </summary>
    /// <summary>Whether a Windows contrast theme is on (UI thread).</summary>
    public bool SystemHighContrast => SystemParameters.HighContrast;

    public void ApplyTheme()
    {
        if (_disposed) return;
        var id = ThemeResolver.Resolve(Settings.Theme, GameCatalog.Find(CurrentGameId)?.ThemeId, SystemHighContrast,
            Settings.Accessibility.HighContrast, ThemeManager.ThemeIds);
        ThemeManager.Apply(id, force: id == ThemeResolver.HighContrast);
    }

    /// <summary>Whether Windows' "Show animations in Windows" is on (UI thread).</summary>
    public bool SystemAnimations => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// The motion level through <see cref="MotionPolicy"/> (spec 09b, REQ-MOT-1): ApplySettings position 10, Start, and
    /// App's listener for Windows' animation setting. Process-wide, like the palette. UI thread.
    /// </summary>
    public void ApplyMotion()
    {
        if (_disposed) return;
        Services.Motion.Apply(MotionPolicy.Resolve(Settings.Appearance.Motion, SystemAnimations));
    }

    /// <summary>Feeds sample messages through the overlay so the user can see and position it without a game.</summary>
    public void ShowDemoMessages()
    {
        var samples = new (string Sender, string Original, string Translation, string Lang)[]
        {
            ("龙骑士", "你好，玩得开心！", "Hello, have fun!", "zh-CN"),
            ("전략가", "ㅈㅈ", "GG, I give up", "ko"),
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

    private void OnWatcherRegionChanged(ScreenRegion? region) => _ui.BeginInvoke(() =>
    {
        if (_disposed) return;
        RegionMoved?.Invoke();
    });

    /// <summary>Publishes a game-window probe from any thread; raises <see cref="GameWindowChanged"/> on the UI thread on change.</summary>
    private void PublishGameWindow(GameWindowInfo? info) => _ui.BeginInvoke(() =>
    {
        if (_disposed || Equals(GameWindow, info)) return;
        GameWindow = info;
        PushGameRunning();
        GameWindowChanged?.Invoke();
        ReleaseDeferredNotices(); // an advisory deferred while the game was in front (contract §2.2)
    });

    /// <summary>
    /// Tells the coordinator whether a game runs (process or window, contract §2.4). When the last one is gone, a tick
    /// starts a download that waited for the game (RefreshGame position 3). UI thread.
    /// </summary>
    private void PushGameRunning()
    {
        var running = _game is not null || GameWindow is not null;
        Updates.SetGameRunning(running);
        // Only after StartUpdates: in safe mode nothing automatic may reach the network (REQ-DIST-13).
        if (_gameWasRunning && !running && _updatesStarted) Updates.TickAsync(UpdatesToken).Forget(_updatesLog, "update tick");
        _gameWasRunning = running;
    }

    // Raised on a background thread by the coordinator; rebuilt on the UI thread. Dispose unsubscribes it.
    private void OnUpdatesChanged() => _ui.BeginInvoke(RefreshUpdateView);

    private void RefreshUpdateView()
    {
        if (_disposed) return;
        _updateView = UpdateViewBuilder.Build(Updates.Snapshot, JustUpdatedTo);
        Notify(nameof(UpdateView));
        Submit(NoticeFactory.UpdateAvailable(Updates.Snapshot, _updateView)); // the update balloon (07a, contract §2.5)
    }

    /// <summary>After every completed check (any thread): remember when, on the UI thread (contract §7).</summary>
    private void OnUpdateCheckCompleted(DateTimeOffset at) => _ui.BeginInvoke(() =>
    {
        if (_disposed || SaveSuppressed) return;
        Settings.Updates.LastCheckUtc = at;
        ScheduleSave();
    });

    /// <summary>The 1 s window timer (contract §2.4): probe while a game runs, clear when it is gone.</summary>
    private void RefreshGameWindow()
    {
        if (_disposed) return;
        if (_game is { } game) PublishGameWindow(GameWindowTracker.Observe(game, Watcher.UniformFrameStreak));
        else if (GameWindow is not null) PublishGameWindow(null);
    }

    private void OnWatcherState(WatcherState state)
    {
        if (_disposed) return;
        Notify(nameof(CaptureStatus));
        if (state != WatcherState.Error) return;
        _captureLog.Warn("Chat reading is failing", null, LogField.Of("error", Watcher.LastError));
        ReportProblem(ProblemText.CaptureError);
    }

    private void OnMessage(IncomingMessage message, bool isNew)
    {
        // A translation can finish after Dispose (the watcher does not wait for in-flight work) or after a reset or
        // "Delete all Envoy data": nothing may write logs/chat-*.txt or speak then.
        if (_disposed || SaveSuppressed) return;
        // While the privacy gate is closed every line fails the same known way (PrivacyText.TranslationOff): not a warning.
        if (!isNew && message.Status == IncomingStatus.Failed && !message.WasCancelled && _privacyAcknowledged)
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

    /// <summary>
    /// Builds the chat stack and the reply box's compose-lane stack (spec 03 §9). Providers that cannot be built show as
    /// "Not set up" in health; attempts and transitions are logged by <see cref="TranslationLogging"/>.
    /// </summary>
    private (ITranslator Chat, ITranslator Compose) BuildTranslator()
    {
        // The privacy gate is outermost (contract §8.1): while closed, nothing reaches 03's runtime, health or cache.
        var chat = new PrivacyGatedTranslator(_factory.Build(Settings.Translation), IsPrivacyGateOpen);
        var compose = new PrivacyGatedTranslator(_factory.Build(Settings.Translation, lane: TranslationLane.Compose), IsPrivacyGateOpen);
        _activeProviderIds = TranslatorFactory.ChainIds(Settings.Translation);
        RefreshProviderStatus();
        return (chat, compose);
    }

    /// <summary>Any thread: the volatile flag ApplySettings publishes.</summary>
    private bool IsPrivacyGateOpen() => _privacyAcknowledged;

    /// <summary>The War Room status line, derived from the health registry (REQ-TRR-18). UI thread.</summary>
    private void RefreshProviderStatus()
    {
        var names = _activeProviderIds.Select(ProviderCatalog.NameOf).ToList();
        var status = ProviderHealthText.Summary(TranslationHealth.Snapshot(_activeProviderIds), names, Time.GetUtcNow());
        if (status == _providerStatus) return;
        _providerStatus = status;
        Notify(nameof(ProviderStatus));
    }

    // Raised on the thread that finished the attempt; marshalled to the UI. Dispose unsubscribes it.
    private void OnProviderHealthChanged(ProviderHealthSnapshot snapshot) => _ui.BeginInvoke(() =>
    {
        if (_disposed) return;
        RefreshProviderStatus();
        ProviderHealthChanged?.Invoke(snapshot);
        // The provider notice follows the state edges (REQ-OUXA-17); Healthy re-arms it.
        switch (ProviderNoticeRules.For(snapshot.State))
        {
            case ProviderNoticeRule.Submit:
                Submit(NoticeFactory.ProviderFailing(snapshot.ProviderId, ProviderCatalog.NameOf(snapshot.ProviderId), snapshot.State));
                break;
            case ProviderNoticeRule.Resolve:
                Notices?.Resolve(NoticeFactory.ProviderKey(snapshot.ProviderId));
                break;
        }
    });

    /// <summary>The pipeline's options snapshot, with the chat format resolved for the current game (REQ-CHAT-20, 33).</summary>
    private ChatPipelineOptions BuildChatOptions() =>
        ChatPipelineOptions.From(Settings, ChatFormats.Resolve(Settings.Chat.FormatProfile, CurrentGameId));

    /// <summary>Re-reads chat-formats.json and re-resolves the format (REQ-CHAT-32). UI thread.</summary>
    public void ReloadChatFormats()
    {
        ChatFormats = ChatFormatCatalog.LoadWithUserFile(ChatFormatsPath, _chatLog);
        Pipeline.Options = BuildChatOptions();
        SettingsApplied?.Invoke();
    }

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
        Pipeline.Options = BuildChatOptions(); // 2: format re-resolve for the new game (REQ-CHAT-34)
        // 3: a download that waited for the game starts once it closes (06, REQ-DIST-16).
        PushGameRunning();
        // 4: advisories deferred while the game was in front (07a, contract §2.2).
        ReleaseDeferredNotices();
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
        Watcher.RegionChanged -= OnWatcherRegionChanged;
        TranslationRuntime.ProviderHealthChanged -= OnProviderHealthChanged;
        Updates.Changed -= OnUpdatesChanged;
        _noticeTimer?.Stop();
        _updatesCts.Cancel(); // aborts an in-flight download; Velopack removes the partial file next time (contract §4)
        _updateTimer.Stop();
        _gameTimer.Stop();
        _windowTimer.Stop();
        Watcher.Dispose();
        Pipeline.Dispose(); // in-flight translations end as "cancelled" (contract §2.6); nothing is written
        _speech?.Dispose();
        SaveNow(); // a no-op once saving is suppressed, so a reset or Delete-all is never undone
        _translationLogging.Dispose();
        TranslationRuntime.Dispose(); // saves remembered translations unless persistence is suppressed (contract §4)
        _http.Dispose();
        _updatesCts.Dispose();
        Log.Flush();
        if (_ownsLog) Log.Dispose();
    }
}
