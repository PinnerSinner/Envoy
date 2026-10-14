using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class CircuitBreakerTests
{
    private static readonly ResilienceOptions Options = new();

    private static TranslationException Error(TranslationErrorKind kind = TranslationErrorKind.Transient, TimeSpan? retryAfter = null) =>
        new("x", null, kind, null, retryAfter);

    [Fact]
    public void Opens_after_threshold_consecutive_failures()
    {
        var clock = TestClock.Create();
        var b = new CircuitBreaker(Options, clock);
        b.OnFailure(Error());
        b.OnFailure(Error());
        Assert.Equal(BreakerState.Closed, b.State);
        b.OnFailure(Error());
        Assert.Equal(BreakerState.Open, b.State);
        Assert.Equal(ProviderState.CoolingDown, b.OpenReason);
        Assert.Equal(TestClock.Start + TimeSpan.FromSeconds(20), b.OpenUntil);
        Assert.False(b.TryEnter(out _));
    }

    [Fact]
    public void Success_resets_consecutive_count()
    {
        var b = new CircuitBreaker(Options, TestClock.Create());
        b.OnFailure(Error());
        b.OnFailure(Error());
        b.OnSuccess();
        b.OnFailure(Error());
        b.OnFailure(Error());
        Assert.Equal(BreakerState.Closed, b.State);
        Assert.Equal(2, b.ConsecutiveFailures);
    }

    [Fact]
    public void Allows_single_half_open_probe()
    {
        var clock = TestClock.Create();
        var b = new CircuitBreaker(Options, clock);
        for (var i = 0; i < 3; i++) b.OnFailure(Error());
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(b.TryEnter(out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(b.TryEnter(out var probe));
        Assert.True(probe);
        Assert.Equal(BreakerState.HalfOpen, b.State);
        Assert.False(b.TryEnter(out _));
        Assert.False(b.TryEnter(out _));
    }

    [Fact]
    public void Probe_success_closes_and_resets_backoff()
    {
        var clock = TestClock.Create();
        var b = new CircuitBreaker(Options, clock);
        for (var i = 0; i < 3; i++) b.OnFailure(Error());
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(b.TryEnter(out _));
        b.OnFailure(Error()); // probe fails: 40 s
        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.True(b.TryEnter(out _));
        b.OnSuccess();
        Assert.Equal(BreakerState.Closed, b.State);
        Assert.True(b.TryEnter(out var probe));
        Assert.False(probe);

        for (var i = 0; i < 3; i++) b.OnFailure(Error());
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(20), b.OpenUntil); // back to the base duration
    }

    [Fact]
    public void Probe_failure_doubles_open_duration_up_to_max()
    {
        var clock = TestClock.Create();
        var b = new CircuitBreaker(Options, clock);
        for (var i = 0; i < 3; i++) b.OnFailure(Error());
        var expected = new[] { 40, 80, 160, 300, 300 };
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(20), b.OpenUntil);
        foreach (var seconds in expected)
        {
            clock.Advance(b.OpenUntil!.Value - clock.GetUtcNow());
            Assert.True(b.TryEnter(out var probe));
            Assert.True(probe);
            b.OnFailure(Error());
            Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(seconds), b.OpenUntil);
        }
    }

    [Fact]
    public void Cancelled_probe_is_not_a_failure()
    {
        var clock = TestClock.Create();
        var b = new CircuitBreaker(Options, clock);
        for (var i = 0; i < 3; i++) b.OnFailure(Error());
        clock.Advance(TimeSpan.FromSeconds(20));
        var until = b.OpenUntil;
        Assert.True(b.TryEnter(out _));
        b.OnProbeCancelled();
        Assert.Equal(BreakerState.Open, b.State);
        Assert.Equal(until, b.OpenUntil);
        Assert.True(b.TryEnter(out var again)); // the next call may probe at once
        Assert.True(again);
        b.OnFailure(Error());
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(40), b.OpenUntil); // no extra doubling from the cancel
    }

    [Fact]
    public void Auth_opens_immediately_until_reset()
    {
        var clock = TestClock.Create();
        var b = new CircuitBreaker(Options, clock);
        b.OnFailure(Error(TranslationErrorKind.Auth));
        Assert.Equal(BreakerState.Open, b.State);
        Assert.Equal(ProviderState.AuthFailed, b.OpenReason);
        Assert.Null(b.OpenUntil);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.False(b.TryEnter(out _));
        b.Reset();
        Assert.True(b.TryEnter(out _));
    }

    [Fact]
    public void Quota_opens_until_retry_after_or_one_hour()
    {
        var clock = TestClock.Create();
        var withHeader = new CircuitBreaker(Options, clock);
        withHeader.OnFailure(Error(TranslationErrorKind.QuotaExceeded, TimeSpan.FromMinutes(10)));
        Assert.Equal(ProviderState.QuotaExceeded, withHeader.OpenReason);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(10), withHeader.OpenUntil);

        var without = new CircuitBreaker(Options, clock);
        without.OnFailure(Error(TranslationErrorKind.QuotaExceeded));
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromHours(1), without.OpenUntil);
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(without.TryEnter(out _));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(without.TryEnter(out _));
    }

    [Fact]
    public void Unsupported_configuration_and_cancellation_do_not_count()
    {
        var clock = TestClock.Create();
        var b = new CircuitBreaker(Options, clock);
        for (var i = 0; i < 10; i++)
        {
            b.OnFailure(Error(TranslationErrorKind.Unsupported));
            b.OnFailure(Error(TranslationErrorKind.Configuration));
            b.OnFailure(Error(TranslationErrorKind.Unavailable));
        }
        Assert.Equal(BreakerState.Closed, b.State);
        Assert.Equal(0, b.ConsecutiveFailures);
    }

    [Fact]
    public void Long_retry_after_opens_as_throttled()
    {
        var clock = TestClock.Create();
        var b = new CircuitBreaker(Options, clock);
        b.OnFailure(Error(TranslationErrorKind.RateLimited, TimeSpan.FromSeconds(120)));
        Assert.Equal(ProviderState.Throttled, b.OpenReason);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(120), b.OpenUntil);
    }
}
