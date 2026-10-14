using Envoy.Core.Chat;
using Envoy.Core.Pipeline;
using Envoy.Core.Translation;
using Envoy.Core.Updates;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests;

public class PlayerDirectoryTests
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
}
