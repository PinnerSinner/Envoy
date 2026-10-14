using Envoy.Core.Settings;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public sealed class TranslationRuntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "envoy-runtime-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_dir, TranslationCacheStore.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static TranslationSettings DeepL(string key = "deepl-key-0001") => new() { Primary = ProviderCatalog.DeepL, Fallbacks = [], DeepLKey = key };

    private static async Task FailDeepLUntilOpen(TranslatorFactory factory, TranslationSettings settings, Microsoft.Extensions.Time.Testing.FakeTimeProvider clock)
    {
        for (var i = 0; i < 5; i++)
        {
            var call = factory.Build(settings).TranslateAsync(new("line " + i, "en"));
            await clock.RunUntil(call, TimeSpan.FromMilliseconds(250));
        }
    }

    [Fact]
    public async Task Breaker_state_survives_factory_rebuild()
    {
        var clock = TestClock.Create();
        var handler = new FakeHttpHandler().Then((req, _) => Task.FromResult(req.RequestUri!.Host.Contains("deepl", StringComparison.Ordinal)
            ? ProviderFixture.Load("deepl", "500").ToResponse()
            : ProviderFixture.Load("google-free", "success").ToResponse()));
        using var runtime = new TranslationRuntime(clock: clock);
        var factory = new TranslatorFactory(handler.Client(), new UsageTracker(clock), runtime);
        var settings = DeepL();
        await FailDeepLUntilOpen(factory, settings, clock);
        Assert.Equal(ProviderState.CoolingDown, runtime.Health.Get(ProviderCatalog.DeepL).State);

        var deeplCalls = handler.Requests.Count(r => r.Uri.Host.Contains("deepl", StringComparison.Ordinal));
        var result = await factory.Build(settings).TranslateAsync(new("after rebuild", "en"));
        Assert.Equal(ProviderCatalog.GoogleFree, result.ProviderId);
        Assert.Equal(deeplCalls, handler.Requests.Count(r => r.Uri.Host.Contains("deepl", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Fingerprint_change_resets_provider()
    {
        var clock = TestClock.Create();
        var handler = new FakeHttpHandler().Then((req, _) => Task.FromResult(req.RequestUri!.Host.Contains("deepl", StringComparison.Ordinal)
            ? ProviderFixture.Load("deepl", "auth").ToResponse()
            : ProviderFixture.Load("google-free", "success").ToResponse()));
        using var runtime = new TranslationRuntime(clock: clock);
        var factory = new TranslatorFactory(handler.Client(), new UsageTracker(clock), runtime);
        await factory.Build(DeepL("bad-key-000001")).TranslateAsync(new("x", "en"));
        Assert.Equal(ProviderState.AuthFailed, runtime.Health.Get(ProviderCatalog.DeepL).State);

        await factory.Build(DeepL("bad-key-000001")).TranslateAsync(new("y", "en")); // same key: still skipped
        Assert.Single(handler.Requests, r => r.Uri.Host.Contains("deepl", StringComparison.Ordinal));

        factory.Build(DeepL("fixed-key-000002"));
        Assert.Equal(ProviderState.Unknown, runtime.Health.Get(ProviderCatalog.DeepL).State);
        Assert.Equal(TranslatorFactory.Fingerprint(ProviderCatalog.DeepL, DeepL("a")), TranslatorFactory.Fingerprint(ProviderCatalog.DeepL, DeepL("a")));
        Assert.NotEqual(TranslatorFactory.Fingerprint(ProviderCatalog.DeepL, DeepL("a")), TranslatorFactory.Fingerprint(ProviderCatalog.DeepL, DeepL("b")));
    }

    [Fact]
    public async Task ResetProvider_closes_breaker_and_clears_health()
    {
        var s = new ResilientSetup(ProviderCatalog.Claude, jitter: 0);
        s.Inner.Default = (_, _) => Task.FromException<TranslationResult>(new TranslationException("x", null, TranslationErrorKind.Auth, 401));
        await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(ResilientSetup.Request()));
        Assert.Equal(ProviderState.AuthFailed, s.Health.State);

        s.Runtime.ResetProvider(ProviderCatalog.Claude);
        Assert.Equal(BreakerState.Closed, s.Guard.Breaker.State);
        Assert.Equal(ProviderHealthSnapshot.Empty(ProviderCatalog.Claude), s.Health);
        Assert.Equal(ProviderState.Unknown, s.Transitions[^1].State);
        s.Inner.Default = null;
        await s.Chat.TranslateAsync(ResilientSetup.Request());
        Assert.Equal(ProviderState.Healthy, s.Health.State);
    }

    [Fact]
    public async Task Diagnostic_raised_per_attempt_with_kind_and_status()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL, jitter: 0);
        s.Inner.Fail(TranslationErrorKind.Transient, 502).Fail(TranslationErrorKind.RateLimited, 429).Succeed();
        await s.Chat.TranslateAsync(ResilientSetup.Request());
        Assert.Equal(3, s.Diagnostics.Count);
        Assert.Equal(("failure", TranslationErrorKind.Transient, 502, 1), (s.Diagnostics[0].Outcome, s.Diagnostics[0].Kind, s.Diagnostics[0].StatusCode, s.Diagnostics[0].Attempt));
        Assert.Equal(("failure", TranslationErrorKind.RateLimited, 429, 2), (s.Diagnostics[1].Outcome, s.Diagnostics[1].Kind, s.Diagnostics[1].StatusCode, s.Diagnostics[1].Attempt));
        Assert.Equal(("success", (TranslationErrorKind?)null, (int?)null, 3), (s.Diagnostics[2].Outcome, s.Diagnostics[2].Kind, s.Diagnostics[2].StatusCode, s.Diagnostics[2].Attempt));
        Assert.All(s.Diagnostics, d => Assert.Equal(ProviderCatalog.DeepL, d.ProviderId));
    }

    [Fact]
    public async Task Disabling_remember_deletes_file()
    {
        var clock = TestClock.Create();
        using var runtime = new TranslationRuntime(CachePath, clock);
        runtime.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = true });
        await new CachingTranslator(new FakeTranslator(), runtime.Cache, "ns").TranslateAsync(new("gg", "en"));
        runtime.Flush();
        Assert.True(File.Exists(CachePath));

        runtime.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = false });
        Assert.False(File.Exists(CachePath));
        Assert.Equal(0, runtime.Cache.Count);

        // A stale file is removed while the option is off.
        File.WriteAllText(CachePath, "{}");
        runtime.Configure(TranslationRuntimeOptions.Default);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task Flush_on_dispose_writes_when_dirty()
    {
        var clock = TestClock.Create();
        var runtime = new TranslationRuntime(CachePath, clock);
        runtime.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = true });
        runtime.Dispose();
        Assert.False(File.Exists(CachePath)); // nothing to save

        using var second = new TranslationRuntime(CachePath, clock);
        second.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = true });
        await new CachingTranslator(new FakeTranslator(), second.Cache, "ns").TranslateAsync(new("gg", "en"));
        Assert.True(second.Cache.IsDirty);
        second.Dispose();
        Assert.True(File.Exists(CachePath));

        using var third = new TranslationRuntime(CachePath, clock);
        third.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = true });
        Assert.True(third.Cache.TryGet(TranslationCache.Key("ns", new("gg", "en")), out _));
    }

    [Fact]
    public async Task Saves_on_the_timer_while_dirty()
    {
        var clock = TestClock.Create();
        using var runtime = new TranslationRuntime(CachePath, clock);
        runtime.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = true });
        await new CachingTranslator(new FakeTranslator(), runtime.Cache, "ns").TranslateAsync(new("gg", "en"));
        Assert.False(File.Exists(CachePath));
        clock.Advance(TranslationRuntime.SaveInterval);
        for (var i = 0; i < 100 && !File.Exists(CachePath); i++) await Task.Delay(10);
        Assert.True(File.Exists(CachePath));
        Assert.False(runtime.Cache.IsDirty);
    }

    [Fact]
    public async Task SuppressPersistence_then_Dispose_does_not_write()
    {
        var clock = TestClock.Create();
        var runtime = new TranslationRuntime(CachePath, clock);
        runtime.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = true });
        await new CachingTranslator(new FakeTranslator(), runtime.Cache, "ns").TranslateAsync(new("gg", "en"));
        runtime.SuppressPersistence();
        runtime.Flush();
        clock.Advance(TranslationRuntime.SaveInterval * 3);
        runtime.Dispose();
        Assert.False(File.Exists(CachePath));
        Assert.True(runtime.PersistenceSuppressed);
    }

    [Fact]
    public async Task Clear_waits_for_inflight_save()
    {
        var clock = TestClock.Create();
        using var runtime = new TranslationRuntime(CachePath, clock);
        runtime.Configure(TranslationRuntimeOptions.Default with { RememberTranslations = true });
        await new CachingTranslator(new FakeTranslator(), runtime.Cache, "ns").TranslateAsync(new("gg", "en"));

        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        runtime.Store!.BeforeWrite = () =>
        {
            writing.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };
        var save = Task.Run(runtime.Flush);
        Assert.True(writing.Wait(TimeSpan.FromSeconds(5)));
        var clear = Task.Run(runtime.ClearCache);
        await Task.Delay(50);
        Assert.False(clear.IsCompleted); // waits for the save to finish
        release.Set();
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await clear.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(CachePath)); // the save never overlapped or outlived the delete
        Assert.Equal(0, runtime.Cache.Count);
    }

    [Fact]
    public void Configure_resizes_the_cache()
    {
        using var runtime = new TranslationRuntime(clock: TestClock.Create());
        runtime.Configure(TranslationRuntimeOptions.Default with { CacheMaxEntries = 150 });
        Assert.Equal(150, runtime.Cache.Capacity);
        Assert.Equal(150, runtime.Options.CacheMaxEntries);
    }
}
