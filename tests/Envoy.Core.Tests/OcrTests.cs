using Envoy.Core.Ocr;
using Xunit;

namespace Envoy.Core.Tests;

public class OcrTextTests
{
    [Theory]
    [InlineData("小 明 : 你 好 吗 ？", "小明 : 你好吗？")]
    [InlineData("[All] 小 明: 再 来 一 局", "[All] 小明: 再来一局")]
    [InlineData("안녕 하세요 잘 부탁", "안녕 하세요 잘 부탁")]
    [InlineData("hello   there", "hello there")]
    [InlineData("よ ろ し く", "よろしく")]
    public void Removes_spaces_between_cjk_characters_only(string input, string expected) =>
        Assert.Equal(expected, OcrText.Clean(input));
}

public class OcrMergerTests
{
    [Fact]
    public void Picks_the_engine_that_read_each_line_in_its_own_script()
    {
        var en = new OcrPass("en", [
            new("Bob: are you rushing", 0, 20),
            new("Li: f* i?", 25, 45),
            new("Kim: OXO", 50, 70),
        ]);
        var zh = new OcrPass("zh-CN", [
            new("Bob: are you rushlng", 1, 21),
            new("Li: 你 好", 26, 44),
            new("Kim: 0", 51, 69),
        ]);
        var ko = new OcrPass("ko", [
            new("Bob : are you rushing", 0, 20),
            new("Li: 01", 25, 45),
            new("Kim: 안녕", 50, 70),
        ]);

        var lines = OcrMerger.Merge([en, zh, ko], "en");

        Assert.Equal(["Bob: are you rushing", "Li: 你好", "Kim: 안녕"], lines);
    }

    [Fact]
    public void Keeps_line_order_top_to_bottom()
    {
        var en = new OcrPass("en", [new("second", 30, 50), new("first", 0, 20)]);
        Assert.Equal(["first", "second"], OcrMerger.Merge([en], "en"));
    }

    [Fact]
    public void Empty_passes_produce_no_lines() =>
        Assert.Empty(OcrMerger.Merge([new OcrPass("en", []), new OcrPass("zh-CN", [new(" ", 0, 10)])], "en"));

    [Fact]
    public void MergeLines_keeps_median_geometry_per_row()
    {
        var en = new OcrPass("en", [new("Bob: hi", 0, 20), new("Li: f* i?", 30, 52)]);
        var zh = new OcrPass("zh-CN", [new("Bob: hl", 2, 22), new("Li: 你 好", 31, 49)]);
        var ko = new OcrPass("ko", [new("Bob : hi", 1, 21), new("Li: 01", 29, 50)]);

        var lines = OcrMerger.MergeLines([en, zh, ko], "en");

        Assert.Equal(2, lines.Count);
        Assert.Equal(new OcrLine("Bob: hi", 1, 21), lines[0]);
        Assert.Equal(new OcrLine("Li: 你好", 30, 50), lines[1]);

        // An even number of readings takes the mean of the middle two.
        var two = OcrMerger.MergeLines([new OcrPass("en", [new("x y", 10, 20)]), new OcrPass("ru", [new("x y", 12, 24)])], "en");
        Assert.Equal(11, two[0].Top);
        Assert.Equal(22, two[0].Bottom);
    }

    [Fact]
    public void MergeLines_text_matches_Merge()
    {
        var en = new OcrPass("en", [new("Bob: are you rushing", 0, 20), new("Li: f* i?", 25, 45), new("Kim: OXO", 50, 70)]);
        var zh = new OcrPass("zh-CN", [new("Bob: are you rushlng", 1, 21), new("Li: 你 好", 26, 44), new("Kim: 0", 51, 69)]);
        var ko = new OcrPass("ko", [new("Bob : are you rushing", 0, 20), new("Li: 01", 25, 45), new("Kim: 안녕", 50, 70)]);
        OcrPass[] passes = [en, zh, ko];

        Assert.Equal(OcrMerger.Merge(passes, "en"), OcrMerger.MergeLines(passes, "en").Select(l => l.Text));
        Assert.Equal(OcrMerger.Merge(passes, "ko"), OcrMerger.MergeLines(passes, "ko").Select(l => l.Text));
        Assert.Empty(OcrMerger.MergeLines([], "en"));
    }

    [Fact]
    public void MergeLines_carries_left_right_medians()
    {
        // Spec 04 (REQ-CHAT-4) replaced 05's "reserved zero" check: Left/Right are the medians of the readings that have them.
        var en = new OcrPass("en", [new("hello there", 0, 20, 15, 220)]);
        var zh = new OcrPass("zh-CN", [new("hello there", 1, 21, 17, 230)]);
        var ko = new OcrPass("ko", [new("hello there", 0, 20, 16, 226), new("no words", 40, 60)]);
        var lines = OcrMerger.MergeLines([en, zh, ko], "en");
        Assert.Equal(16, lines[0].Left);
        Assert.Equal(226, lines[0].Right);
        Assert.Equal(0, lines[1].Left);   // rows without word boxes keep 0
        Assert.Equal(0, lines[1].Right);
        Assert.Equal(0, new OcrLine("x", 1, 2).Left);
    }
}
