using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Envoy.App.Interop;
using Envoy.App.Services;
using Envoy.App.Windows;
using Envoy.Core.Hotkeys;
using Envoy.Core.Languages;
using Envoy.Core.Settings;
using Forms = System.Windows.Forms;

namespace Envoy.App;

public partial class App : Application, IShell
{
    private const string InstanceName = "Envoy.SingleInstance.v1";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private Forms.NotifyIcon? _tray;
    private HotkeyService? _hotkeys;
    private MainWindow? _main;
    private OverlayWindow? _overlay;
    private ComposeWindow? _compose;

    public static AppHost Host { get; internal set; } = null!;
    public new static App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        if (e.Args.Contains("--smoke-test"))
        {
            SmokeTest.Run(this, e.Args);
            return;
        }

        _instanceMutex = new Mutex(true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            // Already running: ask the existing instance to show itself.
            try { EventWaitHandle.OpenExisting(InstanceName + ".Show").Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, false);

        Host = new AppHost(SettingsStore.DefaultPath, Dispatcher);
        Host.Start();

        _overlay = new OverlayWindow(Host);
        if (Host.Settings.Overlay.Visible) _overlay.Show();

        CreateTray();
        RegisterHotkeys();
        Host.SettingsApplied += RegisterHotkeys;
        Host.SettingsApplied += SyncOverlayVisibility;

        if (!e.Args.Contains("--minimized") || !Host.Settings.FirstRunCompleted) ShowMain();
    }

    public void ShowMain()
    {
        _main ??= new MainWindow(Host);
        _main.Closed -= OnMainClosed;
        _main.Closed += OnMainClosed;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void OnMainClosed(object? sender, EventArgs e) => _main = null;

    public async void SelectRegion()
    {
        _main?.Hide();
        _compose?.Hide();
        var region = await RegionSelectorWindow.SelectAsync(Host.Watcher.CurrentRegion);
        if (region is not null) Host.SetRegion(region);
        _main?.Show();
        _overlay?.Reposition();
    }

    public async void ScanAndTranslate()
    {
        _compose?.Hide();
        await LookupWindow.ScanAsync(Host);
    }

    public void OpenCompose()
    {
        // Remember the game window so we can give focus back to it when sending.
        var target = Native.GetForegroundWindow();
        if (_main is not null && new System.Windows.Interop.WindowInteropHelper(_main).Handle == target)
            target = Host.Game?.MainWindow ?? IntPtr.Zero;

        _compose ??= new ComposeWindow(Host);
        _compose.Open(target);
    }

    public void ToggleOverlay()
    {
        if (_overlay is null) return;
        Host.Settings.Overlay.Visible = !_overlay.IsVisible;
        if (_overlay.IsVisible) _overlay.Hide();
        else _overlay.Show();
        Host.ScheduleSave();
    }

    private void SyncOverlayVisibility()
    {
        if (_overlay is null || _overlay.IsVisible == Host.Settings.Overlay.Visible) return;
        if (Host.Settings.Overlay.Visible) _overlay.Show();
        else _overlay.Hide();
    }

    public void ToggleOverlayEditMode() => _overlay?.ToggleEditMode();

    public void ExitApp()
    {
        _tray?.Dispose();
        _tray = null;
        _hotkeys?.Dispose();
        Host.Dispose();
        Shutdown();
    }

    /// <summary>Registers every hotkey and publishes one result per action to <see cref="AppHost.HotkeyResults"/>.</summary>
    private void RegisterHotkeys()
    {
        _hotkeys ??= new HotkeyService();
        _hotkeys.UnregisterAll();
        var keys = Host.Settings.Hotkeys;
        var results = new List<HotkeyRegistrationResult>();
        void Bind(string id, string? combo, Action action)
        {
            var status = string.IsNullOrWhiteSpace(combo) ? HotkeyStatus.NotSet
                : !Hotkey.TryParse(combo, out _) ? HotkeyStatus.Invalid
                : !_hotkeys.Register(combo, action) ? HotkeyStatus.Taken
                : HotkeyStatus.Registered;
            results.Add(new HotkeyRegistrationResult(id, HotkeyText.ActionName(id), combo ?? "", status));
        }
        Bind(nameof(HotkeySettings.Compose), keys.Compose, OpenCompose);
        Bind(nameof(HotkeySettings.ToggleOverlay), keys.ToggleOverlay, ToggleOverlay);
        Bind(nameof(HotkeySettings.ToggleCapture), keys.ToggleCapture, Host.ToggleCapture);
        Bind(nameof(HotkeySettings.SelectRegion), keys.SelectRegion, SelectRegion);
        Bind(nameof(HotkeySettings.OverlayEditMode), keys.OverlayEditMode, ToggleOverlayEditMode);
        Bind(nameof(HotkeySettings.ScanAndTranslate), keys.ScanAndTranslate, ScanAndTranslate);
        Host.HotkeyResults = results;
    }

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Envoy;component/Assets/envoy.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Envoy — chat translator",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Envoy", null, (_, _) => ShowMain());
        menu.Items.Add("Reply…", null, (_, _) => OpenCompose());
        menu.Items.Add("Scan && translate…", null, (_, _) => ScanAndTranslate());
        menu.Items.Add("Select chat area", null, (_, _) => SelectRegion());
        menu.Items.Add("Pause / resume reading", null, (_, _) => Host.ToggleCapture());
        menu.Items.Add("Show / hide overlay", null, (_, _) => ToggleOverlay());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = Path.Combine(AppHost.DataFolder, "error.log");
        try
        {
            Directory.CreateDirectory(AppHost.DataFolder);
            File.AppendAllText(log, $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch (IOException) { }

        if (SmokeTest.IsRunning) return; // the smoke test reports its own failures
        DialogService.ShowWarning(null, "Envoy", $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to {log}");
        e.Handled = true;
    }

    /// <inheritdoc />
    public void OpenUrl(string urlOrDataPath) => Process.Start(new ProcessStartInfo(urlOrDataPath) { UseShellExecute = true });

    /// <inheritdoc />
    public void InstallOcrPack(string languageTag)
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", OcrPackInstall.PowerShellArguments(languageTag)) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            // User declined the admin prompt (or the language is unknown): point them at Settings instead.
            OpenUrl(OcrPackInstall.SettingsFallbackUri);
        }
    }

    /// <inheritdoc />
    public void ShowInfo(string title, string message) => DialogService.ShowInfo(_main, title, message);
}
