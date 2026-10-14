using System.Runtime.InteropServices;
using Envoy.Core.Diagnostics;
using Envoy.Core.Settings;

namespace Envoy.App.Services;

/// <summary>Creates the one <see cref="LogHub"/> at start-up (contract §4 step 2) and writes the session header (REQ-REL-9).</summary>
internal static class LogBootstrap
{
    public const string HeaderMessage = "Envoy started";

    /// <summary>
    /// A hub with the 500-entry memory ring and, when <paramref name="dataFolder"/> is given, the rolling
    /// <c>logs\envoy.log</c>. The user-profile path is aliased to <c>%USERPROFILE%</c>.
    /// </summary>
    public static (LogHub Hub, MemoryLogSink Recent, RollingFileSink? File) Create(string? dataFolder, TimeProvider? clock = null)
    {
        var hub = new LogHub(clock);
        AddProfileAlias(hub);
        var recent = new MemoryLogSink(500);
        hub.AddSink(recent);
        RollingFileSink? file = null;
        if (dataFolder is not null)
        {
            file = new RollingFileSink(Path.Combine(dataFolder, DataInventory.LogsFolderName), clock: clock);
            hub.AddSink(file);
        }
        return (hub, recent, file);
    }

    public static void AddProfileAlias(LogHub hub)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) hub.Redactor.AddPathAlias(profile, "%USERPROFILE%");
    }

    /// <summary>One Info entry per session: version, OS, runtime, architecture, safe mode, settings load. No user or machine name.</summary>
    public static void WriteHeader(LogHub hub, SafeModeReason? safeMode, SettingsLoadResult? load)
    {
        hub.For("App").Info(HeaderMessage,
            LogField.Of("version", AppHost.CurrentVersion.ToString()),
            LogField.Of("os", RuntimeInformation.OSDescription),
            LogField.Of("runtime", RuntimeInformation.FrameworkDescription),
            LogField.Of("arch", RuntimeInformation.ProcessArchitecture),
            LogField.Of("safeMode", safeMode?.ToString() ?? "off"),
            LogField.Of("settings", load?.Status.ToString() ?? "n/a"));
    }
}
