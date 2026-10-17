using Envoy.Core.Compose;
using Envoy.Core.Security;
using Xunit;

namespace Envoy.Core.Tests.Compose;

public class ReplyLengthTests
{
    [Theory]
    [InlineData("👍🏽", 1)]
    [InlineData("你好", 2)]
    [InlineData("é", 1)]
    [InlineData("é", 1)]
    [InlineData("", 0)]
    public void Counts_text_elements(string text, int expected)
    {
        var info = ReplyLength.Measure(text, 0);
        Assert.Equal(expected, info.Count);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.CurrentCulture), info.Text);
    }

    [Fact]
    public void Counts_sanitised_text()
    {
        Assert.Equal(5, ReplyLength.Measure("gl\r\nhf", 0).Count);
        Assert.Equal(5, ReplyLength.Measure("  gl ‮hf  ", 0).Count);
    }

    [Fact]
    public void Limit_zero_never_over()
    {
        var info = ReplyLength.Measure(new string('a', 900), 0);
        Assert.False(info.OverLimit);
        Assert.False(info.WillBeCut);
        Assert.Equal(0, info.Limit);
        Assert.False(ReplyLength.Measure("abc", -5).OverLimit);
    }

    [Fact]
    public void Over_limit_flagged_with_slash_text()
    {
        var at = ReplyLength.Measure("0123456789", 10);
        Assert.False(at.OverLimit);
        Assert.Equal("10 / 10", at.Text);
        var over = ReplyLength.Measure("0123456789x", 10);
        Assert.True(over.OverLimit);
        Assert.False(over.WillBeCut);
        Assert.Equal("11 / 10", over.Text);
    }

    [Fact]
    public void Text_over_hard_cap_will_be_cut()
    {
        var exact = ReplyLength.Measure(new string('a', OutgoingText.HardMaxLength), 0);
        Assert.False(exact.WillBeCut);
        Assert.False(exact.OverLimit);

        var info = ReplyLength.Measure(new string('a', OutgoingText.HardMaxLength) + " b", 0);
        Assert.True(info.WillBeCut);
        Assert.True(info.OverLimit);
        Assert.Equal(OutgoingText.HardMaxLength, info.Count);
        Assert.Contains("longer text is cut", info.Text, StringComparison.Ordinal);
    }
}
