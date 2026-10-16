using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Updates;

public class UpdateViewBuilderTests
{
    private static readonly ReleaseVersion Current = ReleaseVersion.Parse("1.0.0");
    private static readonly ReleaseVersion Next = ReleaseVersion.Parse("1.1.0");

    private static readonly AvailableUpdate Update = new(Next, "## Added\n- [Thing](x)", UpdateEndpoints.ReleasePage(Next),
        "https://github.com/PinnerSinner/Envoy/releases/download/v1.1.0/Envoy.exe",
        "https://github.com/PinnerSinner/Envoy/releases/download/v1.1.0/Envoy-win-Setup.exe");

    public static TheoryData<string, UpdateSnapshot, bool, string, UpdateAction, UpdateAction, bool> Rows => new()
    {
        { "installed available", S(InstallKind.Installed, UpdateState.Available), true, UpdateText.Available(Next, Current), UpdateAction.UpdateNow, UpdateAction.OpenReleasePage, true },
        { "installed deferred", S(InstallKind.Installed, UpdateState.Available) with { DeferredForGame = true }, true, UpdateText.AvailableAfterGame(Next), UpdateAction.UpdateNow, UpdateAction.OpenReleasePage, true },
        { "downloading", S(InstallKind.Installed, UpdateState.Downloading) with { Percent = 42 }, true, UpdateText.Downloading(Next, 42), UpdateAction.None, UpdateAction.OpenReleasePage, false },
        { "ready", S(InstallKind.Installed, UpdateState.ReadyToRestart), true, UpdateText.Ready(Next), UpdateAction.RestartToUpdate, UpdateAction.OpenReleasePage, true },
        { "portable available", S(InstallKind.Portable, UpdateState.Available), true, UpdateText.Available(Next, Current), UpdateAction.OpenReleasePage, UpdateAction.GetInstaller, true },
        { "check failed", S(InstallKind.Portable, UpdateState.Failed) with { Error = UpdateErrorKind.RateLimited }, true, UpdateText.CheckFailed(UpdateErrorKind.RateLimited), UpdateAction.Retry, UpdateAction.None, false },
        { "download failed", S(InstallKind.Installed, UpdateState.Failed) with { Error = UpdateErrorKind.Offline, FailedWhileDownloading = true }, true, UpdateText.DownloadFailed(UpdateErrorKind.Offline), UpdateAction.Retry, UpdateAction.None, false },
        { "up to date", S(InstallKind.Installed, UpdateState.UpToDate) with { Available = null }, false, "", UpdateAction.None, UpdateAction.None, false },
        { "idle", S(InstallKind.Installed, UpdateState.Idle) with { Available = null }, false, "", UpdateAction.None, UpdateAction.None, false },
        { "checking", S(InstallKind.Portable, UpdateState.Checking) with { Available = null }, false, "", UpdateAction.None, UpdateAction.None, false },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Banner_for_each_state(string row, UpdateSnapshot s, bool banner, string headline, UpdateAction primary, UpdateAction secondary, bool canSkip)
    {
        var view = UpdateViewBuilder.Build(s, zone: TimeZoneInfo.Utc);
        Assert.True(banner == view.ShowBanner, row);
        Assert.Equal(headline, view.Headline);
        Assert.Equal(primary, view.Primary);
        Assert.Equal(secondary, view.Secondary);
        Assert.Equal(canSkip, view.CanSkip);
        Assert.Equal(primary != UpdateAction.None, view.PrimaryLabel.Length > 0);
        Assert.Equal(secondary != UpdateAction.None, view.SecondaryLabel.Length > 0);
        Assert.Equal(view.HasPrimary, primary != UpdateAction.None);
        Assert.False(string.IsNullOrEmpty(view.StatusLine));
    }

    [Fact]
    public void Labels_and_notes()
    {
        var portable = UpdateViewBuilder.Build(S(InstallKind.Portable, UpdateState.Available));
        Assert.Equal(UpdateText.DownloadLabel, portable.PrimaryLabel);
        Assert.Equal("Get the installer", portable.SecondaryLabel);
        Assert.Equal("Added\n• Thing", portable.Notes);
        Assert.True(portable.HasNotes);
        var installed = UpdateViewBuilder.Build(S(InstallKind.Installed, UpdateState.Available));
        Assert.Equal("Update now", installed.PrimaryLabel);
        Assert.Equal(UpdateText.ReleaseNotesLabel, installed.SecondaryLabel);
        Assert.Equal("Restart to update", UpdateViewBuilder.Build(S(InstallKind.Installed, UpdateState.ReadyToRestart)).PrimaryLabel);
    }

    [Fact]
    public void JustUpdated_shown_only_without_active_banner()
    {
        var idle = UpdateViewBuilder.Build(S(InstallKind.Installed, UpdateState.UpToDate) with { Available = null }, Current);
        Assert.True(idle.ShowBanner);
        Assert.Equal(UpdateText.UpdatedTo(Current), idle.Headline);
        Assert.Equal(UpdateAction.OpenReleasePage, idle.Primary);
        Assert.Equal(UpdateText.WhatsNewLabel, idle.PrimaryLabel);
        Assert.Equal(UpdateAction.DismissNotice, idle.Secondary);
        Assert.False(idle.CanSkip);

        var ready = UpdateViewBuilder.Build(S(InstallKind.Installed, UpdateState.ReadyToRestart), Current);
        Assert.Equal(UpdateText.Ready(Next), ready.Headline);
    }

    private static UpdateSnapshot S(InstallKind kind, UpdateState state) =>
        new(kind, Current, state, Update, 0, false, null, false, true, DateTimeOffset.UnixEpoch, UpdateChannel.Stable);
}
