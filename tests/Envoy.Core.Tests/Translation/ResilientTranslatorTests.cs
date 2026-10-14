using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class ResilientTranslatorTests
{
    private static readonly TranslationRequest Request = ResilientSetup.Request();

    [Fact]
    public async Task Retries_transient_until_MaxAttempts_then_throws()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL, jitter: 0); // MaxAttempts 3
        s.Inner.Default = (_, _) => Task.FromException<TranslationResult>(new TranslationException("down", null, TranslationErrorKind.Transient, 503));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        Assert.Equal(TranslationErrorKind.Transient, ex.Kind);
        Assert.Equal(3, s.Inner.Calls);
        Assert.Equal(3, s.Diagnostics.Count(d => d.Outcome == "failure"));
        Assert.Equal(ProviderState.Degraded, s.Health.State);
    }

    [Fact]
    public async Task Succeeds_on_retry_and_reports_degraded()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL, jitter: 0);
        s.Inner.Fail(TranslationErrorKind.Transient).Succeed("ok");
        var result = await s.Chat.TranslateAsync(Request);
        Assert.Equal("ok", result.Text);
        Assert.Equal(2, s.Inner.Calls);
        Assert.Equal(ProviderState.Degraded, s.Health.State);
        Assert.Equal(0, s.Health.ConsecutiveFailures);
    }

    [Fact]
    public async Task Attempt_timeout_becomes_Timeout_kind()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL, jitter: 0); // 6 s per attempt, 3 attempts
        s.Inner.Default = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        };
        var call = s.Chat.TranslateAsync(Request);
        await s.Clock.RunUntil(call, TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => call);
        Assert.Equal(TranslationErrorKind.Timeout, ex.Kind);
        Assert.Equal(3, s.Inner.Calls);
    }

    [Fact]
    public async Task Rejects_while_open_without_calling_inner()
    {
        var s = new ResilientSetup(ProviderCatalog.Claude, jitter: 0); // threshold 3, 2 attempts
        s.Inner.Default = (_, _) => Task.FromException<TranslationResult>(new TranslationException("x", null, TranslationErrorKind.Transient));
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        var calls = s.Inner.Calls;

        var started = s.Clock.GetTimestamp();
        var rejected = s.Chat.TranslateAsync(Request);
        Assert.True(rejected.IsCompleted); // synchronous: no I/O, no wait
        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(() => rejected);
        Assert.Equal(ProviderState.CoolingDown, ex.State);
        Assert.Equal(TranslationErrorKind.Unavailable, ex.Kind);
        Assert.Equal(s.Clock.GetUtcNow() + TimeSpan.FromSeconds(20), ex.RetryAt);
        Assert.Equal(calls, s.Inner.Calls);
        Assert.Equal(TimeSpan.Zero, s.Clock.GetElapsedTime(started));
    }

    [Fact]
    public async Task Throttled_request_falls_through_without_network_call()
    {
        var s = new ResilientSetup(ProviderCatalog.GoogleFree); // 2/s, burst 6, wait ≤ 1.5 s
        var calls = Enumerable.Range(0, 10).Select(_ => s.Chat.TranslateAsync(Request)).ToList();
        Assert.Equal(6, s.Inner.Calls); // the burst went straight through
        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(() => calls[9]);
        Assert.Equal(ProviderState.Throttled, ex.State);
        Assert.Equal(ProviderState.Throttled, s.Health.State);

        await s.Clock.RunUntil(Task.WhenAll(calls.Take(9)), TimeSpan.FromMilliseconds(250));
        Assert.Equal(9, s.Inner.Calls); // 3 waited for tokens; the 10th never reached the provider
    }

    [Fact]
    public async Task Non_translation_exceptions_are_wrapped()
    {
        var s = new ResilientSetup(ProviderCatalog.LibreTranslate, jitter: 0);
        s.Inner.Fail(new InvalidOperationException("raw text with 你好"));
        var unknown = await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        Assert.Equal(TranslationErrorKind.Unknown, unknown.Kind);
        Assert.DoesNotContain("raw text", unknown.Message, StringComparison.Ordinal);

        s.Inner.Fail(new HttpRequestException("socket")).Fail(new HttpRequestException("socket"));
        var transient = await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        Assert.Equal(TranslationErrorKind.Transient, transient.Kind);
    }

    [Fact]
    public async Task Waits_short_Retry_After_then_succeeds()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL);
        s.Inner.Fail(TranslationErrorKind.RateLimited, 429, TimeSpan.FromSeconds(1)).Succeed("ok");
        var call = s.Chat.TranslateAsync(Request);
        Assert.False(call.IsCompleted);
        s.Clock.Advance(TimeSpan.FromMilliseconds(999));
        await TestClock.Settle();
        Assert.False(call.IsCompleted);
        s.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("ok", (await call).Text);
        Assert.Equal(ProviderState.Degraded, s.Health.State);
    }

    [Fact]
    public async Task Long_Retry_After_marks_throttled_and_throws_without_waiting()
    {
        var s = new ResilientSetup(ProviderCatalog.GoogleFree);
        s.Inner.Fail(TranslationErrorKind.RateLimited, 429, TimeSpan.FromSeconds(120));
        var call = s.Chat.TranslateAsync(Request);
        Assert.True(call.IsCompleted);
        var ex = await Assert.ThrowsAsync<TranslationException>(() => call);
        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);
        Assert.Equal(1, s.Inner.Calls);
        Assert.Equal(ProviderState.Throttled, s.Health.State);
        Assert.Equal(s.Clock.GetUtcNow() + TimeSpan.FromSeconds(120), s.Health.RetryAt);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => s.Chat.TranslateAsync(Request));
    }

    [Fact]
    public async Task Never_exceeds_MaxConcurrency()
    {
        var s = new ResilientSetup(ProviderCatalog.GoogleFree); // MaxConcurrency 3
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Inner.Default = async (r, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new TranslationResult("ok", null, r.Text);
        };
        var calls = Enumerable.Range(0, 6).Select(_ => s.Chat.TranslateAsync(Request)).ToList();
        await TestClock.Settle();
        Assert.Equal(3, s.Inner.InFlight);
        gate.SetResult();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, s.Inner.PeakInFlight);
        Assert.Equal(6, s.Inner.Calls);
    }

    [Fact]
    public async Task Queue_overflow_fails_fast()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL); // compose lane: 1 in flight, 1 queued
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Inner.Wait(gate.Task);
        var first = s.Compose.TranslateAsync(Request);
        var queued = s.Compose.TranslateAsync(Request);
        var overflow = s.Compose.TranslateAsync(Request);
        Assert.True(overflow.IsCompleted);
        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(() => overflow);
        Assert.Equal(TranslationErrorKind.Unavailable, ex.Kind);
        gate.SetResult();
        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, s.Inner.Calls);
    }

    [Fact]
    public async Task Cancelled_while_queued_makes_no_call()
    {
        var s = new ResilientSetup(ProviderCatalog.DeepL);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Inner.Wait(gate.Task);
        var first = s.Compose.TranslateAsync(Request);
        using var cts = new CancellationTokenSource();
        var queued = s.Compose.TranslateAsync(Request, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, s.Inner.Calls);
    }

    [Fact]
    public async Task Cancelled_while_waiting_for_a_token_makes_no_call()
    {
        var s = new ResilientSetup(ProviderCatalog.GoogleFree);
        for (var i = 0; i < 6; i++) await s.Chat.TranslateAsync(Request);
        using var cts = new CancellationTokenSource();
        var waiting = s.Chat.TranslateAsync(Request, cts.Token);
        Assert.False(waiting.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(6, s.Inner.Calls);
    }

    [Fact]
    public async Task Compose_lane_never_takes_chat_tokens()
    {
        var s = new ResilientSetup(ProviderCatalog.GoogleFree); // compose: 0.5/s, burst 2
        await s.Compose.TranslateAsync(Request);
        await s.Compose.TranslateAsync(Request);
        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(() => s.Compose.TranslateAsync(Request));
        Assert.Equal(ProviderState.Throttled, ex.State);

        var chat = Enumerable.Range(0, 6).Select(_ => s.Chat.TranslateAsync(Request)).ToList();
        Assert.All(chat, t => Assert.True(t.IsCompletedSuccessfully)); // the chat burst is untouched
    }

    [Fact]
    public async Task Compose_lane_shares_breaker_and_health()
    {
        var s = new ResilientSetup(ProviderCatalog.Claude, jitter: 0);
        s.Inner.Default = (_, _) => Task.FromException<TranslationResult>(new TranslationException("x", null, TranslationErrorKind.Transient));
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(() => s.Compose.TranslateAsync(Request));
        Assert.Equal(ProviderState.CoolingDown, ex.State);

        s.Clock.Advance(TimeSpan.FromSeconds(20));
        s.Inner.Default = null;
        await s.Compose.TranslateAsync(Request); // the probe goes through the compose lane
        Assert.Equal(ProviderState.Healthy, s.Health.State);
        Assert.Equal("ok", (await s.Chat.TranslateAsync(Request)).Text.Length > 0 ? "ok" : "");
        Assert.Contains(s.Diagnostics, d => d.Lane == TranslationLane.Compose && d.Outcome == "success");
    }

    [Fact]
    public async Task Cancelled_probe_leaves_breaker_open_for_the_next_probe()
    {
        var s = new ResilientSetup(ProviderCatalog.Claude, jitter: 0);
        s.Inner.Default = (_, _) => Task.FromException<TranslationResult>(new TranslationException("x", null, TranslationErrorKind.Transient));
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(Request));
        s.Clock.Advance(TimeSpan.FromSeconds(20));
        s.Inner.Default = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        };
        using var cts = new CancellationTokenSource();
        var probe = s.Chat.TranslateAsync(Request, cts.Token);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => s.Chat.TranslateAsync(Request)); // one probe only
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        Assert.Equal(BreakerState.Open, s.Guard.Breaker.State);
        s.Inner.Default = null;
        await s.Chat.TranslateAsync(Request);
        Assert.Equal(BreakerState.Closed, s.Guard.Breaker.State);
    }
}
