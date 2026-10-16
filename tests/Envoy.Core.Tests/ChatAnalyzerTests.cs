using Envoy.Core.Chat;
using Envoy.Core.Ocr;
using Xunit;

namespace Envoy.Core.Tests;

public class ChatCaptureAnalyzerTests
{
    private const double H = 18;

    private static IReadOnlyList<ChatMessage> Analyze(IReadOnlyList<OcrLine> lines, ChatContext? context = null) =>
        ChatCaptureAnalyzer.Analyze(lines, context ?? ChatContext.Default);

    private static ChatContext WithPatterns(params string[] patterns) =>
        ChatContext.Default with { SystemPatterns = SystemLinePatterns.Compile(patterns, out _) };

    /// <summary>Lines of height <see cref="H"/>, each starting <paramref name="gapFactor"/>·H below the previous one.</summary>
    private static List<OcrLine> Stack(double gapFactor, params string[] texts) =>
        texts.Select((t, i) => new OcrLine(t, i * H * (1 + gapFactor), i * H * (1 + gapFactor) + H)).ToList();

    [Fact]
    public void Joins_continuation_within_gap()
    {
        var entries = Analyze(Stack(0.3, "[All] Bob: we should attack the gold", "mine before they wall it", "Kim: ok"));
        Assert.Equal(2, entries.Count);
        Assert.Equal("we should attack the gold mine before they wall it", entries[0].Text);
        Assert.Equal("Bob", entries[0].Sender);
        Assert.Equal("All", entries[0].Channel);
        Assert.Equal(ChatLineKind.Chat, entries[0].Kind);
        Assert.Equal("Kim", entries[1].Sender);
    }

    [Fact]
    public void Gap_too_large_starts_fragment()
    {
        var lines = new List<OcrLine> { new("Bob: we should attack", 0, H), new("the gold mine now", H + 2 * H, 2 * H + 2 * H) };
        var entries = Analyze(lines);
        Assert.Equal(2, entries.Count);
        Assert.Equal("we should attack", entries[0].Text);
        Assert.Equal(ChatLineKind.Fragment, entries[1].Kind);
        Assert.Null(entries[1].Sender);
        Assert.Equal("the gold mine now", entries[1].Text);
    }

    [Fact]
    public void Height_mismatch_starts_fragment()
    {
        var lines = new List<OcrLine> { new("Bob: we should attack", 0, H), new("Age of Empires", H + 2, H + 2 + 2 * H), new("Kim: ok", 4 * H, 5 * H) };
        var entries = Analyze(lines);
        Assert.Equal(ChatLineKind.Fragment, entries[1].Kind);
        Assert.Equal("Age of Empires", entries[1].Text);
    }

    [Fact]
    public void Stops_at_max_continuations()
    {
        var entries = Analyze(Stack(0.2, "Bob: one", "two", "three", "four", "five", "six", "seven"));
        Assert.Equal(2, entries.Count);
        Assert.Equal("one two three four five", entries[0].Text);
        Assert.Equal(5, entries[0].LineCount);
        Assert.Equal(ChatLineKind.Fragment, entries[1].Kind);
        Assert.Equal("six seven", entries[1].Text);
    }

    [Fact]
    public void Stitching_off_keeps_one_message_per_line()
    {
        var entries = Analyze(Stack(0.2, "Bob: we should attack", "the gold mine", "now"), ChatContext.Default with { Stitch = false });
        Assert.Equal(3, entries.Count);
        Assert.Equal("we should attack", entries[0].Text);
        Assert.All(entries.Skip(1), e => Assert.Equal(ChatLineKind.Fragment, e.Kind));
        Assert.All(entries, e => Assert.Equal(1, e.LineCount));
    }

    [Fact]
    public void Joins_cjk_without_space()
    {
        var entries = Analyze(Stack(0.2, "[全部] 天下第一：这张地图的金矿在中间，我们", "应该先占领它", "小明：好"));
        Assert.Equal("这张地图的金矿在中间，我们应该先占领它", entries[0].Text);
        Assert.Equal("我们", ChatCaptureAnalyzer.Join("我们", ""));
        Assert.Equal("gg。ok", ChatCaptureAnalyzer.Join("gg。", "ok"));
        Assert.Equal("ok日本", ChatCaptureAnalyzer.Join("ok", "日本"));
        Assert.Equal("すぐ行く", ChatCaptureAnalyzer.Join("すぐ", "行く"));
    }

    [Fact]
    public void Joins_latin_and_hangul_with_space()
    {
        Assert.Equal("hello there", ChatCaptureAnalyzer.Join("hello", "there"));
        Assert.Equal("우리 팀이 이겼어", ChatCaptureAnalyzer.Join("우리 팀이", "이겼어"));
        Assert.Equal("Иван идёт", ChatCaptureAnalyzer.Join("Иван", "идёт"));
        var entries = Analyze(Stack(0.2, "Kim: 우리 팀이 지금 바로", "공격해야 해"));
        Assert.Equal("우리 팀이 지금 바로 공격해야 해", Assert.Single(entries).Text);
    }

    [Fact]
    public void Pattern_with_colon_beats_sender_parse()
    {
        var entries = Analyze(Stack(0.2, "Tribute: Bob sent 100 gold to Kim"), WithPatterns("Tribute: *"));
        var notice = Assert.Single(entries);
        Assert.Equal(ChatLineKind.System, notice.Kind);
        Assert.Null(notice.Sender);
        Assert.Equal("Tribute: Bob sent 100 gold to Kim", notice.Text);
    }

    [Fact]
    public void Leading_lines_are_one_fragment()
    {
        var entries = Analyze(Stack(0.2, "of the map and we", "need more wood", "Bob: gg"));
        Assert.Equal(2, entries.Count);
        Assert.Equal(ChatLineKind.Fragment, entries[0].Kind);
        Assert.Equal("of the map and we need more wood", entries[0].Text);
        Assert.Equal(2, entries[0].LineCount);
        Assert.Equal("Bob", entries[1].Sender);
    }

    [Fact]
    public void Stitched_message_has_raw_linecount_and_bounds()
    {
        var lines = new List<OcrLine> { new("■ Bob: first half", 10, 28), new("second half", 32, 50) };
        var entry = Assert.Single(Analyze(lines));
        Assert.Equal("Bob: first half\nsecond half", entry.Raw);
        Assert.Equal(2, entry.LineCount);
        Assert.Equal(10, entry.Top);
        Assert.Equal(50, entry.Bottom);
    }

    [Fact]
    public void Unknown_geometry_stitches_adjacent_lines()
    {
        var entries = Analyze(OcrLine.FromTexts(["Bob: we should", "attack now", "Kim: ok"]));
        Assert.Equal(2, entries.Count);
        Assert.Equal("we should attack now", entries[0].Text);
    }

    [Fact]
    public void Continuation_of_notice_joins_notice()
    {
        var entries = Analyze(Stack(0.2, "Bob has resigned from the", "game", "Kim: gg"), WithPatterns("* has resigned*"));
        Assert.Equal(2, entries.Count);
        Assert.Equal(ChatLineKind.System, entries[0].Kind);
        Assert.Equal("Bob has resigned from the game", entries[0].Text);
    }

    [Fact]
    public void Line_starting_with_known_player_is_notice()
    {
        var context = ChatContext.Default with { SystemByPlayerName = true, KnownPlayers = ["Zhang Wei", "Hoplite"] };
        var entries = Analyze(Stack(1.5, "Zhanq Wei has advanced to the Castle Age", "Hoplite was defeated"), context);
        Assert.All(entries, e => Assert.Equal(ChatLineKind.System, e.Kind));
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Short_names_do_not_trigger_notice()
    {
        var context = ChatContext.Default with { SystemByPlayerName = true, KnownPlayers = ["Al", "Bo"] };
        var entry = Assert.Single(Analyze(Stack(0.2, "Al will boom"), context));
        Assert.Equal(ChatLineKind.Fragment, entry.Kind);
    }

    [Fact]
    public void Heuristic_off_by_default()
    {
        Assert.False(ChatContext.Default.SystemByPlayerName);
        var context = ChatContext.Default with { KnownPlayers = ["Hoplite"] };
        Assert.Equal(ChatLineKind.Fragment, Assert.Single(Analyze(Stack(0.2, "Hoplite was defeated"), context)).Kind);
    }

    [Fact]
    public void Profile_separator_is_used()
    {
        var catalog = ChatFormatCatalog.Parse(ChatFormatCatalogTests.BuiltInJson,
            """{ "profiles": [ { "id": "arrow", "inherits": "generic", "separators": [">"] } ] }""");
        var context = ChatContext.Default with { Profile = catalog.Find("arrow") };
        var entry = Assert.Single(Analyze(OcrLine.FromTexts(["Bob> hi"], H), context));
        Assert.Equal("Bob", entry.Sender);
        Assert.Equal("hi", entry.Text);
    }
}
