using Envoy.Core.Chat;
using Envoy.Core.Ocr;
using Envoy.Core.Pipeline;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests;

public class ChatPipelineUnderstandingTests
{
    private const double H = 18;

    private static IReadOnlyList<OcrLine> Lines(params string[] texts) => OcrLine.FromTexts(texts, H, 4);

    [Fact]
    public async Task System_lines_hidden_by_default()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions { SystemLinePatterns = ["*resigned*"] });
        Assert.True(p.Options.HideSystemLines);
        var result = await p.ProcessAsync(Lines("Bob has resigned.", "Kim: 你好"));
        Assert.Equal("Kim", Assert.Single(result).Chat.Sender);
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task Shown_system_lines_are_untranslated_and_cost_nothing()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions { MyLanguage = "fr", SystemLinePatterns = ["*a abandonné*"], HideSystemLines = false });
        var updated = 0;
        p.MessageUpdated += _ => updated++;

        var notice = Assert.Single(await p.ProcessAsync(Lines("Zhang Wei a abandonné.")));

        Assert.Equal(ChatLineKind.System, notice.Chat.Kind);
        Assert.Equal(IncomingStatus.Untranslated, notice.Status);
        Assert.Equal("fr", notice.SourceLanguage);
        Assert.Null(notice.Chat.Sender);
        Assert.Empty(t.Requests);
        Assert.Equal(0, updated);
        Assert.Empty(p.Players.Players);
        Assert.Null(p.SuggestedReplyLanguage);
        Assert.Null(p.LastOpponentLanguage);
    }

    [Fact]
    public async Task Filtered_messages_are_remembered_for_fragments()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions { MyPlayerName = "Hoplite", MutedPlayers = ["Spammer"] });

        Assert.Empty(await p.ProcessAsync(Lines("Hoplite: we should attack the gold mine", "before they wall it off",
            "Spammer: buy cheap gold at my", "website right now")));
        // The heads scroll away one by one; their tails are left at the top as fragments.
        Assert.Empty(await p.ProcessAsync(Lines("before they wall it off", "Spammer: buy cheap gold at my", "website right now")));
        Assert.Empty(await p.ProcessAsync(Lines("website right now")));
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task Wrapped_message_costs_one_request()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var msg = Assert.Single(await p.ProcessAsync(Lines("[All] 小明: 这张地图的金矿在中间，我们", "应该先占领它")));
        Assert.Equal("这张地图的金矿在中间，我们应该先占领它", msg.Chat.Text);
        Assert.Equal(2, msg.Chat.LineCount);
        Assert.Equal("这张地图的金矿在中间，我们应该先占领它", Assert.Single(t.Requests).Text);
    }

    [Fact]
    public async Task String_overload_matches_legacy_for_sender_lines()
    {
        string[] texts = ["[All] 小明: 你好，再来一局？", "Kim: ㅈㅈ", "12:04 Bob: nice wall bro", "Ivan (Team): изи"];
        var p = new ChatPipeline(new FakeTranslator(), new ChatPipelineOptions());
        var result = await p.ProcessAsync(texts);
        Assert.Equal(texts.Select(ChatLineParser.Parse).Select(c => (c.Channel, c.Sender, c.Text, c.Raw)),
            result.Select(m => (m.Chat.Channel, m.Chat.Sender, m.Chat.Text, m.Chat.Raw)));
    }

    [Fact]
    public async Task Analyze_has_no_side_effects()
    {
        var t = new FakeTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var events = 0;
        p.MessageReceived += _ => events++;
        p.MessageUpdated += _ => events++;
        var lines = Lines("Bob: 你好", "Kim: 안녕");

        Assert.Equal(2, p.Analyze(lines).Count);
        Assert.Contains("Bob: 你好", p.Describe(lines), StringComparison.Ordinal);

        Assert.Equal(0, events);
        Assert.Empty(t.Requests);
        Assert.Empty(p.Players.Players);
        Assert.Equal(2, (await p.ProcessAsync(lines)).Count); // dedupe memory untouched by Analyze
    }

    [Fact]
    public async Task Analyzer_failure_falls_back_to_line_mode()
    {
        var p = new ChatPipeline(new FakeTranslator(), new ChatPipelineOptions()) { BeforeAnalyze = () => throw new InvalidOperationException("boom") };
        var result = await p.ProcessAsync(Lines("Bob: we should attack", "the gold mine"));
        Assert.Equal(["Bob", null], result.Select(m => m.Chat.Sender));
        Assert.Equal(nameof(InvalidOperationException), p.LastAnalysisError);
    }

    [Fact]
    public async Task Senders_are_canonicalised_to_known_players()
    {
        var p = new ChatPipeline(new FakeTranslator(), new ChatPipelineOptions());
        p.Players.Observe("Zhang Wei", null);
        var msg = Assert.Single(await p.ProcessAsync(Lines("Zhanq Wei: 你好")));
        Assert.Equal("Zhang Wei", msg.Chat.Sender);
        Assert.Single(p.Players.Players);
    }

    [Fact]
    public async Task Caller_token_cancels_analysis_only()
    {
        var p = new ChatPipeline(new FakeTranslator(), new ChatPipelineOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.ProcessAsync(Lines("Bob: 你好"), cts.Token));
        Assert.Single(await p.ProcessAsync(Lines("Bob: 你好"))); // nothing was remembered by the cancelled call
    }
}
