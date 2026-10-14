using Envoy.Core.Settings;

namespace Envoy.Core.Capture;

/// <summary>What one watcher tick found, which decides how long until the next one.</summary>
public enum FrameOutcome
{
    /// <summary>The chat area changed (or a forced/preview frame was read).</summary>
    Changed,

    /// <summary>The chat area looked the same, or OCR was still busy.</summary>
    Unchanged,

    /// <summary>Capture is paused (no region, game minimised, chat covered…).</summary>
    Paused,

    /// <summary>Capture or OCR failed.</summary>
    Failed,
}

/// <summary>The polling intervals, clamped (REQ-CAP-14..17, 19). Built by <see cref="From"/> when the snapshot is.</summary>
public sealed record PollingOptions(int MinIntervalMs, int MaxIntervalMs,
    double BackoffFactor = 1.5, int FastFramesAfterChange = 3, int PausedIntervalMs = 1000,
    int MaxErrorIntervalMs = 5000, double MaxDutyCycle = 0.5, int FloorMs = 50)
{
    public const int MinIntervalLowest = 150;
    public const int MinIntervalHighest = 2000;
    public const int MaxIntervalHighest = 5000;
    public const int FixedIntervalLowest = 150;
    public const int FixedIntervalHighest = 5000;

    /// <summary>
    /// Clamps Min to [150, 2000] and Max to [Min, 5000]. With adaptive polling off both are the fixed
    /// <see cref="CaptureSettings.IntervalMs"/>, clamped to [150, 5000].
    /// </summary>
    public static PollingOptions From(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.AdaptivePolling)
        {
            var fixedMs = Math.Clamp(settings.IntervalMs, FixedIntervalLowest, FixedIntervalHighest);
            return new PollingOptions(fixedMs, fixedMs);
        }
        var min = Math.Clamp(settings.MinIntervalMs, MinIntervalLowest, MinIntervalHighest);
        var max = Math.Clamp(settings.MaxIntervalMs, min, MaxIntervalHighest);
        return new PollingOptions(min, max);
    }
}

/// <summary>
/// The adaptive interval between capture ticks. A changed frame drops to the minimum; it stays there for
/// <see cref="PollingOptions.FastFramesAfterChange"/> more unchanged ticks, then grows by
/// <see cref="PollingOptions.BackoffFactor"/> per unchanged tick up to the maximum. Each consecutive failure doubles the
/// wait from the minimum up to <see cref="PollingOptions.MaxErrorIntervalMs"/>; the first success returns to the adaptive
/// interval. Used by one loop thread at a time (not thread-safe).
/// </summary>
public sealed class AdaptivePollPolicy
{
    private PollingOptions _options;
    private TimeSpan _current;
    private int _fastLeft;
    private int _failures;

    public AdaptivePollPolicy(PollingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Reset();
    }

    /// <summary>Swapped every tick from the snapshot; <see cref="Current"/> is re-clamped into the new range.</summary>
    public PollingOptions Options
    {
        get => _options;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, _options)) return;
            _options = value;
            _current = Clamp(_current);
        }
    }

    /// <summary>The adaptive interval (not counting failure back-off or pauses).</summary>
    public TimeSpan Current => _current;

    /// <summary>Consecutive failed ticks so far.</summary>
    public int ConsecutiveFailures => _failures;

    /// <summary>The interval to wait after a tick with <paramref name="outcome"/>.</summary>
    public TimeSpan Next(FrameOutcome outcome)
    {
        switch (outcome)
        {
            case FrameOutcome.Changed:
                _failures = 0;
                _current = Min;
                _fastLeft = _options.FastFramesAfterChange;
                return _current;
            case FrameOutcome.Unchanged:
                _failures = 0;
                if (_fastLeft > 0) _fastLeft--;
                else _current = Clamp(TimeSpan.FromMilliseconds(_current.TotalMilliseconds * _options.BackoffFactor));
                return _current;
            case FrameOutcome.Paused:
                return TimeSpan.FromMilliseconds(_options.PausedIntervalMs);
            case FrameOutcome.Failed:
                _failures = Math.Min(_failures + 1, 30);
                var ms = Math.Min(_options.MinIntervalMs * Math.Pow(2, _failures), Math.Max(_options.MaxErrorIntervalMs, _options.MinIntervalMs));
                return TimeSpan.FromMilliseconds(ms);
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    /// <summary>Back to the minimum with the fast frames refilled (settings changed, new match, region chosen, preview).</summary>
    public void Reset()
    {
        _current = Min;
        _fastLeft = _options.FastFramesAfterChange;
        _failures = 0;
    }

    /// <summary>
    /// The wait before the next tick after <paramref name="workTime"/> of work for <paramref name="interval"/>:
    /// <c>max(interval − work, work × (1 − duty) / duty, floor)</c>, so capture never takes more than
    /// <see cref="PollingOptions.MaxDutyCycle"/> of the time (REQ-CAP-17).
    /// </summary>
    public TimeSpan DelayAfter(TimeSpan interval, TimeSpan workTime)
    {
        if (workTime < TimeSpan.Zero) workTime = TimeSpan.Zero;
        var duty = Math.Clamp(_options.MaxDutyCycle, 0.05, 1.0);
        var remaining = interval - workTime;
        var dutyWait = TimeSpan.FromTicks((long)(workTime.Ticks * (1 - duty) / duty));
        var floor = TimeSpan.FromMilliseconds(Math.Max(0, _options.FloorMs));
        var longest = remaining > dutyWait ? remaining : dutyWait;
        return longest > floor ? longest : floor;
    }

    private TimeSpan Min => TimeSpan.FromMilliseconds(_options.MinIntervalMs);

    private TimeSpan Clamp(TimeSpan value)
    {
        var min = Min;
        var max = TimeSpan.FromMilliseconds(Math.Max(_options.MaxIntervalMs, _options.MinIntervalMs));
        return value < min ? min : value > max ? max : value;
    }
}
