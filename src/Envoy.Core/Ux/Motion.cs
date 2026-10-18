namespace Envoy.Core.Ux;

/// <summary>The Appearance › Motion setting (spec 09b, REQ-MOT-1). Saved by name; an unknown value reads as Auto.</summary>
public enum MotionSetting
{
    /// <summary>Full motion, or fades only when Windows' "Show animations" is off.</summary>
    Auto,

    /// <summary>Every animation, including the looping ones (candles, torches, the active tab).</summary>
    Full,

    /// <summary>Fades only: nothing moves, slides, scales or loops.</summary>
    Reduced,

    /// <summary>No animation: every change shows its end state at once.</summary>
    Off,
}

/// <summary>How much the app animates right now: the resolved <see cref="MotionSetting"/>.</summary>
public enum MotionLevel
{
    Full,
    Reduced,
    Off,
}

/// <summary>What an animation changes, which decides whether it runs at a <see cref="MotionLevel"/>.</summary>
public enum MotionKind
{
    /// <summary>Opacity or an opacity mask: kept under <see cref="MotionLevel.Reduced"/>.</summary>
    Fade,

    /// <summary>A translate, scale or rotate, a clip or a stroke being drawn: the end state at once unless Full.</summary>
    Move,

    /// <summary>A repeating decoration (a flickering flame, a pulsing seal, a swaying tab): Full only.</summary>
    Loop,
}

/// <summary>
/// The named durations and distances of every animation in the app (spec 09b, REQ-MOT-1), and the rules that turn a
/// <see cref="MotionLevel"/> into what runs. The app's one animation helper (<c>Services/Motion.cs</c>) reads only these.
/// </summary>
public static class Motion
{
    /// <summary>Hover, press and focus responses.</summary>
    public static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(150);

    /// <summary>A switch sliding, a seal stamping, a step page turning, the reply desk folding.</summary>
    public static readonly TimeSpan Snap = TimeSpan.FromMilliseconds(320);

    /// <summary>A charter or a dispatch unrolling.</summary>
    public static readonly TimeSpan Unroll = TimeSpan.FromMilliseconds(450);

    /// <summary>Ink writing itself: a translation on the reply desk, a drawing stroke by stroke.</summary>
    public static readonly TimeSpan Ink = TimeSpan.FromMilliseconds(1400);

    /// <summary>A dispatch's seal stamps in this long after the scroll starts to unfurl (REQ-MOT-5).</summary>
    public static readonly TimeSpan SealDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>An overlay dispatch fades out like ink over the last this-long of its linger time.</summary>
    public static readonly TimeSpan InkFade = TimeSpan.FromSeconds(2);

    /// <summary>Buttons lift this many pixels on hover and press down half of it.</summary>
    public const double Lift = 2;

    /// <summary>A banner tab slides out this far on hover; the active tab rests at <see cref="TabDrop"/>.</summary>
    public const double TabSlide = 8;

    /// <summary>How far the active banner tab stands out from the rail.</summary>
    public const double TabDrop = 14;

    /// <summary>A stamped seal starts this large and settles at 1 with a slight overshoot.</summary>
    public const double StampFrom = 1.8;

    /// <summary>Wax buttons tilt this many degrees on hover.</summary>
    public const double WaxTilt = -3;

    /// <summary>A step page of the setup guide slides in from this far to the right.</summary>
    public const double PageTurn = 24;

    /// <summary>
    /// Whether an animation of <paramref name="kind"/> runs at <paramref name="level"/>. When it does not, the caller shows
    /// the end state at once (a <see cref="MotionKind.Loop"/> shows its resting frame).
    /// </summary>
    public static bool Runs(MotionLevel level, MotionKind kind) => level switch
    {
        MotionLevel.Full => true,
        MotionLevel.Reduced => kind == MotionKind.Fade,
        _ => false,
    };

    /// <summary>The duration an animation of <paramref name="kind"/> gets at <paramref name="level"/>: <paramref name="full"/>, or zero when it does not run.</summary>
    public static TimeSpan DurationFor(MotionLevel level, MotionKind kind, TimeSpan full) =>
        Runs(level, kind) && full > TimeSpan.Zero ? full : TimeSpan.Zero;

    /// <summary>
    /// An overlay dispatch's opacity with <paramref name="remaining"/> linger time left (REQ-MOT-5): 1 until the last
    /// <see cref="InkFade"/>, then fading to 0. Under <see cref="MotionLevel.Off"/> it stays 1 until the dispatch is removed.
    /// </summary>
    public static double LingerOpacity(TimeSpan remaining, MotionLevel level)
    {
        if (remaining <= TimeSpan.Zero) return 0;
        if (!Runs(level, MotionKind.Fade)) return 1;
        return Math.Clamp(remaining.TotalMilliseconds / InkFade.TotalMilliseconds, 0, 1);
    }
}

/// <summary>Resolves the Motion setting and Windows' animation flag into a <see cref="MotionLevel"/> (REQ-MOT-1).</summary>
public static class MotionPolicy
{
    /// <summary>
    /// <paramref name="clientAreaAnimation"/> is Windows' "Show animations in Windows" (<c>SystemParameters.ClientAreaAnimation</c>).
    /// Auto, and any value this build does not know, follows it: Full when on, Reduced when off.
    /// </summary>
    public static MotionLevel Resolve(MotionSetting setting, bool clientAreaAnimation) => setting switch
    {
        MotionSetting.Full => MotionLevel.Full,
        MotionSetting.Reduced => MotionLevel.Reduced,
        MotionSetting.Off => MotionLevel.Off,
        _ => clientAreaAnimation ? MotionLevel.Full : MotionLevel.Reduced,
    };
}
