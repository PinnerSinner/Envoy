using System.Diagnostics.CodeAnalysis;
using Envoy.Core.Languages;

namespace Envoy.Core.Security;

/// <summary>What <see cref="LaunchTarget.Classify"/> decided a launch target is.</summary>
public enum LaunchKind
{
    /// <summary>Never opened.</summary>
    Rejected,
    /// <summary>An absolute http/https URL with a host and no user-info.</summary>
    WebUrl,
    /// <summary>A Windows Settings page (<c>ms-settings:</c>).</summary>
    SettingsUri,
    /// <summary>A non-executable file or folder inside Envoy's data folder.</summary>
    DataPath,
}

/// <summary>How Envoy restarts itself. Each flag maps to exactly one command-line switch.</summary>
[Flags]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The name RestartFlags is fixed by the integration contract (00-integration.md §4, §5).")]
public enum RestartFlags
{
    None = 0,
    SafeMode = 1,
    Minimized = 2,
}

/// <summary>
/// The validation rules behind the App's <c>Security/ShellLauncher</c>, the only code that starts processes
/// (docs/specs/08a-quality-gates.md §3.3). Pure, static and thread-safe.
/// </summary>
public static class LaunchTarget
{
    /// <summary>Extensions Windows would run rather than open. A data path with one of these is rejected.</summary>
    public static IReadOnlySet<string> ExecutableExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".scr", ".lnk", ".url", ".hta", ".cpl", ".reg", ".pif", ".appref-ms",
    };

    private const string SettingsScheme = "ms-settings:";

    /// <summary>
    /// <see cref="LaunchKind.WebUrl"/>: absolute http/https URI with a host and no user-info.
    /// <see cref="LaunchKind.SettingsUri"/>: the <c>ms-settings:</c> scheme.
    /// <see cref="LaunchKind.DataPath"/>: a fully-qualified path equal to <paramref name="allowedFolder"/> or under it
    /// (after <see cref="Path.GetFullPath(string)"/>; ordinal-ignore-case prefix plus separator) whose extension is not
    /// executable. Everything else, including UNC paths, <c>file:</c>, <c>javascript:</c>, <c>shell:</c>,
    /// <c>search-ms:</c>, <c>ms-msdt:</c> and relative paths, is <see cref="LaunchKind.Rejected"/>. Paths are
    /// classified before URIs.
    /// </summary>
    public static LaunchKind Classify(string? target, string allowedFolder)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Any(char.IsControl)) return LaunchKind.Rejected;
        if (IsUnc(target)) return LaunchKind.Rejected;

        if (Path.IsPathFullyQualified(target))
            return IsDataPath(target, allowedFolder) ? LaunchKind.DataPath : LaunchKind.Rejected;

        if (target.StartsWith(SettingsScheme, StringComparison.OrdinalIgnoreCase))
            return target.AsSpan(SettingsScheme.Length).ToString().All(IsSettingsChar) ? LaunchKind.SettingsUri : LaunchKind.Rejected;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.Host.Length > 0
            && uri.UserInfo.Length == 0
            && !target.Any(char.IsWhiteSpace))
            return LaunchKind.WebUrl;

        return LaunchKind.Rejected;
    }

    /// <summary>
    /// <see cref="RestartFlags.SafeMode"/> → <c>--safe-mode</c>, <see cref="RestartFlags.Minimized"/> →
    /// <c>--minimized</c>, in that order. Public so 06's restart-to-update reuses it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Undefined bits are set.</exception>
    public static IReadOnlyList<string> RestartArguments(RestartFlags flags)
    {
        const RestartFlags known = RestartFlags.SafeMode | RestartFlags.Minimized;
        if ((flags & ~known) != 0) throw new ArgumentOutOfRangeException(nameof(flags), flags, "Unknown restart flag.");
        var args = new List<string>(2);
        if (flags.HasFlag(RestartFlags.SafeMode)) args.Add("--safe-mode");
        if (flags.HasFlag(RestartFlags.Minimized)) args.Add("--minimized");
        return args;
    }

    /// <summary>
    /// <c>/select,"&lt;full path&gt;"</c> for Explorer, for a fully-qualified, non-UNC path with no <c>"</c> and no
    /// control characters; otherwise null.
    /// </summary>
    public static string? RevealArguments(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('"', StringComparison.Ordinal) || path.Any(char.IsControl)) return null;
        if (IsUnc(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            return $"/select,\"{Path.GetFullPath(path)}\"";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The elevated PowerShell arguments from <see cref="OcrPackInstall.PowerShellArguments"/> when the code is in
    /// <see cref="LanguageCatalog"/> with a safe tag; otherwise null.
    /// </summary>
    public static string? OcrInstallArguments(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return null;
        try
        {
            return OcrPackInstall.PowerShellArguments(languageCode);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsUnc(string value) =>
        value.Length >= 2 && value[0] is '\\' or '/' && value[1] is '\\' or '/';

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    private static bool IsSettingsChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' or '?' or '=' or '&';

    private static bool IsDataPath(string target, string allowedFolder)
    {
        if (string.IsNullOrWhiteSpace(allowedFolder) || IsUnc(allowedFolder) || !Path.IsPathFullyQualified(allowedFolder)) return false;
        string full, folder;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedFolder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // An alternate data stream ("a.json:x.exe") or anything else with a colon past the drive is not a plain file.
        if (full.Length > 2 && full[2..].Contains(':', StringComparison.Ordinal)) return false;

        var inside = string.Equals(full, folder, StringComparison.OrdinalIgnoreCase)
            || (full.Length > folder.Length
                && full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && IsSeparator(full[folder.Length]));
        if (!inside) return false;

        // Windows ignores trailing dots and spaces in names, so "x.exe. " would still run as x.exe.
        var extension = Path.GetExtension(full.TrimEnd('.', ' '));
        return !ExecutableExtensions.Contains(extension);
    }
}
