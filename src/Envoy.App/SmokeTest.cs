using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Envoy.App.Controls;
using Envoy.App.Interop;
using Envoy.App.Pages;
using Envoy.App.Services;
using Envoy.App.Services.Capture;
using Envoy.App.ViewModels;
using Envoy.App.Windows;
using Envoy.Core.Capture;
using Envoy.Core.Chat;
using Envoy.Core.Diagnostics;
using Envoy.Core.Games;
using Envoy.Core.Notifications;
using Envoy.Core.Ocr;
using Envoy.Core.Onboarding;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Envoy.Core.Updates;
using Forms = System.Windows.Forms;

namespace Envoy.App;

/// <summary>
/// `Envoy.exe --smoke-test [log-path] [--screenshots dir]`: builds every window and settings page under every palette,
/// then exits 0 (or 1 with the error in the log). CI runs this on a Windows runner so XAML resource and binding
/// mistakes fail the build. With `--screenshots <dir>` it also saves PNGs of each page and window under each palette.
/// </summary>
/// <remarks>
/// Three phases (contract §6): setup and once-only work; one pass per <see cref="ThemeManager.PaletteIds"/> entry
/// (pages, MainWindow-dependent checks incl. 03's translation-health rows, close, other windows, binding errors);
/// teardown. Every section logs <c>[smoke] {section} ok elapsed_ms={n}</c>. New windows and pages are exercised in the
/// per-palette phase. Every host runs on its own folder under <c>%TEMP%\envoy-smoke-{guid}</c>, which is deleted at the
/// end, so the smoke test never reads or writes the user's real Envoy data.
/// </remarks>
internal static class SmokeTest
{
    /// <summary>Total time before a warning; build.yml kills the run at 600 s.</summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(420);

    /// <summary>Time per palette before a warning.</summary>
    private static readonly TimeSpan PaletteBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Binding errors that are known and accepted, by exact message. Keep this empty unless a fix is impossible; the
    /// PR that adds an entry must list it.
    /// </summary>
    private static readonly HashSet<string> BindingErrorAllowlist = new(StringComparer.Ordinal);

    /// <summary>The page names used before PageIds existed; Navigate() and the screenshot names depend on them.</summary>
    private static readonly string[] LegacyPageNames =
        ["Dashboard", "Translation", "Capture", "Overlay", "Reply", "Phrases", "Hotkeys", "Appearance", "About"];

    private static readonly Dictionary<string, Type> PageTypes = new()
    {
        [PageIds.Dashboard] = typeof(WarRoomPage),
        [PageIds.Translation] = typeof(TranslationPage),
        [PageIds.Capture] = typeof(ReadingChatPage),
        [PageIds.Overlay] = typeof(OverlayPage),
        [PageIds.Reply] = typeof(ReplyingPage),
        [PageIds.Phrases] = typeof(PhrasebookPage),
        [PageIds.Hotkeys] = typeof(HotkeysPage),
        [PageIds.Appearance] = typeof(AppearancePage),
        [PageIds.About] = typeof(AboutPage),
    };

    private const string ComposeSample = "good game! one more?";

    private static string _logPath = "";
    private static ILog _smokeLog = NullLog.Instance;

    public static bool IsRunning { get; private set; }

    public static async void Run(App app, string[] args, LogHub log, MemoryLogSink recent)
    {
        IsRunning = true;
        _smokeLog = log.For("Smoke");
        _logPath = ArgValue(args, "--smoke-test") ?? Path.Combine(Environment.CurrentDirectory, "smoke-test.log");
        var shots = ArgValue(args, "--screenshots");
        if (shots is not null) Directory.CreateDirectory(shots);
        try { File.WriteAllText(_logPath, ""); }
        catch (IOException) { }

        var exitCode = 0;
        var total = Stopwatch.StartNew();
        string? root = null;
        AppHost? host = null;
        AppHost? safeHost = null;
        AppHost? portableHost = null;
        RollingFileSink? file = null;
        try
        {
            // ================= 1. Setup and once-only work =================
            var sw = Stopwatch.StartNew();
            root = Path.Combine(Path.GetTempPath(), $"envoy-smoke-{Guid.NewGuid():N}");
            var data = Path.Combine(root, "data");
            var settingsPath = Path.Combine(data, "settings.json");
            // 06: a scripted update backend, so the update banner states render without any network.
            var updates = new SmokeUpdateBackend(InstallKind.Installed);
            host = new AppHost(settingsPath, app.Dispatcher, new AppHostOptions { DataFolder = data, Log = log, RecentLog = recent, UpdateBackend = updates });
            App.Host = host;
            // The file sink goes on right before the header, so the header is envoy.log's first line (log-tail checks it).
            file = new RollingFileSink(host.LogsFolder);
            log.AddSink(file);
            LogBootstrap.WriteHeader(log, null, host.SettingsLoad);
            Check(host.DataFolder.StartsWith(root, StringComparison.OrdinalIgnoreCase), $"host data folder {host.DataFolder} is outside the smoke root");
            Section("host", sw);

            Log("ocr languages: " + string.Join(", ", OcrService.InstalledLanguages().Select(l => l.WindowsTag)));
            Section("ocr-languages", sw);

            // ---- 07a: the privacy gate on the fresh host, before anything is sent (AC-OUXA-11) ----
            await CheckPrivacyGateAsync(host);
            Section("privacy-gate", sw);

            await WarmUpProviderAsync(host);
            Section("provider-warmup", sw);

            // ---- 03: display-only provider health for the per-palette rows check ----
            SeedTranslationHealth(host);
            Section("translation-health-seed", sw);

            // ---- 04: stitching, notice patterns and chat formats through the host's wiring ----
            CheckChatUnderstanding(host);
            Section("chat-understanding", sw);

            // Capture + OCR once on a small screen area (the runner's desktop).
            var image = ScreenCapturer.Capture(new ScreenRegion(0, 0, 320, 120), 2.0);
            ScreenCapturer.Preprocess(image, true, 1.3, false);
            var passes = await host.Ocr.RecognizeAsync(image);
            using (var backend = new GdiCaptureBackend())
            using (var frame = backend.Capture(new CaptureTarget(new ScreenRegion(0, 0, 320, 120), IntPtr.Zero, 640, 240), new FramePool()))
            {
                // The watcher's bounded, single-flight overload (REQ-CAP-25, 26).
                var bounded = await host.Ocr.RecognizeAsync(frame, TimeSpan.FromSeconds(10), CancellationToken.None);
                Check(bounded.Count == passes.Count, $"the bounded OCR overload ran {bounded.Count} engines, the one-shot one {passes.Count}");
                Check(!host.Ocr.IsBusy, "OCR is still busy after the bounded call returned");
            }
            Log($"capture+ocr: {image.Width}x{image.Height}, {passes.Count} engines");
            Section("capture-ocr", sw);

            // ---- 05: the capture engine on a real window, once ----
            await CaptureEngineSmokeAsync(host, log);
            Check(sw.Elapsed <= CaptureEngineBudget, $"capture-engine took {sw.ElapsedMilliseconds} ms (budget {CaptureEngineBudget.TotalMilliseconds:0} ms)");
            Section("capture-engine", sw);

            Check(PageIds.All.SequenceEqual(LegacyPageNames), $"PageIds.All is [{string.Join(", ", PageIds.All)}], expected the legacy names in order");
            Section("page-ids", sw);

            // ---- 02: safe host, diagnostics, handler wiring ----
            safeHost = CheckSafeHost(app, root, log, recent);
            Section("safe-host", sw);
            CheckDiagnostics(host, root);
            Section("diagnostics", sw);
            CheckHandlers(app);
            Section("handlers", sw);

            // ---- 06: the main host stops at Available (no background download); one Portable host, reused ----
            host.Settings.Updates.AutoDownload = false;
            host.ApplySettings();
            portableHost = await CheckPortableUpdatesAsync(app, root, log, recent);
            Section("updates-portable", sw);

            // ---- 07a: the guide's entry points (AC-OUXA-3), and a notice so the War Room list renders ----
            await CheckGuideEntryAsync(app, host);
            Section("guide-entry", sw);
            Check(host.Notices is not null, "the main host has no NoticeCenter");
            host.Notices!.Submit(NoticeFactory.OcrMissing(["ja"])!, gameForeground: false);

            // Binding errors fail the run. Refresh() turns WPF tracing on in release builds; set the level after it.
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            // ================= 2. Per palette =================
            var first = true;
            foreach (var palette in ThemeManager.PaletteIds)
            {
                await RunPaletteAsync(app, host, safeHost, updates, settingsPath, palette, first, shots);
                first = false;
            }

            // ================= 3. Teardown =================
            sw.Restart();
            portableHost.Dispose();
            portableHost = null;
            safeHost.Dispose();
            safeHost = null;
            host.Dispose();
            host = null;
            if (shots is not null)
            {
                var expected = ThemeManager.PaletteIds.Count * PageIds.All.Count;
                var actual = Directory.GetFiles(shots, "*-settings-*.png").Length;
                Check(actual == expected, $"expected {expected} settings screenshots, found {actual}");
            }
            log.RemoveSink(file);
            file.Dispose();
            var envoyLog = File.ReadAllLines(file.CurrentPath);
            file = null;
            DeleteTempRoot(root);
            Check(!Directory.Exists(root), $"the smoke temp root {root} still exists");
            Section("teardown", sw);

            CheckLogTail(envoyLog);
            Section("log-tail", sw);
            Log("SMOKE TEST PASSED");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            Log("SMOKE TEST FAILED");
            Log(ex.ToString());
        }
        finally
        {
            try { portableHost?.Dispose(); }
            catch (Exception ex) { Warn($"portable host dispose failed: {ex.Message}"); }
            try { safeHost?.Dispose(); }
            catch (Exception ex) { Warn($"safe host dispose failed: {ex.Message}"); }
            try { host?.Dispose(); }
            catch (Exception ex) { Warn($"host dispose failed: {ex.Message}"); }
            if (file is not null)
            {
                log.RemoveSink(file);
                file.Dispose();
            }
            if (root is not null && Directory.Exists(root)) DeleteTempRoot(root);
            Log($"[smoke] total elapsed_ms={total.ElapsedMilliseconds}");
            if (total.Elapsed > TotalBudget) Warn($"total {total.Elapsed.TotalSeconds:0} s exceeds the {TotalBudget.TotalSeconds:0} s budget");
        }

        app.Shutdown(exitCode);
    }

    private static async Task RunPaletteAsync(App app, AppHost host, AppHost safeHost, SmokeUpdateBackend updates, string settingsPath, string palette,
                                              bool first, string? shots)
    {
        var paletteTime = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        host.Settings.Theme = palette; // so a settings re-apply doesn't switch back to "match game"
        ThemeManager.Apply(palette);

        var listener = new BindingErrorListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            // ---- pages ----
            var main = new MainWindow(host);
            main.Show();
            Check(main.PageNames.SequenceEqual(PageIds.All), "MainWindow.PageNames differs from PageIds.All");
            if (shots is not null) host.ShowDemoMessages();
            foreach (var page in PageIds.All)
            {
                main.Navigate(page);
                await Pump();
                AssertOnlyPageVisible(main, page);
                if (shots is not null) Save(main, Path.Combine(shots, $"{palette}-settings-{page.ToLowerInvariant()}.png"));
            }
            Section($"pages({palette})", sw);

            // ---- MainWindow-dependent checks: 01's first; later areas append theirs in contract order ----
            if (first)
            {
                await CheckCommandsAsync(main);
                Section($"commands({palette})", sw);
                await CheckPreviewLeaseAsync(host, main);
                Section($"preview-lease({palette})", sw);
                await CheckAutoApplyAsync(host, main);
                Section($"autoapply({palette})", sw);
                await CheckChatUnderstandingCardAsync(main);
                Section($"chat-card({palette})", sw);
            }
            Check(main.SafeModeBanner.Visibility == Visibility.Collapsed, "the safe-mode banner shows on a normal host");
            await CheckSafeBannerAsync(safeHost, palette, shots);
            Section($"safe-banner({palette})", sw);
            await CheckToastAsync(main, palette, shots);
            Section($"toast({palette})", sw);
            var rows = await CheckHealthRowsAsync(host, main);
            Log($"[smoke] translation-health-rows ok elapsed_ms={sw.ElapsedMilliseconds} rows={rows} palette={palette}");
            sw.Restart();
            await CheckUpdateBannerAsync(host, updates, main, palette, shots);
            Section($"updates({palette})", sw);

            // ---- 07a: notices, the War Room slot order, notice actions and the section order ----
            await CheckNoticesAsync(host, main);
            Section($"notices({palette})", sw);
            await CheckWarRoomLayoutAsync(host, main, first);
            Section($"war-room-layout({palette})", sw);
            if (first)
            {
                await CheckNoticeActionsAsync(app);
                Section("notice-actions", sw);
                await CheckPageOrderAsync(main);
                Section("page-order", sw);
            }

            // ---- close ----
            if (first)
            {
                var showProvider = FindShowProviderCheckBox(main);
                var expected = !(showProvider.IsChecked ?? false);
                showProvider.IsChecked = expected;
                main.Close(); // must apply the still-pending edit, set FirstRunCompleted and save
                var saved = new SettingsStore(settingsPath, new DpapiProtector()).Load();
                Check(saved.Overlay.ShowProvider == expected, "closing MainWindow did not save the pending edit");
                Check(saved.FirstRunCompleted, "closing MainWindow did not set FirstRunCompleted");
                Section("close-flush", sw);
            }
            else
            {
                main.Close();
            }
            await Pump();
            Check(!host.Watcher.IsPreviewing, "a preview lease is still held after MainWindow closed");
            Section($"close({palette})", sw);

            // ---- other windows ----
            await CheckGuideAsync(app, host, palette, first, shots);
            Section($"guide({palette})", sw);
            if (first)
            {
                CheckTrayNotifier();
                Section("tray-notifier", sw);
            }

            var overlay = new OverlayWindow(host);
            overlay.Show();
            host.ShowDemoMessages();
            await Pump();
            if (shots is not null) Save(overlay, Path.Combine(shots, $"{palette}-overlay.png"), Backdrop());
            overlay.ToggleEditMode();
            await Pump();
            overlay.ToggleEditMode();
            overlay.Close();
            Section($"overlay({palette})", sw);

            var compose = new ComposeWindow(host);
            compose.Open(IntPtr.Zero);
            if (shots is not null)
            {
                compose.Input.Text = ComposeSample;
                // The provider was warmed up in setup, so this is usually instant; never wait more than 2.5 s.
                var wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 2500 && (compose.Preview.Text is "" or "…" || compose.BackTranslation.Text == ""))
                    await Task.Delay(50);
                if (compose.Preview.Text is "" or "…") Warn($"compose preview still empty after 2.5 s ({palette})");
            }
            await Pump();
            if (shots is not null) Save(compose, Path.Combine(shots, $"{palette}-reply.png"), Backdrop());
            compose.Close();
            Section($"compose({palette})", sw);

            var lookup = new LookupWindow(host);
            lookup.Show();
            lookup.ShowResult("[全部] 天下第一：这张地图的金矿在中间\n小心早期快攻", "[All] Number One Under Heaven: the gold on this map is in the middle\nWatch out for an early rush", "zh-CN");
            await Pump();
            if (shots is not null) Save(lookup, Path.Combine(shots, $"{palette}-scan.png"), Backdrop());
            lookup.Close();
            Section($"lookup({palette})", sw);

            var selector = new RegionSelectorWindow(new System.Drawing.Rectangle(0, 0, 800, 600), null);
            selector.Show();
            await Pump();
            selector.Close();
            await Pump();
            Section($"region-selector({palette})", sw);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }

        var errors = listener.Errors.Where(e => !BindingErrorAllowlist.Contains(e)).ToList();
        foreach (var error in errors) Log($"[smoke] binding error ({palette}): {error}");
        Check(errors.Count == 0, $"{errors.Count} binding error(s) under palette {palette}");
        Section($"binding-errors({palette})", sw);

        Log($"[smoke] palette {palette} ok elapsed_ms={paletteTime.ElapsedMilliseconds}");
        if (paletteTime.Elapsed > PaletteBudget)
            Warn($"palette {palette} took {paletteTime.Elapsed.TotalSeconds:0} s (budget {PaletteBudget.TotalSeconds:0} s)");
    }

    // ---------------- setup helpers ----------------

    /// <summary>One translation through the cache the reply box uses, so its preview doesn't wait on the network.</summary>
    private static async Task WarmUpProviderAsync(AppHost host)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var mine = host.Settings.MyLanguage;
            var target = OutgoingTranslator.ResolveTarget(host.Settings.Compose.DefaultTarget, host.Pipeline.SuggestedReplyLanguage, mine);
            var result = await host.Translator.TranslateAsync(new TranslationRequest(ComposeSample, target, mine), cts.Token);
            // The reply box also back-translates its preview.
            await host.Translator.TranslateAsync(new TranslationRequest(result.Text, mine, target), cts.Token);
        }
        catch (Exception ex)
        {
            Warn($"provider warm-up failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- 04 checks ----------------

    /// <summary>
    /// AC-CHAT-17, 25: the pipeline's analysis through AppHost's wiring — stitching on by default, notice patterns
    /// published by ApplySettings (slot 5) with their errors, hidden notices by default, the Describe formatter, and a user
    /// chat-formats.json mapped to the current game and picked up by ReloadChatFormats. Logs no chat text.
    /// </summary>
    private static void CheckChatUnderstanding(AppHost host)
    {
        var sample = OcrLine.FromTexts(["[All] Zhang Wei: this message is long enough that it", "wraps onto a second line", "Kim: ㅋㅋㅋ"], 18, 4);
        var entries = host.Pipeline.Analyze(sample);
        Check(entries.Count == 2 && entries[0].Text.EndsWith("second line", StringComparison.Ordinal) && entries[0].LineCount == 2,
            $"stitching: expected 2 entries with a joined first message, got {entries.Count}");
        Check(host.Pipeline.Options.HideSystemLines, "game notices are not hidden by default");

        host.Settings.Chat.SystemLinePatterns = ["*resigned*", "re:(["];
        host.ApplySettings();
        var notice = host.Pipeline.Analyze(OcrLine.FromTexts(["Bob resigned."], 18, 4));
        Check(notice.Count == 1 && notice[0].Kind == ChatLineKind.System, "a notice pattern set through ApplySettings did not apply");
        Check(host.Pipeline.SystemPatternErrors.Count == 1, $"expected 1 pattern error, got {host.Pipeline.SystemPatternErrors.Count}");
        Check(ChatUnderstandingText.Errors(host.Pipeline.SystemPatternErrors, host.ChatFormats.Errors).Length > 0, "the card's error text is empty");
        Check(host.Pipeline.Describe(sample).Contains("(2 lines)", StringComparison.Ordinal), "Describe does not show the joined line count");
        host.Settings.Chat.SystemLinePatterns = [];
        host.ApplySettings();
        Check(host.Pipeline.SystemPatternErrors.Count == 0, "pattern errors outlived the patterns");

        // REQ-CHAT-32/34: a user profile with separator '>' mapped to the current game.
        var game = host.CurrentGameId;
        File.WriteAllText(host.ChatFormatsPath,
            "{ \"profiles\": [ { \"id\": \"smoke\", \"name\": \"Smoke\", \"inherits\": \"generic\", \"separators\": [\">\"] } ], "
            + $"\"games\": {{ \"{game}\": \"smoke\" }} }}");
        host.ReloadChatFormats();
        var arrow = host.Pipeline.Analyze(OcrLine.FromTexts(["Bob> hi"], 18));
        Check(arrow.Count == 1 && arrow[0].Sender == "Bob", "chat-formats.json was not picked up for the current game");
        Check(host.ChatFormats.Errors.Count == 0, "the smoke chat-formats.json reported errors");
        File.Delete(host.ChatFormatsPath);
        host.ReloadChatFormats();
        Check(host.Pipeline.Analyze(OcrLine.FromTexts(["Bob> hi"], 18)).Single().Sender is null, "the removed chat-formats.json is still in use");
        Log($"chat understanding: game={game}, profiles={host.ChatFormats.Profiles.Count}");
    }

    /// <summary>The "Understanding chat" card shows pattern errors in Danger text and hides them once fixed (AC-CHAT-19).</summary>
    private static async Task CheckChatUnderstandingCardAsync(MainWindow main)
    {
        main.Navigate(PageIds.Capture);
        await Pump();
        var page = main.PageFor(PageIds.Capture);
        var box = page.FindName("SystemLinePatternsBox") as TextBox ?? throw new InvalidOperationException("no SystemLinePatternsBox on Reading Chat");
        var errors = page.FindName("ChatUnderstandingErrors") as TextBlock ?? throw new InvalidOperationException("no ChatUnderstandingErrors on Reading Chat");
        var vm = main.DataContext as MainViewModel ?? throw new InvalidOperationException("MainWindow has no MainViewModel");

        vm.SystemLinePatternsText = "*resigned*\nre:([";
        await Pump();
        Check(errors.Visibility == Visibility.Visible && errors.Text.Contains("Line 2", StringComparison.Ordinal),
            $"the card does not show the pattern error (visibility {errors.Visibility})");
        Check(box.Text.Contains("resigned", StringComparison.Ordinal), "the notice box does not show the saved patterns");

        vm.SystemLinePatternsText = "";
        await Pump();
        Check(errors.Visibility != Visibility.Visible, "the pattern error is still shown after the patterns were cleared");
    }

    // ---------------- 05 checks ----------------

    private static readonly TimeSpan CaptureEngineBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The capture engine's Win32 glue on a real captioned window (AC-CAP-2, 5, 12, 13, 18, 19): window probing,
    /// window-relative regions across a move, minimise and restore, DIP-to-physical conversion in the region selector,
    /// steady-state allocations, the backend fallback, and a dedicated watcher with a preview lease. Its pipeline has an
    /// offline translator, so nothing leaves the runner.
    /// </summary>
    private static async Task CaptureEngineSmokeAsync(AppHost host, LogHub log)
    {
        // 1. A captioned 640×360 "game" with chat-like text, on top so nothing covers it.
        var text = new TextBlock
        {
            Text = "[All] 小明: 你好，再来一局？\nKim: ㅈㅈ\nBob: nice wall bro\nСокол: хорошая стена",
            FontSize = 18,
            Margin = new Thickness(24),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var game = new Window
        {
            Title = "Envoy smoke game",
            Width = 640,
            Height = 360,
            Left = 80,
            Top = 80,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost = true,
            ShowActivated = false,
            Content = text,
        };
        game.SetResourceReference(Control.BackgroundProperty, "Bg.Panel");
        game.Show();
        await Pump();
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(game).Handle;
            Check(GameWindowTracker.IsThreadPerMonitorV2(), "the UI thread is not per-monitor DPI aware (v2)");

            // 2. Probing (AC-CAP-2).
            var fake = new RunningGame(GameCatalog.Find("aoe2de")!, Environment.ProcessId, hwnd);
            var info = GameWindowTracker.Observe(fake, 0);
            Check(info is not null, "Observe found no window for the smoke game");
            Check(info!.Handle == hwnd, "Observe returned another window");
            Check(!info.Minimized, "the smoke game reads as minimised");
            Check(info.HasCaption, "the captioned smoke game has no caption");
            Check(RegionMath.Contains(info.WindowRect, info.ClientRect), $"WindowRect {info.WindowRect} does not contain ClientRect {info.ClientRect}");
            Check(RegionMath.Contains(info.MonitorBounds, info.ClientRect), $"MonitorBounds {info.MonitorBounds} does not contain ClientRect {info.ClientRect}");
            Check(GameWindowTracker.IsOnAnyMonitor(info.ClientRect), "the smoke game's client area is on no monitor");
            Check(!GameWindowTracker.IsOnAnyMonitor(new ScreenRegion(-100000, -100000, 50, 50)), "a far off-screen area reads as on a monitor");

            // 3. Window-relative round trip across a move of (+50, +30) physical px (AC-CAP-2).
            var chosen = new ScreenRegion(info.ClientRect.X + 20, info.ClientRect.Y + 20, 300, 100);
            var relative = RegionMath.ToWindowRelative(chosen, info.ClientRect);
            Check(relative is not null, "a region inside the client area was not stored relative to it");
            Check(Native.SetWindowPos(hwnd, IntPtr.Zero, info.WindowRect.X + 50, info.WindowRect.Y + 30, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE), "SetWindowPos failed");
            await Pump();
            var moved = GameWindowTracker.Observe(fake, 0)!;
            var resolved = RegionMath.Resolve(relative!, moved.ClientRect);
            Check(resolved == chosen with { X = chosen.X + 50, Y = chosen.Y + 30 }, $"after a (+50, +30) move the region resolved to {resolved}, not {chosen} moved");
            Log($"capture-engine: client {info.ClientRect} → {moved.ClientRect}, region {chosen} → {resolved}");

            // 4. Minimise and restore (AC-CAP-5).
            CaptureObservation Observation(GameWindowInfo w) => new(true, GameWindowTracker.IsOnAnyMonitor(resolved), true, true,
                w.Minimized, w.GameForeground, w.EnvoyForeground, RegionSource.WindowRelative, null);
            var gate = new CaptureGateOptions(true, false, false, false);
            game.WindowState = WindowState.Minimized;
            await Pump();
            var minimised = GameWindowTracker.Observe(fake, 0)!;
            Check(minimised.Minimized, "the minimised smoke game does not read as minimised");
            Check(CaptureGate.Evaluate(Observation(minimised), gate) == PauseReason.GameMinimized, "the gate does not pause for a minimised game");
            game.WindowState = WindowState.Normal;
            await Pump();
            var restored = GameWindowTracker.Observe(fake, 0)!;
            Check(!restored.Minimized, "the restored smoke game still reads as minimised");
            Check(CaptureGate.Evaluate(Observation(restored), gate) == PauseReason.None, "the gate still pauses after restore");

            // 5. The region selector converts DIPs to physical pixels through PointToScreen (AC-CAP-13).
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var selector = new RegionSelectorWindow(screen, null);
            selector.Show();
            await Pump();
            try
            {
                var scale = VisualTreeHelper.GetDpi(selector);
                var physical = selector.ToScreen(new Rect(10, 20, 200, 100));
                var expected = new ScreenRegion(screen.X + (int)Math.Round(10 * scale.DpiScaleX), screen.Y + (int)Math.Round(20 * scale.DpiScaleY),
                    (int)Math.Round(200 * scale.DpiScaleX), (int)Math.Round(100 * scale.DpiScaleY));
                Check(Math.Abs(physical.X - expected.X) <= 1 && Math.Abs(physical.Y - expected.Y) <= 1
                    && Math.Abs(physical.Width - expected.Width) <= 1 && Math.Abs(physical.Height - expected.Height) <= 1,
                    $"the selector converted (10, 20, 200, 100) DIPs to {physical}, expected {expected} ±1");
            }
            finally
            {
                selector.Close();
            }

            // 6. Steady-state allocations (AC-CAP-19, warning only) and the backend fallback (AC-CAP-18).
            var pool = new FramePool();
            using (var backend = new GdiCaptureBackend())
            {
                var target = new CaptureTarget(new ScreenRegion(0, 0, 320, 120), IntPtr.Zero, 640, 240);
                var options = new PreprocessOptions(true, 1.3, false);
                void OneFrame()
                {
                    using var frame = backend.Capture(target, pool);
                    _ = FrameProcessing.Analyze(frame.Pixels);
                    FrameProcessing.Preprocess(frame.Pixels, options);
                }
                OneFrame();
                OneFrame();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 20; i++) OneFrame();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Log($"[smoke] capture-engine steady-state alloc {allocated} B for 20 captures of 320x120 at 2x");
                if (allocated >= 307_200) Warn($"capture steady-state allocated {allocated} B for 20 frames (expected < 307200)");
                Check(pool.Outstanding == 0, $"{pool.Outstanding} frame buffers were not returned");
            }
            using (var fallback = CaptureBackendFactory.Create("nonsense"))
                Check(fallback.Id == CaptureBackendIds.Gdi, $"an unknown backend id gave {fallback.Id}, not gdi");

            // 7. A dedicated watcher on the smoke game, with a preview lease (AC-CAP-12).
            var settings = new AppSettings();
            settings.Capture.OnlyWhileGameRuns = false;
            settings.Capture.WindowRegions["aoe2de"] = new WindowRelativeRegion(20, 20, 300, 100, restored.ClientRect.Width, restored.ClientRect.Height);
            var captureOptions = CaptureOptions.From(settings);
            var observed = new List<GameWindowInfo?>();
            var frames = new List<CaptureFrame>();
            using var pipeline = new ChatPipeline(new EchoTranslator(TimeSpan.Zero), new ChatPipelineOptions());
            var watcher = new ChatWatcher(host.Ocr, pipeline, () => captureOptions, () => fake, log.For("Capture"), TimeProvider.System, w =>
            {
                lock (observed) observed.Add(w);
            });
            watcher.FrameCaptured += f =>
            {
                lock (frames) frames.Add(f);
            };
            try
            {
                using (watcher.BeginPreview())
                {
                    watcher.Start();
                    var wait = Stopwatch.StartNew();
                    while (wait.Elapsed < TimeSpan.FromSeconds(10) && (watcher.Metrics.FramesCaptured < 3 || frames.Count == 0)) await Task.Delay(100);
                    var metrics = watcher.Metrics;
                    Log($"capture-engine watcher: {CaptureText.Stats(metrics)}; {CaptureText.Health(metrics)}; state {watcher.State}");
                    Check(metrics.FramesCaptured >= 3, $"the watcher captured {metrics.FramesCaptured} frames in 10 s");
                    Check(metrics.Health != CaptureHealth.Failing, $"the watcher is failing: {watcher.LastError}");
                    Check(metrics.Backend == CaptureBackendIds.Gdi, $"the watcher used backend {metrics.Backend}");
                    Check(watcher.CurrentRegionSource == RegionSource.WindowRelative, $"the watcher resolved a {watcher.CurrentRegionSource} region");
                    lock (observed) Check(observed.Any(o => o?.Handle == hwnd), "windowObserved never saw the smoke game's window");
                    CaptureFrame first;
                    lock (frames) first = frames[0];
                    Check(first.Image.Pixels.Length == first.Image.Width * first.Image.Height * 4,
                        $"a Live view frame has {first.Image.Pixels.Length} bytes for {first.Image.Width}×{first.Image.Height}");
                    watcher.Stop();
                    Check(!watcher.IsRunning && watcher.State == WatcherState.Stopped, "the watcher did not stop");
                }
                Check(!watcher.IsPreviewing, "the smoke watcher's preview lease is still held");
            }
            finally
            {
                watcher.Dispose();
            }
        }
        finally
        {
            game.Close();
            await Pump();
        }
    }

    // ---------------- 03 checks ----------------

    /// <summary>
    /// Seeds display-only health for the main host's chain (google-free Healthy at 340 ms, edge-free cooling down for
    /// 60 s). Seed never touches breakers or buckets, so the compose window's real calls are unaffected.
    /// </summary>
    private static void SeedTranslationHealth(AppHost host)
    {
        var ids = host.ActiveProviderIds;
        Check(ids.Count > 0, "the host has no active translation providers");
        Check(ids.Contains(ProviderCatalog.GoogleFree), "Google (free) is not in the active chain");
        var now = host.Time.GetUtcNow();
        foreach (var id in ids)
        {
            var empty = ProviderHealthSnapshot.Empty(id);
            host.TranslationHealth.Seed(id switch
            {
                ProviderCatalog.GoogleFree => empty with
                {
                    State = ProviderState.Healthy, MedianLatency = TimeSpan.FromMilliseconds(340), LastSuccess = now.AddSeconds(-5), Successes = 12,
                },
                ProviderCatalog.EdgeFree => empty with
                {
                    State = ProviderState.CoolingDown, RetryAt = now.AddSeconds(60), LastFailure = now, Failures = 3, ConsecutiveFailures = 3,
                    LastErrorKind = TranslationErrorKind.Timeout,
                },
                _ => empty,
            });
        }
        Log($"translation health seeded: {string.Join(", ", ids)}");
    }

    /// <summary>The Translation page shows one generated health row per active provider (REQ-TRR-18, AC-TRR-14).</summary>
    private static async Task<int> CheckHealthRowsAsync(AppHost host, MainWindow main)
    {
        main.Navigate(PageIds.Translation);
        await Pump();
        var page = (TranslationPage)main.PageFor(PageIds.Translation);
        var vm = (MainViewModel)main.DataContext;
        var expected = host.ActiveProviderIds.Count;
        Check(vm.IsTranslationPageActive, "the Translation page is visible but its health refresh is off");
        Check(vm.ProviderHealthRows.Count == expected, $"{vm.ProviderHealthRows.Count} health rows in the view model, expected {expected}");
        Check(vm.ProviderHealthRows.Select(r => r.ProviderId).SequenceEqual(host.ActiveProviderIds), "the health rows are not in chain order");
        Check(page.HealthList.Items.Count == expected, $"the health list has {page.HealthList.Items.Count} items, expected {expected}");
        for (var i = 0; i < expected; i++)
            Check(page.HealthList.ItemContainerGenerator.ContainerFromIndex(i) is not null, $"health row {i} was not generated");
        Check(vm.ProviderHealthRows.All(r => r.StateText.Length > 0), "a health row has no state text");
        return expected;
    }

    // ---------------- 07a checks ----------------

    /// <summary>
    /// AC-OUXA-11: on the fresh main host translation is off until "What leaves your PC" is confirmed. Both stacks fail
    /// with Configuration before any provider runs; then the acknowledgement opens the gate.
    /// </summary>
    private static async Task CheckPrivacyGateAsync(AppHost host)
    {
        Check(!host.PrivacyAcknowledged, "a fresh host starts with the privacy acknowledgement set");
        foreach (var (name, translator) in new[] { ("chat", host.Translator), ("compose", host.ComposeTranslator) })
        {
            try
            {
                await translator.TranslateAsync(new TranslationRequest("smoke", "en"));
                Check(false, $"the closed privacy gate let a {name} translation through");
            }
            catch (TranslationException ex)
            {
                Check(ex.Kind == TranslationErrorKind.Configuration, $"the closed gate failed the {name} call with {ex.Kind}, not Configuration");
            }
        }
        host.SetPrivacyAcknowledged(true);
        Check(host.PrivacyAcknowledged && host.Settings.Onboarding.PrivacyAcknowledgedVersion == Core.Privacy.PrivacyText.Version,
            "the acknowledgement was not recorded");
    }

    /// <summary>
    /// AC-OUXA-3: the tray menu has the guide items; OpenSetupWizard is single-instance and moves an open guide; the guide
    /// holds notices while open; closing it records Skip and its step, Finish records Completed and opens the War Room.
    /// </summary>
    private static async Task CheckGuideEntryAsync(App app, AppHost host)
    {
        using (var menu = new Forms.ContextMenuStrip())
        {
            app.AddGuideItems(menu);
            var items = menu.Items.Cast<Forms.ToolStripItem>().Select(i => i.Text).ToList();
            Check(items.Contains(App.TrayGuideItem) && items.Contains(App.TrayCheatSheetItem), $"the tray menu has [{string.Join(", ", items)}]");
        }

        IShell shell = app;
        shell.OpenSetupWizard(nameof(OnboardingStep.Hotkeys));
        await Pump();
        var wizard = app.Wizard;
        Check(wizard is { IsVisible: true }, "OpenSetupWizard did not show the guide");
        Check(wizard!.ViewModel.Current == OnboardingStep.Hotkeys, $"the guide opened at {wizard.ViewModel.Current}, not Hotkeys");
        Check(host.Notices?.IsHeld == true, "notices are not held while the guide is open");
        shell.OpenSetupWizard(nameof(OnboardingStep.OcrLanguages));
        await Pump();
        Check(ReferenceEquals(app.Wizard, wizard), "a second OpenSetupWizard opened another guide");
        Check(wizard.ViewModel.Current == OnboardingStep.OcrLanguages, $"the open guide is at {wizard.ViewModel.Current}, not OcrLanguages");
        wizard.Close();
        await Pump();
        Check(app.Wizard is null, "the closed guide is still App's guide");
        Check(host.Notices?.IsHeld == false, "notices are still held after the guide closed");
        Check(host.Settings.Onboarding.Skipped && host.Settings.Onboarding.LastStep == nameof(OnboardingStep.OcrLanguages),
            "closing the guide did not record Skip and its step");
        Check(!host.Watcher.IsPreviewing, "the closed guide still holds a preview lease");

        // Finish on the last step: Completed and FirstRunCompleted, then MainWindow on the War Room (REQ-OUXA-10).
        host.Settings.Onboarding.Skipped = false;
        shell.OpenSetupWizard(nameof(OnboardingStep.Hotkeys));
        await Pump();
        Check(app.Wizard!.ViewModel.IsLast, "Hotkeys is not the guide's last step");
        app.Wizard.ViewModel.NextCommand.Execute(null);
        await Pump();
        Check(app.Wizard is null, "Finish did not close the guide");
        Check(host.Settings.Onboarding.Completed && host.Settings.FirstRunCompleted, "Finish did not record Completed and FirstRunCompleted");
        Check(app.OpenMainWindow is { } shown && shown.PageFor(PageIds.Dashboard).IsVisible, "Finish did not open MainWindow on the War Room");
        app.OpenMainWindow!.Close();
        await Pump();

        // Start the per-palette checks as a user who has not finished the guide (the Finish-setup callout shows).
        host.Settings.Onboarding.Completed = false;
        host.Settings.Onboarding.LastStep = "";
    }

    /// <summary>AC-OUXA-7: a seeded OCR notice is in the War Room list.</summary>
    private static async Task CheckNoticesAsync(AppHost host, MainWindow main)
    {
        main.Navigate(PageIds.Dashboard);
        var notice = NoticeFactory.OcrMissing(["ja"])!;
        host.Notices!.Submit(notice, gameForeground: false); // ApplySettings re-checks OCR and may have withdrawn the setup seed
        await Pump();
        var page = (WarRoomPage)main.PageFor(PageIds.Dashboard);
        var vm = (MainViewModel)main.DataContext;
        Check(vm.Notices.Any(n => n.Key == notice.Key), "the seeded notice is not in the view model's list");
        Check(page.NoticeList.IsVisible, "the War Room notice list is not visible");
        Check(page.NoticeRows.Items.Cast<Notice>().Any(n => n.Key == notice.Key) || vm.Notices.Count > MainViewModel.VisibleNoticeLimit,
            "the seeded notice has no row in the War Room list");
    }

    /// <summary>
    /// AC-OUXA-12: banner, notice list and hint slot top to bottom; the slot shows Finish-setup (and Privacy without the
    /// acknowledgement); four notices show three rows and "Show all (4)"; "Don't remind me" removes one for good.
    /// </summary>
    private static async Task CheckWarRoomLayoutAsync(AppHost host, MainWindow main, bool first)
    {
        main.Navigate(PageIds.Dashboard);
        var page = (WarRoomPage)main.PageFor(PageIds.Dashboard);
        var vm = (MainViewModel)main.DataContext;
        Check(!host.Settings.Onboarding.Completed, "the layout check expects an unfinished guide");
        vm.RefreshStatus();
        await Pump();
        Check(page.NoticeList.IsVisible && page.HintSlot.IsVisible, "the notice list or the hint slot is not visible");
        Check(page.FinishSetupCallout.IsVisible && !page.PrivacyCallout.IsVisible, "the hint slot does not show the Finish-setup callout");
        var slots = new FrameworkElement[] { main.SafeModeBanner, page.UpdateBanner, page.NoticeList, page.HintSlot }.Where(e => e.IsVisible).ToList();
        var ys = slots.Select(e => e.TranslatePoint(new Point(0, 0), main).Y).ToList();
        for (var i = 1; i < ys.Count; i++)
            Check(ys[i] > ys[i - 1], $"War Room slot {slots[i].Name} (y={ys[i]:0}) is not below {slots[i - 1].Name} (y={ys[i - 1]:0})");
        if (!first) return;

        host.SetPrivacyAcknowledged(false);
        vm.RefreshStatus();
        await Pump();
        Check(page.PrivacyCallout.IsVisible && !page.FinishSetupCallout.IsVisible, "without the acknowledgement the hint slot does not show Privacy");
        host.SetPrivacyAcknowledged(true);

        var center = host.Notices!;
        center.ResolveWhere(_ => true);
        for (var i = 1; i <= 4; i++)
            center.Submit(NoticeFactory.ProviderFailing($"smoke-{i}", $"Smoke {i}", ProviderState.AuthFailed)!, gameForeground: false);
        await Pump();
        Check(page.NoticeRows.Items.Count == MainViewModel.VisibleNoticeLimit, $"{page.NoticeRows.Items.Count} notice rows, expected {MainViewModel.VisibleNoticeLimit}");
        Check(page.ShowAllNoticesToggle.IsVisible && page.ShowAllNoticesText.Text == "Show all (4)", $"the toggle reads '{page.ShowAllNoticesText.Text}'");
        vm.ShowAllNotices = true;
        await Pump();
        Check(page.NoticeRows.Items.Count == 4, "Show all did not list every notice");

        var dismissed = NoticeFactory.ProviderKey("smoke-1");
        vm.DismissNoticeCommand.Execute(dismissed);
        await Pump();
        Check(vm.Notices.All(n => n.Key != dismissed) && host.Settings.Notifications.Dismissed.Contains(dismissed), "Don't remind me did not dismiss the notice");
        Check(center.Submit(NoticeFactory.ProviderFailing("smoke-1", "Smoke 1", ProviderState.AuthFailed)!, false) == NoticeDecision.Suppress
            && vm.Notices.All(n => n.Key != dismissed), "a dismissed notice came back");
        host.Settings.Notifications.Dismissed.Remove(dismissed);
        vm.ShowAllNotices = false;
        center.ResolveWhere(k => k.StartsWith(NoticeFactory.ProviderKeyPrefix + "smoke-", StringComparison.Ordinal));
        await Pump();
    }

    /// <summary>AC-OUXA-9: a balloon's action opens App's MainWindow on Translation or Reading Chat.</summary>
    private static async Task CheckNoticeActionsAsync(App app)
    {
        app.RunNoticeAction(NoticeActions.OpenTranslation);
        await Pump();
        Check(app.OpenMainWindow is { } main && main.PageFor(PageIds.Translation).IsVisible, "open-translation did not show the Translation page");
        app.RunNoticeAction(NoticeActions.OpenCapture);
        await Pump();
        Check(app.OpenMainWindow!.PageFor(PageIds.Capture).IsVisible, "open-capture did not show the Reading Chat page");
        app.OpenMainWindow.Close();
        await Pump();
    }

    /// <summary>AC-OUXA-12: Appearance and About sections in the contract §8.6 order.</summary>
    private static async Task CheckPageOrderAsync(MainWindow main)
    {
        main.Navigate(PageIds.Appearance);
        await Pump();
        var appearance = (AppearancePage)main.PageFor(PageIds.Appearance);
        CheckOrder(appearance, "Appearance", appearance.ThemesList, appearance.NotificationsFrame, appearance.StartupFrame);
        main.Navigate(PageIds.About);
        await Pump();
        var about = (AboutPage)main.PageFor(PageIds.About);
        CheckOrder(about, "About", about.VersionFrame, about.SetupGuideFrame, about.TroubleshootingFrame, about.PrivacyFrame, about.LicencesText);
        main.Navigate(PageIds.Dashboard);
        await Pump();
    }

    private static void CheckOrder(FrameworkElement page, string name, params FrameworkElement[] elements)
    {
        var ys = elements.Select(e => e.TranslatePoint(new Point(0, 0), page).Y).ToList();
        for (var i = 1; i < ys.Count; i++)
            Check(ys[i] > ys[i - 1], $"{name}: {elements[i].Name} (y={ys[i]:0}) is not below {elements[i - 1].Name} (y={ys[i - 1]:0})");
    }

    /// <summary>
    /// AC-OUXA-1, 4: every built step shows exactly one panel and "Step n of N"; the live view is active only on Chat area
    /// and its lease is gone after Close. The first palette also runs the test translation (REQ-OUXA-8).
    /// </summary>
    private static async Task CheckGuideAsync(App app, AppHost host, string palette, bool first, string? shots)
    {
        var wizard = new SetupWizardWindow(host, app, OnboardingStep.Language);
        wizard.Show();
        await Pump();
        var vm = wizard.ViewModel;
        Check(vm.StepCount == OnboardingFlow.AllSteps.Count, $"the guide has {vm.StepCount} steps");
        Check(vm.CanGoNext, "Next is disabled on Language although translation was acknowledged");
        for (var i = 0; i < vm.Steps.Count; i++)
        {
            var step = vm.Steps[i].Step;
            vm.GoTo(step);
            await Pump();
            var visible = vm.Steps.Where(s => wizard.PanelFor(s.Step).IsVisible).Select(s => s.Step).ToList();
            Check(visible.Count == 1 && visible[0] == step, $"step {step}: visible panels [{string.Join(", ", visible)}]");
            Check(wizard.StepCounter.Text == $"Step {i + 1} of {vm.StepCount}", $"step {step}: the counter reads '{wizard.StepCounter.Text}'");
            var chat = step == OnboardingStep.ChatArea;
            Check(vm.CapturePreview.IsActive == chat && host.Watcher.IsPreviewing == chat, $"step {step}: live view active={vm.CapturePreview.IsActive}, lease={host.Watcher.IsPreviewing}");
            if (shots is not null && palette == "imperial") Save(wizard, Path.Combine(shots, $"{palette}-setup-{step.ToString().ToLowerInvariant()}.png"));
        }
        Check(vm.Steps.All(s => s.Status != StepStatus.NotVisited), "a visited step still reads Not visited");

        if (first)
        {
            vm.GoTo(OnboardingStep.TestTranslation);
            var run = vm.TestTranslateCommand.ExecuteAsync();
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
            await Pump();
            Check(vm.TestOutput.Length > 0, "the test translation shows nothing");
            if (!run.IsCompleted || vm.TestOutput == TranslationTestText.Pending) Warn("the guide's test translation did not finish within 5 s");
        }

        wizard.Close();
        await Pump();
        Check(!host.Watcher.IsPreviewing, "the guide's live view still holds a preview lease after Close");
    }

    /// <summary>The tray notifier holds a notice until it is attached, then shows it without throwing (spec 07a smoke plan).</summary>
    private static void CheckTrayNotifier()
    {
        var notice = NoticeFactory.OcrMissing(["ja"])!;
        using var notifier = new TrayNotifier(_ => { }, _smokeLog);
        notifier.Show(notice);
        Check(ReferenceEquals(notifier.Held, notice), "a notice shown before Attach was not held");
        using var icon = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Visible = true, Text = "Envoy smoke" };
        notifier.Attach(icon);
        Check(notifier.Held is null, "Attach did not show the held notice");
        Check(notifier.PendingAction == notice.ActionId, "the held notice's balloon failed (see the log)");
        icon.Visible = false;
    }

    // ---------------- 06 checks ----------------

    /// <summary>
    /// One Portable host on its own data folder (kept until teardown): a manual check offers Download (release page) and
    /// Get the installer, never an in-place update (REQ-DIST-15, AC-DIST-9).
    /// </summary>
    private static async Task<AppHost> CheckPortableUpdatesAsync(App app, string root, LogHub log, MemoryLogSink recent)
    {
        var data = Path.Combine(root, "portable-data");
        var portable = new AppHost(Path.Combine(data, "settings.json"), app.Dispatcher,
            new AppHostOptions { DataFolder = data, Log = log, RecentLog = recent, UpdateBackend = new SmokeUpdateBackend(InstallKind.Portable) });
        portable.SetPrivacyAcknowledged(true); // every smoke host but the main one confirms right after construction (AC-OUXA-11)
        Check(portable.DataFolder.StartsWith(root, StringComparison.OrdinalIgnoreCase), "the portable host's data folder is outside the smoke root");
        await portable.Updates.CheckAsync(manual: true);
        var view = UpdateViewBuilder.Build(portable.Updates.Snapshot);
        Check(portable.Updates.Snapshot.State == UpdateState.Available, $"the portable check ended in {portable.Updates.Snapshot.State}");
        Check(view.ShowBanner && view.Primary == UpdateAction.OpenReleasePage && view.Secondary == UpdateAction.GetInstaller,
            $"the portable banner offers {view.Primary} / {view.Secondary}, expected OpenReleasePage / GetInstaller");
        await portable.Updates.DownloadAsync();
        Check(portable.Updates.Snapshot.State == UpdateState.Available, "a portable copy started a download");
        Check(!portable.Updates.CanRestartToUpdate, "a portable copy offers Restart to update");
        return portable;
    }

    /// <summary>
    /// The War Room update banner in Failed, Available, Downloading and ReadyToRestart (REQ-DIST-27, AC-DIST-9), inside
    /// the palette's binding-error window. Each palette offers a newer version than the last one made ready.
    /// </summary>
    private static async Task CheckUpdateBannerAsync(AppHost host, SmokeUpdateBackend updates, MainWindow main, string palette, string? shots)
    {
        var index = ThemeManager.PaletteIds.ToList().IndexOf(palette);
        updates.NextVersion = new ReleaseVersion(9, 9, Math.Max(0, index));
        updates.ResetDownloadGate();
        main.Navigate(PageIds.Dashboard);
        var page = (WarRoomPage)main.PageFor(PageIds.Dashboard);

        // 0. A manual check offers this palette's newer version, replacing the last palette's ready update, so the failed
        // check below starts from Available (a failed check while an update is ready keeps ReadyToRestart, see 5.).
        await host.Updates.CheckAsync(manual: true);
        await Pump();
        Check(host.Updates.Snapshot is { State: UpdateState.Available } first && first.Available?.Version == updates.NextVersion,
            $"the first manual check ended in {host.Updates.Snapshot.State} offering {host.Updates.Snapshot.Available?.Version}");

        // 1. A failed manual check: Retry.
        updates.FailNextCheck = true;
        await host.Updates.CheckAsync(manual: true);
        await Pump();
        Check(host.Updates.Snapshot.State == UpdateState.Failed, $"a failed manual check ended in {host.Updates.Snapshot.State}");
        Check(page.UpdateBanner.IsVisible, "the update banner is not visible after a failed manual check");
        Check(host.UpdateView.Primary == UpdateAction.Retry, $"the failed banner offers {host.UpdateView.Primary}, not Retry");

        // 2. Available (installed, background download off): Update now.
        await host.Updates.CheckAsync(manual: true);
        await Pump();
        Check(host.UpdateView.Primary == UpdateAction.UpdateNow, $"the available banner offers {host.UpdateView.Primary}, not Update now");
        Check(page.UpdateHeadline.Text == host.UpdateView.Headline, $"the banner headline reads '{page.UpdateHeadline.Text}'");

        // 3. Downloading at 50 %, then ready: Restart to update.
        var download = host.Updates.DownloadAsync();
        var wait = Stopwatch.StartNew();
        while (host.Updates.Snapshot.Percent < 50 && wait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        await Pump();
        Check(host.Updates.Snapshot.State == UpdateState.Downloading, $"the download is in {host.Updates.Snapshot.State}, not Downloading");
        Check(page.UpdateHeadline.Text.Contains("50%", StringComparison.Ordinal), $"the downloading headline reads '{page.UpdateHeadline.Text}'");
        updates.OpenDownloadGate();
        await download;
        await Pump();
        Check(host.Updates.Snapshot.State == UpdateState.ReadyToRestart, $"the download ended in {host.Updates.Snapshot.State}");
        Check(host.UpdateView.Primary == UpdateAction.RestartToUpdate, $"the ready banner offers {host.UpdateView.Primary}, not Restart to update");
        Check(page.UpdateBanner.IsVisible && page.UpdateHeadline.Text == host.UpdateView.Headline, "the ready banner is not shown");

        // 5. A failed manual check keeps Restart to update (REQ-DIST-16).
        updates.FailNextCheck = true;
        await host.Updates.CheckAsync(manual: true);
        await Pump();
        Check(host.Updates.Snapshot.State == UpdateState.ReadyToRestart, $"a failed check after the download ended in {host.Updates.Snapshot.State}");
        Check(host.UpdateView.Primary == UpdateAction.RestartToUpdate, $"after a failed check the ready banner offers {host.UpdateView.Primary}");

        // 6. The War Room with the ready banner.
        if (shots is not null) Save(main, Path.Combine(shots, $"{palette}-update.png"));
    }

    /// <summary>
    /// A scripted <see cref="IUpdateBackend"/> for the smoke test: no network, never restarts. The download reports 0 and
    /// 50 %, waits for <see cref="OpenDownloadGate"/>, then reports 100 %.
    /// </summary>
    private sealed class SmokeUpdateBackend(InstallKind kind) : IUpdateBackend
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InstallKind Kind { get; } = kind;
        public ReleaseVersion CurrentVersion { get; } = AppHost.AppVersion;
        public ReleaseVersion NextVersion { get; set; } = new(9, 9, 0);
        public bool FailNextCheck { get; set; }
        public ReleaseVersion? PendingRestart => null;

        public void ResetDownloadGate() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OpenDownloadGate() => _gate.TrySetResult();

        public Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct)
        {
            if (FailNextCheck)
            {
                FailNextCheck = false;
                return Task.FromException<AvailableUpdate?>(new UpdateCheckException(UpdateErrorKind.Offline));
            }
            var v = NextVersion;
            var download = $"{UpdateEndpoints.ReleasesUrl}/download/v{v}/";
            return Task.FromResult<AvailableUpdate?>(new AvailableUpdate(v, "## Added\n- **Smoke test** release notes with a [link](docs/installing.md).",
                UpdateEndpoints.ReleasePage(v), download + "Envoy.exe", download + "Envoy-win-Setup.exe"));
        }

        public async Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken ct)
        {
            progress.Report(0);
            progress.Report(50);
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            progress.Report(100);
        }

        public void LaunchUpdaterAfterExit(IReadOnlyList<string> restartArgs) =>
            throw new InvalidOperationException("The smoke test must never restart to update.");
    }

    // ---------------- 02 checks ----------------

    /// <summary>A second host in safe mode on its own data folder: Start() must not start capture (REQ-REL-40).</summary>
    private static AppHost CheckSafeHost(App app, string root, LogHub log, MemoryLogSink recent)
    {
        var data = Path.Combine(root, "safe-data");
        var safeHost = new AppHost(Path.Combine(data, "settings.json"), app.Dispatcher,
            new AppHostOptions { DataFolder = data, Log = log, RecentLog = recent, SafeMode = SafeModeReason.CommandLine });
        safeHost.SetPrivacyAcknowledged(true);
        Check(safeHost.DataFolder.StartsWith(root, StringComparison.OrdinalIgnoreCase), "the safe host's data folder is outside the smoke root");
        Check(safeHost.Notices is null, "safe mode created a NoticeCenter (contract §2.2)");
        Check(safeHost.Settings.StartCapturingAutomatically, "the safe host's defaults should ask for automatic capture");
        safeHost.Start();
        Check(safeHost.IsSafeMode, "the safe host is not in safe mode");
        Check(!safeHost.Watcher.IsRunning, "safe mode started capture");
        return safeHost;
    }

    /// <summary>Copy diagnostics builds from the smoke host, and Export writes a zip with report.txt (REQ-REL-23, 24).</summary>
    private static void CheckDiagnostics(AppHost host, string root)
    {
        var text = host.BuildDiagnosticsText(DiagnosticsCollector.Collect(host));
        Check(text.Contains(DiagnosticsReport.Header, StringComparison.Ordinal), "the diagnostics report has no header");
        Check(text.Length <= DiagnosticsReport.MaxClipboardChars, $"the diagnostics report has {text.Length} characters");
        foreach (var section in DiagnosticsReport.Sections)
            Check(text.Contains("### " + section, StringComparison.Ordinal), $"the diagnostics report has no {section} section");
        Check(text.Contains("Updates: Installed, channel Stable", StringComparison.Ordinal), "the diagnostics report has no Updates line (REQ-DIST-31)");

        var zip = Path.Combine(root, "diag.zip");
        DiagnosticsCollector.ExportBundle(host, zip);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Check(names.Contains(DiagnosticsBundle.ReportEntry), $"the diagnostics zip has no report.txt ({string.Join(", ", names)})");
        Check(names.Contains(DiagnosticsBundle.SettingsEntry), "the diagnostics zip has no settings.redacted.json");
        Check(names.All(n => n is DiagnosticsBundle.ReportEntry or DiagnosticsBundle.SettingsEntry or DiagnosticsBundle.CrashEntry || n.StartsWith("logs/envoy", StringComparison.Ordinal)),
            $"unexpected entries in the diagnostics zip: {string.Join(", ", names)}");
        Log($"diagnostics: {text.Length} chars, zip [{string.Join(", ", names)}]");
    }

    /// <summary>All four global handlers are wired (REQ-REL-11/12/13). Wiring only; their bodies are Core-tested.</summary>
    private static void CheckHandlers(App app)
    {
        var installed = app.Handlers?.InstalledSources ?? [];
        foreach (var source in Enum.GetValues<UnhandledSource>())
            Check(installed.Contains(source), $"the {source} exception handler is not installed");
    }

    /// <summary>A MainWindow on the safe host shows the banner (REQ-REL-40).</summary>
    private static async Task CheckSafeBannerAsync(AppHost safeHost, string palette, string? shots)
    {
        var safeMain = new MainWindow(safeHost);
        safeMain.Show();
        safeMain.Navigate(PageIds.Hotkeys);
        await Pump();
        Check(safeMain.SafeModeBanner.IsVisible && safeMain.SafeModeBanner.ActualHeight > 0, "the safe-mode banner is not visible");
        if (shots is not null) Save(safeMain, Path.Combine(shots, $"{palette}-safe-mode.png"));
        safeMain.Close();
        await Pump();
    }

    /// <summary>
    /// A problem toast never takes focus and is themed; Details opens About › Troubleshooting; a fourth toast closes the
    /// oldest (REQ-REL-18, 27).
    /// </summary>
    private static async Task CheckToastAsync(MainWindow main, string palette, string? shots)
    {
        main.Navigate(PageIds.Dashboard);
        main.Activate();
        await Pump();
        var before = Native.GetForegroundWindow();
        var mainHandle = new System.Windows.Interop.WindowInteropHelper(main).Handle;
        if (before != mainHandle) Warn($"MainWindow is not the foreground window before the toast ({palette}); checking that the toast leaves it unchanged");

        var toast = new ToastWindow(new ProblemReport("smoke", "Smoke test", "Sample problem for the screenshot."));
        toast.Show();
        await Pump();
        var style = Native.GetWindowLongPtr(toast.Handle, Native.GWL_EXSTYLE).ToInt64();
        Check((style & Native.WS_EX_NOACTIVATE) != 0, "the toast lacks WS_EX_NOACTIVATE");
        Check((style & Native.WS_EX_TOOLWINDOW) != 0, "the toast lacks WS_EX_TOOLWINDOW");
        Check(!toast.IsActive, "the toast was activated");
        var after = Native.GetForegroundWindow();
        Check(after == before && after != toast.Handle, "showing a toast changed the foreground window");
        if (shots is not null) Save(toast, Path.Combine(shots, $"{palette}-toast.png"), Backdrop());
        toast.Close();

        void OpenTroubleshooting()
        {
            main.Navigate(PageIds.About);
            ((AboutPage)main.PageFor(PageIds.About)).TroubleshootingFrame.BringIntoView();
        }
        var toasts = new ToastService(main.Dispatcher, new NotificationThrottle(), () => true, () => IntPtr.Zero, OpenTroubleshooting, _smokeLog);
        for (var i = 1; i <= 4; i++)
            toasts.Show(new ProblemReport($"smoke-{i}", "Smoke test", "Sample problem " + i + ".", i % 2 == 0 ? LogLevel.Warning : LogLevel.Error));
        await Pump();
        Check(toasts.Open.Count == NotificationThrottle.MaxVisibleToasts, $"{toasts.Open.Count} toasts open after four, expected {NotificationThrottle.MaxVisibleToasts}");
        Check(toasts.Open[0].Report.Key == "smoke-2", "the oldest toast was not the one closed");

        toasts.Open[0].RequestDetails();
        await Pump();
        var about = (AboutPage)main.PageFor(PageIds.About);
        Check(about.IsVisible, "Details did not open the About page");
        Check(about.TroubleshootingFrame.IsVisible, "the Troubleshooting section is not visible");
        Check(toasts.Open.Count == NotificationThrottle.MaxVisibleToasts - 1, "Details did not close its toast");
        toasts.CloseAll();
        await Pump();
        Check(toasts.Open.Count == 0, "CloseAll left toasts open");
    }

    /// <summary>Prints the end of envoy.log; its first line is the session header without the user or machine name.</summary>
    private static void CheckLogTail(string[] lines)
    {
        Log("--- envoy.log tail ---");
        foreach (var line in lines.TakeLast(50)) Log(line);
        Log("--- end of envoy.log ---");
        Check(lines.Length > 0, "envoy.log is empty");
        Check(lines[0].Contains("[App] " + LogBootstrap.HeaderMessage, StringComparison.Ordinal), $"envoy.log line 1 is not the session header: {lines[0]}");
        foreach (var name in new[] { Environment.UserName, Environment.MachineName }.Where(n => n.Length >= 3))
            Check(!lines[0].Contains(name, StringComparison.OrdinalIgnoreCase), "the session header contains the user or machine name");
    }

    // ---------------- 01 checks ----------------

    private static void AssertOnlyPageVisible(MainWindow main, string page)
    {
        var shown = main.PageFor(page);
        Check(shown.GetType() == PageTypes[page], $"PageFor({page}) is {shown.GetType().Name}, expected {PageTypes[page].Name}");
        Check(shown.IsLoaded && shown.IsVisible && shown.ActualHeight > 0,
            $"page {page} is not shown (loaded={shown.IsLoaded}, visible={shown.IsVisible}, height={shown.ActualHeight})");
        foreach (var other in PageIds.All.Where(p => p != page))
            Check(main.PageFor(other).Visibility == Visibility.Collapsed, $"page {other} is not collapsed while {page} is shown");
    }

    /// <summary>Every page Button and Hyperlink runs through a command (REQ-UIF-7).</summary>
    private static async Task CheckCommandsAsync(MainWindow main)
    {
        var buttons = 0;
        var links = 0;
        foreach (var page in PageIds.All)
        {
            main.Navigate(page); // realises ItemsControl templates (e.g. the OCR "Install" buttons)
            await Pump();
            var root = main.PageFor(page);
            foreach (var button in VisualDescendants(root).OfType<Button>())
            {
                // Parts of a control's own template (there are none today) are not page buttons.
                if (button.TemplatedParent is Control) continue;
                buttons++;
                Check(button.Command is not null, $"{page}: button \"{button.Content}\" has no Command");
            }
            foreach (var link in LogicalDescendants(root).OfType<Hyperlink>())
            {
                links++;
                Check(link.Command is not null, $"{page}: hyperlink to {link.NavigateUri} has no Command");
            }
        }
        Check(buttons > 0 && links > 0, $"found {buttons} buttons and {links} hyperlinks; the tree walk is broken");
        Log($"commands: {buttons} buttons, {links} hyperlinks");
    }

    /// <summary>The live view holds a preview lease only while visible, and a second view model never interferes (REQ-UIF-21).</summary>
    private static async Task CheckPreviewLeaseAsync(AppHost host, MainWindow main)
    {
        main.Navigate(PageIds.Capture);
        await Pump();
        Check(host.Watcher.IsPreviewing, "Reading Chat is visible but no preview lease is held");

        using (var second = new CapturePreviewViewModel(host, main.Dispatcher))
        {
            second.IsActive = true;
            second.IsActive = false;
            Check(host.Watcher.IsPreviewing, "a second live view switched off the Reading Chat preview");

            main.Navigate(PageIds.Dashboard);
            await Pump();
            Check(!host.Watcher.IsPreviewing, "leaving Reading Chat did not release the preview lease");

            second.IsActive = true;
            Check(host.Watcher.IsPreviewing, "an active second live view holds no lease");
        }
        Check(!host.Watcher.IsPreviewing, "disposing the second live view did not release its lease");
    }

    /// <summary>One debounced apply per settings edit; none for a control marked AutoApply.Ignore (REQ-UIF-4).</summary>
    private static async Task CheckAutoApplyAsync(AppHost host, MainWindow main)
    {
        var applied = 0;
        void OnApplied() => applied++;
        host.SettingsApplied += OnApplied;
        try
        {
            main.Navigate(PageIds.Overlay);
            await Task.Delay(600); // let anything scheduled before this check settle
            await Pump();
            var showProvider = FindShowProviderCheckBox(main);
            var before = applied;
            showProvider.IsChecked = !(showProvider.IsChecked ?? false);
            await Task.Delay(600);
            await Pump();
            Check(applied == before + 1, $"a settings edit applied {applied - before} times, expected once");

            main.Navigate(PageIds.Translation);
            await Pump();
            var ignored = VisualDescendants(main.PageFor(PageIds.Translation)).OfType<TextBox>().FirstOrDefault(AutoApply.GetIgnore);
            Check(ignored is not null, "no AutoApply.Ignore text box on the Translation page");
            before = applied;
            ignored!.Text = "smoke test";
            await Task.Delay(600);
            await Pump();
            Check(applied == before, "editing a non-setting text box applied settings");

            showProvider.IsChecked = !(showProvider.IsChecked ?? false); // back to where it was
        }
        finally
        {
            host.SettingsApplied -= OnApplied;
        }
    }

    private static CheckBox FindShowProviderCheckBox(MainWindow main) =>
        VisualDescendants(main.PageFor(PageIds.Overlay)).OfType<CheckBox>().FirstOrDefault(cb =>
            BindingOperations.GetBinding(cb, ToggleButton.IsCheckedProperty)?.Path?.Path == "Settings.Overlay.ShowProvider")
        ?? throw new InvalidOperationException("Overlay page has no CheckBox bound to Settings.Overlay.ShowProvider");

    // ---------------- tree walking ----------------

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (node is not (Visual or System.Windows.Media.Media3D.Visual3D)) continue;
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) stack.Push(child);
        }
    }

    // ---------------- logging ----------------

    /// <summary>Appends to the log immediately, so a run killed by the CI timeout still shows how far it got.</summary>
    private static void Log(string line)
    {
        try { File.AppendAllText(_logPath, line + Environment.NewLine); }
        catch (IOException) { }
    }

    private static void Section(string name, Stopwatch sw)
    {
        Log($"[smoke] {name} ok elapsed_ms={sw.ElapsedMilliseconds}");
        sw.Restart();
    }

    private static void Warn(string message)
    {
        Log($"[smoke] WARNING {message}");
        _smokeLog.Warn(message);
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"could not delete {root}: {ex.Message}");
        }
    }

    /// <summary>Collects WPF data-binding errors while a palette is under test.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        private readonly object _gate = new();
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_gate) return [.. _errors];
            }
        }

        public override void Write(string? message)
        {
            // The "System.Windows.Data Error: n :" header; the message itself arrives through WriteLine.
        }

        public override void WriteLine(string? message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_gate) _errors.Add(message);
        }
    }

    // ---------------- screenshots ----------------

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
    }

    /// <summary>A dark, slightly textured stand-in for game scenery behind transparent windows.</summary>
    private static Brush Backdrop()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(0x2B, 0x33, 0x24), Color.FromRgb(0x4A, 0x40, 0x2C), 35);
        brush.Freeze();
        return brush;
    }

    private static void Save(FrameworkElement element, string path, Brush? background = null)
    {
        element.UpdateLayout();
        var source = PresentationSource.FromVisual(element);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (background is not null) dc.DrawRectangle(background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static async Task Pump() =>
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
}
