using Envoy.Core.Languages;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests;

public class ScriptDetectorTests
{
    [Theory]
    [InlineData("你好，我们再来一局", "zh-CN")]
    [InlineData("你們這個打法太強了", "zh-TW")]
    [InlineData("안녕하세요 잘 부탁드립니다", "ko")]
    [InlineData("よろしくお願いします", "ja")]
    [InlineData("最強の文明はどれですか", "ja")]
    [InlineData("привет, как дела", "ru")]
    [InlineData("привіт, як справи? Їжак", "uk")]
    [InlineData("مرحبا", "ar")]
    [InlineData("Xin chào, bạn khỏe không", "vi")]
    [InlineData("hello there", null)]
    [InlineData("hola amigo", null)]
    [InlineData("666", null)]
    public void Guesses_language_from_script(string text, string? expected) =>
        Assert.Equal(expected, ScriptDetector.GuessLanguage(text));
}

public class LanguageCatalogTests
{
    [Theory]
    [InlineData("ZH", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh_tw", "zh-TW")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("EN-US", "en")]
    [InlineData("PT-BR", "pt")]
    [InlineData("ko-KR", "ko")]
    [InlineData("RU", "ru")]
    public void Normalizes_provider_codes(string input, string expected) =>
        Assert.Equal(expected, LanguageCatalog.Normalize(input));

    [Fact]
    public void Chinese_variants_count_as_the_same_language() =>
        Assert.True(LanguageCatalog.SameLanguage("zh-CN", "zh-TW"));

    [Fact]
    public void Every_language_has_a_unique_code() =>
        Assert.Equal(LanguageCatalog.All.Count, LanguageCatalog.All.Select(l => l.Code).Distinct().Count());
}

public class SlangGlossaryTests
{
    [Theory]
    [InlineData("666", "Nice")]
    [InlineData("66666!", "Nice")]
    [InlineData("ㅋㅋㅋㅋ", "LOL")]
    [InlineData("ㅈㅈ", "give up")]
    [InlineData("изи", "Easy")]
    [InlineData("jajaja", "Haha")]
    [InlineData("kkkkkk", "LOL")]
    [InlineData("88", "Bye")]
    [InlineData("牛逼", "Awesome")]
    public void Recognises_whole_message_slang(string text, string meaningContains)
    {
        var entry = SlangGlossary.Match(text);
        Assert.NotNull(entry);
        Assert.Contains(meaningContains, entry!.Meaning);
    }

    [Theory]
    [InlineData("66 villagers")]
    [InlineData("let's play again")]
    [InlineData("你好")]
    public void Ignores_normal_messages(string text) => Assert.Null(SlangGlossary.Match(text));
}
