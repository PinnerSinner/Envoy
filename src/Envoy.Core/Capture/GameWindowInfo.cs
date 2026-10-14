using Envoy.Core.Settings;

namespace Envoy.Core.Capture;

/// <summary>
/// What Envoy knows about the running game's window (contract §2.4), published as <c>AppHost.GameWindow</c>. Every
/// rectangle is in physical screen pixels. Record equality is used to raise <c>GameWindowChanged</c> only on change.
/// </summary>
/// <param name="Handle">The game's top-level window.</param>
/// <param name="ClientRect">The client area on screen (what the chat region is relative to).</param>
/// <param name="WindowRect">The whole window including any frame.</param>
/// <param name="Minimized">The window is minimised.</param>
/// <param name="GameForeground">The foreground window belongs to the game.</param>
/// <param name="EnvoyForeground">The foreground window belongs to Envoy.</param>
/// <param name="HasCaption">The window has a title bar (windowed mode rather than borderless).</param>
/// <param name="MonitorBounds">The monitor the window is on.</param>
/// <param name="UniformFrameStreak">Consecutive blank captured frames, 0..<see cref="BlankFrames.Threshold"/> (REQ-CAP-40).</param>
public sealed record GameWindowInfo(
    IntPtr Handle, ScreenRegion ClientRect, ScreenRegion WindowRect,
    bool Minimized, bool GameForeground, bool EnvoyForeground,
    bool HasCaption, ScreenRegion MonitorBounds,
    int UniformFrameStreak);
