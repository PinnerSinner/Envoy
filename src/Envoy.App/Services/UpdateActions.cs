using Envoy.Core.Diagnostics;
using Envoy.Core.Security;
using Envoy.Core.Updates;

namespace Envoy.App.Services;

/// <summary>
/// Runs an <see cref="UpdateAction"/>: the banner buttons, the tray item and (later) 07a's balloon all come here. Every
/// URL passes <see cref="ReleaseUrlPolicy"/> for <see cref="UpdateEndpoints.Repository"/> before it reaches
/// <see cref="IShell.OpenUrl"/> (REQ-DIST-14).
/// </summary>
internal static class UpdateActions
{
    public static void Run(UpdateAction action, AppHost host, IShell shell, ILog log)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(log);
        var updates = host.Updates;
        var s = updates.Snapshot;
        switch (action)
        {
            case UpdateAction.UpdateNow or UpdateAction.Retry:
                var download = s.FailedWhileDownloading || (s.Kind == InstallKind.Installed && s.State == UpdateState.Available);
                (download ? updates.DownloadAsync(host.UpdatesToken) : updates.CheckAsync(manual: true, host.UpdatesToken))
                    .Forget(log, download ? "update download" : "update check");
                break;
            case UpdateAction.CheckNow:
                updates.CheckAsync(manual: true, host.UpdatesToken).Forget(log, "update check");
                break;
            case UpdateAction.RestartToUpdate:
                shell.RestartToUpdate();
                break;
            case UpdateAction.OpenReleasePage:
                var notice = host.JustUpdatedTo is { } updated && s.Available is null ? UpdateEndpoints.ReleasePage(updated) : null;
                shell.OpenUrl(Trusted(s.Available?.ReleasePageUrl) ?? Trusted(notice) ?? UpdateEndpoints.ReleasesUrl);
                break;
            case UpdateAction.GetInstaller:
                shell.OpenUrl(Trusted(s.Available?.InstallerDownloadUrl) ?? Trusted(s.Available?.ReleasePageUrl) ?? UpdateEndpoints.ReleasesUrl);
                break;
            case UpdateAction.Skip:
                host.SkipUpdate();
                break;
            case UpdateAction.DismissNotice:
                host.DismissJustUpdated();
                break;
        }
    }

    private static string? Trusted(string? url) => ReleaseUrlPolicy.IsTrusted(url, UpdateEndpoints.Repository) ? url : null;
}
