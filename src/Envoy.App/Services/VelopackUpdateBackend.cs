using System.Net;
using System.Net.Http;
using System.Text.Json;
using Envoy.Core.Diagnostics;
using Envoy.Core.Updates;
using Velopack;
using Velopack.Sources;
using VeloUpdateInfo = Velopack.UpdateInfo;

namespace Envoy.App.Services;

/// <summary>
/// Installed copies (Envoy-win-Setup.exe): Velopack checks GitHub Releases for <see cref="UpdateEndpoints.Repository"/>,
/// downloads (delta when possible, checksum-verified) and applies on restart (REQ-DIST-15). The only file that creates an
/// <c>UpdateManager</c> (typecheck UPD1). Names no network type: Velopack does the transfers.
/// </summary>
public sealed class VelopackUpdateBackend : IUpdateBackend
{
    /// <summary>A local folder of packed releases to use instead of GitHub, for testing updates (REQ-DIST-30). URLs are ignored.</summary>
    public const string FeedVariable = "ENVOY_UPDATE_FEED";

    private readonly UpdateManager _local;
    private readonly ILog _log;

    private VelopackUpdateBackend(UpdateManager local, ILog log)
    {
        _local = local;
        _log = log;
        CurrentVersion = ReleaseVersion.FromInformational(local.CurrentVersion?.ToNormalizedString(), null);
    }

    public InstallKind Kind => InstallKind.Installed;

    public ReleaseVersion CurrentVersion { get; }

    /// <summary>
    /// The version of a downloaded, not yet applied package. Reads the local packages folder only; never throws (start-up
    /// slot 5 reads it before anything else is running).
    /// </summary>
    public ReleaseVersion? PendingRestart
    {
        get
        {
            try
            {
                return _local.UpdatePendingRestart is { } asset && ReleaseVersion.TryParse(asset.Version.ToNormalizedString(), out var v) ? v : null;
            }
            catch (Exception ex)
            {
                _log.Warn("Couldn't read the downloaded update", ex);
                return null;
            }
        }
    }

    /// <summary>Null when this copy isn't Velopack-installed (portable exe, dotnet run, smoke test). Never throws.</summary>
    public static VelopackUpdateBackend? TryCreate(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            var manager = CreateManager(UpdateChannel.Stable, log);
            return manager.IsInstalled ? new VelopackUpdateBackend(manager, log) : null;
        }
        catch (Exception ex)
        {
            log.Write(LogLevel.Debug, "Not a Velopack install", ex);
            return null;
        }
    }

    public async Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var manager = CreateManager(channel, _log);
        VeloUpdateInfo? info;
        try
        {
            // Velopack's check takes no token: a cancelled check finishes in the background and is discarded.
            info = await manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UpdateCheckException(Classify(ex, UpdateErrorKind.Unknown), ex);
        }
        if (info?.TargetFullRelease is not { } target) return null;
        if (!ReleaseVersion.TryParse(target.Version.ToNormalizedString(), out var version))
            throw new UpdateCheckException(UpdateErrorKind.BadResponse);
        return new AvailableUpdate(version, target.NotesMarkdown, UpdateEndpoints.ReleasePage(version), null, null,
            new Handle(info, manager));
    }

    public async Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(progress);
        if (update.Handle is not Handle handle) throw new UpdateCheckException(UpdateErrorKind.DownloadFailed);
        try
        {
            // Velopack locks, prefers deltas, verifies checksums and removes partial files itself.
            await handle.Manager.DownloadUpdatesAsync(handle.Info, progress.Report, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UpdateCheckException(Classify(ex, UpdateErrorKind.DownloadFailed), ex);
        }
    }

    public void LaunchUpdaterAfterExit(IReadOnlyList<string> restartArgs)
    {
        ArgumentNullException.ThrowIfNull(restartArgs);
        try
        {
            // Velopack starts its updater, which waits (up to 60 s) for this process to exit, applies and relaunches.
            _local.WaitExitThenApplyUpdates(_local.UpdatePendingRestart, silent: false, restart: true, [.. restartArgs]);
        }
        catch (Exception ex)
        {
            throw new UpdateCheckException(UpdateErrorKind.UpdaterFailed, ex);
        }
    }

    /// <summary>One manager per call, because the channel can change. Never the hosted Velopack Flow source.</summary>
    private static UpdateManager CreateManager(UpdateChannel channel, ILog log)
    {
        IUpdateSource source = new GithubSource(UpdateEndpoints.RepositoryUrl, accessToken: null, prerelease: channel == UpdateChannel.Beta);
        var feed = Environment.GetEnvironmentVariable(FeedVariable);
        if (!string.IsNullOrWhiteSpace(feed))
        {
            if (IsLocalFolder(feed)) source = new SimpleFileSource(new DirectoryInfo(feed));
            else log.Warn("Ignoring " + FeedVariable + ": only an existing local folder is allowed");
        }
        return new UpdateManager(source, new UpdateOptions { AllowVersionDowngrade = false });
    }

    private static bool IsLocalFolder(string path) =>
        Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains("://", StringComparison.Ordinal) && Directory.Exists(path);

    private static UpdateErrorKind Classify(Exception ex, UpdateErrorKind fallback)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }:
                    return UpdateErrorKind.RateLimited;
                case HttpRequestException or IOException or TimeoutException or OperationCanceledException:
                    return UpdateErrorKind.Offline;
                case JsonException or FormatException or InvalidDataException:
                    return UpdateErrorKind.BadResponse;
            }
        }
        return fallback;
    }

    /// <summary>What <see cref="AvailableUpdate.Handle"/> holds: Velopack's answer and the manager (channel) that gave it.</summary>
    private sealed record Handle(VeloUpdateInfo Info, UpdateManager Manager);
}
