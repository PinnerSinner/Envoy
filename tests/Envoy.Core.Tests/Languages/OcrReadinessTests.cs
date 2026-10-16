using Envoy.Core.Languages;
using Xunit;

namespace Envoy.Core.Tests.Languages;

public class OcrReadinessTests
{
    [Fact]
    public void Missing_excludes_installed_and_normalises_codes()
    {
        var missing = OcrReadiness.MissingWanted(["zh-Hans", "KO", "ja", "ru", "ko"], "en", ["en-US", "ko-KR", "zh-hans-cn"]);
        Assert.Equal(["ja", "ru"], missing);
    }

    [Fact]
    public void My_language_is_always_wanted()
    {
        Assert.Equal(["ja", "ko"], OcrReadiness.MissingWanted(["ko"], "ja", ["en-US"]));
        Assert.Empty(OcrReadiness.MissingWanted(["ko"], "ja", ["ja-JP", "ko-KR"]));
    }

    [Fact]
    public void Latin_language_covered_by_any_latin_pack_except_vi()
    {
        Assert.Empty(OcrReadiness.MissingWanted(["de", "fr", "es"], "pt", ["en-US"]));
        Assert.Equal(["vi"], OcrReadiness.MissingWanted(["vi", "de"], "en", ["en-US"]));
        Assert.Equal(["de"], OcrReadiness.MissingWanted(["de"], "ru", ["ru-RU"])); // no Latin pack at all
    }

    [Fact]
    public void Empty_wanted_means_only_my_language_can_be_missing()
    {
        Assert.Equal(["ko"], OcrReadiness.MissingWanted([], "ko", ["en-US", "ja-JP"]));
        Assert.Empty(OcrReadiness.MissingWanted([], "en", ["en-GB"]));
    }

    [Fact]
    public void Recommend_orders_mine_then_wanted_then_common_distinct()
    {
        var rows = OcrReadiness.Recommend("en", ["ru", "uk"], ["en-US", "ru-RU"]);
        Assert.Equal(["en", "ru", "uk", "zh-CN", "ko", "ja"], rows.Select(r => r.Code));
        Assert.True(rows[0].IsMine && rows[0].Installed && rows[0].Wanted);
        Assert.True(rows[1].Installed && rows[1].Wanted && !rows[1].IsMine);
        Assert.True(!rows[2].Installed && rows[2].Wanted);
        Assert.True(!rows[3].Installed && !rows[3].Wanted);
        Assert.Equal("Ukrainian", rows[2].Name);
    }

    [Fact]
    public void Recommend_uses_common_languages_when_nothing_is_wanted()
    {
        var rows = OcrReadiness.Recommend("ko", [], []);
        Assert.Equal(["ko", "zh-CN", "ja", "ru"], rows.Select(r => r.Code));
        Assert.All(rows, r => Assert.False(r.Installed));
    }
}
