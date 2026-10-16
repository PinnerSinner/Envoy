using Envoy.Core.Chat;
using Xunit;

namespace Envoy.Core.Tests;

public class TextSimilarityTests
{
    [Theory]
    [InlineData("Zhanq Wei", "Zhang Wei")]
    [InlineData("rnike", "mike")]
    [InlineData("vvolf", "wolf")]
    [InlineData("Clara", "Dara")]
    [InlineData("Player1", "Playerl")]
    public void OcrKey_folds_confusables(string a, string b) => Assert.Equal(TextSimilarity.OcrKey(a), TextSimilarity.OcrKey(b));

    [Theory]
    [InlineData("Player1", "Player2")]
    [InlineData("Team3", "Team8")]
    [InlineData("Bob", "Rob")]
    public void OcrKey_keeps_distinct_digits(string a, string b) => Assert.NotEqual(TextSimilarity.OcrKey(a), TextSimilarity.OcrKey(b));

    [Theory]
    [InlineData("weshouldattackthegoldmine", "goldmine", true)]
    [InlineData("weshouldattackthegoldmine", "goidmine", true)]
    [InlineData("weshouldattackthegoldmine", "siegerams", false)]
    [InlineData("abc", "", false)]
    [InlineData("gg", "gg", true)]
    [InlineData("gg", "wp", false)]
    public void ContainsFuzzy_finds_approximate_substrings(string haystack, string needle, bool expected) =>
        Assert.Equal(expected, TextSimilarity.ContainsFuzzy(haystack, needle, 0.8));

    [Fact]
    public void ContainsFuzzy_caps_long_inputs()
    {
        var haystack = new string('a', 5000) + "needle";
        Assert.False(TextSimilarity.ContainsFuzzy(haystack, "needle", 0.8)); // beyond the cap
        Assert.True(TextSimilarity.ContainsFuzzy(new string('a', 300) + "needle", "needle", 0.8));
    }
}

public partial class PlayerDirectoryTests
{
    [Fact]
    public void Canonicalize_maps_misread_to_known_name()
    {
        var d = new PlayerDirectory();
        d.Observe("Zhang Wei", "zh-CN");
        d.Observe("mike", "en");
        Assert.Equal("Zhang Wei", d.Canonicalize("Zhanq Wei"));
        Assert.Equal("mike", d.Canonicalize("rnike"));
        Assert.Equal(["Zhang Wei", "mike"], d.KnownNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Canonicalize_keeps_unknown()
    {
        var d = new PlayerDirectory();
        d.Observe("Player1", null);
        Assert.Equal("Player2", d.Canonicalize("Player2"));
        Assert.Equal("Somebody", d.Canonicalize("Somebody"));
        Assert.Null(d.Canonicalize(null));
        Assert.Equal(" ", d.Canonicalize(" "));
    }
}
