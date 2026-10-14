namespace Envoy.Core.Translation.Resilience;

/// <summary>Which queue a request waits in (REQ-TRR-29): chat lines, or the reply box's low-priority lane.</summary>
public enum TranslationLane
{
    Chat,
    Compose,
}

/// <summary>
/// One provider's client-side policy (spec §4). Conservative code constants, not the providers' published limits and
/// not settings, so they can be tuned after release without a settings change.
/// </summary>
public sealed record ResilienceOptions
{
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>1 = no retry.</summary>
    public int MaxAttempts { get; init; } = 2;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A longer Retry-After opens the breaker as Throttled instead of waiting (REQ-TRR-5).</summary>
    public TimeSpan MaxRetryAfterWait { get; init; } = TimeSpan.FromSeconds(2);
    public int BreakerThreshold { get; init; } = 3;
    public TimeSpan BreakerOpenDuration { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan BreakerMaxOpenDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>0 = no throttle.</summary>
    public double TokensPerSecond { get; init; }
    public int Burst { get; init; }
    public TimeSpan MaxQueueWait { get; init; } = TimeSpan.FromMilliseconds(1500);
    public int MaxConcurrency { get; init; } = 4;
    public int MaxQueued { get; init; } = 16;

    /// <summary>The defaults for <paramref name="providerId"/> in <paramref name="lane"/> (spec §4 table).</summary>
    public static ResilienceOptions For(string providerId, TranslationLane lane = TranslationLane.Chat)
    {
        var chat = providerId switch
        {
            ProviderCatalog.GoogleFree or ProviderCatalog.EdgeFree => new ResilienceOptions
            {
                AttemptTimeout = TimeSpan.FromSeconds(5),
                MaxAttempts = 2,
                TokensPerSecond = 2,
                Burst = 6,
                MaxConcurrency = 3,
            },
            ProviderCatalog.DeepL or ProviderCatalog.Aws => new ResilienceOptions
            {
                AttemptTimeout = TimeSpan.FromSeconds(6),
                MaxAttempts = 3,
                MaxConcurrency = 4,
                BreakerThreshold = 5,
            },
            ProviderCatalog.Claude => new ResilienceOptions
            {
                AttemptTimeout = TimeSpan.FromSeconds(12),
                MaxAttempts = 2,
                MaxConcurrency = 2,
            },
            ProviderCatalog.LibreTranslate => new ResilienceOptions
            {
                AttemptTimeout = TimeSpan.FromSeconds(8),
                MaxAttempts = 2,
                MaxConcurrency = 2,
                BreakerOpenDuration = TimeSpan.FromSeconds(10),
                BreakerMaxOpenDuration = TimeSpan.FromMinutes(2),
            },
            _ => new ResilienceOptions(),
        };
        if (lane == TranslationLane.Chat) return chat;

        // The reply box never queues for a slot or retries: a newer typing pause cancels the older preview anyway. A
        // preview costs two requests (translation, then back-translation), so a keyless provider gets 1 token/s with a
        // burst of 4, and the latest preview waits up to 3 s for its tokens instead of failing once typing stops
        // (spec 03 Deviations).
        var keyless = chat.TokensPerSecond > 0;
        return chat with
        {
            TokensPerSecond = keyless ? 1 : 0,
            Burst = keyless ? 4 : 0,
            MaxQueueWait = keyless ? TimeSpan.FromSeconds(3) : chat.MaxQueueWait,
            MaxConcurrency = 1,
            MaxQueued = 1,
            MaxAttempts = 1,
        };
    }
}
