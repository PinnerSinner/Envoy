using System.Collections.ObjectModel;
using System.Windows.Threading;
using Envoy.Core.Chat;
using Envoy.Core.Mvvm;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Providers;
using Envoy.Core.Translation.Resilience;

namespace Envoy.App.ViewModels;

/// <summary>Translation page (and the War Room's interpreter card).</summary>
public sealed partial class MainViewModel
{
    private string _translationTestInput = TranslationTestText.SampleInput;
    private string _translationTestOutput = "";
    private DispatcherTimer? _healthTimer;
    private bool _isTranslationPageActive;
    private string _cacheStatus = "";

    public IReadOnlyList<ProviderInfo> Providers { get; } = ProviderCatalog.All;
    public IReadOnlyList<string> ClaudeModels { get; } = ClaudeTranslator.SuggestedModels;

    public IReadOnlyList<Option> ChannelOptions { get; } =
    [
        new(Core.Pipeline.ChannelFilter.Everything, "All chat"),
        new(Core.Pipeline.ChannelFilter.TeamOnly, "Team chat only"),
        new(Core.Pipeline.ChannelFilter.ExceptTeam, "Everything except team chat"),
    ];

    /// <summary>Muted players as a comma/newline separated list for a simple text box.</summary>
    public string MutedPlayersText
    {
        get => PlayerListText.Format(Settings.MutedPlayers);
        set => Settings.MutedPlayers = PlayerListText.Parse(value);
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

    /// <summary>The "Try it" input box. Not a setting.</summary>
    public string TranslationTestInput
    {
        get => _translationTestInput;
        set => SetProperty(ref _translationTestInput, value);
    }

    /// <summary>The "Try it" result line.</summary>
    public string TranslationTestOutput
    {
        get => _translationTestOutput;
        private set => SetProperty(ref _translationTestOutput, value);
    }

    // ---- Provider health card and saved translations (spec 03, REQ-TRR-18, 20, 21, 28) ----

    /// <summary>One row per provider in the active chain, in chain order.</summary>
    public ObservableCollection<ProviderHealthRow> ProviderHealthRows { get; } = [];

    /// <summary>Parameter: the provider id. Closes its breaker and clears its health ("Retry now").</summary>
    public RelayCommand<string> RetryProviderCommand { get; }

    /// <summary>Empties the memory cache and deletes translation-cache.json.</summary>
    public RelayCommand ClearTranslationCacheCommand { get; }

    /// <summary>"{count} translations saved on this PC."</summary>
    public string CacheCountText =>
        TranslationCacheText.Status(Host.TranslationRuntime.Options.RememberTranslations, Host.TranslationRuntime.Cache.PersistableCount);

    /// <summary>"" or why clearing failed.</summary>
    public string CacheStatus
    {
        get => _cacheStatus;
        private set => SetProperty(ref _cacheStatus, value);
    }

    /// <summary>
    /// Set by <c>TranslationPage</c> from <c>IsVisibleChanged</c> (01's pattern): the 1 s refresh of the health text runs
    /// only while the page is visible.
    /// </summary>
    public bool IsTranslationPageActive
    {
        get => _isTranslationPageActive;
        set
        {
            if (value && _disposed) return;
            if (!SetProperty(ref _isTranslationPageActive, value)) return;
            if (value)
            {
                RebuildHealthRows();
                _healthTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshHealthRows(), Dispatcher.CurrentDispatcher);
                _healthTimer.Start();
            }
            else
            {
                _healthTimer?.Stop();
            }
        }
    }

    private void AttachTranslationHealth()
    {
        Host.ProviderHealthChanged += OnProviderHealthChanged;
        Host.SettingsApplied += RebuildHealthRows;
        RebuildHealthRows();
    }

    private void DetachTranslationHealth()
    {
        Host.ProviderHealthChanged -= OnProviderHealthChanged;
        Host.SettingsApplied -= RebuildHealthRows;
        _healthTimer?.Stop();
        _healthTimer = null;
    }

    private void OnProviderHealthChanged(ProviderHealthSnapshot snapshot) => RefreshHealthRows();

    /// <summary>Matches the rows to the active chain (keeping existing rows), then refreshes their text.</summary>
    private void RebuildHealthRows()
    {
        var ids = Host.ActiveProviderIds;
        if (!ProviderHealthRows.Select(r => r.ProviderId).SequenceEqual(ids, StringComparer.Ordinal))
        {
            var existing = ProviderHealthRows.ToDictionary(r => r.ProviderId, StringComparer.Ordinal);
            ProviderHealthRows.Clear();
            foreach (var id in ids) ProviderHealthRows.Add(existing.TryGetValue(id, out var row) ? row : new ProviderHealthRow(id));
        }
        RefreshHealthRows();
    }

    private void RefreshHealthRows()
    {
        var now = Host.Time.GetUtcNow();
        foreach (var row in ProviderHealthRows) row.Update(Host.TranslationHealth.Get(row.ProviderId), now);
        Notify(nameof(CacheCountText));
    }

    private void RetryProvider(string? providerId)
    {
        if (string.IsNullOrEmpty(providerId)) return;
        Host.TranslationRuntime.ResetProvider(providerId);
        RefreshHealthRows();
    }

    private void ClearTranslationCache()
    {
        CacheStatus = Host.TranslationRuntime.ClearCache() ? "" : TranslationErrorText.CacheClearFailed;
        Notify(nameof(CacheCountText));
    }
}
