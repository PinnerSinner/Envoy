using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class FallbackDeadlineTests
{
    private static readonly TranslationRequest Request = new("你好", "en");

    [Fact]
    public async Task HttpClient_timeout_falls_through_to_next_provider()
    {
        var slow = new FakeTranslator("a") { Throw = new TaskCanceledException("HttpClient.Timeout", new TimeoutException()) };
        var working = new FakeTranslator("b");
        var failures = new List<(string Id, Exception Error)>();
        var chain = new FallbackTranslator([slow, working]);
        chain.ProviderFailed += (id, ex) => failures.Add((id, ex));

        var result = await chain.TranslateAsync(Request);
        Assert.Equal("b", result.ProviderId);
        var (id, error) = Assert.Single(failures);
        Assert.Equal("a", id);
        Assert.Equal(TranslationErrorKind.Timeout, Assert.IsAssignableFrom<TranslationException>(error).Kind);
    }

    [Fact]
    public async Task Caller_cancellation_still_throws_OperationCanceled()
    {
        var chain = new FallbackTranslator([new ScriptedTranslator("a").Hang(), new FakeTranslator("b")], () => TimeSpan.FromSeconds(15), TestClock.Create());
        using var cts = new CancellationTokenSource();
        var call = chain.TranslateAsync(Request, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task Overall_deadline_throws_Timeout_not_OperationCanceled()
    {
        var clock = TestClock.Create();
        var first = new ScriptedTranslator("a").Hang();
        var second = new ScriptedTranslator("b").Hang();
        var chain = new FallbackTranslator([first, second], () => TimeSpan.FromSeconds(15), clock);
        var call = chain.TranslateAsync(Request);
        Assert.False(call.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(15));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TranslationErrorKind.Timeout, ex.Kind);
        Assert.Equal("No translation within 15 s.", ex.Message);
        Assert.Equal(0, second.Calls); // the deadline is shared: nothing left for the next provider
    }

    [Fact]
    public async Task Deadline_follows_Configure()
    {
        var clock = TestClock.Create();
        using var runtime = new TranslationRuntime(clock: clock);
        var chain = new FallbackTranslator([new ScriptedTranslator("a") { Default = Hang }], () => runtime.Options.RequestTimeout, clock);

        runtime.Configure(TranslationRuntimeOptions.Default with { RequestTimeout = TimeSpan.FromSeconds(3) });
        var first = chain.TranslateAsync(Request);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("No translation within 3 s.", (await Assert.ThrowsAsync<TranslationException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)))).Message);

        runtime.Configure(TranslationRuntimeOptions.Default with { RequestTimeout = TimeSpan.FromSeconds(7) });
        var second = chain.TranslateAsync(Request);
        clock.Advance(TimeSpan.FromSeconds(3));
        await TestClock.Settle();
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("No translation within 7 s.", (await Assert.ThrowsAsync<TranslationException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)))).Message);
    }

    [Fact]
    public async Task Unavailable_providers_skipped_without_ProviderFailed_event()
    {
        var skipped = new FakeTranslator("a") { Throw = new ProviderUnavailableException("a", ProviderState.CoolingDown, null, "A is cooling down after repeated failures.") };
        var failures = new List<string>();
        var chain = new FallbackTranslator([skipped, new FakeTranslator("b")]);
        chain.ProviderFailed += (id, _) => failures.Add(id);
        Assert.Equal("b", (await chain.TranslateAsync(Request)).ProviderId);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Combined_message_lists_whole_sentences_without_raw_text()
    {
        var chain = new FallbackTranslator([
            new FakeTranslator("google-free") { Throw = new ProviderUnavailableException("google-free", ProviderState.CoolingDown, null, TranslationErrorText.ForSkip(ProviderState.CoolingDown, "google-free")) },
            new FakeTranslator("edge-free") { Throw = new InvalidOperationException("raw <html> body with sk-ant-api03-abcdefghijkl") },
        ]);
        var ex = await Assert.ThrowsAsync<TranslationException>(() => chain.TranslateAsync(Request));
        Assert.Equal("Translation unavailable. Google Translate (free) is cooling down after repeated failures. Microsoft Translator (free) failed unexpectedly.", ex.Message);
        Assert.True(ex.Message.Length <= ProviderHealthText.MaxErrorLength);
    }

    [Fact]
    public async Task Pipeline_marks_message_failed_when_all_providers_time_out()
    {
        var clock = TestClock.Create();
        var handler = new FakeHttpHandler().Hang();
        using var runtime = new TranslationRuntime(clock: clock);
        var factory = new TranslatorFactory(handler.Client(), new UsageTracker(clock), runtime);
        var translator = factory.Build(new TranslationSettings()); // Google (free) → Microsoft (free)
        var pipeline = new ChatPipeline(translator, new ChatPipelineOptions { MyLanguage = "en" });
        var updates = new List<IncomingMessage>();
        pipeline.MessageUpdated += updates.Add;

        var processing = pipeline.ProcessAsync(["[All] 小明: 你好，再来一局？"]);
        await clock.RunUntil(processing, TimeSpan.FromMilliseconds(250));
        var message = Assert.Single(await processing);

        Assert.Equal(IncomingStatus.Failed, message.Status);
        Assert.Equal("No translation within 15 s.", message.Error);
        Assert.Single(updates);
        Assert.True(clock.GetUtcNow() - TestClock.Start <= TimeSpan.FromSeconds(16));
        Assert.NotEmpty(handler.Requests);
    }

    private static async Task<TranslationResult> Hang(TranslationRequest r, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException();
    }
}
