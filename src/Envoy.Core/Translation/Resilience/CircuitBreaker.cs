namespace Envoy.Core.Translation.Resilience;

public enum BreakerState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>
/// One provider's circuit breaker (REQ-TRR-8, 10). Thread-safe; never blocks. While open, <see cref="TryEnter"/>
/// rejects at once; after the open period exactly one half-open probe is let through.
/// </summary>
public sealed class CircuitBreaker(ResilienceOptions options, TimeProvider clock)
{
    /// <summary>How long a provider quota without Retry-After keeps the breaker open.</summary>
    public static readonly TimeSpan DefaultQuotaOpenDuration = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly ResilienceOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private BreakerState _state;
    private DateTimeOffset? _openUntil;
    private ProviderState _openReason = ProviderState.Unknown;
    private TimeSpan _nextOpenDuration = options.BreakerOpenDuration;
    private int _consecutiveFailures;

    public BreakerState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>When the open period ends; null while closed, or while open for a rejected key (until <see cref="Reset"/>).</summary>
    public DateTimeOffset? OpenUntil
    {
        get
        {
            lock (_gate) return _state == BreakerState.Closed ? null : _openUntil;
        }
    }

    /// <summary>CoolingDown, Throttled, AuthFailed or QuotaExceeded while not closed; Unknown while closed.</summary>
    public ProviderState OpenReason
    {
        get
        {
            lock (_gate) return _state == BreakerState.Closed ? ProviderState.Unknown : _openReason;
        }
    }

    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate) return _consecutiveFailures;
        }
    }

    /// <summary>False → reject fast. True with <paramref name="isProbe"/> → this call is the single half-open probe.</summary>
    public bool TryEnter(out bool isProbe)
    {
        lock (_gate)
        {
            isProbe = false;
            switch (_state)
            {
                case BreakerState.Closed:
                    return true;
                case BreakerState.HalfOpen:
                    return false; // the probe is in flight
                default:
                    if (_openReason == ProviderState.AuthFailed || _openUntil is not { } until || _clock.GetUtcNow() < until) return false;
                    _state = BreakerState.HalfOpen;
                    isProbe = true;
                    return true;
            }
        }
    }

    /// <summary>Closes the breaker and resets the backoff.</summary>
    public void OnSuccess()
    {
        lock (_gate)
        {
            _state = BreakerState.Closed;
            _openUntil = null;
            _openReason = ProviderState.Unknown;
            _consecutiveFailures = 0;
            _nextOpenDuration = _options.BreakerOpenDuration;
        }
    }

    /// <summary>Records a failure of a whole logical call (after its retries).</summary>
    public void OnFailure(TranslationException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            switch (error.Kind)
            {
                case TranslationErrorKind.Unsupported or TranslationErrorKind.Configuration or TranslationErrorKind.Unavailable:
                    // Not the provider's health: a probe that ends this way is neither a success nor a failure.
                    if (_state == BreakerState.HalfOpen) _state = BreakerState.Open;
                    return;
                case TranslationErrorKind.Auth:
                    Open(ProviderState.AuthFailed, null);
                    return;
                case TranslationErrorKind.QuotaExceeded:
                    Open(ProviderState.QuotaExceeded, now + (error.RetryAfter ?? DefaultQuotaOpenDuration));
                    return;
            }

            _consecutiveFailures++;
            if (error.RetryAfter is { } retryAfter && retryAfter > _options.MaxRetryAfterWait
                && error.Kind is TranslationErrorKind.RateLimited or TranslationErrorKind.Transient)
            {
                Open(ProviderState.Throttled, now + retryAfter);
                return;
            }
            if (_state == BreakerState.HalfOpen)
            {
                Open(ProviderState.CoolingDown, now + _nextOpenDuration);
                return;
            }
            if (_state == BreakerState.Closed && _consecutiveFailures >= _options.BreakerThreshold)
                Open(ProviderState.CoolingDown, now + _nextOpenDuration);
        }
    }

    /// <summary>The probe was cancelled by its caller: back to Open with the same <see cref="OpenUntil"/>; not a failure.</summary>
    public void OnProbeCancelled()
    {
        lock (_gate)
        {
            if (_state == BreakerState.HalfOpen) _state = BreakerState.Open;
        }
    }

    /// <summary>"Retry now" or a configuration change: closed, no failures, backoff reset.</summary>
    public void Reset() => OnSuccess();

    private void Open(ProviderState reason, DateTimeOffset? until)
    {
        _state = BreakerState.Open;
        _openReason = reason;
        _openUntil = until;
        if (reason == ProviderState.CoolingDown)
        {
            var doubled = TimeSpan.FromTicks(Math.Min(_nextOpenDuration.Ticks * 2, _options.BreakerMaxOpenDuration.Ticks));
            _nextOpenDuration = doubled;
        }
    }
}
