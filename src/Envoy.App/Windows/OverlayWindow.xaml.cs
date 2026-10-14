using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Envoy.App.Interop;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;

namespace Envoy.App.Windows;

/// <summary>
/// Transparent, click-through, always-on-top strip that shows translated chat next to the game's own chat.
/// </summary>
public partial class OverlayWindow : Window, INotifyPropertyChanged
{
    private readonly AppHost _host;
    private readonly ObservableCollection<OverlayItem> _items = [];
    private readonly DispatcherTimer _tick;
    private IntPtr _hwnd;
    private bool _editMode;

    public OverlayWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        DataContext = this;
        Items.ItemsSource = _items;

        _host.MessageArrived += OnMessage;
        _host.SettingsApplied += ApplySettings;
        SizeChanged += (_, _) => { if (!_editMode) Reposition(); };

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _tick.Start();
        ApplySettings();
    }

    private OverlaySettings S => _host.Settings.Overlay;

    // ---- bound appearance ----
    public double OverlayFontSize => S.FontSize;
    public double SmallFontSize => Math.Max(10, S.FontSize - 3);
    public FontFamily OverlayFontFamily => string.IsNullOrWhiteSpace(S.FontFamily) ? (FontFamily)FindResource("Font.Body") : new FontFamily(S.FontFamily);
    public Visibility ProviderVisibility => S.ShowProvider ? Visibility.Visible : Visibility.Collapsed;

    public Brush CardBackground
    {
        get
        {
            var color = TryFindResource("Overlay.Bg.Color") is Color c ? c : Colors.Black;
            color.A = (byte)(Math.Clamp(S.BackgroundOpacity, 0, 1) * 255);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetClickThrough(_hwnd, clickThrough: true);
        Reposition();
    }

    public void ApplySettings()
    {
        Width = Math.Max(240, S.Width);
        Opacity = Math.Clamp(S.Opacity, 0.2, 1);
        foreach (var name in new[] { nameof(OverlayFontSize), nameof(SmallFontSize), nameof(OverlayFontFamily), nameof(ProviderVisibility), nameof(CardBackground) })
            Notify(name);
        TrimToMax();
        Reposition();
    }

    private void OnMessage(IncomingMessage message, bool isNew)
    {
        if (isNew)
        {
            _items.Add(new OverlayItem(message, S.ShowSender, S.ShowOriginal));
            TrimToMax();
            if (S.PlaySound && message.Status != IncomingStatus.Untranslated) System.Media.SystemSounds.Asterisk.Play();
        }
        else
        {
            _items.FirstOrDefault(i => i.Message == message)?.Refresh();
        }
        UpdateEmptyHint();
    }

    private void TrimToMax()
    {
        while (_items.Count > Math.Max(1, S.MaxMessages)) _items.RemoveAt(0);
    }

    private void Tick()
    {
        // Fade out over the last two seconds of each message's life.
        var lifetime = TimeSpan.FromSeconds(Math.Max(3, S.MessageLifetimeSeconds));
        var now = DateTime.UtcNow;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var remaining = lifetime - (now - _items[i].ShownAt);
            if (_editMode) _items[i].Opacity = 1;
            else if (remaining <= TimeSpan.Zero) _items.RemoveAt(i);
            else _items[i].Opacity = Math.Min(1, remaining.TotalSeconds / 2);
        }
        UpdateEmptyHint();

        // Games re-assert their own z-order; keep the overlay above them.
        if (_hwnd != IntPtr.Zero && IsVisible) Native.KeepOnTop(_hwnd);
    }

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = _editMode && _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Places the overlay relative to the chat region (or at the saved free position).</summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        var region = _host.Watcher.CurrentRegion;
        if (S.Anchor == OverlayAnchor.Free || region is null || region.IsEmpty)
        {
            Left = S.Left;
            Top = S.Top;
            return;
        }

        // Region is in physical pixels; convert using this window's DPI.
        var dpi = VisualTreeHelper.GetDpi(this);
        double x = region.X / dpi.DpiScaleX, y = region.Y / dpi.DpiScaleY;
        double w = region.Width / dpi.DpiScaleX, h = region.Height / dpi.DpiScaleY;
        var height = ActualHeight > 0 ? ActualHeight : 100;
        (Left, Top) = S.Anchor switch
        {
            OverlayAnchor.AboveChat => (x, y - height - 6),
            OverlayAnchor.BelowChat => (x, y + h + 6),
            OverlayAnchor.RightOfChat => (x + w + 8, y + h - height),
            _ => (S.Left, S.Top),
        };
    }

    public void ToggleEditMode()
    {
        _editMode = !_editMode;
        if (_hwnd != IntPtr.Zero) Native.SetClickThrough(_hwnd, clickThrough: !_editMode);
        EditFrame.Visibility = EditBar.Visibility = ResizeGrip.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        if (_editMode)
        {
            if (!IsVisible) Show();
            S.Anchor = OverlayAnchor.Free;
            S.Left = Left;
            S.Top = Top;
        }
        else
        {
            _host.ApplySettings();
        }
        UpdateEmptyHint();
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode) return;
        DragMove();
        S.Left = Left;
        S.Top = Top;
        _host.ScheduleSave();
    }

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(240, Width + e.HorizontalChange);
        S.Width = Width;
        _host.ScheduleSave();
    }

    private void OnDoneEditing(object sender, RoutedEventArgs e) => ToggleEditMode();

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        _host.MessageArrived -= OnMessage;
        _host.SettingsApplied -= ApplySettings;
        base.OnClosed(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
