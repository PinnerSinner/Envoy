using System.Net;
using System.Text;
using Envoy.Core.Security;
using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Security;

public class UpdateUrlTests
{
    private const string Page = "https://github.com/PinnerSinner/Envoy/releases/tag/v1.0.0";
    private const string Download = "https://github.com/PinnerSinner/Envoy/releases/download/v1.0.0/Envoy.exe";

    private static string Release(string page, string download) => $$"""
        {"tag_name":"v1.0.0","html_url":"{{page.Replace("\\", "\\\\", StringComparison.Ordinal)}}","draft":false,"prerelease":false,
         "body":"Notes","assets":[{"name":"Envoy.exe","browser_download_url":"{{download}}"}]}
        """;

    [Fact]
    public void Genuine_release_urls_are_kept()
    {
        var info = UpdateChecker.Parse(Release(Page, Download))!;
        Assert.Equal(Page, info.PageUrl);
        Assert.Equal(Download, info.DownloadUrl);
        Assert.True(ReleaseUrlPolicy.IsTrusted(Page, "pinnersinner/envoy"));
        Assert.True(ReleaseUrlPolicy.IsTrusted(Download, "pinnersinner/envoy"));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\evil\share\x.exe")]
    [InlineData("http://github.com/o/r/releases/tag/v1")]
    [InlineData("https://github.com.evil.example/o/r/releases/x")]
    [InlineData("https://user@github.com/o/r/releases/x")]
    [InlineData("https://github.com:8443/o/r/releases/x")]
    [InlineData("https://github.com/o/r/archive/x")]
    [InlineData("https://github.com/o/r/releases")]
    [InlineData("")]
    public void Untrusted_page_url_rejects_release(string page) => Assert.Null(UpdateChecker.Parse(Release(page, Download)));

    [Fact]
    public void Untrusted_download_url_is_dropped()
    {
        var info = UpdateChecker.Parse(Release(Page, "https://evil.example/Envoy.exe"))!;
        Assert.NotNull(info);
        Assert.Null(info.DownloadUrl);
    }

    [Fact]
    public void Other_repository_is_not_trusted()
    {
        Assert.False(ReleaseUrlPolicy.IsTrusted("https://github.com/other/repo/releases/tag/v1", "PinnerSinner/Envoy"));
        Assert.True(ReleaseUrlPolicy.IsTrusted("https://github.com/other/repo/releases/tag/v1"));
    }

    [Fact]
    public async Task CheckAsync_ignores_release_of_other_repository()
    {
        var json = Release("https://github.com/other/repo/releases/tag/v1.0.0", "https://github.com/other/repo/releases/download/v1.0.0/Envoy.exe");
        using var http = new HttpClient(new FixedResponse(json));
        Assert.Null(await new UpdateChecker(http, "PinnerSinner/Envoy").CheckAsync(new Version(0, 1, 0)));
    }

    [Fact]
    public async Task CheckAsync_keeps_release_of_own_repository_but_drops_foreign_download()
    {
        using var http = new HttpClient(new FixedResponse(Release(Page, "https://github.com/other/repo/releases/download/v1.0.0/Envoy.exe")));
        var info = await new UpdateChecker(http, "PinnerSinner/Envoy").CheckAsync(new Version(0, 1, 0));
        Assert.NotNull(info);
        Assert.Equal(Page, info.PageUrl);
        Assert.Null(info.DownloadUrl);
    }

    private sealed class FixedResponse(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
