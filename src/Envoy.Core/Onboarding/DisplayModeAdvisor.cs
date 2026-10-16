using Envoy.Core.Capture;

namespace Envoy.Core.Onboarding;

/// <summary>How the game is displayed, as far as Envoy can tell from 05's window facts (REQ-OUXA-5).</summary>
public enum DisplayModeVerdict
{
    NotRunning,
    Windowed,
    Borderless,
    LikelyExclusiveFullscreen,
    Unknown,
}

/// <summary>Classifies <c>AppHost.GameWindow</c> (contract §2.4). Pure; no window probing of its own.</summary>
public static class DisplayModeAdvisor
{
    /// <summary>How far (physical px) a borderless window may miss the monitor's edges.</summary>
    public const int EdgeTolerance = 2;

    /// <summary>
    /// null → NotRunning. A blank-frame streak at <see cref="BlankFrames.Threshold"/> → LikelyExclusiveFullscreen.
    /// Minimised while not in front → LikelyExclusiveFullscreen (exclusive games minimise on alt-tab). A caption →
    /// Windowed. A window covering its monitor within 2 px on every edge → Borderless. Otherwise Unknown.
    /// </summary>
    public static DisplayModeVerdict Classify(GameWindowInfo? window)
    {
        if (window is null) return DisplayModeVerdict.NotRunning;
        if (window.UniformFrameStreak >= BlankFrames.Threshold) return DisplayModeVerdict.LikelyExclusiveFullscreen;
        if (window.Minimized && !window.GameForeground) return DisplayModeVerdict.LikelyExclusiveFullscreen;
        if (window.HasCaption) return DisplayModeVerdict.Windowed;
        var w = window.WindowRect;
        var m = window.MonitorBounds;
        if (!m.IsEmpty
            && Math.Abs(w.X - m.X) <= EdgeTolerance && Math.Abs(w.Y - m.Y) <= EdgeTolerance
            && Math.Abs(w.X + w.Width - (m.X + m.Width)) <= EdgeTolerance
            && Math.Abs(w.Y + w.Height - (m.Y + m.Height)) <= EdgeTolerance)
            return DisplayModeVerdict.Borderless;
        return DisplayModeVerdict.Unknown;
    }
}
