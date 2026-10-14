using Envoy.Core.Chat;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Providers;

namespace Envoy.App.ViewModels;

/// <summary>Translation page (and the War Room's interpreter card).</summary>
public sealed partial class MainViewModel
{
    private string _translationTestInput = TranslationTestText.SampleInput;
    private string _translationTestOutput = "";

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
}
