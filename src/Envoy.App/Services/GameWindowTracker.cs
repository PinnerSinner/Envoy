using Envoy.App.Interop;
using Envoy.Core.Capture;
using Envoy.Core.Settings;

namespace Envoy.App.Services;

/// <summary>
/// The app's only game-window probe (contract §2.4): user32 window queries in physical pixels, never anything that
/// touches the game process. Stateless and thread-safe; the watcher's loop and AppHost's 1 s timer both call it.
/// </summary>
public static class GameWindowTracker
{
    /// <summary>
    /// The game window's state, or null when there is no game or its window is gone. The window is
    /// <see cref="RunningGame.MainWindow"/> while it exists, else <see cref="GameDetector.FindMainWindow"/>.
    /// </summary>
    public static GameWindowInfo? Observe(RunningGame? game, int uniformFrameStreak)
    {
        if (game is null) return null;
        var hwnd = game.MainWindow != IntPtr.Zero && Native.IsWindow(game.MainWindow) ? game.MainWindow : GameDetector.FindMainWindow(game.ProcessId);
        if (hwnd == IntPtr.Zero) return null;

        if (!Native.GetClientRect(hwnd, out var client)) return null;
        var origin = new Native.POINT();
        if (!Native.ClientToScreen(hwnd, ref origin)) return null;
        var clientRect = new ScreenRegion(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        var windowRect = Native.GetWindowRect(hwnd, out var w) ? ToRegion(w) : clientRect;

        var foreground = Native.GetForegroundWindow();
        uint foregroundPid = 0;
        if (foreground != IntPtr.Zero) _ = Native.GetWindowThreadProcessId(foreground, out foregroundPid);
        var gameForeground = foreground != IntPtr.Zero && (foreground == hwnd || foregroundPid == (uint)game.ProcessId);
        var envoyForeground = foreground != IntPtr.Zero && foregroundPid == (uint)Environment.ProcessId;

        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        var hasCaption = (style & Native.WS_CAPTION) == Native.WS_CAPTION;

        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        var monitorBounds = monitor != IntPtr.Zero && Native.GetMonitorInfo(monitor, ref info) ? ToRegion(info.rcMonitor) : windowRect;

        return new GameWindowInfo(hwnd, clientRect, windowRect, Native.IsIconic(hwnd), gameForeground, envoyForeground,
            hasCaption, monitorBounds, Math.Clamp(uniformFrameStreak, 0, BlankFrames.Threshold));
    }

    /// <summary>
    /// Who owns the top-level window at each covered-check point of <paramref name="region"/> (REQ-CAP-10): the game
    /// (checked first: its main window, or any other top-level window of process <paramref name="gameProcessId"/>, such
    /// as an owned popup or dialog), Envoy, or another window.
    /// </summary>
    public static IReadOnlyList<SampleOwner> Sample(ScreenRegion region, IntPtr gameWindow, int gameProcessId)
    {
        ArgumentNullException.ThrowIfNull(region);
        var points = RegionMath.SamplePoints(region);
        var owners = new SampleOwner[points.Count];
        var self = (uint)Environment.ProcessId;
        for (var i = 0; i < points.Count; i++)
        {
            var hit = Native.WindowFromPoint(new Native.POINT { X = points[i].X, Y = points[i].Y });
            var root = hit == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hit, Native.GA_ROOT);
            if (root == IntPtr.Zero) root = hit;
            if (root != IntPtr.Zero && root == gameWindow)
            {
                owners[i] = SampleOwner.Game;
                continue;
            }
            uint pid = 0;
            if (root != IntPtr.Zero) _ = Native.GetWindowThreadProcessId(root, out pid);
            owners[i] = root == IntPtr.Zero || pid == 0 ? SampleOwner.Other
                : gameProcessId > 0 && pid == (uint)gameProcessId ? SampleOwner.Game
                : pid == self ? SampleOwner.Envoy
                : SampleOwner.Other;
        }
        return owners;
    }

    /// <summary>True when <paramref name="region"/> intersects at least one monitor (REQ-CAP-5).</summary>
    public static bool IsOnAnyMonitor(ScreenRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var rect = new Native.RECT { Left = region.X, Top = region.Y, Right = region.X + region.Width, Bottom = region.Y + region.Height };
        return Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    /// <summary>True when the calling thread is per-monitor DPI aware (v2), so every coordinate is in physical pixels (REQ-CAP-34).</summary>
    public static bool IsThreadPerMonitorV2() =>
        Native.AreDpiAwarenessContextsEqual(Native.GetThreadDpiAwarenessContext(), Native.DpiAwarenessContextPerMonitorV2);

    private static ScreenRegion ToRegion(Native.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
