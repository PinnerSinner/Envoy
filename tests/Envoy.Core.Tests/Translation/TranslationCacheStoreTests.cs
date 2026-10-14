using System.Text;
using Envoy.Core.Diagnostics;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public sealed class TranslationCacheStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "envoy-cache-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_dir, TranslationCacheStore.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Roundtrip_preserves_entries_and_lru_order()
    {
        var clock = TestClock.Create();
        var cache = new TranslationCache(100, clock);
        cache.Set("a", new TranslationResult("A", "zh-CN", "google-free"), true);
        clock.Advance(TimeSpan.FromSeconds(1));
        cache.Set("b", new TranslationResult("B", null, "deepl"), true);
        cache.Set("c", new TranslationResult("C", "ko", "google-free"), true);
        Assert.True(cache.TryGet("a", out _)); // order now a, c, b

        var store = new TranslationCacheStore(CachePath);
        Assert.True(store.Save(cache.Export()));
        var loaded = store.Load();
        Assert.Equal(cache.Export(), loaded);

        var restored = new TranslationCache(100, clock);
        restored.Import(loaded);
        Assert.Equal(cache.Export(), restored.Export());
        Assert.True(restored.TryGet("b", out var b));
        Assert.Equal(new TranslationResult("B", null, "deepl"), b);
        Assert.False(restored.IsDirty);
    }

    [Fact]
    public void Save_is_atomic_and_capped_at_max_bytes()
    {
        var entries = Enumerable.Range(0, 200)
            .Select(i => new CacheEntry(TranslationCache.Hash("k" + i), new string('x', 100), "zh-CN", "google-free", TestClock.Start))
            .ToList();
        var store = new TranslationCacheStore(CachePath);
        Assert.True(store.Save(entries, maxBytes: 8 * 1024));

        Assert.False(File.Exists(store.TempPath)); // no .tmp left behind
        Assert.True(new FileInfo(CachePath).Length <= 8 * 1024);
        var loaded = store.Load();
        Assert.InRange(loaded.Count, 1, 199);
        Assert.Equal(entries.Take(loaded.Count), loaded); // the most recent entries are kept
    }

    [Fact]
    public void Expired_entries_dropped_on_import()
    {
        var clock = TestClock.Create();
        var cache = new TranslationCache(10, clock);
        cache.Import([
            new CacheEntry(TranslationCache.Hash("fresh"), "F", null, "google-free", clock.GetUtcNow().AddDays(-29)),
            new CacheEntry(TranslationCache.Hash("old"), "O", null, "google-free", clock.GetUtcNow().AddDays(-31)),
        ]);
        Assert.True(cache.TryGet("fresh", out _));
        Assert.False(cache.TryGet("old", out _));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Long_messages_not_persisted()
    {
        var clock = TestClock.Create();
        using var runtime = new TranslationRuntime(CachePath, clock);
        runtime.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = true });
        var caching = new CachingTranslator(new FakeTranslator(), runtime.Cache, "google-free");
        await caching.TranslateAsync(new(new string('長', 201), "en"));
        await caching.TranslateAsync(new("gg wp", "en"));
        runtime.Flush();
        var saved = new TranslationCacheStore(CachePath).Load();
        Assert.Single(saved);
    }

    [Fact]
    public void Corrupt_file_renamed_to_bad()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, "{ \"version\": 1, \"entries\": [ broken");
        var hub = new LogHub(TestClock.Create());
        var sink = new MemoryLogSink();
        hub.AddSink(sink);
        var store = new TranslationCacheStore(CachePath, hub.For("Translation"));

        Assert.Empty(store.Load());
        Assert.False(File.Exists(CachePath));
        Assert.True(File.Exists(store.BadPath));
        Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warning && e.Category == "Translation");
        Assert.NotNull(DataInventory.Match(TranslationCacheStore.FileName + ".bad"));
        Assert.NotNull(DataInventory.Match(TranslationCacheStore.FileName + ".tmp"));
    }

    [Fact]
    public async Task Disk_file_contains_only_key_hashes()
    {
        const string original = "你好，再来一局？ gg wp";
        var clock = TestClock.Create();
        using (var runtime = new TranslationRuntime(CachePath, clock))
        {
            runtime.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = true });
            await new CachingTranslator(new FakeTranslator { Respond = _ => new("Hello, rematch? gg wp!", "zh-CN", "google-free") }, runtime.Cache, "google-free")
                .TranslateAsync(new(original, "en"));
        } // Dispose flushes

        var raw = File.ReadAllText(CachePath, Encoding.UTF8);
        Assert.DoesNotContain(original, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("你好", raw, StringComparison.Ordinal);
        Assert.Contains("\"version\":1", raw, StringComparison.Ordinal);
        Assert.Contains(TranslationCache.Hash(TranslationCache.Key("google-free", new(original, "en"))), raw, StringComparison.Ordinal);
        Assert.Contains("\"at\":\"2026-10-14T12:00:00.000Z\"", raw, StringComparison.Ordinal);
    }
}
