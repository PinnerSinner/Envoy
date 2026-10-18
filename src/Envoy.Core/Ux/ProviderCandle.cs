using Envoy.Core.Translation.Resilience;

namespace Envoy.Core.Ux;

/// <summary>How a provider's candle burns on the Translation page (spec 09a, REQ-HRD-7; 09b animates it).</summary>
public enum CandleState
{
    /// <summary>Answering, or not asked yet.</summary>
    Lit,

    /// <summary>Answering slowly or resting after failures; Envoy tries again by itself.</summary>
    Guttering,

    /// <summary>Out until the user acts: the key was refused or the quota is used up.</summary>
    Snuffed,
}

/// <summary>Maps <see cref="ProviderState"/> onto the three candles.</summary>
public static class ProviderCandle
{
    public static CandleState For(ProviderState state) => state switch
    {
        ProviderState.Healthy or ProviderState.Unknown => CandleState.Lit,
        ProviderState.Degraded or ProviderState.Throttled or ProviderState.CoolingDown => CandleState.Guttering,
        _ => CandleState.Snuffed,
    };
}
