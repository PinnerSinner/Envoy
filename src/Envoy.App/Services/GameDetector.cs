using System.Diagnostics;
using System.Runtime.InteropServices;
using Envoy.App.Interop;
using Envoy.Core.Games;

namespace Envoy.App.Services;

public sealed record RunningGame(GameProfile Profile, int ProcessId, IntPtr MainWindow);

/// <summary>
/// Finds which Age game (if any) is running, so Envoy can switch theme, region and pause capture. It only lists
/// processes by name and asks user32 about their top-level windows; it never opens the game process.
/// </summary>
public static class GameDetector
{
    public static RunningGame? Find()
    {
        var known = GameCatalog.All.SelectMany(g => g.ProcessNames.Select(n => (Name: n, Game: g))).ToList();
        foreach (var (name, game) in known)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            try
            {
                var p = processes.FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero) ?? processes.FirstOrDefault();
                if (p is null) continue;
                var window = p.MainWindowHandle != IntPtr.Zero ? p.MainWindowHandle : FindMainWindow(p.Id);
                return new RunningGame(game, p.Id, window);
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        return null;
    }

    /// <summary>
    /// The largest visible, unowned top-level window of <paramref name="processId"/>, or zero (spec 05). Used when the
    /// process reports no main window, or the one it reported (a launcher or splash) has closed.
    /// </summary>
    public static unsafe IntPtr FindMainWindow(int processId)
    {
        if (processId <= 0) return IntPtr.Zero;
        var search = new WindowSearch { ProcessId = (uint)processId };
        _ = Native.EnumWindows(&OnWindow, (IntPtr)(&search)); // false also when the callback stops early
        return search.Best;
    }

    private struct WindowSearch
    {
        public uint ProcessId;
        public IntPtr Best;
        public long BestArea;
    }

    /// <summary>EnumWindows callback: a static function pointer, so no delegate has to be kept alive.</summary>
    [UnmanagedCallersOnly]
    private static unsafe int OnWindow(IntPtr hwnd, IntPtr state)
    {
        var search = (WindowSearch*)state;
        _ = Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != search->ProcessId || !Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero)
            return 1;
        if (!Native.GetWindowRect(hwnd, out var r)) return 1;
        var area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
        if (area > search->BestArea)
        {
            search->BestArea = area;
            search->Best = hwnd;
        }
        return 1;
    }
}
