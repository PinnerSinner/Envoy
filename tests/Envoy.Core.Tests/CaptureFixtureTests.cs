using Envoy.Core.Ocr;
using Xunit;

namespace Envoy.Core.Tests;

public class CaptureFixtureTests
{
    private const string Sample = """
        # A sample
        @me Hoplite
        @language en
        @muted Spammer, Toxic99
        @players 天下第一, Kim
        @set hideSystem=false stitch=true
        @pattern *resigned*
        @pattern re:^Tribute: .*$
        @frame
        @pass zh-CN
         10.0  28.0 | [全 部] 天 下 第 一 ： 这 张 地 图
         32.0  50.0 12 300.5 | 应 该 先 占 领 它
        @pass en
         10  28 | [All] garbage
        @expect
        chat | All | 天下第一 | 这张地图应该先占领它
        @frame
        @set clean=false
        0 18 | Bob resigned.
        @expect
        (none)
        """;

    [Fact]
    public void Round_trips_frames_passes_geometry()
    {
        var fixture = CaptureFixture.Parse(Sample);
        Assert.Equal("Hoplite", fixture.Directives["me"]);
        Assert.Equal("en", fixture.Language);
        Assert.Equal(["*resigned*", "re:^Tribute: .*$"], fixture.Patterns);
        Assert.Equal("false", fixture.Settings["hideSystem"]);
        Assert.Equal(2, fixture.Frames.Count);
        var first = fixture.Frames[0];
        Assert.Equal(["zh-CN", "en"], first.Passes.Select(p => p.LanguageCode));
        Assert.Equal(new OcrLine("[全 部] 天 下 第 一 ： 这 张 地 图", 10, 28), first.Passes[0].Lines[0]);
        Assert.Equal(new OcrLine("应 该 先 占 领 它", 32, 50, 12, 300.5), first.Passes[0].Lines[1]);
        Assert.Equal(["chat | All | 天下第一 | 这张地图应该先占领它"], first.Expect);
        Assert.Equal("false", fixture.Frames[1].Settings["clean"]);
        Assert.Equal("en", Assert.Single(fixture.Frames[1].Passes).LanguageCode);
        Assert.Empty(fixture.Frames[1].Expect);

        var again = CaptureFixture.Parse(fixture.Write());
        Assert.Equal(fixture.Write(), again.Write());
        Assert.Equal(fixture.Directives, again.Directives);
        Assert.Equal(fixture.Patterns, again.Patterns);
        Assert.Equal(fixture.Settings, again.Settings);
        for (var i = 0; i < fixture.Frames.Count; i++)
        {
            Assert.Equal(fixture.Frames[i].Expect, again.Frames[i].Expect);
            Assert.Equal(fixture.Frames[i].Settings, again.Frames[i].Settings);
            Assert.Equal(fixture.Frames[i].Passes.SelectMany(p => p.Lines), again.Frames[i].Passes.SelectMany(p => p.Lines));
            Assert.Equal(fixture.Frames[i].Passes.Select(p => p.LanguageCode), again.Frames[i].Passes.Select(p => p.LanguageCode));
        }
    }

    [Theory]
    [InlineData("@frame\n0 18 | ok\n@expect\nbogus | - | - | x", 4)]
    [InlineData("@me Bob\n\n0 18 | before any frame", 3)]
    [InlineData("@frame\nten 18 | x", 2)]
    [InlineData("@frame\n0 18 x", 2)]
    [InlineData("@frame\n@pattern *x*", 2)]
    [InlineData("@whatever x", 1)]
    [InlineData("@frame\n@expect\nchat | All", 3)]
    public void Parse_error_reports_line_number(string text, int line)
    {
        var ex = Assert.Throws<FormatException>(() => CaptureFixture.Parse(text));
        Assert.StartsWith($"line {line}:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_set_key_is_rejected()
    {
        var ex = Assert.Throws<FormatException>(() => CaptureFixture.Parse("@set stitch=true colourMode=on\n@frame"));
        Assert.Contains("colourMode", ex.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => CaptureFixture.Parse("@set stitch"));
    }

    [Fact]
    public void Write_of_merged_lines_parses()
    {
        var merged = OcrMerger.MergeLines([new OcrPass("en", [new("Bob: we should", 0, 18, 4, 200), new("attack now", 22, 40)])], "en");
        var text = CaptureFixture.Write(merged, "en");
        Assert.StartsWith("#", text, StringComparison.Ordinal);
        var fixture = CaptureFixture.Parse(text);
        var frame = Assert.Single(fixture.Frames);
        Assert.Equal(merged, Assert.Single(frame.Passes).Lines);
        Assert.Empty(frame.Expect);
        Assert.Equal("en", fixture.Language);
    }
}
