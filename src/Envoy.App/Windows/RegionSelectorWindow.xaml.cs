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
/// pixels so it lines up with the capture code regardless of display scaling. Conversions go through
/// <see cref="Visual.PointToScreen"/>/<see cref="Visual.PointFromScreen"/>, which use this window's own monitor DPI
/// (REQ-CAP-31).
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
    public static Task<ScreenRegion?> SelectAsync(ScreenRegion? current, string? title = null, string? instructions = null)
    {
        var tcs = new TaskCompletionSource<ScreenRegion?>();
        var windows = Forms.Screen.AllScreens.Select(s => new RegionSelectorWindow(s.Bounds, current)).ToList();
        foreach (var w in windows)
        {
            if (title is not null) w.BannerTitle.Text = title;
            if (instructions is not null) w.BannerBody.Text = instructions;
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

    /// <summary>For the size label only; positions always convert through PointToScreen/PointFromScreen.</summary>
    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void ShowCurrent()
    {
        if (_current is null || _current.IsEmpty || PresentationSource.FromVisual(Surface) is null) return;
        var r = new Rect(
            Surface.PointFromScreen(new Point(_current.X, _current.Y)),
            Surface.PointFromScreen(new Point(_current.X + _current.Width, _current.Y + _current.Height)));
        if (r.Right < 0 || r.Bottom < 0 || r.Left > ActualWidth || r.Top > ActualHeight) return;
        Draw(r);
    }

    /// <summary>
    /// The physical screen rectangle for a rectangle in <see cref="Surface"/> coordinates (DIPs): both corners go
    /// through <see cref="Visual.PointToScreen"/>, normalised with min/max.
    /// </summary>
    internal ScreenRegion ToScreen(Rect surfaceRect)
    {
        var a = Surface.PointToScreen(surfaceRect.TopLeft);
        var b = Surface.PointToScreen(surfaceRect.BottomRight);
        var left = (int)Math.Round(Math.Min(a.X, b.X));
        var top = (int)Math.Round(Math.Min(a.Y, b.Y));
        var right = (int)Math.Round(Math.Max(a.X, b.X));
        var bottom = (int)Math.Round(Math.Max(a.Y, b.Y));
        return new ScreenRegion(left, top, right - left, bottom - top);
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

        Completed?.Invoke(ToScreen(rect));
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
