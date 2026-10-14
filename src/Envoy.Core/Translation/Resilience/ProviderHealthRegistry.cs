using System.Collections.Concurrent;
using Envoy.Core.Diagnostics;

namespace Envoy.Core.Translation.Resilience;

/// <summary>A provider's health as the UI, the logs and the diagnostics report see it (contract §2.1).</summary>
public enum ProviderState
{
    /// <summary>Never called, reset, or not set up (a <see cref="TranslationErrorKind.Configuration"/> failure).</summary>
    Unknown,

    /// <summary>The last real outcome was a first-attempt success.</summary>
    Healthy,

    /// <summary>Breaker closed but recent failures, or the last success needed a retry.</summary>
    Degraded,

    /// <summary>Opened by a long Retry-After, or the last call was rejected by the token bucket or a full queue.</summary>
    Throttled,

    /// <summary>Breaker opened by consecutive failures.</summary>
    CoolingDown,

    /// <summary>The key was rejected; skipped until the key changes or "Retry now".</summary>
    AuthFailed,

    /// <summary>The provider's quota or Envoy's daily cap is used up.</summary>
    QuotaExceeded,
}

/// <summary>One provider's health (contract §2.1). <see cref="LastError"/> is sanitised; it never holds chat text, keys or bodies.</summary>
/// <param name="MedianLatency">Median of the last 20 successful calls.</param>
/// <param name="LastErrorStatus">The HTTP status of the last failure, if any (for the diagnostics report).</param>
public sealed record ProviderHealthSnapshot(
    string ProviderId,
    ProviderState State,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure,
    string? LastError,
    TranslationErrorKind? LastErrorKind,
    int ConsecutiveFailures,
    TimeSpan? MedianLatency,
    DateTimeOffset? RetryAt,
    long Successes,
    long Failures,
    int? LastErrorStatus = null)
{
    public static ProviderHealthSnapshot Empty(string providerId) =>
        new(providerId, ProviderState.Unknown, null, null, null, null, 0, null, null, 0, 0);
}

/// <summary>
/// The single source of provider health (REQ-TRR-16, contract §2.1). Read by the Translation page, the War Room status
/// line, the diagnostics report and (07a) notices. Thread-safe.
/// </summary>
public sealed class ProviderHealthRegistry(TimeProvider? clock = null, LogRedactor? redactor = null)
{
    private readonly ConcurrentDictionary<string, ProviderHealthEntry> _entries = new(StringComparer.Ordinal);

    internal TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    internal LogRedactor Redactor { get; } = redactor ?? new LogRedactor();

    /// <summary>Raised once per <see cref="ProviderState"/> change, on the thread that caused it. Not raised for count/latency-only changes.</summary>
    public event Action<ProviderHealthSnapshot>? StateChanged;

    public ProviderHealthSnapshot Get(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        return _entries.TryGetValue(providerId, out var entry) ? entry.Snapshot() : ProviderHealthSnapshot.Empty(providerId);
    }

    public IReadOnlyList<ProviderHealthSnapshot> Snapshot(IEnumerable<string> providerIds)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        return [.. providerIds.Select(Get)];
    }

    /// <summary>Smoke test and previews only: sets the displayed snapshot; never touches breakers or buckets.</summary>
    public void Seed(ProviderHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Entry(snapshot.ProviderId).Seed(snapshot);
    }

    internal ProviderHealthEntry Entry(string providerId) =>
        _entries.GetOrAdd(providerId, id => new ProviderHealthEntry(id, this));

    internal void Raise(ProviderHealthSnapshot snapshot) => StateChanged?.Invoke(snapshot);
}

/// <summary>
/// One provider's health record. Every change goes through <see cref="Apply"/>, which raises
/// <see cref="ProviderHealthRegistry.StateChanged"/> exactly once per state change, outside the lock.
/// </summary>
internal sealed class ProviderHealthEntry(string providerId, ProviderHealthRegistry registry)
{
    private const int LatencySamples = 20;

    private readonly object _gate = new();
    private readonly Queue<TimeSpan> _latencies = new();
    private ProviderState _state;
    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _lastFailure;
    private string? _lastError;
    private TranslationErrorKind? _lastErrorKind;
    private int? _lastErrorStatus;
    private int _consecutiveFailures;
    private DateTimeOffset? _retryAt;
    private long _successes;
    private long _failures;

    public string ProviderId { get; } = providerId;

    public ProviderState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public ProviderHealthSnapshot Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    /// <summary>A real success. Healthy on the first attempt, Degraded when it needed a retry.</summary>
    public void RecordSuccess(TimeSpan latency, bool firstAttempt) => Apply(() =>
    {
        _lastSuccess = registry.Clock.GetUtcNow();
        _successes++;
        _consecutiveFailures = 0;
        _retryAt = null;
        _latencies.Enqueue(latency);
        while (_latencies.Count > LatencySamples) _latencies.Dequeue();
        return firstAttempt ? ProviderState.Healthy : ProviderState.Degraded;
    });

    /// <summary>A real failure (after retries); <paramref name="state"/> and <paramref name="retryAt"/> come from the breaker.</summary>
    public void RecordFailure(TranslationException error, ProviderState? state, DateTimeOffset? retryAt, bool counted) => Apply(() =>
    {
        _lastFailure = registry.Clock.GetUtcNow();
        _failures++;
        _lastError = ProviderHealthText.Sanitize(error.Message, registry.Redactor);
        _lastErrorKind = error.Kind;
        _lastErrorStatus = error.StatusCode;
        if (counted) _consecutiveFailures++;
        if (state is null) return _state;
        _retryAt = retryAt;
        return state.Value;
    });

    /// <summary>A call rejected locally (breaker open, bucket empty, queue full). Not a failure of the provider.</summary>
    public void RecordRejection(ProviderState state, DateTimeOffset? retryAt) => Apply(() =>
    {
        _retryAt = retryAt;
        return state;
    });

    /// <summary>Envoy's daily cap: QuotaExceeded until <paramref name="until"/>.</summary>
    public void RecordBudgetExceeded(TranslationException error, DateTimeOffset until) => Apply(() =>
    {
        _lastFailure = registry.Clock.GetUtcNow();
        _lastError = ProviderHealthText.Sanitize(error.Message, registry.Redactor);
        _lastErrorKind = TranslationErrorKind.QuotaExceeded;
        _lastErrorStatus = null;
        _retryAt = until;
        return ProviderState.QuotaExceeded;
    });

    /// <summary>The provider could not be built (missing key or URL): shown as "Not set up".</summary>
    public void RecordBuildFailure(TranslationException error) => Apply(() =>
    {
        _lastError = ProviderHealthText.Sanitize(error.Message, registry.Redactor);
        _lastErrorKind = TranslationErrorKind.Configuration;
        _lastErrorStatus = null;
        _retryAt = null;
        return ProviderState.Unknown;
    });

    /// <summary>"Retry now" or a configuration change: back to a fresh, never-called record.</summary>
    public void Reset() => Apply(() =>
    {
        _latencies.Clear();
        _lastSuccess = null;
        _lastFailure = null;
        _lastError = null;
        _lastErrorKind = null;
        _lastErrorStatus = null;
        _consecutiveFailures = 0;
        _retryAt = null;
        _successes = 0;
        _failures = 0;
        return ProviderState.Unknown;
    });

    public void Seed(ProviderHealthSnapshot s) => Apply(() =>
    {
        _latencies.Clear();
        if (s.MedianLatency is { } median) _latencies.Enqueue(median);
        _lastSuccess = s.LastSuccess;
        _lastFailure = s.LastFailure;
        _lastError = s.LastError is null ? null : ProviderHealthText.Sanitize(s.LastError, registry.Redactor);
        _lastErrorKind = s.LastErrorKind;
        _lastErrorStatus = s.LastErrorStatus;
        _consecutiveFailures = s.ConsecutiveFailures;
        _retryAt = s.RetryAt;
        _successes = s.Successes;
        _failures = s.Failures;
        return s.State;
    });

    private void Apply(Func<ProviderState> change)
    {
        ProviderHealthSnapshot? changed = null;
        lock (_gate)
        {
            var before = _state;
            _state = change();
            if (_state != before) changed = SnapshotLocked();
        }
        if (changed is not null) registry.Raise(changed);
    }

    private ProviderHealthSnapshot SnapshotLocked() => new(
        ProviderId, _state, _lastSuccess, _lastFailure, _lastError, _lastErrorKind, _consecutiveFailures,
        Median(), _retryAt, _successes, _failures, _lastErrorStatus);

    private TimeSpan? Median()
    {
        if (_latencies.Count == 0) return null;
        var sorted = _latencies.OrderBy(t => t).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2);
    }
}
