using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;

namespace Envoy.Core.Ux;

/// <summary>How a provider's candle burns on the Translation page (spec 09a, REQ-HRD-7; 09b animates it).</summary>
public enum CandleState
{
    /// <summary>Answering, or never asked yet.</summary>
    Lit,

    /// <summary>Answering slowly or resting after failures; Envoy tries again by itself.</summary>
    Guttering,

    /// <summary>Out until the user acts: the key was refused, the quota is used up, or it is not set up.</summary>
    Snuffed,
}

/// <summary>Maps <see cref="ProviderState"/> onto the three candles.</summary>
public static class ProviderCandle
{
    /// <summary>
    /// The candle for a provider's state and last error kind. <see cref="ProviderState.Unknown"/> covers both "never called"
    /// (lit) and "not set up", a <see cref="TranslationErrorKind.Configuration"/> failure, which cannot translate (snuffed).
    /// </summary>
    public static CandleState For(ProviderState state, TranslationErrorKind? lastErrorKind) =>
        state == ProviderState.Unknown && lastErrorKind == TranslationErrorKind.Configuration ? CandleState.Snuffed : For(state);

    /// <summary>The candle for a state alone (an <see cref="ProviderState.Unknown"/> provider counts as never called).</summary>
    public static CandleState For(ProviderState state) => state switch
    {
        ProviderState.Healthy or ProviderState.Unknown => CandleState.Lit,
        ProviderState.Degraded or ProviderState.Throttled or ProviderState.CoolingDown => CandleState.Guttering,
        _ => CandleState.Snuffed,
    };
}
