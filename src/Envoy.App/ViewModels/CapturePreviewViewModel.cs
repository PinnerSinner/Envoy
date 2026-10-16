using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Envoy.App.Services;
using Envoy.Core.Capture;
using Envoy.Core.Chat;
using Envoy.Core.Mvvm;
using Envoy.Core.Ocr;

namespace Envoy.App.ViewModels;

/// <summary>
/// The live view of what Envoy captured and read: the only home for preview state (contract §2.8). While
/// <see cref="IsActive"/> is true it holds a <see cref="ChatWatcher.BeginPreview"/> lease, so the watcher publishes
/// frames; several instances (settings page, setup guide) can be active at once without switching each other off.
/// While active, a 1 s timer refreshes the capture metrics and health lines (REQ-CAP-39). Each frame also shows how
/// Envoy understood it ("Understood as", REQ-CHAT-41), and "Copy as test capture" copies the frame's lines (REQ-CHAT-42).
/// </summary>
public sealed class CapturePreviewViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _metricsTimer;
    private IDisposable? _lease;
    private bool _isActive;
    private bool _disposed;
    private ImageSource? _image;
    private string _text = "";
    private string _stats = "";
    private string _understood = "";
    private string _copyCaption = "";
    private IReadOnlyList<OcrLine> _lastLines = [];
    private readonly Func<string, bool>? _copyText;

    /// <param name="copyText">
    /// <see cref="IShell.CopyText"/> for "Copy as test capture"; null (the setup guide) hides the button.
    /// </param>
    public CapturePreviewViewModel(AppHost host, Dispatcher dispatcher, Func<string, bool>? copyText = null)
    {
        _host = host;
        _dispatcher = dispatcher;
        _copyText = copyText;
        CopyAsTestCaptureCommand = new RelayCommand(CopyAsTestCapture, () => _copyText is not null && _lastLines.Count > 0);
        _host.Watcher.FrameCaptured += OnFrame;
        _metricsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshMetrics(), dispatcher)
        {
            IsEnabled = false,
        };
    }

    /// <summary>True while a view shows the preview; holds a preview lease exactly while true.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (value && _disposed) return;
            if (!SetProperty(ref _isActive, value)) return;
            if (value)
            {
                _lease ??= _host.Watcher.BeginPreview();
                RefreshMetrics();
                _metricsTimer.Start();
            }
            else
            {
                _metricsTimer.Stop();
                _lease?.Dispose();
                _lease = null;
            }
        }
    }

    /// <summary>The captured image after enhancement (frozen, built on the UI thread).</summary>
    public ImageSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    /// <summary>What OCR read, one line per row.</summary>
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    /// <summary>Line count and OCR time.</summary>
    public string Stats
    {
        get => _stats;
        private set => SetProperty(ref _stats, value);
    }

    /// <summary>How the last frame was understood: one line per message, notice or fragment (contract §2.8).</summary>
    public string UnderstoodText
    {
        get => _understood;
        private set => SetProperty(ref _understood, value);
    }

    /// <summary>Copies the last frame's merged lines as a test capture (REQ-CHAT-42). Runs only when clicked.</summary>
    public RelayCommand CopyAsTestCaptureCommand { get; }

    /// <summary>True when this live view can copy (the settings page; not the setup guide).</summary>
    public bool CanCopy => _copyText is not null;

    /// <summary>Shown after copying: the text contains chat and player names.</summary>
    public string CopyCaption
    {
        get => _copyCaption;
        private set => SetProperty(ref _copyCaption, value);
    }

    /// <summary>Capture and OCR times, skip rate, interval and capture method, e.g. "capture 4 ms · OCR 61 ms · …".</summary>
    public string MetricsText => CaptureText.Stats(_host.Watcher.Metrics);

    /// <summary>One sentence on how chat reading is doing.</summary>
    public string HealthText => _host.Watcher.IsRunning ? CaptureText.Health(_host.Watcher.Metrics) : CaptureText.Stopped;

    private void RefreshMetrics()
    {
        OnPropertyChanged(nameof(MetricsText));
        OnPropertyChanged(nameof(HealthText));
    }

    private void OnFrame(CaptureFrame frame)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsActive) return;
            var bmp = BitmapSource.Create(frame.Image.Width, frame.Image.Height, 96, 96, PixelFormats.Bgra32, null, frame.Image.Pixels, frame.Image.Stride);
            bmp.Freeze();
            Image = bmp;
            Text = CapturePreviewText.Lines(frame.Lines.Select(l => l.Text).ToList());
            Stats = CapturePreviewText.Stats(frame.Lines.Count, frame.OcrTime);
            _lastLines = frame.Lines;
            UnderstoodText = _host.Pipeline.Describe(frame.Lines); // no side effects (contract §2.8)
            CopyAsTestCaptureCommand.RaiseCanExecuteChanged();
        });
    }

    private void CopyAsTestCapture()
    {
        if (_copyText is null || _lastLines.Count == 0) return;
        var copied = _copyText(CaptureFixture.Write(_lastLines, _host.Settings.MyLanguage));
        CopyCaption = copied ? ChatUnderstandingText.CopiedCaption : ChatUnderstandingText.CopyFailedCaption;
    }

    /// <summary>Releases the lease and stops listening for frames. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        IsActive = false;
        _metricsTimer.Stop();
        _host.Watcher.FrameCaptured -= OnFrame;
        _disposed = true;
    }
}
