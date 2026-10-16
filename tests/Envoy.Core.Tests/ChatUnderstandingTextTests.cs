using Envoy.Core.Chat;
using Xunit;

namespace Envoy.Core.Tests;

public class ChatUnderstandingTextTests
{
    [Fact]
    public void Describes_kinds_and_line_counts()
    {
        var text = ChatUnderstandingText.Describe(
        [
            new ChatMessage("", "All", "Bob", "we should attack now") { LineCount = 2 },
            new ChatMessage("", null, "Kim", "ㅋㅋㅋ"),
            new ChatMessage("", null, null, "Bob has resigned.") { Kind = ChatLineKind.System },
            new ChatMessage("", null, null, "the gold mine") { Kind = ChatLineKind.Fragment },
            new ChatMessage("", null, null, "plain line"),
        ]);
        Assert.Equal(string.Join(Environment.NewLine,
            "Understood as:",
            "[All] Bob: we should attack now (2 lines)",
            "Kim: ㅋㅋㅋ",
            "(notice) Bob has resigned.",
            "(part of an earlier message) the gold mine",
            "(no sender) plain line"), text);
        Assert.Equal("Understood as:" + Environment.NewLine + ChatUnderstandingText.Nothing, ChatUnderstandingText.Describe([]));
    }

    [Fact]
    public void Errors_format_pattern_and_profile_errors()
    {
        var text = ChatUnderstandingText.Errors(
            [new PatternError(2, "re:([", "Invalid pattern '(['  at offset 2. Not enough )'s.")],
            [
                new ChatFormatError(null, null, ChatFormatErrorKind.FileUnreadable),
                new ChatFormatError("aoe4-en", "timestamp", ChatFormatErrorKind.FieldInvalid),
                new ChatFormatError(null, "games", ChatFormatErrorKind.FieldInvalid),
                new ChatFormatError(null, "id", ChatFormatErrorKind.FieldInvalid),
            ]);
        var lines = text.Split(Environment.NewLine);
        Assert.Equal("Line 3 isn't a pattern Envoy understands, so it's ignored. Check its brackets and symbols, or remove \"re:\".", lines[0]);
        Assert.Equal(ChatUnderstandingText.FormatsFileUnreadable, lines[1]);
        Assert.Equal("In chat-formats.json, the \"timestamp\" setting of profile \"aoe4-en\" isn't valid, so Envoy uses the inherited value.", lines[2]);
        Assert.Equal("In chat-formats.json, the \"games\" setting isn't valid, so Envoy ignores it.", lines[3]);
        Assert.Equal(ChatUnderstandingText.FormatsProfileWithoutId, lines[4]);
        Assert.DoesNotContain("offset", text, StringComparison.Ordinal);
        Assert.DoesNotContain("re:([", text, StringComparison.Ordinal);
        Assert.Equal("", ChatUnderstandingText.Errors([], null));
    }
}
