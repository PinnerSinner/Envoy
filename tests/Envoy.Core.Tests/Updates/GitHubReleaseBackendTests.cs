using System.Net;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Updates;

public class GitHubReleaseBackendTests
{
    private const string Latest = """
        {"tag_name":"v0.5.0","html_url":"https://github.com/PinnerSinner/Envoy/releases/tag/v0.5.0","draft":false,"prerelease":false,
         "body":"## Added\n- Things","assets":[
           {"name":"Envoy-win-Setup.exe","browser_download_url":"https://github.com/PinnerSinner/Envoy/releases/download/v0.5.0/Envoy-win-Setup.exe"},
           {"name":"Envoy.exe","browser_download_url":"https://github.com/PinnerSinner/Envoy/releases/download/v0.5.0/Envoy.exe"}]}
        """;

    [Fact]
    public async Task Maps_update_info_to_available_update()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Latest);
        using var http = handler.Client();
        var backend = new GitHubReleaseBackend(new UpdateChecker(http, UpdateEndpoints.Repository), ReleaseVersion.Parse("0.4.0"));
        Assert.Equal(InstallKind.Portable, backend.Kind);
        Assert.Equal(ReleaseVersion.Parse("0.4.0"), backend.CurrentVersion);

        var update = await backend.CheckAsync(UpdateChannel.Stable, CancellationToken.None);
        Assert.NotNull(update);
        Assert.Equal(ReleaseVersion.Parse("0.5.0"), update.Version);
        Assert.Equal("https://github.com/PinnerSinner/Envoy/releases/tag/v0.5.0", update.ReleasePageUrl);
        Assert.EndsWith("/Envoy.exe", update.PortableDownloadUrl);
        Assert.EndsWith("/Envoy-win-Setup.exe", update.InstallerDownloadUrl);
        Assert.Equal("## Added\n- Things", update.NotesMarkdown);
        Assert.Null(update.Handle);
    }

    [Fact]
    public async Task Up_to_date_maps_to_null()
    {
        using var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Latest).Client();
        var backend = new GitHubReleaseBackend(new UpdateChecker(http, UpdateEndpoints.Repository), ReleaseVersion.Parse("0.5.0"));
        Assert.Null(await backend.CheckAsync(UpdateChannel.Stable, CancellationToken.None));
    }

    [Fact]
    public async Task Download_throws_not_supported()
    {
        using var http = new FakeHttpHandler().Client();
        var backend = new GitHubReleaseBackend(new UpdateChecker(http, UpdateEndpoints.Repository), ReleaseVersion.Parse("0.4.0"));
        var update = new AvailableUpdate(ReleaseVersion.Parse("0.5.0"), null, UpdateEndpoints.ReleasePage(ReleaseVersion.Parse("0.5.0")), null, null);
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.DownloadAsync(update, new Progress<int>(), CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => backend.LaunchUpdaterAfterExit([]));
    }

    [Fact]
    public void PendingRestart_is_null()
    {
        using var http = new FakeHttpHandler().Client();
        Assert.Null(new GitHubReleaseBackend(new UpdateChecker(http, UpdateEndpoints.Repository), ReleaseVersion.Parse("0.4.0")).PendingRestart);
    }
}
