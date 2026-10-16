using System.Diagnostics;
using Envoy.Core.Chat;
using Xunit;

namespace Envoy.Core.Tests;

public class SystemLinePatternsTests
{
    private static SystemLinePatterns Compile(params string[] patterns)
    {
        var compiled = SystemLinePatterns.Compile(patterns, out var errors);
        Assert.Empty(errors);
        return compiled;
    }

    [Fact]
    public void Wildcard_player_and_regex_forms()
    {
        var p = Compile("*resigned*", "{player} advanced to the  Castle Age", "re:^Tribute: \\d+ (gold|food)$");
        Assert.True(p.IsMatch("Bob has RESIGNED."));
        Assert.True(p.IsMatch("Zhang Wei advanced to the Castle Age"));
        Assert.False(p.IsMatch("advanced to the Castle Age"));
        Assert.False(p.IsMatch("Someone with a name longer than thirty-two characters advanced to the Castle Age"));
        Assert.True(p.IsMatch("Tribute: 100 gold"));
        Assert.False(p.IsMatch("Tribute: lots of gold"));
        Assert.False(p.IsMatch("Bob: are you ready?"));
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void Patterns_match_the_whole_line()
    {
        var p = Compile("gg");
        Assert.True(p.IsMatch("GG"));
        Assert.False(p.IsMatch("Bob: gg"));
    }

    [Fact]
    public void Matches_with_and_without_timestamp()
    {
        var p = Compile("{player} has resigned.");
        Assert.True(p.IsMatch("Bob has resigned."));
        Assert.True(p.IsMatch("12:04 Bob has resigned."));
        Assert.True(p.IsMatch("[12:04:33] Bob has resigned."));
    }

    [Fact]
    public void Comments_and_blanks_ignored()
    {
        var p = SystemLinePatterns.Compile(["# a comment", "", "   ", "*left the game"], out var errors);
        Assert.Empty(errors);
        Assert.Equal(1, p.Count);
        Assert.True(p.IsMatch("Kim left the game"));
        Assert.False(p.IsMatch("# a comment"));
        Assert.Equal(0, SystemLinePatterns.Empty.Count);
        Assert.False(SystemLinePatterns.Empty.IsMatch("anything"));
    }

    [Fact]
    public void Invalid_regex_reported_and_skipped()
    {
        var p = SystemLinePatterns.Compile(["*ok*", "re:([unclosed", "re:", "*fine*"], out var errors);
        Assert.Equal(2, p.Count);
        Assert.Equal([1, 2], errors.Select(e => e.Index));
        Assert.Equal("re:([unclosed", errors[0].Pattern);
        Assert.False(string.IsNullOrEmpty(errors[0].Message));
        Assert.True(p.IsMatch("this is fine"));
    }

    [Fact]
    public void Catastrophic_regex_times_out_as_no_match()
    {
        var p = Compile("re:(a+)+$");
        var sw = Stopwatch.StartNew();
        Assert.False(p.IsMatch(new string('a', 40) + "!"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void SplitLines_trims_and_drops_blanks()
    {
        Assert.Equal(["*resigned*", "# note", "re:x"], SystemLinePatterns.SplitLines("  *resigned*  \r\n\n# note\r\n   \nre:x\n"));
        Assert.Empty(SystemLinePatterns.SplitLines(null));
    }
}
