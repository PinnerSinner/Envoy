using Envoy.Core.Chat;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests;

public class ChatLineParserTests
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

public class ChatDeduplicatorTests
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
