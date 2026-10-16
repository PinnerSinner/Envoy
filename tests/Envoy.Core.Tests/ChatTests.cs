using Envoy.Core.Chat;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests;

public partial class ChatLineParserTests
{
    [Theory]
    [InlineData("[All] Zhang Wei: 你好", "All", "Zhang Wei", "你好")]
    [InlineData("(Team) Kim: ㄱㄱ", "Team", "Kim", "ㄱㄱ")]
    [InlineData("[全部] 小明：打得好", "All", "小明", "打得好")]
    [InlineData("Ivan (Team): изи", "Team", "Ivan", "изи")]
    [InlineData("12:04 Pedro: hola amigo", null, "Pedro", "hola amigo")]
    [InlineData("[12:04:33] 李雷: 666", null, "李雷", "666")]
    [InlineData("天下第一：gg", null, "天下第一", "gg")]
    public void Splits_channel_sender_and_text(string line, string? channel, string? sender, string text)
    {
        var msg = ChatLineParser.Parse(line);
        Assert.Equal(channel, msg.Channel);
        Assert.Equal(sender, msg.Sender);
        Assert.Equal(text, msg.Text);
    }

    [Theory]
    [InlineData("meet at 12:30 ok")]
    [InlineData("https://example.com is cool")]
    [InlineData("no colon here at all")]
    public void Leaves_lines_without_a_sender_intact(string line)
    {
        var msg = ChatLineParser.Parse(line);
        Assert.Null(msg.Sender);
        Assert.Equal(line, msg.Text);
    }

    [Fact]
    public void Keeps_colons_inside_the_message()
    {
        var msg = ChatLineParser.Parse("Bob: time is 3:15: go");
        Assert.Equal("Bob", msg.Sender);
        Assert.Equal("time is 3:15: go", msg.Text);
    }

    [Fact]
    public void Unknown_bracket_tags_are_not_channels()
    {
        var msg = ChatLineParser.Parse("[CLAN] Bob: hi");
        Assert.Null(msg.Channel);
        Assert.Equal("[CLAN] Bob", msg.Sender);
    }
}

public partial class ChatDeduplicatorTests
{
    [Fact]
    public void Same_line_read_again_is_rejected()
    {
        var d = new ChatDeduplicator();
        Assert.True(d.TryAccept("[All] 小明: 你好吗"));
        Assert.False(d.TryAccept("[All] 小明: 你好吗"));
    }

    [Fact]
    public void Small_ocr_differences_count_as_the_same_line()
    {
        var d = new ChatDeduplicator();
        Assert.True(d.TryAccept("[All] Player1: are you rushing me?"));
        Assert.False(d.TryAccept("[AII] Playerl: are you rushing me ?"));
        Assert.False(d.TryAccept("[All] Player1: are you rushlng me?"));
    }

    [Fact]
    public void Partially_revealed_line_is_the_same_line()
    {
        var d = new ChatDeduplicator();
        Assert.True(d.TryAccept("Bob: we should attack the gold"));
        Assert.False(d.TryAccept("Bob: we should attack"));
    }

    [Fact]
    public void Different_lines_are_accepted()
    {
        var d = new ChatDeduplicator();
        Assert.True(d.TryAccept("Bob: gg"));
        Assert.True(d.TryAccept("Bob: rematch?"));
        Assert.True(d.TryAccept("Alice: gg"));
    }

    [Fact]
    public void Lines_are_forgotten_after_the_memory_window()
    {
        var clock = new FakeTimeProvider();
        var d = new ChatDeduplicator(clock) { Memory = TimeSpan.FromMinutes(1) };
        Assert.True(d.TryAccept("Bob: gg"));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(d.TryAccept("Bob: gg"));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(d.TryAccept("Bob: gg"));
    }

    [Fact]
    public void Empty_or_punctuation_only_lines_are_rejected()
    {
        var d = new ChatDeduplicator();
        Assert.False(d.TryAccept(""));
        Assert.False(d.TryAccept(" .,;: "));
    }
}

public partial class ChatDeduplicatorTests
{
    private static ChatMessage Chat(string? sender, string text, string? channel = null) =>
        new($"{sender}: {text}", channel, sender, text);

    private static ChatMessage Fragment(string text) => new(text, null, null, text) { Kind = ChatLineKind.Fragment };

    [Fact]
    public void Same_text_from_different_senders_is_two_messages()
    {
        var d = new ChatDeduplicator();
        Assert.Single(d.AcceptNew([Chat("Alice", "gg")]));
        Assert.Single(d.AcceptNew([Chat("Bob", "gg")]));
        Assert.Empty(d.AcceptNew([Chat("Alice", "gg"), Chat("Bob", "gg")]));
    }

    [Fact]
    public void Sender_jitter_is_same_message()
    {
        var d = new ChatDeduplicator();
        Assert.Single(d.AcceptNew([Chat("Player1_Pro", "are you rushing me?")]));
        Assert.Empty(d.AcceptNew([Chat("Playerl_Pro", "are you rushing me ?")]));
        Assert.Empty(d.AcceptNew([Chat("Player1_Pr0", "are you rushlng me?")]));
    }

    [Fact]
    public void Channel_and_timestamp_do_not_affect_identity()
    {
        var d = new ChatDeduplicator();
        Assert.Single(d.AcceptNew([new ChatMessage("[All] 12:04 Bob: hello there", "All", "Bob", "hello there")]));
        Assert.Empty(d.AcceptNew([new ChatMessage("Bob (Team): hello there", "Team", "Bob", "hello there")]));
    }

    [Fact]
    public void Fragment_contained_in_known_message_is_rejected()
    {
        var d = new ChatDeduplicator();
        Assert.Single(d.AcceptNew([Chat("Bob", "we should attack the gold mine before they wall it")]));
        Assert.Empty(d.AcceptNew([Fragment("mine before they wall it")]));
    }

    [Fact]
    public void Fuzzy_fragment_is_rejected()
    {
        var d = new ChatDeduplicator();
        Assert.Single(d.AcceptNew([Chat("Bob", "we should attack the gold mine before they wall it")]));
        Assert.Empty(d.AcceptNew([Fragment("rnine bef0re they waII it")]));
    }

    [Fact]
    public void Unknown_fragment_is_accepted()
    {
        var d = new ChatDeduplicator();
        Assert.Single(d.AcceptNew([Chat("Bob", "we should attack the gold mine")]));
        Assert.Single(d.AcceptNew([Fragment("and bring the siege rams too")]));
        Assert.Empty(d.AcceptNew([Fragment("and bring the siege rams too")]));
    }

    [Fact]
    public void Concurrent_captures_do_not_double_accept()
    {
        var d = new ChatDeduplicator();
        var accepted = 0;
        Parallel.For(0, 50, _ => Interlocked.Add(ref accepted, d.AcceptNew([Chat("A", "hello")]).Count));
        Assert.Equal(1, accepted);
    }

    [Fact]
    public void Two_visible_copies_are_two_messages()
    {
        var d = new ChatDeduplicator();
        Assert.Equal(2, d.AcceptNew([Chat("Bob", "gg"), Chat("Kim", "wp"), Chat("Bob", "gg")]).Count(m => m.Sender == "Bob"));
    }

    [Fact]
    public void Rereading_copies_accepts_none()
    {
        var d = new ChatDeduplicator();
        d.AcceptNew([Chat("Bob", "gg"), Chat("Bob", "gg")]);
        Assert.Empty(d.AcceptNew([Chat("Bob", "gg"), Chat("Bob", "gg")]));
        Assert.Empty(d.AcceptNew([Chat("Bob", "gg")])); // one copy scrolled away
    }

    [Fact]
    public void Third_copy_accepts_one()
    {
        var d = new ChatDeduplicator();
        d.AcceptNew([Chat("Bob", "gg"), Chat("Bob", "gg")]);
        Assert.Single(d.AcceptNew([Chat("Bob", "gg"), Chat("Bob", "gg"), Chat("Bob", "gg")]));
    }

    [Fact]
    public void Filtered_fragments_are_not_remembered()
    {
        var d = new ChatDeduplicator();
        d.AcceptNew([Chat("Bob", "we should attack the gold mine")]);
        Assert.Empty(d.AcceptNew([Fragment("gold mine")]));
        // A suppressed fragment isn't stored, so it can't suppress a later real message with the same words.
        Assert.Single(d.AcceptNew([Chat("Kim", "gold mine")]));
    }
}

public partial class ChatLineParserTests
{
    /// <summary>Today's hard-coded channel words and their canonical names (before spec 04 moved them into data).</summary>
    private static readonly Dictionary<string, string> LegacyChannels = new()
    {
        ["all"] = "All", ["everyone"] = "All", ["全部"] = "All", ["所有人"] = "All", ["所有"] = "All", ["전체"] = "All",
        ["все"] = "All", ["todos"] = "All", ["alle"] = "All", ["tous"] = "All", ["全員"] = "All",
        ["team"] = "Team", ["allies"] = "Team", ["ally"] = "Team", ["队伍"] = "Team", ["隊伍"] = "Team", ["团队"] = "Team",
        ["團隊"] = "Team", ["盟友"] = "Team", ["팀"] = "Team", ["아군"] = "Team", ["команда"] = "Team", ["союзники"] = "Team",
        ["equipo"] = "Team", ["aliados"] = "Team", ["equipe"] = "Team", ["verbündete"] = "Team", ["équipe"] = "Team",
        ["alliés"] = "Team", ["チーム"] = "Team", ["味方"] = "Team",
        ["observers"] = "Observers", ["observer"] = "Observers", ["spectators"] = "Observers", ["观察者"] = "Observers",
        ["觀察者"] = "Observers", ["관전자"] = "Observers",
        ["private"] = "Private", ["whisper"] = "Private", ["私聊"] = "Private",
    };

    [Fact]
    public void Generic_profile_equals_legacy_parse()
    {
        var generic = ChatFormatProfile.Generic;
        foreach (var (word, canonical) in LegacyChannels)
        {
            Assert.Equal(canonical, generic.CanonicalChannel(word));
            Assert.Equal(canonical, generic.CanonicalChannel(word.ToUpperInvariant()));
            Assert.Equal(canonical, ChatLineParser.Parse($"[{word}] Bob: hi").Channel);
            Assert.Equal(canonical, ChatLineParser.Parse($"Bob ({word}): hi").Channel);
        }
        Assert.Equal(LegacyChannels.Count, generic.Channels.Values.Sum(v => v.Count));
        Assert.Null(generic.CanonicalChannel("clan"));

        string[] lines = ["[All] Zhang Wei: 你好", "(Team) Kim: ㄱㄱ", "Ivan (Team): изи", "12:04 Pedro: hola", "<All> x: y", "[All) a: b",
            "meet at 12:30 ok", "https://example.com is cool", "Bob: time is 3:15: go", "[CLAN] Bob: hi", "a very long sender name that exceeds the limit: hi"];
        foreach (var line in lines) Assert.Equal(ChatLineParser.Parse(line), ChatLineParser.Parse(line, generic));
        Assert.Equal(32, generic.MaxSenderLength);
        Assert.Equal([":", "："], generic.Separators);
    }

    [Fact]
    public void Custom_profile_separator_and_channels()
    {
        var catalog = ChatFormatCatalog.Parse(ChatFormatCatalogTests.BuiltInJson, """
            {
              "profiles": [
                { "id": "custom", "name": "Custom", "inherits": "generic", "separators": [">", " says "],
                  "channels": { "All": ["global"], "team": ["squad"] }, "brackets": ["{}"], "maxSenderLength": 12 }
              ]
            }
            """);
        Assert.Empty(catalog.Errors);
        var profile = catalog.Find("custom")!;
        var a = ChatLineParser.Parse("{global} Bob> hi", profile);
        Assert.Equal(("All", "Bob", "hi"), (a.Channel, a.Sender, a.Text));
        var b = ChatLineParser.Parse("Kim {squad} says go now", profile);
        Assert.Equal(("Team", "Kim", "go now"), (b.Channel, b.Sender, b.Text));
        var c = ChatLineParser.Parse("[All] Bob: hi", profile);
        Assert.Null(c.Channel);
        Assert.Null(c.Sender);
    }
}
