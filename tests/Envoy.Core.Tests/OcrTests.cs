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
}
