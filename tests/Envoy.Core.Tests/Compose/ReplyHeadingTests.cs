using Envoy.Core.Compose;
using Xunit;

namespace Envoy.Core.Tests.Compose;

/// <summary>Spec 09a, REQ-HRD-9: the scribe's desk heading, a drop cap R and the rest.</summary>
public class ReplyHeadingTests
{
    [Fact]
    public void Names_the_last_sender()
    {
        var heading = ReplyHeading.For(" 龙骑士 ");
        Assert.Equal("R", heading.DropCap);
        Assert.Equal("eply to 龙骑士", heading.Rest);
        Assert.Equal("Reply to 龙骑士", heading.AccessibleName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_sender_it_is_just_reply(string? sender)
    {
        var heading = ReplyHeading.For(sender);
        Assert.Equal(("R", "eply", "Reply"), (heading.DropCap, heading.Rest, heading.AccessibleName));
    }

    [Fact]
    public void Braces_in_a_name_are_kept() => Assert.Equal("Reply to {x}", ReplyHeading.For("{x}").AccessibleName);
}
