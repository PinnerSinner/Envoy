namespace Envoy.Core.Ux;

/// <summary>What a provider's candle does when its state changes (spec 09b, REQ-MOT-7).</summary>
public enum CandleChange
{
    /// <summary>No change, or the first time the candle is drawn: it simply shows its state.</summary>
    None,

    /// <summary>Lit to guttering: the flame sinks and dims.</summary>
    Gutter,

    /// <summary>Guttering to lit: the flame grows back.</summary>
    Flare,

    /// <summary>To snuffed: the flame goes out and smoke rises.</summary>
    Snuff,

    /// <summary>Snuffed to lit or guttering ("Retry now" worked): the flame relights with a pop.</summary>
    Relight,
}

/// <summary>How a burning candle flickers: a period and how far the flame stretches (0 for a snuffed candle).</summary>
public readonly record struct CandleFlicker(TimeSpan Period, double Stretch);

/// <summary>The candle animations of the Interpreters page, as pure rules (REQ-MOT-7).</summary>
public static class CandleMotion
{
    /// <summary>The animation between two states; <paramref name="from"/> is null the first time the candle is drawn.</summary>
    public static CandleChange Between(CandleState? from, CandleState to)
    {
        if (from is not { } previous || previous == to) return CandleChange.None;
        if (to == CandleState.Snuffed) return CandleChange.Snuff;
        if (previous == CandleState.Snuffed) return CandleChange.Relight;
        return to == CandleState.Guttering ? CandleChange.Gutter : CandleChange.Flare;
    }

    /// <summary>
    /// A lit flame flickers quickly and a little; a guttering one slowly and further, so it looks about to go out; a
    /// snuffed candle does not flicker.
    /// </summary>
    public static CandleFlicker FlickerFor(CandleState state) => state switch
    {
        CandleState.Lit => new(TimeSpan.FromMilliseconds(1100), 0.08),
        CandleState.Guttering => new(TimeSpan.FromMilliseconds(2300), 0.22),
        _ => new(TimeSpan.Zero, 0),
    };
}
