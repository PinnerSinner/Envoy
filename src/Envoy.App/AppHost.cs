using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Envoy.App.Services;
using Envoy.Core.Chat;
using Envoy.Core.Games;
using Envoy.Core.Phrases;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Updates;

namespace Envoy.App;

/// <summary>
/// Owns the app's long-lived services and state. Windows bind to this; it marshals background events
/// (capture, translation) onto the UI thread.
/// </summary>
public sealed class AppHost : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly SettingsStore _store;
    private readonly HttpClient _http;
    private readonly TranslatorFactory _factory;
    private readonly DispatcherTimer _gameTimer;
    private readonly DispatcherTimer _saveTimer;
    private ITranslator _translator;
    private RunningGame? _game;
    private string _providerStatus = "";
    private UpdateInfo? _update;

    public AppHost(string settingsPath, Dispatcher ui)
    {
        _ui = ui;
        _store = new SettingsStore(settingsPath, new DpapiProtector());
        Settings = _store.Load();
        Usage.Restore(Settings.Usage);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Envoy/0.1");
        _factory = new TranslatorFactory(_http, Usage);
        _translator = BuildTranslator();

        Phrases = PhraseBook.LoadWithUserFile(UserPhrasesPath);
        Ocr = new OcrService();
        Pipeline = new ChatPipeline(_translator, PipelineOptions());
        Outgoing = new OutgoingTranslator(() => _translator, Phrases);
        Watcher = new ChatWatcher(Ocr, Pipeline, () => Settings, () => CurrentGameId)
        {
            ShouldRead = () => !Settings.Capture.OnlyWhileGameRuns || _game is not null || Settings.Game != GameCatalog.Any,
        };
        Transcript = new TranscriptWriter(LogsFolder);

        Pipeline.MessageReceived += m => _ui.BeginInvoke(() => OnMessage(m, isNew: true));
        Pipeline.MessageUpdated += m => _ui.BeginInvoke(() => OnMessage(m, isNew: false));
        Watcher.StateChanged += _ => _ui.BeginInvoke(() => Notify(nameof(CaptureStatus)));

        ConfigureOcr();

        _gameTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => RefreshGame(), _ui);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), _ui) { IsEnabled = false };
    }

    public static string DataFolder => Path.GetDirectoryName(SettingsStore.DefaultPath)!;
    public static string UserPhrasesPath => Path.Combine(DataFolder, "phrases.json");
    public static string LogsFolder => Path.Combine(DataFolder, "logs");

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
    public SpeechService Speech => _speech ??= new SpeechService();
    public UpdateInfo? Update => _update;
    public bool HasUpdate => _update is not null;
    public string UpdateText => _update is null ? "" : $"Envoy {_update.Tag} is available — you have v{CurrentVersion.ToString(3)}.";

    /// <summary>Players seen this match, for the dashboard.</summary>
    public ObservableCollection<PlayerInfo> Players { get; } = [];

    /// <summary>Most recent messages, newest first (for the dashboard log and the reply box).</summary>
    public ObservableCollection<IncomingMessage> History { get; } = [];

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

    public void Start()
    {
        RefreshGame();
        ApplyTheme();
        _gameTimer.Start();
        if (Settings.StartCapturingAutomatically) Watcher.Start();
        Notify(nameof(CaptureStatus));
        if (Settings.CheckForUpdates) _ = CheckForUpdatesAsync();
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

    public void SaveNow()
    {
        _saveTimer.Stop();
        Settings.Usage = Usage.Snapshot();
        try
        {
            _store.Save(Settings);
        }
        catch (IOException)
        {
            // Retry on the next change.
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

    private void OnMessage(IncomingMessage message, bool isNew)
    {
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
                catch (IOException) { }
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
        catch (Exception)
        {
            // Speech is a nicety; never let it break message handling.
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
        catch (Exception)
        {
            // OCR unavailable (very old Windows / stripped-down edition): capture will report it.
        }
    }

    private void RefreshGame()
    {
        var before = _game?.Profile.Id;
        _game = GameDetector.Find();
        if (_game?.Profile.Id == before) return;
        if (_game is not null && before is null) NewMatch();
        Notify(nameof(Game));
        Notify(nameof(GameStatus));
        if (Settings.Theme == ThemeManager.MatchGame) ApplyTheme();
        Watcher.Invalidate();
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _gameTimer.Stop();
        Watcher.Dispose();
        _speech?.Dispose();
        SaveNow();
        _http.Dispose();
    }
}
