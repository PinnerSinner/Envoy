using Envoy.Core.Security;
using Xunit;

namespace Envoy.Core.Tests.Security;

public class OutgoingTextTests
{
    [Fact]
    public void Newlines_and_tabs_become_single_space() => Assert.Equal("gl hf", OutgoingText.Sanitize("gl\r\n\thf"));

    [Theory]
    [InlineData('\u0000')]
    [InlineData('\u0007')]
    [InlineData('\u001B')]
    [InlineData('\u007F')]
    [InlineData('\u0085')]
    [InlineData('\u009B')]
    public void Control_characters_are_removed(char c)
    {
        Assert.Equal("ab", OutgoingText.Sanitize("a" + c + "b"));
        Assert.True(OutgoingText.IsUnsafe(c));
    }

    [Theory]
    [InlineData('\u202A')]
    [InlineData('\u202B')]
    [InlineData('\u202C')]
    [InlineData('\u202D')]
    [InlineData('\u202E')]
    [InlineData('\u2066')]
    [InlineData('\u2067')]
    [InlineData('\u2068')]
    [InlineData('\u2069')]
    public void Bidi_controls_are_removed(char c)
    {
        Assert.Equal("gg wp", OutgoingText.Sanitize("gg" + c + " wp" + c));
        Assert.True(OutgoingText.IsUnsafe(c));
    }

    [Fact]
    public void Keeps_cjk_emoji_and_joiners()
    {
        const string text = "你好 👍 👨\u200D👩\u200D👧 क्\u200Dष";
        Assert.Equal(text, OutgoingText.Sanitize(text));
        Assert.False(OutgoingText.IsUnsafe('\u200D'));
        Assert.False(OutgoingText.IsUnsafe('\u200C'));
    }

    [Fact]
    public void Collapses_whitespace_and_trims() => Assert.Equal("a b", OutgoingText.Sanitize("  a   b  "));

    [Fact]
    public void Caps_at_max_length_without_splitting_surrogates()
    {
        var result = OutgoingText.Sanitize(new string('a', 999) + "😀", 1000);
        Assert.Equal(999, result.Length);
        Assert.False(char.IsSurrogate(result[^1]));
    }

    [Fact]
    public void Default_cap_is_1000() => Assert.Equal(1000, OutgoingText.Sanitize(new string('x', 5000)).Length);

    [Fact]
    public void Smaller_caps_are_honoured_and_larger_ones_clamped()
    {
        Assert.Equal("abc", OutgoingText.Sanitize("abc def", 4));
        Assert.Equal(OutgoingText.HardMaxLength, OutgoingText.Sanitize(new string('x', 5000), 4000).Length);
    }

    [Fact]
    public void Unpaired_surrogates_are_removed() => Assert.Equal("ab", OutgoingText.Sanitize("a\uD83Db\uDE00"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public void Null_and_whitespace_give_empty(string? text) => Assert.Equal("", OutgoingText.Sanitize(text));

    [Theory]
    [InlineData("gl\r\n\thf")]
    [InlineData("a\u0007b\u202Ec")]
    [InlineData("你好 👍 👨\u200D👩\u200D👧 क्\u200Dष")]
    [InlineData("  a   b  ")]
    [InlineData(" \n ")]
    [InlineData("x \u2029 y\u00A0z")]
    public void Is_idempotent(string text)
    {
        var once = OutgoingText.Sanitize(text);
        Assert.Equal(once, OutgoingText.Sanitize(once));
    }

    [Fact]
    public void Is_idempotent_at_the_cap()
    {
        var once = OutgoingText.Sanitize(new string('a', 998) + " 😀 tail");
        Assert.Equal(once, OutgoingText.Sanitize(once));
    }
}
