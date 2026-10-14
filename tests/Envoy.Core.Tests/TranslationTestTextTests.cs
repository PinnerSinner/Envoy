using Envoy.Core.Languages;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests;

public class TranslationTestTextTests
{
    [Fact]
    public void Describe_KnownProvider_UsesDisplayName()
    {
        var result = new TranslationResult("Hello! One more game?", "zh-CN", ProviderCatalog.DeepL);
        var expected = $"Hello! One more game?    ({LanguageCatalog.NameOf("zh-CN")} → {LanguageCatalog.NameOf("en")}, via {ProviderCatalog.Find(ProviderCatalog.DeepL)!.Name})";
        Assert.Equal(expected, TranslationTestText.Describe(result, "en"));
        Assert.Equal("Hello! One more game?    (Chinese (Simplified) → English, via DeepL)", TranslationTestText.Describe(result, "en"));
    }

    [Fact]
    public void Describe_UnknownProvider_FallsBackToId()
    {
        var result = new TranslationResult("Hi", "ko", "my-proxy");
        Assert.Equal("Hi    (Korean → German, via my-proxy)", TranslationTestText.Describe(result, "de"));
    }

    [Fact]
    public void Describe_NullDetected_UsesNameOfNull()
    {
        var result = new TranslationResult("Hi", null, ProviderCatalog.GoogleFree);
        var expected = $"Hi    ({LanguageCatalog.NameOf(null)} → English, via Google Translate (free))";
        Assert.Equal(expected, TranslationTestText.Describe(result, "en"));
    }

    [Fact]
    public void Failure_PrefixesWarning()
    {
        Assert.Equal("⚠ Network down", TranslationTestText.Failure(new HttpRequestException("Network down")));
    }

    [Fact]
    public void SampleAndPending_MatchLegacyText()
    {
        Assert.Equal("你好！再来一局吗？", TranslationTestText.SampleInput);
        Assert.Equal("…", TranslationTestText.Pending);
    }
}
