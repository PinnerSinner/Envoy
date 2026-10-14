using System.Net;
using Envoy.Core.Diagnostics;
using Envoy.Core.Settings;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class ProviderHealthTests
{
    private static readonly TranslationRequest Request = ResilientSetup.Request();

    private static Task<TranslationResult> Fails(TranslationRequest r, CancellationToken ct) =>
        Task.FromException<TranslationResult>(new TranslationException("x", null, TranslationErrorKind.Transient, 503));

    [Fact]
    public void Records_success_latency_median_of_last_20()
    {
        var registry = new ProviderHealthRegistry(TestClock.Create());
        var entry = registry.Entry("p");
        for (var ms = 1; ms <= 25; ms++) entry.RecordSuccess(TimeSpan.FromMilliseconds(ms), firstAttempt: true);
        var s = registry.Get("p");
        Assert.Equal(TimeSpan.FromMilliseconds(15.5), s.MedianLatency); // median of 6..25
        Assert.Equal(25, s.Successes);
        Assert.Equal(ProviderState.Healthy, s.State);
    }

    [Fact]
    public async Task State_transitions_follow_breaker()
    {
        var s = new ResilientSetup(ProviderCatalog.Claude, jitter: 0);
        await s.Chat.TranslateAsync(Request);
        Assert.Equal(ProviderState.Healthy, s.Health.State);

        s.Inner.Default = Fails;
        await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        Assert.Equal(ProviderState.Degraded, s.Health.State);
        await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        Assert.Equal(ProviderState.CoolingDown, s.Health.State);
        Assert.Equal(s.Clock.GetUtcNow() + TimeSpan.FromSeconds(20), s.Health.RetryAt);
        Assert.Equal(3, s.Health.ConsecutiveFailures);

        s.Clock.Advance(TimeSpan.FromSeconds(20));
        s.Inner.Default = null;
        await s.Chat.TranslateAsync(Request);
        Assert.Equal(ProviderState.Healthy, s.Health.State);
        Assert.Equal(
            [ProviderState.Healthy, ProviderState.Degraded, ProviderState.CoolingDown, ProviderState.Healthy],
            s.Transitions.Select(t => t.State));
    }

    [Fact]
    public async Task StateChanged_raised_once_per_transition_only()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL);
        for (var i = 0; i < 5; i++)
        {
            s.Clock.Advance(TimeSpan.FromMilliseconds(10));
            await s.Chat.TranslateAsync(Request);
        }
        var only = Assert.Single(s.Transitions);
        Assert.Equal(ProviderState.Healthy, only.State);
        Assert.Equal(5, s.Health.Successes); // counts and latency changed without another event
    }

    [Fact]
    public async Task Local_rejection_transition_raises_StateChanged()
    {
        var s = new ResilientSetup(ProviderCatalog.GoogleFree);
        for (var i = 0; i < 6; i++) await s.Chat.TranslateAsync(Request);
        Assert.Equal(ProviderState.Healthy, Assert.Single(s.Transitions).State);
        var waiting = Enumerable.Range(0, 3).Select(_ => s.Chat.TranslateAsync(Request)).ToList();
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => s.Chat.TranslateAsync(Request));
        Assert.Equal(ProviderState.Throttled, s.Transitions[^1].State);
        Assert.Equal(2, s.Transitions.Count);
        await s.Clock.RunUntil(Task.WhenAll(waiting), TimeSpan.FromMilliseconds(250));
        Assert.Equal(ProviderState.Healthy, s.Transitions[^1].State); // the next real outcome
    }

    [Fact]
    public async Task Breaker_skip_after_a_display_change_raises_StateChanged()
    {
        var s = new ResilientSetup(ProviderCatalog.Claude, jitter: 0);
        s.Inner.Default = Fails;
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        s.Runtime.Health.Seed(s.Health with { State = ProviderState.Healthy });
        var before = s.Transitions.Count;
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => s.Chat.TranslateAsync(Request));
        Assert.Equal(before + 1, s.Transitions.Count);
        Assert.Equal(ProviderState.CoolingDown, s.Transitions[^1].State);
    }

    [Fact]
    public void Build_failure_shows_not_set_up()
    {
        var clock = TestClock.Create();
        using var runtime = new TranslationRuntime(clock: clock);
        var factory = new TranslatorFactory(new FakeHttpHandler().Client(), new UsageTracker(clock), runtime);
        factory.Build(new TranslationSettings { Primary = ProviderCatalog.DeepL, Fallbacks = [] });
        var s = runtime.Health.Get(ProviderCatalog.DeepL);
        Assert.Equal(ProviderState.Unknown, s.State);
        Assert.Equal(TranslationErrorKind.Configuration, s.LastErrorKind);
        Assert.Equal(ProviderHealthText.NotSetUp, ProviderHealthText.Describe(s, clock.GetUtcNow()));
        Assert.Equal([ProviderCatalog.DeepL, ProviderCatalog.GoogleFree], TranslatorFactory.ChainIds(new TranslationSettings { Primary = ProviderCatalog.DeepL, Fallbacks = [] }));

        // Adding the key starts the provider from a fresh record.
        factory.Build(new TranslationSettings { Primary = ProviderCatalog.DeepL, Fallbacks = [], DeepLKey = "a-key-123456" });
        Assert.Null(runtime.Health.Get(ProviderCatalog.DeepL).LastErrorKind);
    }

    [Fact]
    public async Task Last_error_never_contains_request_text_or_key()
    {
        const string text = "秘密的聊天内容 secret chat line";
        const string key = "deepl-secret-key-0123456789";
        var clock = TestClock.Create();
        var hub = new LogHub(clock);
        var sink = new MemoryLogSink();
        hub.AddSink(sink);
        var echo = $"{{\"message\":\"you sent {text} with {key}\"}}";
        var handler = new FakeHttpHandler().Then((req, _) =>
        {
            var status = req.RequestUri!.Host.Contains("deepl", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : HttpStatusCode.InternalServerError;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(echo) });
        });
        using var runtime = new TranslationRuntime(clock: clock);
        var diagnostics = new List<TranslationDiagnostic>();
        runtime.Diagnostic += diagnostics.Add;
        using var logging = TranslationLogging.Attach(runtime, hub.For("Translation"));
        var translator = new TranslatorFactory(handler.Client(), new UsageTracker(clock), runtime)
            .Build(new TranslationSettings { Primary = ProviderCatalog.DeepL, Fallbacks = [ProviderCatalog.LibreTranslate], DeepLKey = key, LibreTranslateKey = key });

        var call = translator.TranslateAsync(new TranslationRequest(text, "en"));
        await clock.RunUntil(call, TimeSpan.FromMilliseconds(250));
        var aggregate = await Assert.ThrowsAsync<TranslationException>(() => call);

        var everything = new List<string> { aggregate.Message };
        everything.AddRange(runtime.Health.Snapshot([ProviderCatalog.DeepL, ProviderCatalog.LibreTranslate, ProviderCatalog.GoogleFree]).Select(h => h.LastError ?? ""));
        everything.AddRange(diagnostics.Select(d => d.ToString()));
        everything.AddRange(sink.Snapshot().Select(LogFormatter.Format));
        Assert.NotEmpty(diagnostics);
        Assert.Contains(sink.Snapshot(), e => e.Message.StartsWith(TranslationLogging.AttemptMessage, StringComparison.Ordinal));
        foreach (var line in everything)
        {
            Assert.DoesNotContain(text, line, StringComparison.Ordinal);
            Assert.DoesNotContain("secret chat", line, StringComparison.Ordinal);
            Assert.DoesNotContain(key, line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Budget_exceeded_marks_quota_until_midnight()
    {
        var clock = TestClock.Create(); // 12:00 UTC, local zone UTC
        var handler = new FakeHttpHandler().Respond(ProviderFixture.Load("google-free", "success"));
        using var runtime = new TranslationRuntime(clock: clock);
        var translator = new TranslatorFactory(handler.Client(), new UsageTracker(clock), runtime)
            .Build(new TranslationSettings { Primary = ProviderCatalog.DeepL, Fallbacks = [], DeepLKey = "a-key-123456", DailyCharacterLimit = 1 });

        var result = await translator.TranslateAsync(new TranslationRequest("more than one character", "en"));
        Assert.Equal(ProviderCatalog.GoogleFree, result.ProviderId); // the fallback served it
        var deepl = runtime.Health.Get(ProviderCatalog.DeepL);
        Assert.Equal(ProviderState.QuotaExceeded, deepl.State);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), deepl.RetryAt);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host.Contains("deepl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seed_does_not_touch_breaker()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL);
        s.Runtime.Health.Seed(ProviderHealthSnapshot.Empty(ProviderCatalog.DeepL) with
        {
            State = ProviderState.CoolingDown,
            RetryAt = s.Clock.GetUtcNow().AddMinutes(1),
        });
        Assert.Equal(ProviderState.CoolingDown, s.Health.State);
        Assert.Equal(BreakerState.Closed, s.Guard.Breaker.State);
        await s.Chat.TranslateAsync(Request); // not skipped
        Assert.Equal(1, s.Inner.Calls);
        Assert.Equal(ProviderState.Healthy, s.Health.State);
    }
}
