using Envoy.Core.Chat;
using Xunit;

namespace Envoy.Core.Tests;

public class PlayerListTextTests
{
    [Fact]
    public void Parse_SplitsOnCommaSemicolonNewlines()
    {
        Assert.Equal(["a", "b", "c", "d", "e"], PlayerListText.Parse("a,b;c\nd\r\ne"));
    }

    [Fact]
    public void Parse_TrimsAndDropsEmpty()
    {
        Assert.Equal(["Li Wei", "Bob"], PlayerListText.Parse("  Li Wei , ,;  \n Bob  ,"));
    }

    [Fact]
    public void Parse_DistinctIgnoreCase_FirstSpellingWins()
    {
        Assert.Equal(["Bob", "li"], PlayerListText.Parse("Bob, bob, li, BOB, LI"));
    }

    [Fact]
    public void Parse_Null_ReturnsEmpty()
    {
        Assert.Empty(PlayerListText.Parse(null));
        Assert.Empty(PlayerListText.Parse(""));
    }

    [Fact]
    public void Format_JoinsWithCommaSpace()
    {
        Assert.Equal("a, b, c", PlayerListText.Format(["a", "b", "c"]));
        Assert.Equal("", PlayerListText.Format([]));
    }

    [Fact]
    public void RoundTrip_FormatThenParse_Identity()
    {
        List<string> players = ["龙骑士", "Сокол", "Bob the Builder"];
        Assert.Equal(players, PlayerListText.Parse(PlayerListText.Format(players)));
    }
}
