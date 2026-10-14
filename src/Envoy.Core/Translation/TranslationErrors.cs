using Envoy.Core.Translation.Resilience;

namespace Envoy.Core.Translation;

/// <summary>What went wrong with a translation call; decides retry, breaker and refund behaviour (REQ-TRR-1, 2, 4).</summary>
public enum TranslationErrorKind
{
    /// <summary>Not classified; counted by the breaker, not retried.</summary>
    Unknown,

    /// <summary>408, 5xx, 529, network failure (<see cref="HttpRequestException"/>, <see cref="IOException"/>).</summary>
    Transient,

    /// <summary>Attempt timeout or overall deadline.</summary>
    Timeout,

    /// <summary>429, or a keyless endpoint refusing us (403 on Google, a second 401 on Edge).</summary>
    RateLimited,

    /// <summary>The key was rejected.</summary>
    Auth,

    /// <summary>Provider quota (DeepL 456, AWS LimitExceeded) or Envoy's daily cap.</summary>
    QuotaExceeded,

    /// <summary>2xx with a body Envoy cannot parse.</summary>
    BadResponse,

    /// <summary>Request-specific 4xx (language pair, size). Not counted by the breaker.</summary>
    Unsupported,

    /// <summary>Missing key or URL. Not counted by the breaker.</summary>
    Configuration,

    /// <summary>Rejected locally: breaker open, throttled, queue full. No network call was made.</summary>
    Unavailable,
}

/// <summary>A provider was skipped locally (breaker open, throttled or queue full) without any network I/O (REQ-TRR-8, 12, 13).</summary>
public sealed class ProviderUnavailableException(string providerId, ProviderState state, DateTimeOffset? retryAt, string message)
    : TranslationException(message, kind: TranslationErrorKind.Unavailable)
{
    public string ProviderId { get; } = providerId;
    public ProviderState State { get; } = state;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
