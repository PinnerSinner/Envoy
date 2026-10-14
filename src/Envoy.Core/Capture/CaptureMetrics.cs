namespace Envoy.Core.Capture;

/// <summary>How chat reading is doing, for the status line and the Live view (REQ-CAP-30).</summary>
public enum CaptureHealth
{
    Healthy,
    Paused,

    /// <summary>A text-recognition timeout or a loop restart in the last 60 s.</summary>
    Degraded,

    /// <summary>Three failed ticks in a row, or the watchdog gave up.</summary>
    Failing,
}

/// <summary>One consistent reading of <see cref="CaptureMetrics"/> (REQ-CAP-38).</summary>
public sealed record CaptureMetricsSnapshot(double AvgCaptureMs, double AvgOcrMs, long FramesCaptured, long FramesSkipped,
    long OcrRuns, long OcrTimeouts, long OcrBusySkips, long Restarts, int ConsecutiveFailures, TimeSpan CurrentInterval,
    string Backend, CaptureHealth Health, PauseReason PauseReason)
{
    public static readonly CaptureMetricsSnapshot Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, CaptureBackendIds.Gdi,
        CaptureHealth.Healthy, PauseReason.None);
}

/// <summary>
/// Counters and rolling averages of the capture loop (REQ-CAP-30, 38). One lock; two fixed rings of the last
/// <c>window</c> capture and OCR times, so recording allocates nothing. Thread-safe.
/// </summary>
public sealed class CaptureMetrics
{
    /// <summary>Consecutive failed ticks that make the health <see cref="CaptureHealth.Failing"/>.</summary>
    public const int FailingAfter = 3;

    /// <summary>How long a timeout or restart keeps the health <see cref="CaptureHealth.Degraded"/>.</summary>
    public static readonly TimeSpan DegradedFor = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Ring _capture;
    private readonly Ring _ocr;
    private long _framesCaptured, _framesSkipped, _ocrRuns, _ocrTimeouts, _ocrBusySkips, _restarts;
    private int _consecutiveFailures;
    private bool _gaveUp;
    private DateTimeOffset? _lastTrouble;
    private TimeSpan _interval;
    private string _backend = CaptureBackendIds.Gdi;
    private PauseReason _pause;

    public CaptureMetrics(TimeProvider time, int window = 50)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        _time = time;
        _capture = new Ring(window);
        _ocr = new Ring(window);
    }

    /// <summary>A frame was captured; <paramref name="changed"/> false means OCR was skipped for it.</summary>
    public void RecordCapture(TimeSpan elapsed, bool changed)
    {
        lock (_gate)
        {
            _framesCaptured++;
            if (!changed) _framesSkipped++;
            _capture.Add(elapsed.TotalMilliseconds);
        }
    }

    public void RecordOcr(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _ocrRuns++;
            _ocr.Add(elapsed.TotalMilliseconds);
        }
    }

    public void RecordOcrTimeout()
    {
        lock (_gate)
        {
            _ocrTimeouts++;
            _lastTrouble = _time.GetUtcNow();
        }
    }

    public void RecordOcrBusy()
    {
        lock (_gate) _ocrBusySkips++;
    }

    public void RecordFailure()
    {
        lock (_gate) _consecutiveFailures++;
    }

    public void RecordSuccess()
    {
        lock (_gate) _consecutiveFailures = 0;
    }

    public void RecordRestart()
    {
        lock (_gate)
        {
            _restarts++;
            _lastTrouble = _time.GetUtcNow();
        }
    }

    public void SetGaveUp(bool gaveUp)
    {
        lock (_gate) _gaveUp = gaveUp;
    }

    public void SetInterval(TimeSpan interval)
    {
        lock (_gate) _interval = interval;
    }

    public void SetBackend(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate) _backend = id;
    }

    public void SetPause(PauseReason reason)
    {
        lock (_gate) _pause = reason;
    }

    /// <summary>The counters, averages and health: Paused → Failing → Degraded → Healthy (REQ-CAP-30).</summary>
    public CaptureMetricsSnapshot Snapshot()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var health = _pause != PauseReason.None ? CaptureHealth.Paused
                : _gaveUp || _consecutiveFailures >= FailingAfter ? CaptureHealth.Failing
                : _lastTrouble is { } t && now - t < DegradedFor ? CaptureHealth.Degraded
                : CaptureHealth.Healthy;
            return new CaptureMetricsSnapshot(_capture.Average, _ocr.Average, _framesCaptured, _framesSkipped, _ocrRuns,
                _ocrTimeouts, _ocrBusySkips, _restarts, _consecutiveFailures, _interval, _backend, health, _pause);
        }
    }

    /// <summary>Back to zero (a manual Start). The backend id is kept.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _capture.Clear();
            _ocr.Clear();
            _framesCaptured = _framesSkipped = _ocrRuns = _ocrTimeouts = _ocrBusySkips = _restarts = 0;
            _consecutiveFailures = 0;
            _gaveUp = false;
            _lastTrouble = null;
            _interval = TimeSpan.Zero;
            _pause = PauseReason.None;
        }
    }

    private sealed class Ring(int size)
    {
        private readonly double[] _values = new double[size];
        private int _next;
        private int _count;

        public double Average
        {
            get
            {
                if (_count == 0) return 0;
                double sum = 0;
                for (var i = 0; i < _count; i++) sum += _values[i];
                return sum / _count;
            }
        }

        public void Add(double value)
        {
            if (_count < _values.Length) _count++;
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
        }

        public void Clear()
        {
            System.Array.Clear(_values);
            _next = _count = 0;
        }
    }
}
