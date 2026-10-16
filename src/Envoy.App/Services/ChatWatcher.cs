using Envoy.App.Services.Capture;
using Envoy.Core.Capture;
using Envoy.Core.Diagnostics;
using Envoy.Core.Ocr;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Envoy.Core.Threading;

namespace Envoy.App.Services;

public enum WatcherState
{
    Stopped,
    NoRegion,
    Running,

    /// <summary>Waiting for a game (<see cref="PauseReason.NoGame"/>).</summary>
    Idle,

    /// <summary>An automatic pause; <see cref="ChatWatcher.PauseReason"/> says why.</summary>
    Paused,
    Error,
}

/// <summary>
/// One capture for the Live view (contract §2.7). <see cref="Image"/> is an exact-length copy, never a pooled buffer.
/// </summary>
public sealed record CaptureFrame(CapturedImage Image, IReadOnlyList<OcrLine> Lines, TimeSpan OcrTime);

/// <summary>
/// The capture loop (spec 05): game window → region → pause gate → capture → fingerprint → enhance → OCR (single-flight,
/// timed out) → merge → pipeline. It reads settings only through the <see cref="CaptureOptions"/> snapshot, polls
/// adaptively, backs off on failure, and a watchdog restarts a stalled loop on a new capture backend. Stopping or
/// restarting never cancels translations already in flight: the pipeline gets <see cref="CancellationToken.None"/>.
/// Events are raised on the loop's thread.
/// </summary>
public sealed class ChatWatcher : IDisposable
{
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromSeconds(2);

    private readonly OcrService _ocr;
    private readonly ChatPipeline _pipeline;
    private readonly Func<CaptureOptions> _options;
    private readonly Func<RunningGame?> _currentGame;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly Action<GameWindowInfo?>? _windowObserved;
    private readonly LeaseCounter _preview = new();
    private readonly FramePool _pool = new();
    private readonly CaptureMetrics _metrics;
    private readonly LoopWatchdog _watchdog;
    private readonly object _gate = new();

    // Loop control; guarded by _gate.
    private CancellationTokenSource? _cts;
    private ITimer? _watchdogTimer;
    private int _generation;
    private bool _running;
    private bool _disposed;

    // Shared between the loop and the UI thread.
    private long _lastHash;
    private int _resetRequested = 1;
    private int _streak;
    private volatile WatcherState _state = WatcherState.Stopped;
    private volatile PauseReason _pause;
    private volatile string? _lastError;
    private volatile RegionResolution? _resolution;
    private (WatcherState, PauseReason, CaptureHealth, bool) _announced = (WatcherState.Stopped, PauseReason.None, CaptureHealth.Healthy, false);
    private bool _dpiChecked;

    // Loop-thread only (one generation runs at a time; a stalled old one checks its generation before writing).
    private GameWindowInfo? _lastPublished;
    private ScreenRegion? _lastClient;

    public ChatWatcher(OcrService ocr, ChatPipeline pipeline, Func<CaptureOptions> options, Func<RunningGame?> currentGame,
        ILog log, TimeProvider time, Action<GameWindowInfo?>? windowObserved = null)
    {
        _ocr = ocr;
        _pipeline = pipeline;
        _options = options;
        _currentGame = currentGame;
        _log = log;
        _time = time;
        _windowObserved = windowObserved;
        _metrics = new CaptureMetrics(time);
        var o = options();
        _watchdog = new LoopWatchdog(time, LoopWatchdog.StallThreshold(o.OcrTimeoutMs, o.Polling.MaxIntervalMs));
    }

    public WatcherState State => _state;

    /// <summary>Why the watcher isn't capturing; <see cref="PauseReason.None"/> while reading or stopped.</summary>
    public PauseReason PauseReason => _pause;

    /// <summary>The last problem, always a <see cref="CaptureText"/> sentence (never exception text).</summary>
    public string? LastError => _lastError;

    /// <summary><see cref="CaptureText.LikelyExclusiveFullscreen"/> after <see cref="BlankFrames.Threshold"/> blank frames (REQ-CAP-40).</summary>
    public string? Hint => UniformFrameStreak >= BlankFrames.Threshold ? CaptureText.LikelyExclusiveFullscreen : null;

    /// <summary>Consecutive blank frames, saturating at <see cref="BlankFrames.Threshold"/>; 0 when stopped or paused.</summary>
    public int UniformFrameStreak => Volatile.Read(ref _streak);

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _running;
        }
    }

    /// <summary>The pool the loop rents frames from (the soak run reads its counters).</summary>
    public FramePool Pool => _pool;

    /// <summary>
    /// Frames are published to <see cref="FrameCaptured"/> while at least one lease is held (a live view is open). Each
    /// call also forces the next frame through OCR. Dispose the lease to give it back; disposing twice is harmless.
    /// </summary>
    public IDisposable BeginPreview()
    {
        var lease = _preview.Acquire();
        Invalidate();
        return lease;
    }

    /// <summary>True while any preview lease is held.</summary>
    public bool IsPreviewing => _preview.IsHeld;

    /// <summary>The region the loop resolved last; resolved on demand from the snapshot when the loop hasn't run yet.</summary>
    public ScreenRegion? CurrentRegion => Resolution.Region;

    public RegionSource CurrentRegionSource => Resolution.Source;

    /// <summary>The window-relative region was chosen at another client size and is being scaled (REQ-CAP-8).</summary>
    public bool ClientSizeDiffers => Resolution.ClientSizeDiffers;

    public CaptureMetricsSnapshot Metrics => _metrics.Snapshot();

    public CaptureHealth Health => Metrics.Health;

    /// <summary>Raised on a background thread when <see cref="State"/>, <see cref="PauseReason"/>, <see cref="Health"/> or <see cref="Hint"/> changes.</summary>
    public event Action<WatcherState>? StateChanged;

    /// <summary>Raised on the loop thread, only while <see cref="IsPreviewing"/>.</summary>
    public event Action<CaptureFrame>? FrameCaptured;

    /// <summary>Raised on the loop thread when the resolved region moves (REQ-CAP-7).</summary>
    public event Action<ScreenRegion?>? RegionChanged;

    private RegionResolution Resolution
    {
        get
        {
            if (IsRunning && _resolution is { } r) return r;
            // Stopped, or not resolved by the loop since the last change: resolve now from the snapshot (any thread;
            // user32 queries only).
            var o = _options();
            var game = _currentGame();
            var info = GameWindowTracker.Observe(game, 0);
            return RegionMath.ResolveForGame(o, o.GameIdFor(game?.Profile.Id), info is { Minimized: false } ? info.ClientRect : null);
        }
    }

    /// <summary>Starts reading. Resets the watchdog's restart budget, the polling interval and the metrics.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _running) return;
            _running = true;
            _watchdog.ResetBudget();
            _metrics.Reset();
            _lastError = null;
            _resolution = null;
            Interlocked.Exchange(ref _resetRequested, 1);
            Volatile.Write(ref _lastHash, 0);
            StartLoopLocked("start");
            _watchdogTimer = _time.CreateTimer(_ => OnWatchdog(), null, WatchdogPeriod, WatchdogPeriod);
        }
    }

    /// <summary>Stops the loop. In-flight translations carry on (they never saw the loop's token).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopLoopLocked();
            _running = false;
            Volatile.Write(ref _streak, 0);
            SetStateLocked(WatcherState.Stopped, PauseReason.None);
        }
        Announce();
    }

    /// <summary>Forces the next frame through OCR and polls fast again (settings changed, new match, region chosen, preview).</summary>
    public void Invalidate()
    {
        Volatile.Write(ref _lastHash, 0);
        Interlocked.Exchange(ref _resetRequested, 1);
        _resolution = null; // settings may have changed the region: resolve on demand until the next tick
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) _disposed = true;
    }

    // ---------------- loop control ----------------

    private void StartLoopLocked(string why)
    {
        _generation++;
        var generation = _generation;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var backend = CaptureBackendFactory.Create(_options().Backend);
        _metrics.SetBackend(backend.Id);
        _log.Info("Capture loop started", LogField.Of("backend", backend.Id), LogField.Of("reason", why), LogField.Of("generation", generation));
        Task.Run(() => RunAsync(generation, backend, token), CancellationToken.None).Forget(_log, "capture loop");
    }

    private void StopLoopLocked()
    {
        _generation++;
        // Cancelled, not disposed: the old loop (perhaps stuck in a native call) still holds its token.
        _cts?.Cancel();
        _cts = null;
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
    }

    private bool IsCurrent(int generation)
    {
        lock (_gate) return _running && generation == _generation;
    }

    /// <summary>The 2 s watchdog tick (REQ-CAP-27, 28): restart a stalled loop, or give up after too many restarts.</summary>
    private void OnWatchdog()
    {
        string? gaveUp = null;
        lock (_gate)
        {
            if (!_running || _disposed) return;
            switch (_watchdog.Check())
            {
                case WatchdogVerdict.Healthy:
                    return;
                case WatchdogVerdict.Stalled:
                    // The old loop keeps its own token and backend; it disposes the backend when it finally returns.
                    _cts?.Cancel();
                    _cts = null;
                    _watchdog.RecordRestart();
                    _metrics.RecordRestart();
                    _log.Warn("Capture loop stalled; restarting it", null, LogField.Of("restarts", _watchdog.Restarts),
                        LogField.Of("stallAfterMs", (int)_watchdog.StallAfter.TotalMilliseconds));
                    StartLoopLocked("watchdog");
                    break;
                case WatchdogVerdict.GiveUp:
                    StopLoopLocked();
                    _running = false;
                    gaveUp = CaptureText.StoppedResponding;
                    _metrics.SetGaveUp(true);
                    _lastError = gaveUp;
                    SetStateLocked(WatcherState.Error, PauseReason.None);
                    break;
            }
        }
        if (gaveUp is not null) _log.Error("Chat reading stopped responding; gave up after repeated restarts", null, LogField.Of("restarts", _watchdog.Restarts));
        Announce();
    }

    private async Task RunAsync(int generation, ICaptureBackend backend, CancellationToken ct)
    {
        try
        {
            CheckDpiAwareness();
            var policy = new AdaptivePollPolicy(_options().Polling); // one per generation: a stalled old loop never shares it
            while (!ct.IsCancellationRequested && IsCurrent(generation))
            {
                TimeSpan delay;
                try
                {
                    delay = await TickAsync(generation, backend, policy, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Steps 1–4 and 12 should never throw; if they do, count it like a failed capture and keep going.
                    Fail(generation, ex);
                    delay = policy.DelayAfter(policy.Next(FrameOutcome.Failed), TimeSpan.Zero);
                    Announce();
                }
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped or restarted.
        }
        finally
        {
            backend.Dispose();
        }
    }

    /// <summary>Once per watcher: every coordinate is physical only on a per-monitor-v2 thread (REQ-CAP-34).</summary>
    private void CheckDpiAwareness()
    {
        if (_dpiChecked) return;
        _dpiChecked = true;
        if (GameWindowTracker.IsThreadPerMonitorV2()) return;
        _lastError = CaptureText.DpiUnaware;
        _log.Warn("The capture thread is not per-monitor DPI aware (v2); regions may be misaligned");
    }

    // ---------------- the tick (contract §2.7) ----------------

    private async Task<TimeSpan> TickAsync(int generation, ICaptureBackend backend, AdaptivePollPolicy policy, CancellationToken ct)
    {
        var started = _time.GetTimestamp();

        // 1. Heartbeat.
        _watchdog.Heartbeat();

        // 2. The only settings read of the tick.
        var o = _options();
        policy.Options = o.Polling;
        if (Interlocked.Exchange(ref _resetRequested, 0) == 1) policy.Reset();
        _watchdog.StallAfter = LoopWatchdog.StallThreshold(o.OcrTimeoutMs, o.Polling.MaxIntervalMs);

        // 3. Probe and publish the game window.
        var game = _currentGame();
        var info = GameWindowTracker.Observe(game, UniformFrameStreak);
        if (!Equals(info, _lastPublished) && IsCurrent(generation))
        {
            _lastPublished = info;
            _windowObserved?.Invoke(info);
        }

        // 4. Resolve the region and decide pause or preview override. A minimised window has no useful client rect, so
        //    keep resolving against the last one it had.
        if (info is { Minimized: false }) _lastClient = info.ClientRect;
        var client = info is null ? null : info.Minimized ? _lastClient : info.ClientRect;
        var resolution = RegionMath.ResolveForGame(o, o.GameIdFor(game?.Profile.Id), client);
        PublishRegion(generation, resolution);
        var region = resolution.Region is { IsEmpty: false } r ? r : null;
        var onScreen = region is not null && GameWindowTracker.IsOnAnyMonitor(region);
        IReadOnlyList<SampleOwner>? samples = null;
        if (region is not null && onScreen && info is { Minimized: false } && resolution.Source == RegionSource.WindowRelative
            && o.PauseWhenCovered && !backend.SupportsOccludedCapture)
            samples = GameWindowTracker.Sample(region, info.Handle);
        var observation = new CaptureObservation(region is not null, onScreen, o.ShouldRead(game is not null),
            info is not null, info?.Minimized ?? false, info?.GameForeground ?? false, info?.EnvoyForeground ?? false,
            resolution.Source, samples);
        var gate = new CaptureGateOptions(o.PauseWhenCovered, o.PauseWhenNotForeground, backend.SupportsOccludedCapture, IsPreviewing);
        var reason = CaptureGate.Evaluate(observation, gate);
        var reading = reason == PauseReason.None && CaptureGate.IsReading(observation, gate);

        FrameOutcome outcome;
        if (reason != PauseReason.None || region is null)
        {
            outcome = FrameOutcome.Paused;
            SetPaused(generation, reason == PauseReason.None ? PauseReason.NoRegion : reason);
        }
        else
        {
            outcome = await CaptureAndReadAsync(generation, backend, o, region, info, reading, ct).ConfigureAwait(false);
        }

        // 12. Metrics and the next delay.
        var interval = policy.Next(outcome);
        _metrics.SetInterval(outcome == FrameOutcome.Paused ? policy.Current : interval);
        if (IsCurrent(generation)) Announce();
        return policy.DelayAfter(interval, _time.GetElapsedTime(started));
    }

    /// <summary>Steps 5–11. Returns the outcome; failures are counted and described, never thrown (REQ-CAP-30).</summary>
    private async Task<FrameOutcome> CaptureAndReadAsync(int generation, ICaptureBackend backend, CaptureOptions o,
        ScreenRegion region, GameWindowInfo? info, bool reading, CancellationToken ct)
    {
        FrameBuffer? buffer = null;
        try
        {
            // 5. Capture into a pooled buffer.
            var (w, h, _) = RegionMath.OutputSize(region, o.Upscale, OcrService.MaxImageDimension);
            var captureStarted = _time.GetTimestamp();
            buffer = backend.Capture(new CaptureTarget(region, info?.Handle ?? IntPtr.Zero, w, h), _pool);
            var captureTime = _time.GetElapsedTime(captureStarted);

            // 6. Fingerprint the raw pixels; skip unchanged frames unless a Live view is open.
            var signature = FrameProcessing.Analyze(buffer.Pixels);
            Volatile.Write(ref _streak, BlankFrames.Next(UniformFrameStreak, signature));
            var changed = signature.Hash != (ulong)Interlocked.Read(ref _lastHash);
            if (!changed && !IsPreviewing)
            {
                _metrics.RecordCapture(captureTime, changed: false);
                return Succeeded(generation, FrameOutcome.Unchanged);
            }
            if (_ocr.IsBusy)
            {
                // An earlier (perhaps abandoned) OCR call is still running: leave the fingerprint unset and retry.
                _metrics.RecordCapture(captureTime, changed: false);
                _metrics.RecordOcrBusy();
                return Succeeded(generation, FrameOutcome.Unchanged);
            }
            _metrics.RecordCapture(captureTime, changed: true);

            // 7. Enhance.
            FrameProcessing.Preprocess(buffer.Pixels, o.Preprocess);

            // 8. OCR, single-flight with a timeout.
            var ocrStarted = _time.GetTimestamp();
            var passes = await _ocr.RecognizeAsync(buffer, TimeSpan.FromMilliseconds(o.OcrTimeoutMs), ct).ConfigureAwait(false);
            var ocrTime = _time.GetElapsedTime(ocrStarted);
            _metrics.RecordOcr(ocrTime);
            if (!IsCurrent(generation)) return FrameOutcome.Unchanged;

            // 9. Merge the engines' readings.
            var lines = OcrMerger.MergeLines(passes, o.MyLanguage);
            Interlocked.Exchange(ref _lastHash, (long)signature.Hash);

            // 10. Translation never sees the loop's token: stopping capture must not strand a message (contract §2.6).
            if (reading) _pipeline.ProcessAsync(lines, CancellationToken.None).Forget(_log, "pipeline");

            // 11. The Live view gets an exact-length copy.
            if (IsPreviewing)
                FrameCaptured?.Invoke(new CaptureFrame(new CapturedImage(buffer.ToArray(), buffer.Width, buffer.Height), lines, ocrTime));

            return Succeeded(generation, changed ? FrameOutcome.Changed : FrameOutcome.Unchanged);
        }
        catch (SingleFlightBusyException)
        {
            _metrics.RecordOcrBusy();
            return Succeeded(generation, FrameOutcome.Unchanged);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(generation, ex);
            return FrameOutcome.Failed;
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    // ---------------- state ----------------

    /// <summary>A successful tick clears failures (and a failure's LastError) and means Running.</summary>
    private FrameOutcome Succeeded(int generation, FrameOutcome outcome)
    {
        lock (_gate)
        {
            if (!_running || generation != _generation) return outcome;
            var hadFailures = _metrics.Snapshot().ConsecutiveFailures > 0;
            _metrics.RecordSuccess();
            if (hadFailures && _lastError != CaptureText.DpiUnaware) _lastError = null;
            SetStateLocked(WatcherState.Running, PauseReason.None);
        }
        return outcome;
    }

    /// <summary>A failed tick (REQ-CAP-30): counted, described with a CaptureText sentence, logged without OCR text.</summary>
    private void Fail(int generation, Exception ex)
    {
        lock (_gate)
        {
            if (!_running || generation != _generation) return;
            _metrics.RecordFailure();
            if (ex is TimeoutException) _metrics.RecordOcrTimeout();
            _lastError = CaptureErrors.Describe(ex);
            _log.Warn("Capture tick failed", ex, LogField.Of("kind", ex.GetType().Name));
            if (_metrics.Snapshot().ConsecutiveFailures >= CaptureMetrics.FailingAfter) SetStateLocked(WatcherState.Error, PauseReason.None);
        }
    }

    private void SetPaused(int generation, PauseReason reason)
    {
        lock (_gate)
        {
            if (!_running || generation != _generation) return;
            Volatile.Write(ref _streak, 0);
            SetStateLocked(reason switch
            {
                PauseReason.NoRegion => WatcherState.NoRegion,
                PauseReason.NoGame => WatcherState.Idle,
                _ => WatcherState.Paused,
            }, reason);
        }
    }

    /// <summary>Every state write happens under the gate for the current generation, so a stopped loop can't overwrite it.</summary>
    private void SetStateLocked(WatcherState state, PauseReason reason)
    {
        if (_pause != reason)
            _log.Info("Capture pause changed", LogField.Of("from", _pause), LogField.Of("to", reason));
        _pause = reason;
        _metrics.SetPause(reason);
        if (_state != state)
            _log.Info("Capture state changed", LogField.Of("from", _state), LogField.Of("to", state));
        _state = state;
    }

    private void PublishRegion(int generation, RegionResolution resolution)
    {
        if (!IsCurrent(generation)) return;
        var previous = _resolution;
        _resolution = resolution;
        if (!Equals(previous?.Region, resolution.Region)) RegionChanged?.Invoke(resolution.Region);
    }

    /// <summary>Raises <see cref="StateChanged"/> once per change of state, pause reason, health or hint.</summary>
    private void Announce()
    {
        var now = (_state, _pause, _metrics.Snapshot().Health, Hint is not null);
        lock (_gate)
        {
            if (now == _announced) return;
            _announced = now;
        }
        StateChanged?.Invoke(now.Item1);
    }
}
