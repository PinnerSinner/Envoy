using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Envoy.App.Interop;
using Envoy.App.Pages;
using Envoy.App.Security;
using Envoy.App.Services;
using Envoy.App.Windows;
using Envoy.Core.Diagnostics;
using Envoy.Core.Hotkeys;
using Envoy.Core.Languages;
using Envoy.Core.Security;
using Envoy.Core.Settings;
using Forms = System.Windows.Forms;

namespace Envoy.App;

/// <summary>
/// Start-up, tray, hotkeys and exit. <see cref="OnStartup"/> and <see cref="ExitApp(RestartFlags?, bool)"/> follow the
/// order in contract §4; later areas fill the slots marked <c>[NN …]</c> in their own step.
/// </summary>
public partial class App : Application, IShell
{
    private const string InstanceName = "Envoy.SingleInstance.v1";
    private const string SessionFileName = "session.json";
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(30);

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private Forms.NotifyIcon? _tray;
    private HotkeyService? _hotkeys;
    private MainWindow? _main;
    private OverlayWindow? _overlay;
    private ComposeWindow? _compose;
    private LogHub? _hub;
    private MemoryLogSink? _recent;
    private ILog _log = NullLog.Instance;
    private GlobalExceptionHandlers? _handlers;
    private StartupSentinel? _sentinel;
    private ToastService? _toasts;
    private DispatcherTimer? _healthyTimer;
    private string? _dataFolder;
    private bool _healthy;
    private bool _exiting;

    public static AppHost Host { get; internal set; } = null!;
    public new static App Current => (App)Application.Current;

    /// <summary>The installed global handlers (the smoke test checks their wiring).</summary>
    internal GlobalExceptionHandlers? Handlers => _handlers;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 0. Read Shift first, so a quick release still counts (REQ-REL-39).
        var shiftHeld = Native.IsShiftDown();
        base.OnStartup(e);

        // 1. [06 Program.Main runs Velopack before WPF]

        // 2. Log bootstrap and the global handlers. The smoke test adds its own file sink on its temp data folder.
        var smoke = e.Args.Contains("--smoke-test");
        var soak = e.Args.Contains(SoakTest.Flag);
        _dataFolder = smoke || soak ? null : Path.GetDirectoryName(SettingsStore.DefaultPath);
        var (hub, recent, _) = LogBootstrap.Create(_dataFolder);
        _hub = hub;
        _recent = recent;
        _log = hub.For("App");
        ShellLauncher.Log = hub.For("Shell");
        var policy = new UnhandledErrorPolicy(hub, () => SmokeTest.IsRunning, CrashMarkerPath,
            AppHost.CurrentVersion.ToString(), new CrashLoopGuard());
        _handlers = GlobalExceptionHandlers.Install(this, policy, hub.For(UnhandledErrorPolicy.Category));
        SessionEnding += OnSessionEnding;

        // 3. Smoke branch. [08b --ocr-e2e]
        if (smoke)
        {
            SmokeTest.Run(this, e.Args, hub, recent);
            return;
        }
        if (soak)
        {
            SoakTest.Run(this, e.Args, hub);
            return;
        }

        // 4. Single-instance mutex. A second instance asks the first to show itself and exits.
        _instanceMutex = new Mutex(true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(InstanceName + ".Show").Set(); }
            catch (WaitHandleCannotBeOpenedException) { _log.Info("The running instance has no show signal"); }
            hub.Dispose();
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, false);

        // 5. [06 pending update apply: skipped with --safe-mode or Shift held; runs before the sentinel]

        // 6. Safe-mode decision, then the start-up sentinel.
        _sentinel = new StartupSentinel(Path.Combine(_dataFolder!, SessionFileName));
        var history = _sentinel.BeginStartup();
        var safe = SafeModeDecision.Decide(e.Args, shiftHeld, history.UnfinishedStarts, SafeModeDecision.AutoThreshold);

        // 7. AppHost.
        Host = new AppHost(SettingsStore.DefaultPath, Dispatcher, new AppHostOptions { Log = hub, RecentLog = recent, SafeMode = safe });
        LogBootstrap.WriteHeader(hub, safe, Host.SettingsLoad);
        var plan = StartupPlan.For(safe, e.Args.Contains("--minimized"), Host.Settings.FirstRunCompleted,
            Host.Settings.StartCapturingAutomatically);

        // 8. Notification services: toasts and the throttle always. [07a NoticeCenter + TrayNotifier unless safe]
        //    [07b high-contrast listeners]
        _toasts = new ToastService(Dispatcher, Host.Notifications, () => Host.Settings.Diagnostics.ShowErrorToasts,
            () => Host.Game?.MainWindow ?? IntPtr.Zero, OpenTroubleshooting, hub.For("Toast"));
        _handlers.ShowProblem = _toasts.Show;
        _handlers.BeforeCrashLoopExit = PrepareCrashLoopExit;
        Host.ProblemReported += _toasts.Show;

        // 9. Start (no automatic capture in safe mode). [06 Updates.StartAsync unless safe]
        Host.Start();

        // 10. Overlay and hotkeys, unless in safe mode.
        if (plan.CreateOverlay)
        {
            _overlay = new OverlayWindow(Host);
            if (Host.Settings.Overlay.Visible) _overlay.Show();
            Host.SettingsApplied += SyncOverlayVisibility;
            Host.RegionMoved += RepositionOverlay; // the anchored overlay follows a moved game window (REQ-CAP-7)
        }
        if (plan.RegisterHotkeys)
        {
            RegisterHotkeys();
            Host.SettingsApplied += RegisterHotkeys;
        }
        else
        {
            Host.HotkeyResults = [.. HotkeyBindings().Select(b => new HotkeyRegistrationResult(b.Id, HotkeyText.ActionName(b.Id), b.Combo ?? "", HotkeyStatus.SkippedSafeMode))];
        }

        // 11. Tray, then the window choice. [07a guide gate]
        CreateTray(plan.SafeMode);
        if (plan.ShowMainWindow) ShowMain();
        ShowStartupProblem(history);

        // 12. Healthy after 30 s or the first MainWindow render, whichever comes first.
        _healthyTimer = new DispatcherTimer(HealthyAfter, DispatcherPriority.Background, (_, _) => MarkHealthy(), Dispatcher);
        _healthyTimer.Start();
    }

    private string? CrashMarkerPath() =>
        Host is { } host ? host.CrashMarkerPath : _dataFolder is null ? null : Path.Combine(_dataFolder, "last-crash.json");

    public void ShowMain()
    {
        if (_exiting) return;
        if (_main is null)
        {
            _main = new MainWindow(Host);
            _main.ContentRendered += (_, _) => MarkHealthy();
        }
        _main.Closed -= OnMainClosed;
        _main.Closed += OnMainClosed;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void OnMainClosed(object? sender, EventArgs e) => _main = null;

    /// <summary>The toast's Details button: MainWindow on About › Troubleshooting.</summary>
    private void OpenTroubleshooting()
    {
        ShowMain();
        if (_main is not { } main) return;
        main.Navigate(PageIds.About);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ((AboutPage)main.PageFor(PageIds.About)).TroubleshootingFrame.BringIntoView());
    }

    private void MarkHealthy()
    {
        if (_healthy || _exiting) return;
        _healthy = true;
        _healthyTimer?.Stop();
        _sentinel?.MarkHealthy();
    }

    /// <summary>One toast about the previous session or the settings file (REQ-REL-22).</summary>
    private void ShowStartupProblem(StartupHistory history)
    {
        var problem = Host.SettingsLoad.Status switch
        {
            SettingsLoadStatus.RecoveredFromBackup => ProblemText.SettingsRecovered,
            SettingsLoadStatus.ResetCorrupt => ProblemText.SettingsReset,
            _ => history.PreviousSessionEndedUncleanly ? ProblemText.PreviousCrash : null,
        };
        if (history.PreviousSessionEndedUncleanly)
            _log.Warn("The previous session did not end cleanly", null, LogField.Of("crashMarker", File.Exists(Host.CrashMarkerPath)));
        if (problem is not null) Host.ReportProblem(problem);
    }

    /// <summary>
    /// The crash-loop exit (REQ-REL-15) skips <see cref="ExitApp(RestartFlags?, bool)"/>: hide what would otherwise
    /// outlive the process (tray icon, toasts) and stop hotkeys. The session stays unclean on purpose, so the next start
    /// reports the crash.
    /// </summary>
    private void PrepareCrashLoopExit()
    {
        _exiting = true;
        _healthyTimer?.Stop();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _toasts?.CloseAll();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }

    /// <summary>Windows is signing out or shutting down: end the session cleanly so the next start isn't a "crash".</summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _log.Info("Windows session ending", LogField.Of("reason", e.ReasonSessionEnding));
        if (Host is { } host && !host.SaveSuppressed) host.SaveNow();
        _sentinel?.EndSession();
        _hub?.Flush();
    }

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
        // Choose the reply target now: the game window, else whatever was in front unless it is Envoy or the taskbar.
        var target = KeyboardSender.ResolveTarget(Host.Game?.MainWindow ?? IntPtr.Zero);
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

    private void RepositionOverlay() => _overlay?.Reposition();

    /// <inheritdoc />
    public void ExitApp() => ExitApp(null);

    /// <summary>
    /// The exit sequence (contract §4): hide and unregister, save unless suppressed, dispose the host, end the
    /// session, close notifications and tray, close the log, optionally wipe the data folder, release the mutex,
    /// optionally restart, shut down.
    /// </summary>
    private void ExitApp(RestartFlags? restart, bool wipeData = false)
    {
        if (_exiting) return;
        _exiting = true;
        _healthyTimer?.Stop();

        // 1. Hide the overlay and unregister hotkeys.
        _overlay?.Hide();
        _hotkeys?.Dispose();
        _hotkeys = null;

        // 2. Apply pending edits and save, unless a reset or Delete-all suppressed saving. This is the last settings write:
        //    MainWindow.OnClosed (run later by Shutdown) no longer applies or saves.
        _main?.PrepareForAppExit();
        if (!Host.SaveSuppressed) Host.SaveNow();

        // 3. Dispose the host. [06 cancels the update CTS] [03 saves the cache unless suppressed]
        Host.Dispose();

        // 4. End the session cleanly.
        _sentinel?.EndSession();

        // 5. Notifications and tray. [07a notifier]
        _toasts?.CloseAll();
        _tray?.Dispose();
        _tray = null;

        // 6. Close the log: the last thing that writes envoy.log.
        _log.Info("Envoy exiting", LogField.Of("restart", restart?.ToString() ?? "no"), LogField.Of("wipeData", wipeData));
        _hub?.Flush();
        _hub?.Dispose();

        // 6a. Delete all Envoy data, now that the log file is closed.
        if (wipeData)
        {
            var result = DataInventory.DeleteAll(Host.DataFolder);
            if (result.Failed.Count > 0) DialogService.ShowInfo(null, DataText.SomeFilesKeptTitle, DataText.SomeFilesKept(result.Failed));
        }

        // 7. Release the mutex so the restarted copy can start.
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (already released): nothing to do.
        }
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        _showSignal?.Dispose();
        _showSignal = null;

        // 8. Restart. [06: or UpdateCoordinator.RestartToUpdate]
        if (restart is { } flags) ShellLauncher.RestartSelf(flags);

        // 9. Shut down.
        Shutdown();
    }

    /// <summary>Every hotkey action with its configured combination and what it does.</summary>
    private IEnumerable<(string Id, string? Combo, Action Action)> HotkeyBindings()
    {
        var keys = Host.Settings.Hotkeys;
        yield return (nameof(HotkeySettings.Compose), keys.Compose, OpenCompose);
        yield return (nameof(HotkeySettings.ToggleOverlay), keys.ToggleOverlay, ToggleOverlay);
        yield return (nameof(HotkeySettings.ToggleCapture), keys.ToggleCapture, Host.ToggleCapture);
        yield return (nameof(HotkeySettings.SelectRegion), keys.SelectRegion, SelectRegion);
        yield return (nameof(HotkeySettings.OverlayEditMode), keys.OverlayEditMode, ToggleOverlayEditMode);
        yield return (nameof(HotkeySettings.ScanAndTranslate), keys.ScanAndTranslate, ScanAndTranslate);
    }

    /// <summary>Registers every hotkey and publishes one result per action to <see cref="AppHost.HotkeyResults"/>.</summary>
    private void RegisterHotkeys()
    {
        _hotkeys ??= new HotkeyService();
        _hotkeys.UnregisterAll();
        var previous = Host.HotkeyResults;
        var results = new List<HotkeyRegistrationResult>();
        foreach (var (id, combo, action) in HotkeyBindings())
        {
            var status = string.IsNullOrWhiteSpace(combo) ? HotkeyStatus.NotSet
                : !Hotkey.TryParse(combo, out _) ? HotkeyStatus.Invalid
                : !_hotkeys.Register(combo, action) ? HotkeyStatus.Taken
                : HotkeyStatus.Registered;
            var result = new HotkeyRegistrationResult(id, HotkeyText.ActionName(id), combo ?? "", status);
            results.Add(result);
            if (status is HotkeyStatus.Taken or HotkeyStatus.Invalid && !previous.Contains(result))
                Log("Hotkeys").Warn("Hotkey not registered", null, LogField.Of("action", id), LogField.Of("combo", combo), LogField.Of("status", status));
        }
        Host.HotkeyResults = results;
    }

    private ILog Log(string category) => _hub?.For(category) ?? NullLog.Instance;

    private void CreateTray(bool safeMode)
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Envoy;component/Assets/envoy.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = safeMode ? SafeModeText.Tray : "Envoy — chat translator",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Envoy", null, (_, _) => ShowMain());
        if (safeMode) menu.Items.Add("Restart normally", null, (_, _) => Restart(false));
        menu.Items.Add("Reply…", null, (_, _) => OpenCompose());
        menu.Items.Add("Scan && translate…", null, (_, _) => ScanAndTranslate());
        menu.Items.Add("Select chat area", null, (_, _) => SelectRegion());
        menu.Items.Add("Pause / resume reading", null, (_, _) => Host.ToggleCapture());
        if (!safeMode) menu.Items.Add("Show / hide overlay", null, (_, _) => ToggleOverlay());
#if DEBUG
        // Manual steps M5 and M8 (docs/specs/02-reliability-observability.md): debug builds only.
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Throw test error", null, (_, _) => throw new InvalidOperationException("Test error from the tray menu."));
        menu.Items.Add("Crash (background thread)", null, (_, _) =>
            new Thread(() => throw new InvalidOperationException("Test crash on a background thread.")).Start());
#endif
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    /// <inheritdoc />
    public void OpenUrl(string urlOrDataPath) => _ = ShellLauncher.TryOpen(urlOrDataPath, Host.DataFolder);

    /// <inheritdoc />
    public void InstallOcrPack(string languageTag)
    {
        // Declined admin prompt or unknown language: point them at Windows language settings instead.
        if (!ShellLauncher.InstallOcrPack(languageTag))
            _ = ShellLauncher.TryOpen(OcrPackInstall.SettingsFallbackUri, Host.DataFolder);
    }

    /// <inheritdoc />
    public void ShowInfo(string title, string message) => DialogService.ShowInfo(_main, title, message);

    // ---- 02 ----

    /// <inheritdoc />
    public bool Confirm(string title, string message) => DialogService.Confirm(_main, title, message);

    /// <inheritdoc />
    public void Restart(bool safeMode) => ExitApp(safeMode ? RestartFlags.SafeMode : RestartFlags.None);

    /// <inheritdoc />
    public void ResetSettingsAndRestart()
    {
        if (!Confirm(DataText.ResetTitle, DataText.ResetMessage)) return;
        try
        {
            var backup = Host.ResetSettings(); // suppresses saving first
            _log.Info("Settings reset", LogField.Of("backup", backup is null ? null : Path.GetFileName(backup)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Settings reset could not remove settings.json", ex);
        }
        ExitApp(RestartFlags.None);
    }

    /// <inheritdoc />
    public void DeleteAllDataAndRestart()
    {
        var files = DataInventory.Enumerate(Host.DataFolder);
        if (!Confirm(DataText.DeleteAllTitle, DataText.DeleteAllMessage(files, Host.DataFolder))) return;
        Host.SuppressSave();
        StartupRegistration.Apply(false);
        _log.Info("Deleting all Envoy data", LogField.Of("files", files.Count));
        ExitApp(RestartFlags.None, wipeData: true);
    }

    /// <inheritdoc />
    public string? ChooseExportPath(string defaultFileName) => DialogService.ChooseSavePath(_main, defaultFileName);

    /// <inheritdoc />
    public bool CopyText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException ex)
            {
                _log.Debug("Clipboard busy", LogField.Of("attempt", attempt + 1), LogField.Of("error", ex.GetType().Name));
                if (attempt < 2) Thread.Sleep(100);
            }
        }
        Host.ReportProblem(ProblemText.ClipboardBusy);
        return false;
    }

    /// <inheritdoc />
    public void RevealFile(string path) => _ = ShellLauncher.RevealFile(path);
}
