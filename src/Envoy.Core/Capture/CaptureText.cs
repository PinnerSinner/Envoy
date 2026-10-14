using System.Globalization;
using Envoy.Core.Settings;
using Envoy.Core.Text;

namespace Envoy.Core.Capture;

/// <summary>
/// Every capture status, error and region sentence (contract §8.7). Whole sentences with named placeholders, filled by
/// <see cref="TextTemplate.Format"/>; no "Error:" prefixes and never exception text.
/// </summary>
public static class CaptureText
{
    public const string Reading = "Reading chat";
    public const string ReadingSlow = "Reading chat. Text recognition is slow right now.";
    public const string NoRegion = "Chat area not set";
    public const string WaitingForGame = "Waiting for a game to start";
    public const string Stopped = "Chat reading is off";
    public const string PausedGameMinimized = "Paused while the game is minimised.";
    public const string PausedChatCovered = "Paused because another window covers the chat area. Move it away to resume.";
    public const string PausedGameNotForeground = "Paused while the game isn't the active window.";
    public const string PausedRegionOffScreen = "The chat area is off-screen. Select it again.";
    public const string ScreenUnreadable = "Envoy can't read the screen right now. It keeps trying, for example after you unlock Windows.";
    public const string OcrTimedOut = "Text recognition took too long. Envoy keeps trying.";
    public const string CaptureFailed = "Reading the chat area failed. Envoy keeps trying.";
    public const string StoppedResponding = "Chat reading stopped responding. Turn chat reading off and on to try again.";
    public const string DpiUnaware = "Display scaling awareness is off, so the chat area may be misaligned. Restart Envoy.";
    public const string LikelyExclusiveFullscreen = "The chat area is blank. Exclusive fullscreen can't be read. Switch the game to borderless or windowed mode, or select the chat area again.";

    public const string HealthHealthy = "Chat reading is working normally.";
    public const string HealthDegraded = "Chat reading is slow right now. Envoy keeps trying.";
    public const string HealthFailing = "Chat reading keeps failing. Check that the game and its chat are visible on screen.";

    /// <summary>Shown in the Reading Chat region row before a chat area is chosen.</summary>
    public const string RegionNotSet = "Not set yet. Click “Select chat area”.";

    /// <summary>{width}, {height} in physical px; {game} = the game's name.</summary>
    public const string RegionFollowsFormat = "{width} × {height} px · follows the {game} window";

    /// <summary>{width}, {height}, {x}, {y} in physical px.</summary>
    public const string RegionFixedFormat = "{width} × {height} px at ({x}, {y}) · fixed screen position";

    public const string RegionResizedHint = "The game's size changed since you chose the chat area. Select it again if the Live view looks wrong.";

    /// <summary>Live view header; invariant culture. {interval} is in seconds.</summary>
    public const string StatsFormat = "capture {captureMs:0} ms · OCR {ocrMs:0} ms · skipped {skipped}/{captured} ({skipPercent:0}%) · every {interval:0.0#} s · {backend}";

    /// <summary>The label of the GDI capture method in the "Capture method" list.</summary>
    public const string BackendGdi = "Screen copy (windowed and borderless games)";

    /// <summary>The status sentence for a pause; <see cref="PauseReason.None"/> is <see cref="Reading"/>.</summary>
    public static string Paused(PauseReason reason) => reason switch
    {
        PauseReason.None => Reading,
        PauseReason.NoRegion => NoRegion,
        PauseReason.NoGame => WaitingForGame,
        PauseReason.GameMinimized => PausedGameMinimized,
        PauseReason.GameNotForeground => PausedGameNotForeground,
        PauseReason.ChatCovered => PausedChatCovered,
        PauseReason.RegionOffScreen => PausedRegionOffScreen,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>One short sentence for the snapshot's health.</summary>
    public static string Health(CaptureMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Health switch
        {
            CaptureHealth.Paused => Paused(snapshot.PauseReason),
            CaptureHealth.Degraded => HealthDegraded,
            CaptureHealth.Failing => HealthFailing,
            _ => HealthHealthy,
        };
    }

    /// <summary>The Live view's numbers, e.g. "capture 4 ms · OCR 61 ms · skipped 45/50 (90%) · every 1.5 s · gdi".</summary>
    public static string Stats(CaptureMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var percent = snapshot.FramesCaptured == 0 ? 0 : 100.0 * snapshot.FramesSkipped / snapshot.FramesCaptured;
        return TextTemplate.Format(StatsFormat, CultureInfo.InvariantCulture,
            ("captureMs", snapshot.AvgCaptureMs), ("ocrMs", snapshot.AvgOcrMs),
            ("skipped", snapshot.FramesSkipped), ("captured", snapshot.FramesCaptured), ("skipPercent", percent),
            ("interval", snapshot.CurrentInterval.TotalSeconds), ("backend", snapshot.Backend));
    }

    public static string RegionFollows(int width, int height, string game) =>
        TextTemplate.Format(RegionFollowsFormat, CultureInfo.InvariantCulture, ("width", width), ("height", height), ("game", game));

    public static string RegionFixed(ScreenRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return TextTemplate.Format(RegionFixedFormat, CultureInfo.InvariantCulture,
            ("width", region.Width), ("height", region.Height), ("x", region.X), ("y", region.Y));
    }
}

/// <summary>Maps a capture failure to the sentence the user sees (REQ-CAP-30).</summary>
public static class CaptureErrors
{
    /// <summary>
    /// <see cref="TimeoutException"/> → <see cref="CaptureText.OcrTimedOut"/>; <see cref="CaptureException"/> →
    /// <see cref="CaptureText.ScreenUnreadable"/>; anything else → <see cref="CaptureText.CaptureFailed"/>. Never
    /// returns exception text.
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        TimeoutException => CaptureText.OcrTimedOut,
        CaptureException => CaptureText.ScreenUnreadable,
        _ => CaptureText.CaptureFailed,
    };
}
