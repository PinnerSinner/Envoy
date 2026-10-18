using Envoy.Core.Languages;
using Envoy.Core.Ux;
using Xunit;

namespace Envoy.Core.Tests.Ux;

/// <summary>Spec 09a, REQ-HRD-8: the overlay dispatch's seal glyph and wax come from Core.</summary>
public class LanguageSealTests
{
    [Theory]
    [InlineData("zh-CN", "中", SealWax.Gules)]
    [InlineData("zh-TW", "中", SealWax.Gules)]
    [InlineData("zh-Hans", "中", SealWax.Gules)]
    [InlineData("ZH_cn", "中", SealWax.Gules)]
    [InlineData("ko", "한", SealWax.Azure)]
    [InlineData("ja", "日", SealWax.Or)]
    [InlineData("ru", "Р", SealWax.Vert)]
    [InlineData("en", "E", SealWax.Purpure)]
    [InlineData("en-GB", "E", SealWax.Purpure)]
    [InlineData("de", "G", SealWax.Purpure)]
    [InlineData("uk", "U", SealWax.Purpure)]
    [InlineData("vi", "V", SealWax.Purpure)]
    public void Known_languages(string code, string glyph, SealWax wax)
    {
        var seal = LanguageSeal.For(code);
        Assert.Equal(glyph, seal.Glyph);
        Assert.Equal(wax, seal.Wax);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xx-unknown")]
    [InlineData("auto")]
    public void Unknown_or_missing_code_is_a_question_mark_on_sable(string? code)
    {
        Assert.Equal(LanguageSeal.Unknown, LanguageSeal.For(code));
        Assert.Equal("?", LanguageSeal.Unknown.Glyph);
        Assert.Equal(SealWax.Sable, LanguageSeal.Unknown.Wax);
    }

    [Fact]
    public void Every_catalog_language_has_a_one_letter_seal()
    {
        foreach (var language in LanguageCatalog.All)
        {
            var seal = LanguageSeal.For(language.Code);
            Assert.Equal(1, new System.Globalization.StringInfo(seal.Glyph).LengthInTextElements);
            Assert.NotEqual(LanguageSeal.Unknown, seal);
        }
    }

    [Fact]
    public void Brush_key_names_a_wax_palette_key()
    {
        Assert.Equal("Wax.Gules", LanguageSeal.For("zh-CN").BrushKey);
        foreach (var wax in Enum.GetValues<SealWax>()) Assert.Equal("Wax." + wax, new LanguageSeal("x", wax).BrushKey);
    }
}
