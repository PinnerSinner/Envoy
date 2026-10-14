namespace Envoy.Core.Capture;

/// <summary>Why the watcher is not capturing (REQ-CAP-5, 9..13).</summary>
public enum PauseReason
{
    None,

    /// <summary>No chat area is set for this game (or "any").</summary>
    NoRegion,

    /// <summary>No game is running and "only while a game runs" is on.</summary>
    NoGame,

    /// <summary>The game window is minimised.</summary>
    GameMinimized,

    /// <summary>"Pause when the game isn't the active window" is on and another app is in front.</summary>
    GameNotForeground,

    /// <summary>Another window covers the chat area and the capture method can't see through it.</summary>
    ChatCovered,

    /// <summary>The chat area is on no monitor.</summary>
    RegionOffScreen,
}

/// <summary>Whose top-level window is at one sample point of the chat area.</summary>
public enum SampleOwner
{
    Game,
    Envoy,
    Other,
}

/// <summary>What the tick saw before deciding whether to capture.</summary>
/// <param name="HasRegion">A non-empty region was resolved.</param>
/// <param name="RegionOnScreen">The region intersects a monitor.</param>
/// <param name="ShouldRead"><see cref="CaptureOptions.ShouldRead"/> for the current game state.</param>
/// <param name="WindowFound">The game window was found.</param>
/// <param name="Minimized">The game window is minimised.</param>
/// <param name="GameForeground">The game owns the foreground window.</param>
/// <param name="EnvoyForeground">Envoy owns the foreground window.</param>
/// <param name="Source">Where the region came from.</param>
/// <param name="Samples">Owners at the covered-check sample points; null when not sampled.</param>
public sealed record CaptureObservation(bool HasRegion, bool RegionOnScreen, bool ShouldRead,
    bool WindowFound, bool Minimized, bool GameForeground, bool EnvoyForeground,
    RegionSource Source, IReadOnlyList<SampleOwner>? Samples);

/// <summary>The pause settings and the capture context.</summary>
/// <param name="PauseWhenCovered"><c>Capture.PauseWhenCovered</c>.</param>
/// <param name="PauseWhenNotForeground"><c>Capture.PauseWhenNotForeground</c>.</param>
/// <param name="BackendSeesOccluded">The capture method reads the game even under other windows.</param>
/// <param name="PreviewOpen">A Live view holds a preview lease (REQ-CAP-13).</param>
/// <param name="CoveredSampleThreshold">Sample points owned by another window that make the chat "covered".</param>
public sealed record CaptureGateOptions(bool PauseWhenCovered, bool PauseWhenNotForeground,
    bool BackendSeesOccluded, bool PreviewOpen, int CoveredSampleThreshold = 3);

/// <summary>Decides whether a tick captures, and if not, why (REQ-CAP-5, 9, 10, 11, 13). Pure.</summary>
public static class CaptureGate
{
    /// <summary>
    /// NoRegion → RegionOffScreen → GameMinimized → (an open preview captures anyway) → NoGame → GameNotForeground →
    /// ChatCovered → None. ChatCovered applies only to a window-relative region.
    /// </summary>
    public static PauseReason Evaluate(CaptureObservation observation, CaptureGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(options);
        if (!observation.HasRegion) return PauseReason.NoRegion;
        if (!observation.RegionOnScreen) return PauseReason.RegionOffScreen;
        if (observation.WindowFound && observation.Minimized) return PauseReason.GameMinimized;
        if (options.PreviewOpen) return PauseReason.None;
        if (!observation.ShouldRead) return PauseReason.NoGame;
        if (options.PauseWhenNotForeground && observation.WindowFound && !observation.GameForeground && !observation.EnvoyForeground)
            return PauseReason.GameNotForeground;
        if (options.PauseWhenCovered && !options.BackendSeesOccluded && observation.Source == RegionSource.WindowRelative
            && observation.Samples is { } samples && CountCovered(samples) >= options.CoveredSampleThreshold)
            return PauseReason.ChatCovered;
        return PauseReason.None;
    }

    /// <summary>
    /// True when frames may go to the pipeline: the gate passes without the preview override. A frame captured only
    /// because a Live view is open goes to the Live view, never to translation.
    /// </summary>
    public static bool IsReading(CaptureObservation observation, CaptureGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Evaluate(observation, options with { PreviewOpen = false }) == PauseReason.None;
    }

    /// <summary>Sample points owned by a window that is neither the game nor Envoy.</summary>
    public static int CountCovered(IReadOnlyList<SampleOwner> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var count = 0;
        foreach (var s in samples)
        {
            if (s == SampleOwner.Other) count++;
        }
        return count;
    }
}
