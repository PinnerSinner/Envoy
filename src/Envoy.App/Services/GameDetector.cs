using System.Diagnostics;
using Envoy.Core.Games;

namespace Envoy.App.Services;

public sealed record RunningGame(GameProfile Profile, int ProcessId, IntPtr MainWindow);

/// <summary>Finds which Age game (if any) is running, so Envoy can switch theme, region and pause capture.</summary>
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
                if (p is not null) return new RunningGame(game, p.Id, p.MainWindowHandle);
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        return null;
    }
}
