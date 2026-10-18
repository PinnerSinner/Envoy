using Envoy.Core.Mvvm;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Envoy.Core.Ux;

namespace Envoy.App.ViewModels;

/// <summary>
/// One row of the Translation page's "Provider health" card (REQ-TRR-18). The text updates in place, so the row (and
/// its "Retry now" button) keeps focus while the page refreshes.
/// </summary>
public sealed class ProviderHealthRow(string providerId) : ObservableObject
{
    private string _stateText = "";
    private string _lastSuccessText = "";
    private ProviderState _state;
    private TranslationErrorKind? _lastErrorKind;

    public string ProviderId { get; } = providerId;
    public string Name { get; } = ProviderCatalog.NameOf(providerId);

    /// <summary>The button's <c>AutomationProperties.Name</c>: "Retry {provider} now".</summary>
    public string RetryName { get; } = ProviderHealthText.RetryButtonName(ProviderCatalog.NameOf(providerId));

    /// <summary>The row's accessible name: provider name and state.</summary>
    public string AccessibleName => Name + ": " + StateText;

    public ProviderState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(Candle));
        }
    }

    /// <summary>The candle's look, a <see cref="CandleState"/> name: Lit, Guttering or Snuffed (spec 09a).</summary>
    public string Candle => ProviderCandle.For(State, _lastErrorKind).ToString();

    public string StateText
    {
        get => _stateText;
        private set
        {
            if (SetProperty(ref _stateText, value)) OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public string LastSuccessText
    {
        get => _lastSuccessText;
        private set => SetProperty(ref _lastSuccessText, value);
    }

    public void Update(ProviderHealthSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candle = Candle;
        _lastErrorKind = snapshot.LastErrorKind;
        State = snapshot.State;
        if (Candle != candle) OnPropertyChanged(nameof(Candle));
        StateText = ProviderHealthText.Describe(snapshot, now);
        LastSuccessText = ProviderHealthText.LastSuccess(snapshot, now);
    }
}
