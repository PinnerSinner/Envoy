using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Envoy.App.Interop;
using Envoy.Core.Diagnostics;
using Envoy.Core.Notifications;

namespace Envoy.App.Windows;

/// <summary>
/// A problem toast (REQ-REL-18): topmost, never activated (<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>), closes itself
/// after 10 s (6 s for a Warning) unless the pointer is over it. Created and placed only by <c>ToastService</c>.
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;

    public ToastWindow(ProblemReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
        InitializeComponent();
        TitleText.Text = report.Title;
        DetailText.Text = report.Detail;
        // The seal's wax: gules for an error, or (gold) for a warning (spec 09a).
        Glyph.SetResourceReference(BackgroundProperty, report.Severity >= LogLevel.Error ? "Wax.Gules" : "Wax.Or");

        Lifetime = report.Severity >= LogLevel.Error ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(6);
        _timer = new DispatcherTimer(Lifetime, DispatcherPriority.Background, (_, _) => Close(), Dispatcher) { IsEnabled = false };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeNoActivate(hwnd);
            Native.KeepOnTop(hwnd);
        };
        Loaded += (_, _) => _timer.Start();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) =>
        {
            _timer.Stop();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            IsClosed = true;
        };
    }

    public ProblemReport Report { get; }

    /// <summary>How long the toast stays up while the pointer is elsewhere.</summary>
    public TimeSpan Lifetime { get; }

    public bool IsClosed { get; private set; }

    /// <summary>The window handle (zero before it is shown), for the smoke test's style check.</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Raised by the Details button; ToastService opens About › Troubleshooting.</summary>
    public event Action? DetailsRequested;

    /// <summary>Bottom-right of <paramref name="workAreaDip"/>, <paramref name="offsetFromBottom"/> DIPs up from its bottom edge.</summary>
    public void Place(Rect workAreaDip, double offsetFromBottom)
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 120;
        Left = workAreaDip.Right - width - 4;
        Top = workAreaDip.Bottom - offsetFromBottom - height - 4;
    }

    /// <summary>Same as clicking Details (the smoke test uses it).</summary>
    public void RequestDetails()
    {
        DetailsRequested?.Invoke();
        Close();
    }

    private void OnDetails(object sender, RoutedEventArgs e) => RequestDetails();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
