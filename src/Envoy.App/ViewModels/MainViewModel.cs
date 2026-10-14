using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Envoy.App.Services;
using Envoy.Core.Games;
using Envoy.Core.Languages;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Providers;

namespace Envoy.App.ViewModels;

public sealed record Option(object Value, string Label);

public sealed class OcrLanguageItem(OcrLanguageStatus status, bool selected, bool isMine, Action changed) : INotifyPropertyChanged
{
    private bool _selected = selected;
    public string Code => status.Code;
    public string Name => status.Name;
    public bool Installed => status.Installed;
    public bool IsMine => isMine;

    /// <summary>Only installed languages can be toggled; your own language is always read.</summary>
    public bool CanToggle => Installed && !isMine;

    public string StatusText => isMine ? "your language · always on" : Installed ? "" : "not installed";

    public bool IsSelected
    {
        get => isMine || (_selected && Installed);
        set
        {
            if (!CanToggle) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            changed();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ThemeOption(string id, string name, string inspiration, Brush[] swatches, Func<string> current, Action<string> select) : INotifyPropertyChanged
{
    public string Id => id;
    public string Name => name;
    public string Inspiration => inspiration;
    public Brush[] Swatches => swatches;

    public bool IsSelected
    {
        get => current() == id;
        set { if (value) select(id); }
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    public MainViewModel(AppHost host)
    {
        Host = host;
        host.PropertyChanged += (_, e) => Notify(nameof(Host));

        ReplyLanguages = [new Option(LanguageCatalog.Auto, "Auto — whatever they last wrote in"), .. Languages.Select(l => new Option(l.Code, l.DisplayName))];
        Games = [new Option(GameCatalog.Any, "Detect automatically"), .. GameCatalog.All.Select(g => new Option(g.Id, g.Name))];

        Themes =
        [
            new ThemeOption(ThemeManager.MatchGame, "Match the game", "Switches automatically: Imperial for AoE IV, Parchment for AoE II, and so on.",
                [Swatch("#0E1420"), Swatch("#CDB98F"), Swatch("#0D1C1B"), Swatch("#0B1828")], () => Settings.Theme, SetTheme),
            .. ThemeManager.Themes.Select(t => new ThemeOption(t.Id, t.Name, t.Inspiration, SwatchesFor(t.Id), () => Settings.Theme, SetTheme)),
        ];

        OcrLanguages = new ObservableCollection<OcrLanguageItem>(LoadOcrLanguages());
    }

    public AppHost Host { get; }
    public AppSettings Settings => Host.Settings;

    public IReadOnlyList<Language> Languages { get; } = LanguageCatalog.All;
    public IReadOnlyList<Option> ReplyLanguages { get; }
    public IReadOnlyList<Option> Games { get; }
    public IReadOnlyList<ProviderInfo> Providers { get; } = ProviderCatalog.All;
    public IReadOnlyList<string> ClaudeModels { get; } = ClaudeTranslator.SuggestedModels;
    public IReadOnlyList<ThemeOption> Themes { get; }
    public ObservableCollection<OcrLanguageItem> OcrLanguages { get; }

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

    public IReadOnlyList<Option> Anchors { get; } =
    [
        new(OverlayAnchor.AboveChat, "Above the chat area"),
        new(OverlayAnchor.BelowChat, "Below the chat area"),
        new(OverlayAnchor.RightOfChat, "Right of the chat area"),
        new(OverlayAnchor.Free, "Wherever I drag it"),
    ];

    public IReadOnlyList<Option> Voices { get; } =
        [new Option("", "Windows default voice"), .. SpeechService.Voices.Select(v => new Option(v.Id, v.Name))];

    public IReadOnlyList<Option> ChannelOptions { get; } =
    [
        new(Core.Pipeline.ChannelFilter.Everything, "All chat"),
        new(Core.Pipeline.ChannelFilter.TeamOnly, "Team chat only"),
        new(Core.Pipeline.ChannelFilter.ExceptTeam, "Everything except team chat"),
    ];

    /// <summary>Muted players as a comma/newline separated list for a simple text box.</summary>
    public string MutedPlayersText
    {
        get => string.Join(", ", Settings.MutedPlayers);
        set => Settings.MutedPlayers = value
            .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<Option> FallbackOptions =>
        [new Option("", "None (Google free is always the last resort)"), .. Providers.Select(p => new Option(p.Id, p.Name))];

    public string FallbackProvider
    {
        get => Settings.Translation.Fallbacks.FirstOrDefault() ?? "";
        set
        {
            Settings.Translation.Fallbacks = string.IsNullOrEmpty(value) ? [] : [value];
            Notify();
            RefreshProviderSections();
        }
    }

    public string PrimaryProvider
    {
        get => Settings.Translation.Primary;
        set
        {
            Settings.Translation.Primary = value;
            Notify();
            Notify(nameof(PrimaryInfo));
            Notify(nameof(NeedsDeepL));
            Notify(nameof(NeedsClaude));
            Notify(nameof(NeedsAws));
            Notify(nameof(NeedsLibre));
        }
    }

    public ProviderInfo? PrimaryInfo => ProviderCatalog.Find(PrimaryProvider);

    // Credential sections are shown when the provider is primary or fallback.
    private bool Uses(string id) => PrimaryProvider == id || FallbackProvider == id;
    public bool NeedsDeepL => Uses(ProviderCatalog.DeepL);
    public bool NeedsClaude => Uses(ProviderCatalog.Claude);
    public bool NeedsAws => Uses(ProviderCatalog.Aws);
    public bool NeedsLibre => Uses(ProviderCatalog.LibreTranslate);

    public void RefreshProviderSections()
    {
        Notify(nameof(NeedsDeepL));
        Notify(nameof(NeedsClaude));
        Notify(nameof(NeedsAws));
        Notify(nameof(NeedsLibre));
    }

    public string RegionText => Host.Watcher.CurrentRegion is { IsEmpty: false } r
        ? $"{r.Width} × {r.Height} px at ({r.X}, {r.Y})"
        : "Not set yet — click “Select chat area”";

    public bool HasRegion => Host.Watcher.CurrentRegion is { IsEmpty: false };

    public string UsageText
    {
        get
        {
            var snapshot = Host.Usage.Snapshot();
            var metered = snapshot.Characters.Where(kv => ProviderCatalog.Find(kv.Key)?.IsMetered == true).ToList();
            if (metered.Count == 0) return "No paid translation used today.";
            var limit = Settings.Translation.DailyCharacterLimit;
            return string.Join("  ·  ", metered.Select(kv =>
                $"{ProviderCatalog.Find(kv.Key)?.Name}: {kv.Value:N0}{(limit > 0 ? $" / {limit:N0}" : "")} characters today"));
        }
    }

    public string OcrSummary => Host.Ocr.ActiveLanguages.Count == 0
        ? "No OCR languages available"
        : "Reading: " + string.Join(", ", Host.Ocr.ActiveLanguages.Select(LanguageCatalog.NameOf));

    public string HotkeyErrors => App.Current is { } app && app.HotkeyErrors.Count > 0
        ? "These hotkeys are taken by another app: " + string.Join(", ", app.HotkeyErrors)
        : "";

    public string DataFolder => AppHost.DataFolder;

    public string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public void RefreshStatus()
    {
        Notify(nameof(RegionText));
        Notify(nameof(HasRegion));
        Notify(nameof(UsageText));
        Notify(nameof(OcrSummary));
        Notify(nameof(HotkeyErrors));
    }

    private void SetTheme(string id)
    {
        Settings.Theme = id;
        foreach (var t in Themes) t.Refresh();
        Host.ApplySettings();
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
