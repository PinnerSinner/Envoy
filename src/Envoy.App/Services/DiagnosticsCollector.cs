using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Envoy.App.Interop;
using Envoy.Core.Diagnostics;
using Envoy.Core.Hotkeys;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Forms = System.Windows.Forms;

namespace Envoy.App.Services;

/// <summary>Gathers the machine and app facts for the diagnostics report (REQ-REL-23). Never names a window title or a position.</summary>
internal static class DiagnosticsCollector
{
    public static DiagnosticsSnapshot Collect(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var settings = host.Settings;
        return new DiagnosticsSnapshot
        {
            AppVersion = AppHost.CurrentVersion.ToString(),
            InformationalVersion = typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Runtime = RuntimeInformation.FrameworkDescription,
            OsDescription = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Culture = CultureInfo.CurrentUICulture.Name,
            Monitors = Monitors(host),
            Game = host.Game?.Profile.Name,
            CaptureState = host.Watcher.State.ToString(),
            RegionSize = host.Watcher.CurrentRegion is { } region ? SettingsRedaction.Size(region) : null,
            OcrInstalled = OcrInstalled(host),
            OcrSelected = [.. settings.Capture.OcrLanguages],
            Providers = Providers(settings),
            ProviderStatus = host.ProviderStatus,
            TranslationHealth = host.TranslationHealth.Snapshot(host.ActiveProviderIds),
            Hotkeys = [.. host.HotkeyResults.Where(r => r.Status != HotkeyStatus.Registered).Select(r => new HotkeyLine(r.ActionName, r.Combo, r.Status.ToString()))],
            SafeMode = host.SafeMode,
            SettingsLoad = host.SettingsLoad,
            LastCrash = CrashMarker.TryRead(host.CrashMarkerPath),
            Uptime = host.Uptime,
            DroppedLogWrites = host.Log.DroppedWrites,
        };
    }

    /// <summary>
    /// "Export diagnostics…" (REQ-REL-24): report, redacted settings, the diagnostic log files and the crash marker.
    /// </summary>
    /// <exception cref="IOException">The zip could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is read-only.</exception>
    public static void ExportBundle(AppHost host, string zipPath)
    {
        ArgumentNullException.ThrowIfNull(host);
        var report = host.BuildDiagnosticsText(Collect(host));
        var logs = Directory.Exists(host.LogsFolder)
            ? Directory.GetFiles(host.LogsFolder).Where(f => RollingFileSink.IsLogFileName(Path.GetFileName(f)))
            : [];
        host.Log.Flush();
        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        DiagnosticsBundle.Write(stream, report, host.Settings, logs, host.CrashMarkerPath);
    }

    private static List<ProviderLine> Providers(AppSettings settings)
    {
        var t = settings.Translation;
        return [.. new[] { t.Primary }.Concat(t.Fallbacks).Distinct(StringComparer.Ordinal).Select(id => new ProviderLine(id, KeySet(settings, id)))];
    }

    private static bool KeySet(AppSettings settings, string providerId) => providerId switch
    {
        ProviderCatalog.DeepL => SettingsRedaction.HasSecret(settings, nameof(TranslationSettings.DeepLKey)),
        ProviderCatalog.Claude => SettingsRedaction.HasSecret(settings, nameof(TranslationSettings.ClaudeKey)),
        ProviderCatalog.LibreTranslate => SettingsRedaction.HasSecret(settings, nameof(TranslationSettings.LibreTranslateKey)),
        ProviderCatalog.Aws => SettingsRedaction.HasSecret(settings, nameof(TranslationSettings.AwsAccessKeyId))
                               && SettingsRedaction.HasSecret(settings, nameof(TranslationSettings.AwsSecretAccessKey)),
        _ => false,
    };

    private static List<string> OcrInstalled(AppHost host)
    {
        try
        {
            return [.. OcrService.InstalledLanguages().Select(l => l.WindowsTag)];
        }
        catch (Exception ex)
        {
            host.Log.For("Diagnostics").Warn("Listing OCR languages failed", ex);
            return [];
        }
    }

    private static List<MonitorInfo> Monitors(AppHost host)
    {
        try
        {
            return [.. Forms.Screen.AllScreens.Select(s =>
            {
                var b = s.Bounds;
                var center = new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 };
                var scale = Native.ScaleOf(Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST));
                return new MonitorInfo(s.DeviceName, s.Primary, b.X, b.Y, b.Width, b.Height, scale);
            })];
        }
        catch (Exception ex)
        {
            host.Log.For("Diagnostics").Warn("Listing monitors failed", ex);
            return [];
        }
    }
}
