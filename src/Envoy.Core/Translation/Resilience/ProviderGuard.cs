namespace Envoy.Core.Translation.Resilience;

/// <summary>
/// One provider's long-lived resilience state (REQ-TRR-11): its breaker, its health entry and, per lane, a token
/// bucket and a concurrency gate. Lives in <see cref="TranslationRuntime"/>, keyed by provider id, and survives
/// translator rebuilds until the provider's configuration fingerprint changes.
/// </summary>
internal sealed class ProviderGuard
{
    private readonly Lane[] _lanes;
    private readonly Action<TranslationDiagnostic>? _diagnostic;

    public ProviderGuard(string providerId, string fingerprint, TimeProvider clock, ProviderHealthEntry health,
                         Action<TranslationDiagnostic>? diagnostic = null)
    {
        ProviderId = providerId;
        Fingerprint = fingerprint;
        Clock = clock;
        Health = health;
        _diagnostic = diagnostic;
        Options = ResilienceOptions.For(providerId);
        Breaker = new CircuitBreaker(Options, clock);
        _lanes = [.. Enum.GetValues<TranslationLane>().Select(l => new Lane(ResilienceOptions.For(providerId, l), clock))];
    }

    public string ProviderId { get; }
    public string Fingerprint { get; }
    public TimeProvider Clock { get; }

    /// <summary>The chat lane's options; the breaker uses them for every lane.</summary>
    public ResilienceOptions Options { get; }
    public CircuitBreaker Breaker { get; }
    public ProviderHealthEntry Health { get; }

    public Lane For(TranslationLane lane) => _lanes[(int)lane];

    /// <summary>"Retry now": breaker closed, buckets full, health back to "Not used yet".</summary>
    public void Reset()
    {
        Breaker.Reset();
        foreach (var lane in _lanes) lane.RefillBucket();
        Health.Reset();
    }

    public void Report(TranslationDiagnostic diagnostic) => _diagnostic?.Invoke(diagnostic);

    /// <summary>A lane's own throttle and concurrency gate (REQ-TRR-12, 13, 29). Lanes never share tokens or slots.</summary>
    internal sealed class Lane(ResilienceOptions options, TimeProvider clock)
    {
        private TokenBucket _bucket = new(options.TokensPerSecond, options.Burst, clock);
        private int _waiting;

        public ResilienceOptions Options { get; } = options;

        /// <summary>Never disposed: no wait handle is allocated unless AvailableWaitHandle is read.</summary>
        public SemaphoreSlim Gate { get; } = new(Math.Max(1, options.MaxConcurrency), Math.Max(1, options.MaxConcurrency));

        public TokenBucket Bucket => Volatile.Read(ref _bucket);

        /// <summary>Counts a waiter; false (and not counted) when the waiting queue is full.</summary>
        public bool TryEnqueue()
        {
            if (Interlocked.Increment(ref _waiting) <= Options.MaxQueued) return true;
            Interlocked.Decrement(ref _waiting);
            return false;
        }

        public void Dequeue() => Interlocked.Decrement(ref _waiting);

        public void RefillBucket() => Volatile.Write(ref _bucket, new TokenBucket(Options.TokensPerSecond, Options.Burst, clock));
    }
}
