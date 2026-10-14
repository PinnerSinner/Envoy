using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class RetryPolicyTests
{
    private static readonly ResilienceOptions Options = new() { MaxAttempts = 10 };

    private static TranslationException Error(TranslationErrorKind kind, TimeSpan? retryAfter = null) =>
        new("x", null, kind, null, retryAfter);

    [Fact]
    public void Backoff_is_full_jitter_and_capped()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), RetryPolicy.Backoff(1, Options, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(500), RetryPolicy.Backoff(2, Options, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), RetryPolicy.Backoff(5, Options, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), RetryPolicy.Backoff(30, Options, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(125), RetryPolicy.Backoff(1, Options, 0.5));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.Backoff(3, Options, 0));
    }

    [Theory]
    [InlineData(TranslationErrorKind.Auth)]
    [InlineData(TranslationErrorKind.QuotaExceeded)]
    [InlineData(TranslationErrorKind.BadResponse)]
    [InlineData(TranslationErrorKind.Unsupported)]
    [InlineData(TranslationErrorKind.Configuration)]
    [InlineData(TranslationErrorKind.Unavailable)]
    [InlineData(TranslationErrorKind.Unknown)]
    public void Never_retries_non_retryable_kinds(TranslationErrorKind kind)
    {
        Assert.False(RetryPolicy.IsRetryable(kind));
        Assert.Null(RetryPolicy.NextDelay(Error(kind), 1, Options, null, 1));
    }

    [Theory]
    [InlineData(TranslationErrorKind.Transient)]
    [InlineData(TranslationErrorKind.Timeout)]
    [InlineData(TranslationErrorKind.RateLimited)]
    public void Retries_retryable_kinds_until_MaxAttempts(TranslationErrorKind kind)
    {
        var o = new ResilienceOptions { MaxAttempts = 2 };
        Assert.Equal(TimeSpan.FromMilliseconds(250), RetryPolicy.NextDelay(Error(kind), 1, o, null, 1));
        Assert.Null(RetryPolicy.NextDelay(Error(kind), 2, o, null, 1));
    }

    [Fact]
    public void Retry_never_exceeds_deadline()
    {
        Assert.Null(RetryPolicy.NextDelay(Error(TranslationErrorKind.RateLimited, TimeSpan.FromSeconds(2)), 1, Options, TimeSpan.FromSeconds(1), 1));
        Assert.Null(RetryPolicy.NextDelay(Error(TranslationErrorKind.Transient), 1, Options, TimeSpan.FromMilliseconds(100), 1));
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.NextDelay(Error(TranslationErrorKind.RateLimited, TimeSpan.FromSeconds(2)), 1, Options, TimeSpan.FromSeconds(10), 1));
    }

    [Fact]
    public void Long_Retry_After_is_never_waited_for()
    {
        Assert.Null(RetryPolicy.NextDelay(Error(TranslationErrorKind.RateLimited, TimeSpan.FromSeconds(120)), 1, Options, null, 1));
    }
}
