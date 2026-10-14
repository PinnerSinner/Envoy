using Envoy.Core.Settings;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class TranslationCacheTests
{
    private static TranslationResult Result(string text) => new(text, "zh-CN", "google-free");

    [Fact]
    public void Lru_evicts_oldest_at_capacity()
    {
        var cache = new TranslationCache(3, TestClock.Create());
        cache.Set("a", Result("A"), true);
        cache.Set("b", Result("B"), true);
        cache.Set("c", Result("C"), true);
        Assert.True(cache.TryGet("a", out _)); // a is now the most recent
        cache.Set("d", Result("D"), true);
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
        Assert.Equal(3, cache.Count);

        cache.Capacity = 1;
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("d", out var kept));
        Assert.Equal("D", kept.Text);
    }

    [Fact]
    public async Task Cache_survives_rebuild_in_runtime()
    {
        var clock = TestClock.Create();
        var handler = new FakeHttpHandler().Respond(ProviderFixture.Load("google-free", "success"));
        using var runtime = new TranslationRuntime(clock: clock);
        var factory = new TranslatorFactory(handler.Client(), new UsageTracker(clock), runtime);
        var settings = new TranslationSettings();

        await factory.Build(settings).TranslateAsync(new("你好，再来一局？", "en"));
        settings.SkipMyLanguage = !settings.SkipMyLanguage; // any settings edit rebuilds the stack
        var again = await factory.Build(settings).TranslateAsync(new("你好，再来一局？", "en"));

        Assert.True(again.FromCache);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Switching_primary_provider_misses_cache()
    {
        var clock = TestClock.Create();
        var handler = new FakeHttpHandler()
            .Respond(ProviderFixture.Load("google-free", "success"))
            .Respond(System.Net.HttpStatusCode.OK, "token", "text/plain")
            .Respond(ProviderFixture.Load("edge-free", "success"));
        using var runtime = new TranslationRuntime(clock: clock);
        var factory = new TranslatorFactory(handler.Client(), new UsageTracker(clock), runtime);

        var google = await factory.Build(new TranslationSettings()).TranslateAsync(new("你好", "en"));
        var edge = await factory.Build(new TranslationSettings { Primary = ProviderCatalog.EdgeFree, Fallbacks = [] }).TranslateAsync(new("你好", "en"));

        Assert.Equal(ProviderCatalog.GoogleFree, google.ProviderId);
        Assert.False(edge.FromCache);
        Assert.Equal(ProviderCatalog.EdgeFree, edge.ProviderId);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public void Claude_model_is_part_of_the_namespace()
    {
        var opus = new TranslationSettings { Primary = ProviderCatalog.Claude, ClaudeModel = "claude-opus-5-5" };
        var haiku = new TranslationSettings { Primary = ProviderCatalog.Claude, ClaudeModel = "claude-haiku-5-5" };
        Assert.NotEqual(TranslatorFactory.CacheNamespace(opus), TranslatorFactory.CacheNamespace(haiku));
        Assert.Equal(ProviderCatalog.DeepL, TranslatorFactory.CacheNamespace(new TranslationSettings { Primary = ProviderCatalog.DeepL }));
    }

    [Fact]
    public async Task Glossary_and_long_results_are_not_persistable()
    {
        var cache = new TranslationCache(10, TestClock.Create());
        var inner = new FakeTranslator { Respond = r => new TranslationResult("x", null, "fake", FromGlossary: r.Text == "gloss") };
        var caching = new CachingTranslator(inner, cache, "ns");
        await caching.TranslateAsync(new("gloss", "en"));
        await caching.TranslateAsync(new(new string('a', 201), "en"));
        await caching.TranslateAsync(new("short", "en"));
        Assert.Equal(3, cache.Count);
        Assert.Equal(1, cache.PersistableCount);
        Assert.Single(cache.Export());
    }
}
