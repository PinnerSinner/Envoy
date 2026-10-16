namespace Envoy.Core.Updates;

/// <summary>
/// Portable and developer builds: notify only (REQ-DIST-15). Checks GitHub Releases through <see cref="UpdateChecker"/>;
/// never downloads, never writes the exe.
/// </summary>
public sealed class GitHubReleaseBackend(UpdateChecker checker, ReleaseVersion current) : IUpdateBackend
{
    public InstallKind Kind => InstallKind.Portable;

    public ReleaseVersion CurrentVersion { get; } = current;

    public ReleaseVersion? PendingRestart => null;

    public async Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        var info = await checker.CheckAsync(CurrentVersion, channel, ct).ConfigureAwait(false);
        if (info?.SemVer is not { } version) return null;
        return new AvailableUpdate(version, info.Notes, info.PageUrl, info.DownloadUrl, info.InstallerUrl);
    }

    public Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken ct) =>
        throw new NotSupportedException("The portable Envoy.exe never downloads updates; it only offers the release page.");

    public void LaunchUpdaterAfterExit(IReadOnlyList<string> restartArgs) =>
        throw new NotSupportedException("The portable Envoy.exe has no updater.");
}
