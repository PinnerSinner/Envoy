namespace Envoy.Core.Updates;

/// <summary>Which releases an update check considers (REQ-DIST-18).</summary>
public enum UpdateChannel
{
    /// <summary>Releases only.</summary>
    Stable,

    /// <summary>Releases and GitHub prereleases.</summary>
    Beta,
}

/// <summary>How this copy of Envoy was put on the PC.</summary>
public enum InstallKind
{
    /// <summary>Installed with Envoy-win-Setup.exe: Velopack downloads and applies updates.</summary>
    Installed,

    /// <summary>The single-file Envoy.exe (or a developer build): notify only, never writes itself.</summary>
    Portable,
}

/// <summary>Why an update check or download failed. User-facing text comes from <see cref="UpdateText"/>.</summary>
public enum UpdateErrorKind
{
    Offline,
    RateLimited,
    BadResponse,
    DownloadFailed,
    UpdaterFailed,
    Unknown,
}

/// <summary>One update the backend found. <paramref name="Handle"/> is backend-private (the Velopack UpdateInfo).</summary>
public sealed record AvailableUpdate(
    ReleaseVersion Version, string? NotesMarkdown, string ReleasePageUrl,
    string? PortableDownloadUrl, string? InstallerDownloadUrl, object? Handle = null);

/// <summary>A failed check, download or updater start. The message is the kind's name, never response text.</summary>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(UpdateErrorKind kind, Exception? inner = null)
        : base(kind.ToString(), inner) => Kind = kind;

    public UpdateCheckException()
        : this(UpdateErrorKind.Unknown)
    {
    }

    public UpdateCheckException(string message)
        : base(message) => Kind = UpdateErrorKind.Unknown;

    public UpdateCheckException(string message, Exception innerException)
        : base(message, innerException) => Kind = UpdateErrorKind.Unknown;

    public UpdateErrorKind Kind { get; }
}

/// <summary>Where updates come from and how they are applied. Portable: <see cref="GitHubReleaseBackend"/>.</summary>
public interface IUpdateBackend
{
    InstallKind Kind { get; }

    ReleaseVersion CurrentVersion { get; }

    /// <summary>Null when up to date. Throws <see cref="UpdateCheckException"/> for network, HTTP and feed errors; honours ct.</summary>
    Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct);

    /// <summary>Installed only (Portable throws <see cref="NotSupportedException"/>). progress: 0–100.</summary>
    Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken ct);

    /// <summary>Version of a fully downloaded, not yet applied update; null if none. Local, no network.</summary>
    ReleaseVersion? PendingRestart { get; }

    /// <summary>
    /// Starts the updater, which waits for this process to exit, applies the update and relaunches Envoy with
    /// <paramref name="restartArgs"/>. The caller must then exit. Installed only.
    /// </summary>
    void LaunchUpdaterAfterExit(IReadOnlyList<string> restartArgs);
}

/// <summary>The one repository Envoy's update traffic goes to (REQ-DIST-14).</summary>
public static class UpdateEndpoints
{
    public const string Repository = "PinnerSinner/Envoy";
    public const string RepositoryUrl = "https://github.com/PinnerSinner/Envoy";

    /// <summary>The releases list, the fallback for every "release notes" link.</summary>
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    public static string ReleasePage(ReleaseVersion v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return $"{RepositoryUrl}/releases/tag/v{v}";
    }
}

/// <summary>Start-up slot 5: whether a downloaded update is applied before Envoy starts (REQ-DIST-12 b).</summary>
public static class PendingUpdatePolicy
{
    /// <summary>
    /// Set by Velopack's updater on the Envoy it starts after applying (or failing to apply) an update. Velopack's own
    /// auto-apply never re-applies in such a process; neither does slot 5, so a package that fails to apply cannot start
    /// a loop of updater runs (the next start by the user tries again). <c>VelopackApp.Run</c> clears it, so
    /// <c>Program.Main</c> reads it first.
    /// </summary>
    public const string RestartedByUpdaterVariable = "VELOPACK_RESTART";

    public static bool ShouldApplyAtStartup(bool hasPendingPackage, bool safeModeArg, bool shiftHeld)
        => ShouldApplyAtStartup(hasPendingPackage, safeModeArg, shiftHeld, startedByUpdater: false);

    public static bool ShouldApplyAtStartup(bool hasPendingPackage, bool safeModeArg, bool shiftHeld, bool startedByUpdater)
        => hasPendingPackage && !safeModeArg && !shiftHeld && !startedByUpdater;
}

/// <summary>
/// Whether this process is a Velopack-installed Envoy, decided from the file layout alone (REQ-DIST-10). Velopack's
/// locator opens a log file outside the data folder as soon as it is created, so the portable exe, Debug builds and
/// <c>--smoke-test</c> never create one: Program.Main runs <c>VelopackApp</c> and <c>VelopackUpdateBackend</c> creates an
/// <c>UpdateManager</c> only when this says so.
/// </summary>
public static class InstallLayout
{
    /// <summary>The installer's and updater's hook arguments (Velopack's "fast exit hooks", and the Squirrel ones it still accepts).</summary>
    public static bool IsInstallerHook(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 && args[0] is { } first
            && (first.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase) || first.StartsWith("--squirrel-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Velopack's own two layouts: <c>&lt;root&gt;\current\Envoy.exe</c> with <c>&lt;root&gt;\Update.exe</c> beside
    /// <c>current</c>, or an exe anywhere below a <c>current</c> folder whose parent holds <c>Update.exe</c>. Reads
    /// nothing but <paramref name="fileExists"/>; never throws.
    /// </summary>
    public static bool LooksInstalled(string? processPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(processPath)) return false;
        try
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir) && fileExists(Path.GetFullPath(Path.Combine(dir, "..", "Update.exe")))) return true;
            var marker = Path.DirectorySeparatorChar + "current" + Path.DirectorySeparatorChar;
            var at = processPath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return at > 0 && fileExists(Path.Combine(processPath[..at], "Update.exe"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Program.Main: run <c>VelopackApp</c> for an installer hook or an installed copy, else skip it.</summary>
    public static bool ShouldRunVelopack(string? processPath, IReadOnlyList<string> args, Func<string, bool> fileExists)
        => IsInstallerHook(args) || LooksInstalled(processPath, fileExists);
}
