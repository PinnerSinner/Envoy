using Envoy.Core.Updates;
using Xunit;

namespace Envoy.Core.Tests.Updates;

public class ReleaseNotesTextTests
{
    [Fact]
    public void Strips_headings_and_emphasis()
    {
        var text = ReleaseNotesText.ToPlain("## Added\n- **Bold** and __strong__ with `code`\n* star bullet\n### Fixed\nA fix.");
        Assert.Equal("Added\n• Bold and strong with code\n• star bullet\nFixed\nA fix.", text);
    }

    [Fact]
    public void Links_become_their_text() =>
        Assert.Equal("See the privacy page and logo.", ReleaseNotesText.ToPlain("See the [privacy page](docs/privacy.md) and ![logo](a.png)."));

    [Fact]
    public void Collapses_blank_lines() =>
        Assert.Equal("One\n\nTwo", ReleaseNotesText.ToPlain("\n\nOne\n\n\n\nTwo\n\n"));

    [Fact]
    public void Truncates_at_line_boundary_with_ellipsis()
    {
        var lines = Enumerable.Range(0, 100).Select(i => $"- Line {i:000} with a little text to fill the row");
        var markdown = string.Join("\n", lines);
        Assert.True(markdown.Length > 3000);
        var text = ReleaseNotesText.ToPlain(markdown);
        Assert.True(text.Length <= 1201, $"length {text.Length}");
        Assert.EndsWith("…", text);
        Assert.EndsWith("fill the row…", text); // cut at a line break, not mid-line
        var single = ReleaseNotesText.ToPlain(new string('x', 3000));
        Assert.True(single.Length <= 1201);
        Assert.EndsWith("…", single);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n \n")]
    public void Null_or_blank_gives_empty(string? markdown) => Assert.Equal("", ReleaseNotesText.ToPlain(markdown));
}
