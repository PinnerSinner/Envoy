using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Envoy.App.Interop;
using Envoy.Core.Settings;
using Forms = System.Windows.Forms;

namespace Envoy.App.Windows;

/// <summary>
/// Full-screen "drag a rectangle" picker. One instance covers each monitor; the result is in physical screen
/// pixels so it lines up with the capture code regardless of display scaling.
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private readonly System.Drawing.Rectangle _screen;
    private readonly ScreenRegion? _current;
    private Point? _start;

    public event Action<ScreenRegion?>? Completed;

    public RegionSelectorWindow(System.Drawing.Rectangle physicalScreenBounds, ScreenRegion? current)
    {
        _screen = physicalScreenBounds;
        _current = current;
        InitializeComponent();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += OnKey;
        Loaded += (_, _) => { Activate(); Focus(); ShowCurrent(); };
        SizeChanged += (_, _) => FullRect.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
    }

    /// <summary>Shows a picker on every monitor and returns the chosen region, or null if cancelled.</summary>
    public static Task<ScreenRegion?> SelectAsync(ScreenRegion? current)
    {
        var tcs = new TaskCompletionSource<ScreenRegion?>();
        var windows = Forms.Screen.AllScreens.Select(s => new RegionSelectorWindow(s.Bounds, current)).ToList();
        foreach (var w in windows)
        {
            w.Completed += result =>
            {
                foreach (var other in windows) other.Close();
                tcs.TrySetResult(result);
            };
            w.Show();
        }
        return tcs.Task;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Position in physical pixels; WPF's Left/Top are DIPs and differ per monitor.
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, _screen.X, _screen.Y, _screen.Width, _screen.Height, Native.SWP_SHOWWINDOW);
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void ShowCurrent()
    {
        if (_current is null || _current.IsEmpty) return;
        var s = Scale;
        var r = new Rect((_current.X - _screen.X) / s, (_current.Y - _screen.Y) / s, _current.Width / s, _current.Height / s);
        if (r.Right < 0 || r.Bottom < 0 || r.Left > ActualWidth || r.Top > ActualHeight) return;
        Draw(r);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Surface);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } start) return;
        var p = e.GetPosition(Surface);
        Draw(new Rect(start, p));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start) return;
        ReleaseMouseCapture();
        _start = null;
        var rect = new Rect(start, e.GetPosition(Surface));
        if (rect.Width < 20 || rect.Height < 12) return; // a click, not a drag

        var s = Scale;
        Completed?.Invoke(new ScreenRegion(
            _screen.X + (int)Math.Round(rect.X * s),
            _screen.Y + (int)Math.Round(rect.Y * s),
            (int)Math.Round(rect.Width * s),
            (int)Math.Round(rect.Height * s)));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Completed?.Invoke(null);
        else if (e.Key == Key.Enter) Completed?.Invoke(_current);
    }

    private void Draw(Rect r)
    {
        Selection.Visibility = SizeTag.Visibility = Visibility.Visible;
        Canvas.SetLeft(Selection, r.X);
        Canvas.SetTop(Selection, r.Y);
        Selection.Width = r.Width;
        Selection.Height = r.Height;
        HoleRect.Rect = r;

        var s = Scale;
        SizeText.Text = $"{Math.Round(r.Width * s)} × {Math.Round(r.Height * s)}";
        Canvas.SetLeft(SizeTag, r.X);
        Canvas.SetTop(SizeTag, Math.Max(0, r.Y - 22));
    }
}
