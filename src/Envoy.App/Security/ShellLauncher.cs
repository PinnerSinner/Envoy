#pragma warning disable RS0030 // The repository's only sanctioned process launches (docs/specs/08a-quality-gates.md §3.3).
using System.ComponentModel;
using System.Diagnostics;
using Envoy.Core.Security;

namespace Envoy.App.Security;

/// <summary>
/// The only code in Envoy that starts processes (contract §5). Each member validates its input with the Core
/// <see cref="LaunchTarget"/> rules first and returns false, opening nothing, when validation or the start fails.
/// Never add a member here without a tested <see cref="LaunchTarget"/> validator.
/// </summary>
public static class ShellLauncher
{
    /// <summary>
    /// Opens a web URL, a Windows Settings page, or an existing non-executable file or folder under
    /// <paramref name="allowedFolder"/> with the shell (<c>UseShellExecute=true</c>).
    /// </summary>
    public static bool TryOpen(string target, string allowedFolder)
    {
        var kind = LaunchTarget.Classify(target, allowedFolder);
        if (kind == LaunchKind.Rejected) return false;
        if (kind == LaunchKind.DataPath && !File.Exists(target) && !Directory.Exists(target)) return false;
        return Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <summary>
    /// Installs the Windows OCR pack for an Envoy language code with an elevated Windows PowerShell. Returns false when
    /// the code is unknown or unsafe, or the user declines the admin prompt.
    /// </summary>
    public static bool InstallOcrPack(string languageTag)
    {
        var arguments = LaunchTarget.OcrInstallArguments(languageTag);
        if (arguments is null) return false;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return Start(new ProcessStartInfo(powershell, arguments) { UseShellExecute = true, Verb = "runas" });
    }

    /// <summary>Starts a new copy of Envoy with only the switches <paramref name="flags"/> maps to.</summary>
    public static bool RestartSelf(RestartFlags flags)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        IReadOnlyList<string> arguments;
        try
        {
            arguments = LaunchTarget.RestartArguments(flags);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Start(info);
    }

    /// <summary>Shows an existing file selected in Explorer (for user-chosen export paths).</summary>
    public static bool RevealFile(string path)
    {
        var arguments = LaunchTarget.RevealArguments(path);
        if (arguments is null || !File.Exists(path)) return false;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        return Start(new ProcessStartInfo(explorer, arguments) { UseShellExecute = false });
    }

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            // With UseShellExecute the shell may hand the target to an existing process and return null; that's success.
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
            // Win32Exception includes "the user declined the UAC prompt" (ERROR_CANCELLED).
            return false;
        }
    }
}
