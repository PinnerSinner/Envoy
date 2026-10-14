using Envoy.Core.Ocr;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;

namespace Envoy.App.Services;

public enum WatcherState
{
    Stopped,
    NoRegion,
    Running,
    Idle,
    Error,
}

/// <summary>Result of one capture, for the live preview in settings.</summary>
public sealed record CaptureFrame(CapturedImage Image, IReadOnlyList<string> Lines, TimeSpan OcrTime);

/// <summary>Background loop: capture chat region → OCR (all configured languages) → pipeline.</summary>
public sealed class ChatWatcher(OcrService ocr, ChatPipeline pipeline, Func<AppSettings> settings, Func<string> currentGameId) : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ulong _lastFingerprint;

    public WatcherState State { get; private set; } = WatcherState.Stopped;
    public string? LastError { get; private set; }
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>When true each frame is published to <see cref="FrameCaptured"/> (only while the preview is open).</summary>
    public bool PublishFrames { get; set; }

    /// <summary>When this returns false the loop idles (e.g. no game running).</summary>
    public Func<bool> ShouldRead { get; init; } = () => true;

    public event Action<WatcherState>? StateChanged;
    public event Action<CaptureFrame>? FrameCaptured;

    public ScreenRegion? CurrentRegion
    {
        get
        {
            var regions = settings().Capture.Regions;
            return regions.TryGetValue(currentGameId(), out var r) ? r
                : regions.TryGetValue(Core.Games.GameCatalog.Any, out var any) ? any
                : null;
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _lastFingerprint = 0;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        SetState(WatcherState.Stopped);
    }

    /// <summary>Forces the next frame through OCR even if the image hasn't changed (after settings changes).</summary>
    public void Invalidate() => _lastFingerprint = 0;

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            var cfg = settings().Capture;
            try
            {
                await TickAsync(cfg, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SetState(WatcherState.Error);
            }

            var wait = TimeSpan.FromMilliseconds(Math.Max(200, cfg.IntervalMs)) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task TickAsync(CaptureSettings cfg, CancellationToken ct)
    {
        if (!ShouldRead() && !PublishFrames)
        {
            SetState(WatcherState.Idle);
            return;
        }

        if (CurrentRegion is not { IsEmpty: false } region)
        {
            SetState(WatcherState.NoRegion);
            return;
        }

        var scale = Math.Clamp(cfg.Upscale, 1.0, 4.0);
        var max = OcrService.MaxImageDimension;
        scale = Math.Min(scale, Math.Min((double)max / region.Width, (double)max / region.Height));

        var image = ScreenCapturer.Capture(region, scale);
        ScreenCapturer.Preprocess(image, cfg.Grayscale, cfg.Contrast, cfg.Invert);

        var fingerprint = ScreenCapturer.Fingerprint(image);
        if (fingerprint == _lastFingerprint && !PublishFrames)
        {
            SetState(WatcherState.Running);
            return;
        }
        _lastFingerprint = fingerprint;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var passes = await ocr.RecognizeAsync(image);
        var lines = OcrMerger.Merge(passes, settings().MyLanguage);
        sw.Stop();

        SetState(WatcherState.Running);
        if (PublishFrames) FrameCaptured?.Invoke(new CaptureFrame(image, lines, sw.Elapsed));

        // Don't await: translation latency shouldn't delay reading the next lines.
        _ = pipeline.ProcessAsync(lines, ct).ContinueWith(
            t => LastError = t.Exception?.GetBaseException().Message,
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void SetState(WatcherState state)
    {
        if (State == state) return;
        State = state;
        if (state != WatcherState.Error) LastError = null;
        StateChanged?.Invoke(state);
    }

    public void Dispose() => Stop();
}
