namespace Envoy.Core.Translation.Resilience;

/// <summary>
/// A token bucket (REQ-TRR-12): <c>burst</c> tokens, refilled at <c>tokensPerSecond</c>. A waiter reserves the next
/// token up front, so waiters are served in order and a cancelled waiter gives its token back. A rate of 0 means no
/// throttle. Thread-safe; no lock is held across an await.
/// </summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly double _rate;
    private readonly int _burst;
    private readonly TimeProvider _clock;
    private double _tokens;
    private long _lastRefill;

    public TokenBucket(double tokensPerSecond, int burst, TimeProvider clock)
    {
        _rate = Math.Max(0, tokensPerSecond);
        _burst = Math.Max(1, burst);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tokens = _burst;
        _lastRefill = clock.GetTimestamp();
    }

    public bool IsUnlimited => _rate <= 0;

    /// <summary>Takes a token if one is available now.</summary>
    public bool TryTake()
    {
        if (IsUnlimited) return true;
        lock (_gate)
        {
            Refill();
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }

    /// <summary>
    /// Takes a token, waiting up to <paramref name="maxWait"/>. Returns false at once (without waiting) when no token
    /// can be had in time. Cancellation throws <see cref="OperationCanceledException"/> and returns the reserved token.
    /// </summary>
    public async Task<bool> TakeAsync(TimeSpan maxWait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsUnlimited) return true;
        TimeSpan wait;
        lock (_gate)
        {
            Refill();
            if (_tokens >= 1)
            {
                _tokens -= 1;
                return true;
            }
            wait = TimeSpan.FromSeconds((1 - _tokens) / _rate);
            if (wait > maxWait) return false;
            _tokens -= 1; // reserve the next token
        }

        try
        {
            await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                Refill();
                _tokens = Math.Min(_burst, _tokens + 1);
            }
            throw;
        }
    }

    private void Refill()
    {
        var now = _clock.GetTimestamp();
        var elapsed = _clock.GetElapsedTime(_lastRefill, now);
        _lastRefill = now;
        if (elapsed > TimeSpan.Zero) _tokens = Math.Min(_burst, _tokens + elapsed.TotalSeconds * _rate);
    }
}
