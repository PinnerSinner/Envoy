namespace Envoy.Core.Translation.Resilience;

/// <summary>Which failures are retried and how long to wait (REQ-TRR-4, 5).</summary>
public static class RetryPolicy
{
    /// <summary><see cref="TranslationErrorKind.Transient"/>, <see cref="TranslationErrorKind.Timeout"/> and <see cref="TranslationErrorKind.RateLimited"/>.</summary>
    public static bool IsRetryable(TranslationErrorKind kind) =>
        kind is TranslationErrorKind.Transient or TranslationErrorKind.Timeout or TranslationErrorKind.RateLimited;

    /// <summary>Full jitter: <c>jitter01 × min(MaxDelay, BaseDelay × 2^(attempt−1))</c>; <paramref name="attempt"/> is 1-based.</summary>
    public static TimeSpan Backoff(int attempt, ResilienceOptions o, double jitter01)
    {
        ArgumentNullException.ThrowIfNull(o);
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var cap = Math.Min(o.MaxDelay.Ticks, o.BaseDelay.Ticks * Math.Pow(2, exponent));
        return TimeSpan.FromTicks((long)(Math.Clamp(jitter01, 0, 1) * cap));
    }

    /// <summary>
    /// The delay before attempt <paramref name="attempt"/> + 1, or null when the caller should stop and fall through:
    /// the kind is not retryable, the attempts are used up, a Retry-After is longer than
    /// <see cref="ResilienceOptions.MaxRetryAfterWait"/>, or the wait would not fit <paramref name="remainingDeadline"/>.
    /// </summary>
    public static TimeSpan? NextDelay(TranslationException error, int attempt, ResilienceOptions o,
                                      TimeSpan? remainingDeadline, double jitter01)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(o);
        if (!IsRetryable(error.Kind) || attempt >= o.MaxAttempts) return null;
        TimeSpan delay;
        if (error.RetryAfter is { } retryAfter)
        {
            if (retryAfter > o.MaxRetryAfterWait) return null;
            delay = retryAfter;
        }
        else
        {
            delay = Backoff(attempt, o, jitter01);
        }
        if (remainingDeadline is { } remaining && delay >= remaining) return null;
        return delay;
    }
}
