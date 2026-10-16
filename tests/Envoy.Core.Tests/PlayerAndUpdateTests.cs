using System.Net;
using Envoy.Core.Chat;
using Envoy.Core.Pipeline;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Updates;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests;

public partial class PlayerDirectoryTests
{
    [Fact]
    public void Remembers_each_players_language()
    {
        var d = new PlayerDirectory();
        d.Observe("小明", "zh-CN");
        d.Observe("Kim", "ko");
        Assert.Equal("zh-CN", d.LanguageOf("小明"));
        Assert.Equal("ko", d.LanguageOf("Kim"));
    }

    [Fact]
    public void Tolerates_ocr_misreads_of_names()
    {
        var d = new PlayerDirectory();
        d.Observe("Player1_Pro", "ru");
        Assert.Equal("ru", d.LanguageOf("Playerl_Pro"));
        Assert.Single(d.Players);
    }

    [Fact]
    public void English_shorthand_does_not_overwrite_a_known_language()
    {
        var d = new PlayerDirectory();
        d.Observe("小明", "zh-CN");
        d.Observe("小明", "en"); // "gg"
        Assert.Equal("zh-CN", d.LanguageOf("小明"));
    }

    [Fact]
    public void Tracks_the_last_speaker()
    {
        var clock = new FakeTimeProvider();
        var d = new PlayerDirectory(clock);
        d.Observe("A", "zh-CN");
        clock.Advance(TimeSpan.FromSeconds(1));
        d.Observe("B", "ko");
        Assert.Equal("B", d.LastSpeaker?.Name);
        clock.Advance(TimeSpan.FromSeconds(1));
        d.Observe("A", null);
        Assert.Equal("A", d.LastSpeaker?.Name);
        Assert.Equal(2, d.LastSpeaker?.MessageCount);
    }

    [Theory]
    [InlineData("Toxic99", true)]
    [InlineData("toxic 99", true)]
    [InlineData("Toxlc99", true)]
    [InlineData("NiceGuy", false)]
    [InlineData(null, false)]
    public void Mute_list_matches_loosely(string? sender, bool muted) =>
        Assert.Equal(muted, MuteList.IsMuted(sender, ["Toxic99"]));
}

public class PipelineFeatureTests
{
    [Fact]
    public async Task Muted_players_are_dropped_before_translation()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions { MutedPlayers = ["Spammer"] });
        Assert.Empty(await p.ProcessAsync(["Spammer: 你好你好你好"]));
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task Channel_filter_keeps_only_team_chat()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions { Channels = ChannelFilter.TeamOnly });
        var result = await p.ProcessAsync(["[All] A: 你好", "[Team] B: 进攻"]);
        Assert.Equal("B", Assert.Single(result).Chat.Sender);
    }

    [Fact]
    public async Task Reply_language_follows_the_last_foreign_speaker()
    {
        var t = new FakeTranslator { Respond = r => new("x", r.Text.Any(c => c >= '가' && c <= '힯') ? "ko" : "zh-CN", "fake") };
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        await p.ProcessAsync(["A: 你好"]);
        await p.ProcessAsync(["B: 안녕하세요"]);
        Assert.Equal("ko", p.SuggestedReplyLanguage);
        Assert.Equal("zh-CN", p.Players.LanguageOf("A"));
    }

    [Fact]
    public async Task Slang_meaning_is_translated_for_non_english_readers()
    {
        var t = new FakeTranslator { Respond = r => new("Bravo !", "en", "fake") };
        var p = new ChatPipeline(t, new ChatPipelineOptions { MyLanguage = "fr" });
        var msg = Assert.Single(await p.ProcessAsync(["小明: 666"]));
        Assert.True(msg.FromGlossary);
        Assert.Equal("Bravo !", msg.Translation);
        var req = Assert.Single(t.Requests);
        Assert.Equal("en", req.SourceLanguage);
        Assert.Contains("Nice", req.Text);
        Assert.Equal("zh-CN", msg.SourceLanguage);
    }

    [Fact]
    public async Task New_match_forgets_players_and_seen_lines()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        await p.ProcessAsync(["A: 你好"]);
        p.NewMatch();
        Assert.Empty(p.Players.Players);
        Assert.Null(p.LastOpponentLanguage);
        Assert.Single(await p.ProcessAsync(["A: 你好"]));
    }
}

public class TranscriptTests
{
    [Fact]
    public void Formats_a_translated_line()
    {
        var m = IncomingMessage.Preview("小明", "你好", "Hello", "zh-CN");
        var line = TranscriptWriter.Format(m, new DateTimeOffset(2026, 1, 1, 20, 15, 3, TimeSpan.Zero));
        Assert.Equal("[20:15:03] [All] 小明 (zh-CN): 你好  =>  Hello", line);
    }

    [Fact]
    public void Writes_one_file_per_session_and_rotates()
    {
        var folder = Path.Combine(Path.GetTempPath(), "envoy-tests-" + Guid.NewGuid().ToString("N"));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var w = new TranscriptWriter(folder, clock);
        w.Append(IncomingMessage.Preview("A", "你好", "Hello", "zh-CN"));
        w.Append(IncomingMessage.Preview("B", "ㅋㅋ", "LOL", "ko"));
        var first = w.CurrentFile!;
        Assert.Equal(3, File.ReadAllLines(first).Length);

        clock.Advance(TimeSpan.FromMinutes(30));
        w.Rotate();
        w.Append(IncomingMessage.Preview("C", "gg", "gg", "en"));
        Assert.NotEqual(first, w.CurrentFile);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }
}

public class UpdateCheckerTests
{
    private const string Release = """
        {"tag_name":"v0.3.1","html_url":"https://github.com/o/r/releases/tag/v0.3.1","draft":false,"prerelease":false,
         "body":"Notes","assets":[{"name":"Envoy.exe","browser_download_url":"https://github.com/o/r/releases/download/v0.3.1/Envoy.exe"}]}
        """;

    [Fact]
    public void Parses_latest_release()
    {
        var info = UpdateChecker.Parse(Release)!;
        Assert.Equal(new Version(0, 3, 1), info.Version);
        Assert.EndsWith("Envoy.exe", info.DownloadUrl);
        Assert.Equal("Notes", info.Notes);
    }

    [Fact]
    public void Ignores_prereleases() =>
        Assert.Null(UpdateChecker.Parse(Release.Replace("\"prerelease\":false", "\"prerelease\":true")));

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("v2.0.0-beta.1", 2, 0, 0)]
    public void Parses_version_tags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Fact]
    public void Rejects_garbage_tags() => Assert.False(UpdateChecker.TryParseVersion("latest", out _));

    // ---- spec 06: channels, asset picking, typed errors ----

    private const string Repo = "PinnerSinner/Envoy";
    private const string Base = "https://github.com/PinnerSinner/Envoy/releases";

    private static string ReleaseJson(string tag, bool prerelease = false, bool draft = false, string? page = null,
                                      string? installerUrl = null, bool setupFirst = false)
    {
        var portable = $$"""{"name":"Envoy.exe","browser_download_url":"{{Base}}/download/{{tag}}/Envoy.exe"}""";
        var setup = $$"""{"name":"Envoy-win-Setup.exe","browser_download_url":"{{installerUrl ?? $"{Base}/download/{tag}/Envoy-win-Setup.exe"}}"}""";
        var nupkg = $$"""{"name":"Envoy-{{tag.TrimStart('v')}}-full.nupkg","browser_download_url":"{{Base}}/download/{{tag}}/x.nupkg"}""";
        var assets = setupFirst ? $"{setup},{nupkg},{portable}" : $"{portable},{setup},{nupkg}";
        return $$"""
            {"tag_name":"{{tag}}","html_url":"{{page ?? $"{Base}/tag/{tag}"}}","draft":{{(draft ? "true" : "false")}},
             "prerelease":{{(prerelease ? "true" : "false")}},"body":"Notes for {{tag}}","assets":[{{assets}}]}
            """;
    }

    [Fact]
    public void Parse_prefers_portable_exe_over_setup()
    {
        var info = UpdateChecker.Parse(ReleaseJson("v1.0.0", setupFirst: true))!;
        Assert.Equal($"{Base}/download/v1.0.0/Envoy.exe", info.DownloadUrl);
        Assert.Equal($"{Base}/download/v1.0.0/Envoy-win-Setup.exe", info.InstallerUrl);

        // Without an asset named exactly Envoy.exe, the first exe that isn't a Setup.exe.
        var other = UpdateChecker.Parse(ReleaseJson("v1.0.0", setupFirst: true).Replace("\"Envoy.exe\"", "\"Envoy-portable.exe\"", StringComparison.Ordinal))!;
        Assert.EndsWith("/Envoy.exe", other.DownloadUrl); // the URL is unchanged; only the name differs
        var setupOnly = UpdateChecker.Parse($$"""
            {"tag_name":"v1.0.0","html_url":"{{Base}}/tag/v1.0.0","draft":false,"prerelease":false,
             "assets":[{"name":"Envoy-win-Setup.exe","browser_download_url":"{{Base}}/download/v1.0.0/Envoy-win-Setup.exe"}]}
            """)!;
        Assert.Null(setupOnly.DownloadUrl);
        Assert.NotNull(setupOnly.InstallerUrl);
    }

    [Fact]
    public void Parse_with_prereleases_included_returns_prerelease()
    {
        var json = ReleaseJson("v1.1.0-beta.1", prerelease: true);
        Assert.Null(UpdateChecker.Parse(json));
        var info = UpdateChecker.Parse(json, includePrereleases: true)!;
        Assert.True(info.IsPrerelease);
        Assert.Equal("1.1.0-beta.1", info.SemVer!.ToString());
        Assert.Null(UpdateChecker.Parse(ReleaseJson("v1.1.0", draft: true), includePrereleases: true));
    }

    [Fact]
    public void Parse_drops_untrusted_installer_url()
    {
        var info = UpdateChecker.Parse(ReleaseJson("v1.0.0", installerUrl: "https://evil.example/Envoy-win-Setup.exe"))!;
        Assert.Null(info.InstallerUrl);
        Assert.NotNull(info.DownloadUrl);
    }

    [Fact]
    public void PickLatest_stable_ignores_prereleases_and_drafts()
    {
        var list = $"[{ReleaseJson("v2.0.0-beta.1", prerelease: true)},{ReleaseJson("v3.0.0", draft: true)},{ReleaseJson("v1.5.0")},{ReleaseJson("v1.4.0")}]";
        Assert.Equal("1.5.0", UpdateChecker.PickLatest(list, UpdateChannel.Stable)!.SemVer!.ToString());
    }

    [Fact]
    public void PickLatest_beta_picks_highest_semver_not_newest_published()
    {
        // Newest published first: a 1.4.1 hotfix published after 1.5.0-beta.2.
        var list = $"[{ReleaseJson("v1.4.1")},{ReleaseJson("v1.5.0-beta.10", prerelease: true)},{ReleaseJson("v1.5.0-beta.2", prerelease: true)}]";
        Assert.Equal("1.5.0-beta.10", UpdateChecker.PickLatest(list, UpdateChannel.Beta)!.SemVer!.ToString());
    }

    [Fact]
    public void PickLatest_skips_untrusted_html_url()
    {
        var list = $"[{ReleaseJson("v9.0.0", page: "https://evil.example/o/r/releases/tag/v9.0.0")},{ReleaseJson("v1.0.0")}]";
        Assert.Equal("1.0.0", UpdateChecker.PickLatest(list, UpdateChannel.Beta)!.SemVer!.ToString());
    }

    [Fact]
    public async Task Check_stable_calls_latest_endpoint_with_minimal_headers()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ReleaseJson("v1.0.0"));
        using var http = handler.Client();
        var info = await new UpdateChecker(http, Repo).CheckAsync(ReleaseVersion.Parse("0.9.0-beta.1"), UpdateChannel.Stable);
        Assert.Equal("1.0.0", info!.SemVer!.ToString());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/PinnerSinner/Envoy/releases/latest", request.Uri.ToString());
        Assert.Equal(["Accept", "User-Agent"], request.Headers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Envoy/0.9.0-beta.1", request.Headers["User-Agent"]);
        Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Check_beta_calls_list_endpoint()
    {
        var list = $"[{ReleaseJson("v1.1.0-beta.1", prerelease: true)},{ReleaseJson("v1.0.0")}]";
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, list);
        using var http = handler.Client();
        var info = await new UpdateChecker(http, Repo).CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateChannel.Beta);
        Assert.Equal("1.1.0-beta.1", info!.SemVer!.ToString());
        Assert.Equal("https://api.github.com/repos/PinnerSinner/Envoy/releases?per_page=10", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Theory]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v1.0.0", "1.0.1", false)]
    [InlineData("v1.0.0", "1.0.0-beta.3", true)]
    [InlineData("v0.4.0", "0.4.0-beta.2", true)]
    [InlineData("v0.4.1", "0.4.0", true)]
    public async Task Check_returns_null_when_not_newer(string tag, string current, bool offered)
    {
        using var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, ReleaseJson(tag)).Client();
        var info = await new UpdateChecker(http, Repo).CheckAsync(ReleaseVersion.Parse(current), UpdateChannel.Stable);
        Assert.Equal(offered, info is not null);
    }

    [Fact]
    public async Task Check_404_returns_null()
    {
        using var http = new FakeHttpHandler().Respond(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}").Client();
        Assert.Null(await new UpdateChecker(http, Repo).CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateChannel.Stable));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Check_403_and_429_throw_RateLimited(HttpStatusCode status)
    {
        using var http = new FakeHttpHandler().Respond(status, "{\"message\":\"API rate limit exceeded\"}").Client();
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => new UpdateChecker(http, Repo).CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateChannel.Beta));
        Assert.Equal(UpdateErrorKind.RateLimited, ex.Kind);
        Assert.DoesNotContain("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>")]
    [InlineData(HttpStatusCode.OK, "{\"no_tag\":1}")]
    [InlineData(HttpStatusCode.InternalServerError, "oops")]
    public async Task Check_bad_json_throws_BadResponse(HttpStatusCode status, string body)
    {
        using var http = new FakeHttpHandler().Respond(status, body).Client();
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => new UpdateChecker(http, Repo).CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateChannel.Stable));
        Assert.Equal(UpdateErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task Check_network_error_throws_Offline()
    {
        using var http = new FakeHttpHandler().Throw(new HttpRequestException("No such host is known.")).Client();
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => new UpdateChecker(http, Repo).CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateChannel.Stable));
        Assert.Equal(UpdateErrorKind.Offline, ex.Kind);

        using var slow = new FakeHttpHandler().Throw(new TaskCanceledException("timeout")).Client();
        Assert.Equal(UpdateErrorKind.Offline, (await Assert.ThrowsAsync<UpdateCheckException>(
            () => new UpdateChecker(slow, Repo).CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateChannel.Stable))).Kind);
    }

    [Fact]
    public async Task Check_honours_cancellation()
    {
        using var http = new FakeHttpHandler().Hang().Client();
        using var cts = new CancellationTokenSource();
        var check = new UpdateChecker(http, Repo).CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateChannel.Stable, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public async Task Check_drops_untrusted_installer_and_foreign_release()
    {
        using var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, ReleaseJson("v2.0.0", installerUrl: "https://github.com/other/repo/releases/download/v2.0.0/Envoy-win-Setup.exe")).Client();
        var info = await new UpdateChecker(http, Repo).CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateChannel.Stable);
        Assert.Null(info!.InstallerUrl);
        Assert.NotNull(info.DownloadUrl);

        using var foreign = new FakeHttpHandler().Respond(HttpStatusCode.OK, ReleaseJson("v2.0.0", page: "https://github.com/other/repo/releases/tag/v2.0.0")).Client();
        Assert.Null(await new UpdateChecker(foreign, Repo).CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateChannel.Stable));
    }
}
