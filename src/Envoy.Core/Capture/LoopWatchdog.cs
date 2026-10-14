namespace Envoy.Core.Capture;

/// <summary>What the watchdog decided.</summary>
public enum WatchdogVerdict
{
    /// <summary>Heartbeats are arriving.</summary>
    Healthy,

    /// <summary>No heartbeat for <see cref="LoopWatchdog.StallAfter"/>: restart the loop.</summary>
    Stalled,

    /// <summary>Stalled again after the restart budget is spent: stop and report.</summary>
    GiveUp,
}

/// <summary>
/// Detects a capture loop that stopped ticking (REQ-CAP-27, 28). The loop calls <see cref="Heartbeat"/> every tick; a
/// timer calls <see cref="Check"/>. After more than <c>maxRestarts</c> restarts within <c>restartWindow</c> (5 min) the
/// verdict is <see cref="WatchdogVerdict.GiveUp"/>. Thread-safe.
/// </summary>
public sealed class LoopWatchdog
{
    private static readonly TimeSpan MinimumStall = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private readonly int _maxRestarts;
    private readonly TimeSpan _restartWindow;
    private readonly Queue<DateTimeOffset> _restarts = new();
    private readonly object _gate = new();
    private long _lastBeatTicks;
    private long _stallAfterTicks;

    public LoopWatchdog(TimeProvider time, TimeSpan stallAfter, int maxRestarts = 3, TimeSpan? restartWindow = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _maxRestarts = Math.Max(0, maxRestarts);
        _restartWindow = restartWindow ?? TimeSpan.FromMinutes(5);
        StallAfter = stallAfter;
        Heartbeat();
    }

    /// <summary>How long without a heartbeat counts as stalled. Updated from the snapshot every tick.</summary>
    public TimeSpan StallAfter
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _stallAfterTicks));
        set => Interlocked.Exchange(ref _stallAfterTicks, value.Ticks);
    }

    /// <summary>Restarts recorded within the restart window.</summary>
    public int Restarts
    {
        get
        {
            lock (_gate)
            {
                Prune(_time.GetUtcNow());
                return _restarts.Count;
            }
        }
    }

    /// <summary>The loop is alive. Lock-free; called every tick.</summary>
    public void Heartbeat() => Interlocked.Exchange(ref _lastBeatTicks, _time.GetUtcNow().UtcTicks);

    public WatchdogVerdict Check()
    {
        var now = _time.GetUtcNow();
        var silent = TimeSpan.FromTicks(now.UtcTicks - Interlocked.Read(ref _lastBeatTicks));
        if (silent < StallAfter) return WatchdogVerdict.Healthy;
        lock (_gate)
        {
            Prune(now);
            return _restarts.Count >= _maxRestarts ? WatchdogVerdict.GiveUp : WatchdogVerdict.Stalled;
        }
    }

    /// <summary>The loop was restarted; also counts as a heartbeat.</summary>
    public void RecordRestart()
    {
        lock (_gate) _restarts.Enqueue(_time.GetUtcNow());
        Heartbeat();
    }

    /// <summary>Forgets earlier restarts (a manual Start); also a heartbeat.</summary>
    public void ResetBudget()
    {
        lock (_gate) _restarts.Clear();
        Heartbeat();
    }

    /// <summary><c>max(15 s, 2 × OCR timeout + slowest interval)</c>: long enough that a slow but working loop is never restarted.</summary>
    public static TimeSpan StallThreshold(int ocrTimeoutMs, int maxIntervalMs)
    {
        var formula = TimeSpan.FromMilliseconds(2.0 * Math.Max(0, ocrTimeoutMs) + Math.Max(0, maxIntervalMs));
        return formula > MinimumStall ? formula : MinimumStall;
    }

    private void Prune(DateTimeOffset now)
    {
        while (_restarts.Count > 0 && now - _restarts.Peek() >= _restartWindow) _restarts.Dequeue();
    }
}
