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
using Envoy.App.Services;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Envoy.Core.Ux;

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
        ThemeManager.Applied += OnPaletteApplied;
        SizeChanged += (_, _) => { if (!_editMode) Reposition(); };

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _tick.Start();
        ApplySettings();
    }

    /// <summary>The item templates in OverlayWindow.xaml: today's layout and the compact one (REQ-OUXC-44).</summary>
    public const string FullItemKey = "FullItem";
    public const string CompactItemKey = "CompactItem";

    private OverlaySettings S => _host.Settings.Overlay;

    // ---- bound appearance ----
    public double OverlayFontSize => S.FontSize;
    public double SmallFontSize => Math.Max(10, S.FontSize - 3);
    /// <summary>The compact layout's font size (REQ-OUXC-44).</summary>
    public double CompactFontSize => Math.Max(11, S.FontSize - 2);

    /// <summary>True in edit mode (Ctrl+Shift+E): the cards show their Copy button (REQ-OUXC-45).</summary>
    public bool IsEditing
    {
        get => _editMode;
        private set
        {
            if (_editMode == value) return;
            _editMode = value;
            Notify();
        }
    }
    public FontFamily OverlayFontFamily => string.IsNullOrWhiteSpace(S.FontFamily) ? (FontFamily)FindResource("Font.Body") : new FontFamily(S.FontFamily);
    public Visibility ProviderVisibility => S.ShowProvider ? Visibility.Visible : Visibility.Collapsed;

    public Brush CardBackground
    {
        get
        {
            var color = TryFindResource("Overlay.Bg.Color") is Color c ? c : Colors.Black;
            color.A = (byte)Math.Round(CardBackgroundAlpha * 255);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// The card background alpha (REQ-OUXB-36): the user's value, raised so Overlay.Text stays at 4.5:1 over any backdrop
    /// when "Keep text readable" is on; always opaque in high contrast.
    /// </summary>
    public double CardBackgroundAlpha => ThemeManager.OverlayCardAlpha(S);

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
        var template = S.Compact ? CompactItemKey : FullItemKey;
        if (!ReferenceEquals(Items.ItemTemplate, FindResource(template))) Items.ItemTemplate = (DataTemplate)FindResource(template);
        foreach (var name in new[] { nameof(OverlayFontSize), nameof(SmallFontSize), nameof(CompactFontSize), nameof(OverlayFontFamily), nameof(ProviderVisibility), nameof(CardBackground) })
            Notify(name);
        TrimToMax();
        Reposition();
    }

    /// <summary>A palette change (including a live high-contrast switch) recomputes the card brush.</summary>
    private void OnPaletteApplied() => Notify(nameof(CardBackground));

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

    /// <summary>
    /// Places the overlay relative to the chat region, or at the saved free position. Anchored placement is computed in
    /// physical pixels and applied with SetWindowPos, so it is right on every monitor whatever its scale (REQ-CAP-33).
    /// </summary>
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

        // The overlay's own size in physical pixels at its current DPI.
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round((ActualWidth > 0 ? ActualWidth : Width) * dpi.DpiScaleX);
        var height = (int)Math.Round((ActualHeight > 0 ? ActualHeight : 100) * dpi.DpiScaleY);
        var gap = (int)Math.Round(6 * dpi.DpiScaleY);
        var (x, y) = S.Anchor switch
        {
            OverlayAnchor.AboveChat => (region.X, region.Y - height - gap),
            OverlayAnchor.BelowChat => (region.X, region.Y + region.Height + gap),
            OverlayAnchor.RightOfChat => (region.X + region.Width + (int)Math.Round(8 * dpi.DpiScaleX), region.Y + region.Height - height),
            _ => (region.X, region.Y),
        };
        _ = Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Moving to a monitor with another scale changes the overlay's physical size: place it again.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!_editMode) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Reposition);
    }

    public void ToggleEditMode()
    {
        IsEditing = !_editMode;
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
        ThemeManager.Applied -= OnPaletteApplied;
        base.OnClosed(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
