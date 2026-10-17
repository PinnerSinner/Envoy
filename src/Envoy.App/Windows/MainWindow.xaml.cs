using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Envoy.App.Controls;
using Envoy.App.Pages;
using Envoy.App.Services;
using Envoy.App.ViewModels;

namespace Envoy.App.Windows;

/// <summary>
/// The settings window: title bar, sidebar and one host per page in <c>Pages/</c>. Every page shares this window's
/// <see cref="MainViewModel"/>; any settings edit is applied (and saved) 400 ms after the last change.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly SettingsAutoApplier _applier;
    private readonly DispatcherTimer _statusTimer;
    private bool _appExiting;

    public MainWindow(AppHost host) : this(host, App.Current)
    {
    }

    public MainWindow(AppHost host, IShell shell)
    {
        _host = host;
        _applier = new SettingsAutoApplier(Dispatcher, TimeSpan.FromMilliseconds(400), ApplySettingsNow);
        _vm = new MainViewModel(host, shell, _applier);
        InitializeComponent();
        DataContext = _vm;

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _vm.RefreshStatus(), Dispatcher);
        _statusTimer.Start();

        // Any edit anywhere in the settings pages is applied (and saved) shortly after it happens.
        AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler(OnEdited));
        PreviewKeyDown += OnPagesKeyDown; // Ctrl+Tab / Ctrl+Shift+Tab (REQ-OUXB-30)
#if DEBUG
        // Manual step M4 (docs/specs/02-reliability-observability.md): Ctrl+Alt+Shift+F12 throws on the UI thread.
        InputBindings.Add(new KeyBinding(new Core.Mvvm.RelayCommand(() => throw new InvalidOperationException("Test error from MainWindow.")),
            Key.F12, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift));
#endif
    }

    /// <summary>
    /// <c>ExitApp</c> step 2: applies an edit still waiting for its debounce and records that the window was seen (unless
    /// saving is suppressed). From now on <see cref="OnClosed"/> applies and saves nothing, so the exit save is the last
    /// settings write and a restarted copy never races this process on <c>settings.json.tmp</c>.
    /// </summary>
    public void PrepareForAppExit()
    {
        _appExiting = true;
        _statusTimer.Stop();
        if (_host.SaveSuppressed) return;
        _applier.FlushIfPending();
        _host.Settings.FirstRunCompleted = true;
    }

    /// <summary>Page ids, in sidebar order (the smoke test walks them).</summary>
    public IReadOnlyList<string> PageNames => PageIds.All;

    /// <summary>Shows a page by its <see cref="PageIds"/> id.</summary>
    public void Navigate(string page)
    {
        if (FindName("Nav" + page) is RadioButton nav) nav.IsChecked = true;
    }

    /// <summary>The page whose sidebar item is checked, or null before the first navigation.</summary>
    public string? CurrentPage => PageIds.All.FirstOrDefault(p => FindName("Nav" + p) is RadioButton { IsChecked: true });

    /// <summary>Moves to the next (or previous) page, wrapping at both ends, and focuses its sidebar item.</summary>
    public void NavigateRelative(int step)
    {
        var pages = PageIds.All;
        var current = CurrentPage is { } page ? pages.ToList().IndexOf(page) : 0;
        var next = pages[(((current + step) % pages.Count) + pages.Count) % pages.Count];
        Navigate(next);
        if (FindName("Nav" + next) is RadioButton nav) nav.Focus();
    }

    private void OnPagesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        NavigateRelative((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        e.Handled = true;
    }

    /// <summary>The page instance hosted for a <see cref="PageIds"/> id.</summary>
    public UserControl PageFor(string pageId) => pageId switch
    {
        PageIds.Dashboard => WarRoomPage,
        PageIds.Translation => TranslationPage,
        PageIds.Capture => ReadingChatPage,
        PageIds.Overlay => OverlayPage,
        PageIds.Reply => ReplyingPage,
        PageIds.Phrases => PhrasebookPage,
        PageIds.Hotkeys => HotkeysPage,
        PageIds.Appearance => AppearancePage,
        PageIds.About => AboutPage,
        _ => throw new ArgumentOutOfRangeException(nameof(pageId), pageId, "Unknown page id."),
    };

    private void OnEdited(object sender, RoutedEventArgs e)
    {
        // Controls that aren't settings (navigation, test boxes, previews) opt out with c:AutoApply.Ignore.
        if (e.OriginalSource is DependencyObject d && AutoApply.GetIgnore(d)) return;
        if (!IsLoaded) return;
        _applier.Schedule();
    }

    private void ApplySettingsNow()
    {
        _host.ApplySettings();
        _vm.RefreshStatus();
        _vm.RefreshProviderSections();
    }

    // ---- window chrome ----
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        // After a reset or "Delete all Envoy data" nothing may be applied or written again (contract §4). During
        // ExitApp, step 2 already applied and saved, and the host is disposed by the time Shutdown closes this window.
        if (!_appExiting && !_host.SaveSuppressed)
        {
            _applier.FlushIfPending();
            _host.Settings.FirstRunCompleted = true;
            _host.SaveNow();
        }
        _vm.Dispose();
        base.OnClosed(e);
    }
}
