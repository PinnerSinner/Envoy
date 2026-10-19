using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
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
using Envoy.Core.Ux;
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
            // 09b: every check runs with motion off, so screenshots and layout checks see finished frames; the motion and
            // tale sections turn it on. The level is process-wide, so every smoke host gets the same setting.
            SetMotion(host, MotionSetting.Off);
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
            Check(PageHeadings.All.Select(h => h.PageId).SequenceEqual(PageIds.All), "Core PageHeadings do not follow PageIds.All");
            Section("page-ids", sw);

            // ---- 09a: the four embedded font families resolve to their own faces (no fallback for Latin text) ----
            CheckHeraldFonts(app);
            Section("herald-fonts", sw);

            // ---- 02: safe host, diagnostics, handler wiring ----
            safeHost = CheckSafeHost(app, root, log, recent);
            SetMotion(safeHost, MotionSetting.Off);
            Section("safe-host", sw);
            CheckDiagnostics(host, root);
            Section("diagnostics", sw);
            CheckHandlers(app);
            Section("handlers", sw);

            // ---- 06: the main host stops at Available (no background download); one Portable host, reused ----
            host.Settings.Updates.AutoDownload = false;
            host.ApplySettings();
            portableHost = await CheckPortableUpdatesAsync(app, root, log, recent);
            SetMotion(portableHost, MotionSetting.Off);
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
        var highContrast = palette == ThemeResolver.HighContrast;
        if (highContrast)
        {
            // 07b: high contrast is chosen by the resolver, never by Settings.Theme; Always keeps it through a re-apply.
            host.Settings.Accessibility.HighContrast = HighContrastMode.Always;
            ThemeManager.Apply(palette, force: true);
        }
        else
        {
            host.Settings.Theme = palette; // so a settings re-apply doesn't switch back to "match game"
            ThemeManager.Apply(palette);
        }
        Check(ThemeManager.CurrentId == palette, $"palette {palette} did not load (current: {ThemeManager.CurrentId})");
        SetMotion(host, MotionSetting.Off);
        Check(Services.Motion.Level == MotionLevel.Off, $"motion is {Services.Motion.Level} at the start of palette {palette}");

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

            // ---- 09a: the war table (rail, charter heading, marginal drawing, crest) and the control gallery ----
            await CheckWarTableAsync(main, palette);
            Section($"war-table({palette})", sw);
            if (first)
            {
                await CheckRowsFitAsync(main);
                Section("rows-fit", sw);
            }
            await CheckHeraldControlsAsync(palette, shots);
            Section($"herald-controls({palette})", sw);

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

            // ---- 07b: the focus ring, then (first palette) the accessible-name audit of every page ----
            CheckFocusVisual(main);
            Section($"focus-visual({palette})", sw);
            if (first)
            {
                await CheckPagesNamedAsync(main);
                Section("a11y-audit(pages)", sw);
                await CheckPageKeysAsync(main);
                Section("page-keys", sw);
            }

            // ---- close ----
            if (first)
            {
                // Earlier checks closed other MainWindows, which set FirstRunCompleted; clear it again and confirm the file
                // reads false, so the assertion below can fail (REQ-HRD-12 (5)).
                host.Settings.FirstRunCompleted = false;
                host.SaveNow();
                Check(!new SettingsStore(settingsPath, new DpapiProtector()).Load().FirstRunCompleted,
                    "close-flush could not clear FirstRunCompleted before closing MainWindow");
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
            await CheckOathAsync(app, host, palette);
            Section($"oath({palette})", sw);
            if (first)
            {
                await CheckKeyboardAsync(app, host);
                Section("keyboard", sw);
            }
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
            if (first) AuditNames(overlay, "overlay edit mode");
            overlay.ToggleEditMode();
            overlay.Close();
            Section($"overlay({palette})", sw);
            var readable = new OverlayWindow(host);
            readable.Show();
            await CheckOverlayReadableAsync(host, readable, palette);
            readable.Close();
            Section($"overlay-readable({palette})", sw);
            await CheckOverlayCompactAsync(host, palette, shots);
            Section($"overlay-compact({palette})", sw);

            var compose = new ComposeWindow(host);
            compose.Open(IntPtr.Zero);
            if (first)
            {
                CheckComposeHistory(compose);
                Section("compose-history", sw);
            }
            if (shots is not null)
            {
                compose.Input.Text = ComposeSample;
                // The provider was warmed up in setup, so this is usually instant; never wait more than 2.5 s.
                var wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 2500 && (compose.Preview.Text is "" or "…" || compose.BackTranslation.Text == ""))
                    await Task.Delay(50);
                if (compose.Preview.Text is "" or "…") Warn($"compose preview still empty after 2.5 s ({palette})");
                else Check(compose.CharCount.Visibility == Visibility.Visible && compose.CharCount.Text.Length > 0, $"the reply box's length counter is hidden after a translation ({palette})");
            }
            await Pump();
            if (shots is not null) Save(compose, Path.Combine(shots, $"{palette}-reply.png"), Backdrop());
            if (first) AuditWindow(compose, "compose");
            CheckDesk(compose, palette);
            compose.Close();
            Section($"compose({palette})", sw);
            Section($"desk({palette})", sw);

            var lookup = new LookupWindow(host);
            lookup.Show();
            lookup.ShowResult("[全部] 天下第一：这张地图的金矿在中间\n小心早期快攻", "[All] Number One Under Heaven: the gold on this map is in the middle\nWatch out for an early rush", "zh-CN");
            await Pump();
            if (shots is not null) Save(lookup, Path.Combine(shots, $"{palette}-scan.png"), Backdrop());
            if (first) AuditNames(lookup, "lookup");
            lookup.Close();
            Section($"lookup({palette})", sw);

            var selector = new RegionSelectorWindow(new System.Drawing.Rectangle(0, 0, 800, 600), null);
            selector.Show();
            await Pump();
            selector.Close();
            await Pump();
            Section($"region-selector({palette})", sw);

            // ---- 09b: every animated window with motion Off and Full, then the Tale through all six chapters ----
            await CheckMotionAsync(app, host, palette);
            Section($"motion({palette})", sw);
            await CheckTaleAsync(app, host, palette, first, shots);
            Section($"tale({palette})", sw);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            if (highContrast) host.Settings.Accessibility.HighContrast = HighContrastMode.FollowWindows;
        }

        var errors = listener.Errors.Where(e => !BindingErrorAllowlist.Contains(e)).ToList();
        foreach (var error in errors) Log($"[smoke] binding error ({palette}): {error}");
        Check(errors.Count == 0, $"{errors.Count} binding error(s) under palette {palette}");
        Section($"binding-errors({palette})", sw);

        Log($"[smoke] palette {palette} ok elapsed_ms={paletteTime.ElapsedMilliseconds}");
        if (paletteTime.Elapsed > PaletteBudget)
            Warn($"palette {palette} took {paletteTime.Elapsed.TotalSeconds:0} s (budget {PaletteBudget.TotalSeconds:0} s)");
    }

    // ---------------- 09a: the Herald's Writ ----------------

    /// <summary>The embedded families and the face each must resolve to (REQ-HRD-3).</summary>
    private static readonly (string Key, string Family, FontStyle Style, FontWeight Weight)[] HeraldFonts =
    [
        ("Font.Wordmark", "UnifrakturMaguntia", FontStyles.Normal, FontWeights.Normal),
        ("Font.Title", "IM FELL English SC", FontStyles.Normal, FontWeights.Normal),
        ("Font.Body", "EB Garamond", FontStyles.Normal, FontWeights.Normal),
        ("Font.Body", "EB Garamond", FontStyles.Italic, FontWeights.Normal),
        ("Font.Body", "EB Garamond", FontStyles.Normal, FontWeights.SemiBold),
        ("Font.Aside", "IM FELL English", FontStyles.Italic, FontWeights.Normal),
    ];

    /// <summary>
    /// AC-HRD-3: each font resource's first family is the embedded font, a real face (not a simulated one) for the styles
    /// the look uses, and the chain ends in the Windows UI fonts for CJK.
    /// </summary>
    private static void CheckHeraldFonts(App app)
    {
        foreach (var (key, family, style, weight) in HeraldFonts)
        {
            Check(app.TryFindResource(key) is FontFamily, $"{key} is not a FontFamily");
            var resource = (FontFamily)app.FindResource(key);
            var source = resource.Source;
            Check(source.EndsWith("Segoe UI", StringComparison.Ordinal), $"{key} does not end its fallback chain in Segoe UI: {source}");
            // The first family is a relative pack location ("/Envoy;component/Assets/Fonts/#Name", no commas: WPF reads a
            // doubled comma in a family list as an escape, and the absolute "pack://application:,,,/" form failed on CI). It
            // resolves against the dictionary's base URI, as it does for every control that uses the resource.
            var end = source.IndexOf(',', StringComparison.Ordinal);
            var first = new FontFamily(resource.BaseUri ?? new Uri("pack://application:,,,/"), (end < 0 ? source : source[..end]).Trim());
            var typeface = new Typeface(first, style, weight, FontStretches.Normal);
            Check(typeface.TryGetGlyphTypeface(out var glyphs), $"{key} ({family} {style} {weight}) has no glyph typeface (source {source}, base {resource.BaseUri})");
            Check(glyphs.FamilyNames.Values.Any(n => string.Equals(n, family, StringComparison.OrdinalIgnoreCase))
                  || glyphs.Win32FamilyNames.Values.Any(n => n.StartsWith(family, StringComparison.OrdinalIgnoreCase)),
                $"{key} resolved to {string.Join("/", glyphs.FamilyNames.Values)}, not {family}");
            Check(glyphs.StyleSimulations == StyleSimulations.None, $"{key} ({family} {style} {weight}) is simulated ({glyphs.StyleSimulations})");
            Check(glyphs.FontUri.ToString().Contains("Assets/Fonts", StringComparison.OrdinalIgnoreCase), $"{key} loaded from {glyphs.FontUri}, not the embedded fonts");
        }
        Log("herald fonts: " + string.Join(", ", HeraldFonts.Select(f => $"{f.Family} {f.Style} {f.Weight}")));
    }

    /// <summary>
    /// AC-HRD-6: every banner tab carries its playful and plain name; navigating sets the charter's drop cap, title, lede and
    /// drawing; the marginal column shows from 1100 px; the crest names the palette.
    /// </summary>
    private static async Task CheckWarTableAsync(MainWindow main, string palette)
    {
        foreach (var heading in PageHeadings.All)
        {
            var tab = (RadioButton)main.FindName("Nav" + heading.PageId);
            Check(Equals(tab.Content, heading.Name) && Equals(tab.Tag, heading.PlainName) && AutomationProperties.GetName(tab) == heading.AccessibleName,
                $"the {heading.PageId} tab reads '{tab.Content}' / '{tab.Tag}'");
            main.Navigate(heading.PageId);
            await Pump();
            Check(main.Heading == heading && Equals(main.PageDropCap.Content, heading.DropCap) && main.PageTitle.Text == heading.Name
                  && main.PageLede.Text == heading.Lede && main.FigureCaption.Text == heading.FigureCaption,
                $"the charter heading for {heading.PageId} reads '{main.PageTitle.Text}' ({palette})");
            Check(main.Figure.Children.Count > 0, $"the {heading.PageId} drawing has no strokes ({palette})");
            Check(tab.ActualWidth > 0 && tab.IsVisible, $"the {heading.PageId} tab is not rendered ({palette})");
        }
        // The CI desktop is 1024x768 and Windows keeps a window within the screen, so a real 1180 px window may not be
        // possible: the wide case drives the layout rule directly, and the real window must agree with the rule.
        var width = main.Width;
        main.LayoutWarTable(PageHeadings.MarginaliaMinWidth + 80);
        await Pump();
        Check(main.Marginalia.IsVisible, $"the marginal drawing is hidden at {PageHeadings.MarginaliaMinWidth + 80:0} px ({palette})");
        main.Width = PageHeadings.MarginaliaMinWidth + 80;
        await Pump();
        main.LayoutWarTable(main.ActualWidth);
        await Pump();
        Check(main.Marginalia.IsVisible == PageHeadings.ShowsMarginalia(main.ActualWidth),
            $"the marginal drawing visibility does not follow the rule at {main.ActualWidth:0} px ({palette})");
        main.Width = PageHeadings.MarginaliaMinWidth - 120;
        await Pump();
        Check(!main.Marginalia.IsVisible, $"the marginal drawing shows at {main.ActualWidth:0} px ({palette})");
        main.Width = width;
        await Pump();

        // AC-HRD-6: the last banner tab ends above the rail's foot, at the default height and at the minimum height.
        var height = main.Height;
        foreach (var h in new[] { height, main.MinHeight })
        {
            main.Height = h;
            await Pump();
            main.LayoutRail(main.Rail.ActualHeight);
            await Pump();
            var last = main.NavAbout;
            var tabBottom = last.TransformToAncestor(main.Rail).Transform(new Point(0, last.ActualHeight)).Y;
            var footTop = main.RailFoot.TransformToAncestor(main.Rail).Transform(new Point(0, 0)).Y;
            Check(last.IsVisible && tabBottom <= footTop + 0.5,
                $"the {last.Content} tab ends at {tabBottom:0} px, below the rail's foot at {footTop:0} px (window {main.ActualHeight:0} px, {palette})");
            Log($"rail at {main.ActualHeight:0} px: crest {main.RailCrest.IsVisible}, motto {main.RailMotto.IsVisible}, status {main.RailStatus.IsVisible}, tab {last.ActualHeight:0} px");
        }
        main.Height = height;
        await Pump();
        var crest = ThemeCatalog.Find(palette) is { } livery ? $"{livery.Name} livery" : "High contrast";
        Check(main.CrestName.Text == crest, $"the crest reads '{main.CrestName.Text}', not '{crest}'");
        main.Navigate(PageIds.Dashboard);
        await Pump();
    }

    /// <summary>
    /// REQ-HRD-6/7: every setting row's controls fit inside the row's slot on the charter, at the default size, at
    /// <c>MinWidth</c>, and at the narrowest width that shows the marginal drawing. The CI desktop may be narrower than that
    /// width, so the last case forces the drawing on and credits the row with the width the window could not grow by
    /// (the charter's columns are all star-sized, so a wider window widens every row by the same amount).
    /// </summary>
    private static async Task CheckRowsFitAsync(MainWindow main)
    {
        var width = main.Width;
        var rowStyle = (Style)main.FindResource("SettingRow");
        async Task CheckAt(double windowWidth, bool forceMarginalia, string label)
        {
            main.Width = windowWidth;
            await Pump();
            main.LayoutWarTable(forceMarginalia ? PageHeadings.MarginaliaMinWidth : main.ActualWidth);
            await Pump();
            var credit = forceMarginalia ? Math.Max(0, PageHeadings.MarginaliaMinWidth - main.ActualWidth) : 0;
            foreach (var pageId in PageIds.All)
            {
                main.Navigate(pageId);
                await Pump();
                foreach (var row in VisualDescendants(main.PageFor(pageId)).OfType<Grid>().Where(g => g.Style == rowStyle && g.IsVisible))
                {
                    var slot = LayoutInformation.GetLayoutSlot(row).Width - row.Margin.Left - row.Margin.Right;
                    foreach (var child in row.Children.OfType<FrameworkElement>().Where(c => c.IsVisible))
                    {
                        var right = child.TransformToAncestor(row).Transform(new Point(child.ActualWidth, 0)).X;
                        Check(right <= slot + credit + 0.5,
                            $"a {child.GetType().Name} ('{AutomationProperties.GetName(child)}') on {pageId} ends at {right:0} px in a {slot + credit:0} px row ({label}, window {main.ActualWidth:0} px)");
                    }
                }
            }
        }
        await CheckAt(width, forceMarginalia: false, "default size");
        await CheckAt(main.MinWidth, forceMarginalia: false, "MinWidth");
        await CheckAt(Math.Max(width, PageHeadings.MarginaliaMinWidth), forceMarginalia: true, "marginal drawing shown");
        main.Width = width;
        await Pump();
        main.LayoutWarTable(main.ActualWidth);
        main.Navigate(PageIds.Dashboard);
        await Pump();
    }

    /// <summary>
    /// AC-HRD-4, 5: a gallery of every restyled control and ornament renders under the palette with a non-empty size, and
    /// every wax a language seal can name resolves.
    /// </summary>
    private static async Task CheckHeraldControlsAsync(string palette, string? shots)
    {
        foreach (var wax in Enum.GetValues<SealWax>())
            Check(Application.Current.TryFindResource(new LanguageSeal("x", wax).BrushKey) is Brush, $"no {wax} wax brush ({palette})");
        foreach (var key in new[] { "Knot.Horizontal", "Knot.Vertical", "Damask.Oak", "Vellum.Gradient", "Wax.Livery", "Livery.Primary", "Focus.Ring.Oak", "Row.Rule" })
            Check(Application.Current.TryFindResource(key) is Brush, $"no {key} brush ({palette})");

        var panel = new StackPanel { Margin = new Thickness(16) };
        T Add<T>(T element, string? style = null) where T : FrameworkElement
        {
            if (style is not null) element.SetResourceReference(FrameworkElement.StyleProperty, style);
            element.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(element);
            return element;
        }
        var row = new WrapPanel();
        foreach (var (text, style) in new[] { ("Secondary", (string?)null), ("Seal and send", "Button.Primary"), ("Quiet", "Button.Ghost"), ("gl hf", "Button.Chip") })
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            if (style is not null) button.SetResourceReference(FrameworkElement.StyleProperty, style);
            AutomationProperties.SetName(button, text);
            row.Children.Add(button);
        }
        Add(row);
        var on = Add(new CheckBox { Content = "Explain gamer slang", IsChecked = true });
        var off = Add(new CheckBox { Content = "Show game notices", IsChecked = false });
        var toggle = Add(new ToggleButton { Content = "Follow the game", IsChecked = true }, "Switch.Seal");
        Add(new TextBox { Text = "Hello! Another round?", Width = 300, HorizontalAlignment = HorizontalAlignment.Left });
        Add(new ComboBox { ItemsSource = new[] { "Google Translate (free)", "DeepL (your key)" }, SelectedIndex = 0, Width = 300, HorizontalAlignment = HorizontalAlignment.Left });
        Add(new Slider { Minimum = 0, Maximum = 100, Value = 40, Width = 300, HorizontalAlignment = HorizontalAlignment.Left });
        Add(new Control(), "Fleuron");
        var seals = new WrapPanel();
        foreach (var wax in Enum.GetValues<SealWax>())
        {
            var seal = new ContentControl { Content = LanguageSeal.Unknown.Glyph, Margin = new Thickness(0, 0, 8, 0) };
            seal.SetResourceReference(FrameworkElement.StyleProperty, "WaxSeal");
            seal.SetResourceReference(Control.BackgroundProperty, new LanguageSeal("x", wax).BrushKey);
            seals.Children.Add(seal);
        }
        foreach (var candle in Enum.GetValues<CandleState>())
        {
            var c = new ContentControl { Tag = candle.ToString(), Margin = new Thickness(0, 0, 8, 0) };
            c.SetResourceReference(FrameworkElement.StyleProperty, "Candle");
            seals.Children.Add(c);
        }
        var cap = new ContentControl { Content = "W", Margin = new Thickness(0, 0, 8, 0) };
        cap.SetResourceReference(FrameworkElement.StyleProperty, "DropCap");
        seals.Children.Add(cap);
        var crest = new Control();
        crest.SetResourceReference(FrameworkElement.StyleProperty, "Crest");
        seals.Children.Add(crest);
        Add(seals);
        Add(new Control { Width = 300, HorizontalAlignment = HorizontalAlignment.Left }, "ScrollRods");
        // Callouts bind their button to the page's view model through a UserControl ancestor, as on a real page.
        var callouts = new StackPanel();
        foreach (var severity in Enum.GetValues<HintSeverity>())
        {
            var callout = new ContentControl { Content = new PageHint(severity, $"A {severity} dispatch card.") };
            callout.SetResourceReference(FrameworkElement.StyleProperty, "Callout");
            callouts.Children.Add(callout);
        }
        Add(new UserControl { Content = callouts });
        Add(new ContentControl { Content = new ContentControl { Content = "Scouts", Style = (Style)Application.Current.FindResource("RowLabel") }, Height = 220 }, "CharterFrame");

        var window = new Window
        {
            Title = "Herald controls",
            Width = 760,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            Content = new ScrollViewer { Content = panel, MaxHeight = 900 },
        };
        window.SetResourceReference(Control.BackgroundProperty, "Vellum.Gradient");
        window.Show();
        await Pump();
        try
        {
            foreach (var element in panel.Children.OfType<FrameworkElement>())
                Check(element.ActualWidth > 0 && element.ActualHeight > 0, $"{element.GetType().Name} {element.Style?.TargetType?.Name} did not render ({palette})");
            Check(on.IsChecked == true && off.IsChecked == false && toggle.IsChecked == true, $"the seal switches lost their state ({palette})");
            if (shots is not null) Save(window, Path.Combine(shots, $"{palette}-herald-controls.png"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>AC-HRD-9: the scribe's desk heading is the drop cap R and the rest of "Reply to {name}"; the send seal is the primary style.</summary>
    private static void CheckDesk(ComposeWindow compose, string palette)
    {
        Check(Equals(compose.HeadingCap.Content, "R") && compose.HeadingRest.Text.StartsWith("eply", StringComparison.Ordinal),
            $"the desk heading reads '{compose.HeadingCap.Content}{compose.HeadingRest.Text}' ({palette})");
        Check(ReferenceEquals(compose.SendButton.Style, compose.FindResource("Button.Primary")), $"Seal and send is not the wax primary button ({palette})");
        Check(compose.HeadingCap.ActualWidth > 0 && compose.SendButton.ActualWidth > 0, $"the desk did not render ({palette})");
        // The footer must be on screen: a desk taller than the work area once clipped Seal and send away.
        var area = SystemParameters.WorkArea;
        var sendBottom = compose.SendButton.TranslatePoint(new Point(0, compose.SendButton.ActualHeight), compose).Y;
        Check(compose.SendButton.IsVisible && sendBottom <= compose.ActualHeight + 0.5,
            $"Seal and send ends at {sendBottom:0} px, below the desk's {compose.ActualHeight:0} px ({palette})");
        Check(compose.Top >= area.Top - 0.5 && compose.Top + compose.ActualHeight <= area.Bottom + 0.5,
            $"the desk ({compose.Top:0} to {compose.Top + compose.ActualHeight:0} px) leaves the work area ({area.Top:0} to {area.Bottom:0} px) ({palette})");
    }

    /// <summary>AC-HRD-9: the oath shows one seal per step in the chain, numbered, and a drop cap for the step title.</summary>
    private static async Task CheckOathAsync(App app, AppHost host, string palette)
    {
        var wizard = new SetupWizardWindow(host, app, OnboardingStep.Language);
        wizard.Show();
        await Pump();
        try
        {
            var vm = wizard.ViewModel;
            Check(wizard.StepList.Items.Count == vm.Steps.Count, $"the seal chain has {wizard.StepList.Items.Count} seals ({palette})");
            Check(vm.Steps.Select(s => s.Number).SequenceEqual(Enumerable.Range(1, vm.Steps.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                "the step seals are not numbered 1 to n");
            Check(Equals(wizard.StepDropCap.Content, vm.CurrentTitle[..1].ToUpperInvariant()), $"the oath's drop cap reads '{wizard.StepDropCap.Content}' ({palette})");
        }
        finally
        {
            wizard.Close();
            await Pump();
        }
    }

    // ---------------- 09b: motion and the Tale ----------------

    /// <summary>Long enough for every one-off animation to finish: the ink (1.4 s) after the longest delay, with margin.</summary>
    private static readonly TimeSpan SettleTime = Core.Ux.Motion.Ink + TimeSpan.FromMilliseconds(800);

    /// <summary>Sets the Motion setting on a host and applies it (the level is process-wide).</summary>
    private static void SetMotion(AppHost host, MotionSetting setting)
    {
        host.Settings.Appearance.Motion = setting;
        host.ApplyMotion();
    }

    /// <summary>
    /// True when nothing under <paramref name="root"/> moves: every part this helper moved is back at rest, and two samples
    /// of every opacity and transform 300 ms apart agree (no loop is running). Returns the first mover, or null.
    /// </summary>
    private static async Task<string?> MoverUnderAsync(DependencyObject root)
    {
        static Dictionary<UIElement, string> Sample(DependencyObject root) =>
            VisualDescendants(root).OfType<UIElement>().ToDictionary(e => e, e =>
                FormattableString.Invariant($"{e.Opacity:0.###}|{Services.Motion.ExistingTransformOf(e)?.Snapshot}"));
        foreach (var node in VisualDescendants(root).OfType<UIElement>())
            if (Services.Motion.ExistingTransformOf(node) is { IsIdentity: false }) return $"{node.GetType().Name} (not at rest)";
        var before = Sample(root);
        await Task.Delay(300);
        var after = Sample(root);
        foreach (var (node, value) in before)
            if (after.TryGetValue(node, out var later) && later != value) return $"{node.GetType().Name} ({value} then {later})";
        return null;
    }

    /// <summary>
    /// AC-MOT-3 to 8 (spec 09b): the animated windows render with motion Off and Full under the palette: page change,
    /// candles, torches, the overlay's dispatches, the desk's ink and fold, and the guide's page turn and seals. After the
    /// animations settle, every part is back at rest: the overlay (REQ-MOT-2) runs nothing once its dispatches are in.
    /// </summary>
    private static async Task CheckMotionAsync(App app, AppHost host, string palette)
    {
        foreach (var setting in new[] { MotionSetting.Off, MotionSetting.Full })
        {
            SetMotion(host, setting);
            var level = MotionPolicy.Resolve(setting, host.SystemAnimations);
            Check(Services.Motion.Level == level, $"Motion {setting} resolved to {Services.Motion.Level}, not {level} ({palette})");
            var full = level == MotionLevel.Full;

            var main = new MainWindow(host);
            var overlay = new OverlayWindow(host);
            var compose = new ComposeWindow(host);
            var wizard = new SetupWizardWindow(host, app, OnboardingStep.Game);
            var candle = new ContentControl { Tag = nameof(CandleState.Lit) };
            candle.SetResourceReference(FrameworkElement.StyleProperty, "Candle");
            var holder = new Window { Content = candle, Width = 120, Height = 140, WindowStyle = WindowStyle.None, ShowActivated = false, Title = "Candle" };
            holder.SetResourceReference(Control.BackgroundProperty, "Vellum.Gradient");
            try
            {
                main.Show();
                await Pump();
                var torches = full && palette != ThemeResolver.HighContrast;
                Check(main.Torches.IsVisible == torches && main.TorchLights.IsVisible == torches,
                    $"the torches are {(main.Torches.IsVisible ? "lit" : "out")} with motion {setting} ({palette})");
                // The CI desktop is too narrow for the marginal column: drive the layout rule so the drawing is shown and redrawn.
                main.LayoutWarTable(PageHeadings.MarginaliaMinWidth + 80);
                await Pump();
                Check(main.Marginalia.IsVisible, $"the marginal drawing is hidden for the motion check (motion {setting}, {palette})");
                main.Navigate(PageIds.Translation); // the charter unrolls, the drawing redraws, the candles burn
                await Pump();
                main.Navigate(PageIds.Appearance);
                await Pump();

                overlay.Show();
                host.ShowDemoMessages(); // dispatches unfurl, seals stamp
                compose.Open(IntPtr.Zero);
                compose.Preview.Text = "좋은 게임이었어요! 한 판 더?";
                Services.Motion.InkWrite(compose.Preview); // the scribe writes
                wizard.Show();
                await Pump();
                var step = wizard.ViewModel.Current;
                wizard.ViewModel.NextCommand.Execute(null); // the step page turns
                Check(wizard.ViewModel.Current != step, $"Next did not leave the {step} step, so no page turned (motion {setting}, {palette})");
                holder.Show();
                await Pump();
                candle.Tag = nameof(CandleState.Snuffed); // smoke rises
                await Pump();
                candle.Tag = nameof(CandleState.Lit); // relights with a pop
                await Pump();

                if (full) await Task.Delay(SettleTime);
                await Pump();

                Check(main.PageStage.Clip is null && Services.Motion.ExistingTransformOf(main.PageStage) is null or { IsIdentity: true },
                    $"the charter did not settle after unrolling (motion {setting}, {palette})");
                Check(main.PageDropCap.Opacity == 1 && main.PageLede.Opacity == 1,
                    $"the drop cap or lede did not settle (motion {setting}, {palette})");
                Check(main.Figure.Children.OfType<System.Windows.Shapes.Shape>().All(p =>
                        p.ReadLocalValue(System.Windows.Shapes.Shape.StrokeDashArrayProperty) == DependencyProperty.UnsetValue),
                    $"the marginal drawing did not finish (motion {setting}, {palette})");
                Check(overlay.Items.Items.Count > 0, $"the overlay shows no dispatches (motion {setting}, {palette})");
                var moving = await MoverUnderAsync(overlay);
                Check(moving is null, $"the overlay still animates {moving} after its dispatches came in (motion {setting}, {palette})");
                Check(!overlay.IsActive && !overlay.IsKeyboardFocusWithin, $"the overlay took focus (motion {setting}, {palette})");
                Check(compose.Preview.OpacityMask is null && compose.Preview.Opacity == 1, $"the desk's ink did not finish (motion {setting}, {palette})");
                Check(Services.Motion.ExistingTransformOf(wizard.StepPage) is null or { IsIdentity: true },
                    $"the guide's page did not finish turning (motion {setting}, {palette})");
                Check(wizard.StepPage.Opacity == 1, $"the guide's page did not fade in (motion {setting}, {palette})");

                // Full really moves: the relit candle flickers (its scale loop came back after the pop) and the torches burn.
                // Off moves nothing.
                var flame = await MoverUnderAsync(candle);
                Check(full ? flame is not null : flame is null,
                    $"the lit candle {(flame is null ? "is still" : "moves: " + flame)} with motion {setting} ({palette})");
                if (torches)
                    Check(await MoverUnderAsync(main.Torches) is not null, $"the torches do not burn with motion {setting} ({palette})");

                // The compact layout's dispatches come in too, and then nothing in the overlay moves (CompactItem).
                var compactWas = host.Settings.Overlay.Compact;
                try
                {
                    host.Settings.Overlay.Compact = true;
                    overlay.ApplySettings();
                    host.ShowDemoMessages();
                    await Pump();
                    Check(ReferenceEquals(overlay.Items.ItemTemplate, overlay.FindResource(OverlayWindow.CompactItemKey)),
                        $"the compact overlay does not use CompactItem (motion {setting}, {palette})");
                    if (full) await Task.Delay(SettleTime);
                    await Pump();
                    var compactMoving = await MoverUnderAsync(overlay);
                    Check(compactMoving is null, $"the compact overlay still animates {compactMoving} (motion {setting}, {palette})");
                }
                finally
                {
                    host.Settings.Overlay.Compact = compactWas;
                    overlay.ApplySettings();
                }

                // The desk folds toward the chat when a reply is sent, and opens whole again.
                var fold = Services.Motion.FoldAwayAsync(compose.Desk);
                Check(await Task.WhenAny(fold, Task.Delay(TimeSpan.FromSeconds(3))) == fold, $"the desk never finished folding (motion {setting}, {palette})");
                Services.Motion.Unfold(compose.Desk);
                Check(compose.Desk.Opacity == 1 && Services.Motion.ExistingTransformOf(compose.Desk) is null or { IsIdentity: true },
                    $"the desk did not unfold (motion {setting}, {palette})");
            }
            finally
            {
                holder.Close();
                wizard.Close();
                compose.Close();
                overlay.Close();
                main.Close();
                await Pump();
            }
        }
        SetMotion(host, MotionSetting.Off);
    }

    /// <summary>
    /// AC-MOT-9, 10 (spec 09b): the Tale steps through all six chapters with Right under motion Off (a still tale) and Full
    /// (playing), each caption from Core TaleText and only its chapter shown; Left, Space and Esc work; OpenTale is single
    /// instance (first palette).
    /// </summary>
    private static async Task CheckTaleAsync(App app, AppHost host, string palette, bool first, string? shots)
    {
        var hotkey = host.Settings.Hotkeys.Compose;
        var chapters = TaleText.Chapters(hotkey);
        foreach (var setting in new[] { MotionSetting.Off, MotionSetting.Full })
        {
            SetMotion(host, setting);
            var full = Services.Motion.Level == MotionLevel.Full;
            var tale = new TaleWindow(hotkey, null);
            tale.Show();
            await Pump();
            try
            {
                Check(tale.Player.AutoPlay == full && tale.Player.IsPlaying == full, $"the tale's autoplay is {tale.Player.AutoPlay} with motion {setting} ({palette})");
                for (var i = 0; i < chapters.Count; i++)
                {
                    if (i > 0) tale.Press(TaleKey.Right);
                    await Pump();
                    Check(tale.Player.Chapter == i, $"Right reached chapter {tale.Player.Chapter + 1}, not {i + 1} (motion {setting}, {palette})");
                    Check(tale.CaptionHeadingText == chapters[i].Heading && tale.CaptionSentence == chapters[i].Sentence,
                        $"chapter {i + 1}'s caption reads '{tale.CaptionHeadingText}: {tale.CaptionSentence}' ({palette})");
                    // The live region is announced by its automation name, so that name must be the caption itself.
                    var spoken = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(tale.CaptionText)?.GetName();
                    Check(spoken == chapters[i].Sentence, $"chapter {i + 1}'s caption is announced as '{spoken}' ({palette})");
                    for (var k = 0; k < chapters.Count; k++)
                        Check(tale.ChapterView(k).IsVisible == (k == i), $"chapter {k + 1} visibility is wrong on chapter {i + 1} ({palette})");
                    Check(tale.ChapterView(i).ActualWidth > 0, $"chapter {i + 1} did not render (motion {setting}, {palette})");
                    if (!full && i == 2 && shots is not null) Save(tale, Path.Combine(shots, $"{palette}-tale.png"));
                }
                tale.Press(TaleKey.Right);
                Check(tale.Player.Chapter == chapters.Count - 1, $"Right moved past chapter VI ({palette})");
                tale.Press(TaleKey.Left);
                Check(tale.Player.Chapter == chapters.Count - 2, $"Left did not go back to chapter V ({palette})");
                tale.Press(TaleKey.Space);
                if (full)
                {
                    Check(!tale.Player.IsPlaying, $"Space did not pause the tale ({palette})");
                    tale.Press(TaleKey.Space);
                    Check(tale.Player.IsPlaying, $"Space did not resume the tale ({palette})");
                }
                else
                {
                    Check(tale.Player.Chapter == chapters.Count - 1, $"Space did not move a still tale on ({palette})");
                }
                await Pump();
                if (first && !full) AuditWindow(tale, "tale");
                Check(tale.Press(TaleKey.Escape), $"Esc was not handled ({palette})");
                await Pump();
                Check(!tale.IsVisible, $"Esc did not close the tale ({palette})");
            }
            finally
            {
                if (tale.IsLoaded) tale.Close();
                await Pump();
            }
        }
        SetMotion(host, MotionSetting.Off);

        if (!first) return;
        IShell shell = app;
        shell.OpenTale();
        await Pump();
        var opened = app.Tale;
        Check(opened is { IsVisible: true }, "OpenTale did not show the tale");
        shell.OpenTale();
        await Pump();
        Check(ReferenceEquals(app.Tale, opened), "a second OpenTale opened another tale");
        opened!.Close();
        await Pump();
        Check(app.Tale is null, "the closed tale is still App's tale");
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
            Check(items.Contains(App.TrayGuideItem) && items.Contains(App.TrayCheatSheetItem) && items.Contains(App.TrayTaleItem),
                $"the tray menu has [{string.Join(", ", items)}]");
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

        // REQ-HRD-12 (1): a new guide asked to open past the privacy step opens on it while privacy is not acknowledged.
        host.SetPrivacyAcknowledged(false);
        shell.OpenSetupWizard(nameof(OnboardingStep.Hotkeys));
        await Pump();
        Check(app.Wizard?.ViewModel.Current == OnboardingStep.Language, $"a new guide opened at {app.Wizard?.ViewModel.Current} before privacy was acknowledged");
        app.Wizard?.Close();
        await Pump();
        host.SetPrivacyAcknowledged(true);

        // REQ-HRD-12 (2): Skip or close on the guide the start-up choice opened shows the War Room, so Envoy is never
        // left with no window. The step list cannot jump past the privacy step while it is not acknowledged (1).
        host.Settings.Onboarding.Skipped = false;
        app.OpenSetupWizard(nameof(OnboardingStep.Language), autoOpened: true);
        await Pump();
        host.SetPrivacyAcknowledged(false);
        app.Wizard!.ViewModel.GoTo(OnboardingStep.Hotkeys);
        Check(app.Wizard.ViewModel.Current == OnboardingStep.Language, "the step list skipped the privacy step while it was not acknowledged");
        host.SetPrivacyAcknowledged(true);
        app.Wizard.ViewModel.GoTo(OnboardingStep.Hotkeys);
        Check(app.Wizard.ViewModel.Current == OnboardingStep.Hotkeys, "the step list did not move on once privacy was acknowledged");
        app.Wizard.Close();
        await Pump();
        Check(app.Wizard is null && host.Settings.Onboarding.Skipped, "closing the auto-opened guide did not record Skip");
        Check(app.OpenMainWindow is { } fallback && fallback.PageFor(PageIds.Dashboard).IsVisible,
            "closing the auto-opened guide did not open MainWindow on the War Room");
        app.OpenMainWindow!.Close();
        await Pump();

        // Start the per-palette checks as a user who has not finished the guide (the Finish-setup callout shows), and with
        // FirstRunCompleted cleared and saved, so close-flush checks that closing MainWindow sets it again (REQ-HRD-12 (5)).
        host.Settings.Onboarding.Completed = false;
        host.Settings.Onboarding.Skipped = false;
        host.Settings.Onboarding.LastStep = "";
        host.Settings.FirstRunCompleted = false;
        host.SaveNow();
    }

    /// <summary>AC-OUXA-7: a seeded OCR notice is in the War Room list.</summary>
    private static async Task CheckNoticesAsync(AppHost host, MainWindow main)
    {
        main.Navigate(PageIds.Dashboard);
        var notice = NoticeFactory.OcrMissing(["ja"])!;
        // ApplySettings re-checks OCR and withdraws any OCR key this machine does not need, so the setup seed may be gone,
        // and an apply that lands during the pump (an earlier section's debounced edit) withdraws this one too. Re-seed
        // after such an apply; the check below still needs the seed listed once nothing else applies.
        var applied = false;
        void OnApplied() => applied = true;
        host.SettingsApplied += OnApplied;
        try
        {
            for (var round = 0; round < 3; round++)
            {
                applied = false;
                host.Notices!.Submit(notice, gameForeground: false);
                await Pump();
                if (!applied) break;
                Log("notices: a settings apply landed during the pump; seeding the notice again");
            }
        }
        finally
        {
            host.SettingsApplied -= OnApplied;
        }
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
        // A provider-health edge from an earlier section's real translation can land on the UI thread at any point
        // in this check and add its own notice. Withdraw anything that is not one of the four before every count.
        var smokeKeys = Enumerable.Range(1, 4).Select(i => NoticeFactory.ProviderKey($"smoke-{i}")).ToHashSet(StringComparer.Ordinal);
        async Task DropStrayNotices()
        {
            for (var round = 0; round < 3; round++)
            {
                var stray = center.Active.Select(n => n.Key).Where(k => !smokeKeys.Contains(k)).ToList();
                if (stray.Count == 0) return;
                Log("war-room notices: withdrew " + string.Join(", ", stray));
                center.ResolveWhere(k => !smokeKeys.Contains(k));
                await Pump();
            }
        }
        await DropStrayNotices();
        Check(page.NoticeRows.Items.Count == MainViewModel.VisibleNoticeLimit, $"{page.NoticeRows.Items.Count} notice rows, expected {MainViewModel.VisibleNoticeLimit}");
        Check(page.ShowAllNoticesToggle.IsVisible && page.ShowAllNoticesText.Text == "Show all (4)", $"the toggle reads '{page.ShowAllNoticesText.Text}'");
        vm.ShowAllNotices = true;
        await Pump();
        await DropStrayNotices();
        Check(page.NoticeRows.Items.Count == 4, $"Show all listed {page.NoticeRows.Items.Count} notices, expected 4");

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
        CheckOrder(appearance, "Appearance", appearance.ThemesList, appearance.AccessibilityFrame, appearance.NotificationsFrame, appearance.StartupFrame);
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

    // ---------------- 07b: accessibility ----------------

    /// <summary>
    /// AC-OUXB-1: the resolved Button, CheckBox, Slider and NavItem styles carry a focus visual, and the palette defines
    /// Focus.Ring as a brush.
    /// </summary>
    private static void CheckFocusVisual(MainWindow main)
    {
        foreach (var (name, key) in new (string, object)[] { ("Button", typeof(Button)), ("CheckBox", typeof(CheckBox)), ("Slider", typeof(Slider)), ("NavItem", "NavItem") })
        {
            var style = main.TryFindResource(key) as Style;
            Check(style is not null, $"no {name} style");
            Check(FocusVisualOf(style!) is not null, $"the {name} style has no FocusVisualStyle");
        }
        Check(main.TryFindResource("Focus.Ring") is SolidColorBrush, "Focus.Ring is not a SolidColorBrush");
    }

    private static Style? FocusVisualOf(Style style)
    {
        for (var s = style; s is not null; s = s.BasedOn)
        {
            var setter = s.Setters.OfType<Setter>().FirstOrDefault(x => x.Property == FrameworkElement.FocusVisualStyleProperty);
            if (setter is not null) return setter.Value as Style;
        }
        return null;
    }

    /// <summary>REQ-OUXB-27: every visible, enabled, focusable control on every page has a UI Automation name.</summary>
    private static async Task CheckPagesNamedAsync(MainWindow main)
    {
        var total = 0;
        foreach (var page in PageIds.All)
        {
            main.Navigate(page);
            await Pump();
            AuditNames(main, $"page {page}");
            total += AccessibilityAudit.Audited(main).Count;
        }
        main.Navigate(PageIds.Dashboard);
        await Pump();
        Log($"[smoke] a11y: ok ({total} controls)");
    }

    /// <summary>REQ-OUXB-30: Ctrl+Tab and Ctrl+Shift+Tab move through the pages and wrap at both ends.</summary>
    private static async Task CheckPageKeysAsync(MainWindow main)
    {
        main.Navigate(PageIds.All[^1]);
        await Pump();
        main.NavigateRelative(1);
        await Pump();
        Check(main.CurrentPage == PageIds.All[0], $"Ctrl+Tab from the last page went to {main.CurrentPage}");
        main.NavigateRelative(-1);
        await Pump();
        Check(main.CurrentPage == PageIds.All[^1], $"Ctrl+Shift+Tab from the first page went to {main.CurrentPage}");
        main.Navigate(PageIds.Dashboard);
        await Pump();
    }

    private static void AuditNames(DependencyObject root, string where)
    {
        var unnamed = AccessibilityAudit.FindUnnamed(root);
        foreach (var control in unnamed) Log($"[smoke] unnamed control ({where}): {control}");
        Check(unnamed.Count == 0, $"{unnamed.Count} control(s) without an accessible name on {where}");
    }

    /// <summary>REQ-OUXB-27, 31: names, then Tab reaches every control (a window that cannot take focus logs a warning).</summary>
    private static void AuditWindow(Window window, string where)
    {
        AuditNames(window, where);
        var unreachable = AccessibilityAudit.FindUnreachable(window);
        if (unreachable is null)
        {
            Warn($"a11y: {where} could not take keyboard focus; Tab reachability not checked");
            return;
        }
        foreach (var control in unreachable) Log($"[smoke] not reachable with Tab ({where}): {control}");
        Check(unreachable.Count == 0, $"{unreachable.Count} control(s) on {where} are not reachable with Tab");
    }

    /// <summary>AC-OUXB-4: the guide's Next is the default and Skip the cancel button; compose cycles Tab.</summary>
    private static async Task CheckKeyboardAsync(App app, AppHost host)
    {
        var wizard = new SetupWizardWindow(host, app, OnboardingStep.Language);
        wizard.Show();
        await Pump();
        Check(KeyboardNavigation.GetTabNavigation(wizard) == KeyboardNavigationMode.Cycle, "the guide's root does not cycle Tab");
        Check(wizard.NextButton.IsDefault, "the guide's Next is not the default button");
        Check(wizard.SkipButton.IsCancel, "the guide's Skip is not the cancel button");
        wizard.Close();
        await Pump();

        var compose = new ComposeWindow(host);
        compose.Open(IntPtr.Zero);
        await Pump();
        Check(KeyboardNavigation.GetTabNavigation(compose) == KeyboardNavigationMode.Cycle, "the reply box's root does not cycle Tab");
        Check(!compose.SendButton.IsDefault, "the reply box's Send must not be IsDefault (Enter in the input sends)");
        Check(compose.Input.TabIndex < compose.CopyButton.TabIndex && compose.CopyButton.TabIndex < compose.SendButton.TabIndex
              && compose.SendButton.TabIndex < compose.TargetLanguage.TabIndex && compose.TargetLanguage.TabIndex < compose.CloseButton.TabIndex,
            "the reply box's tab order is not input, phrases, Copy, Send, language, Close");
        compose.Close();
        await Pump();
    }

    /// <summary>
    /// AC-OUXB-8: at 20% background the readable-text floor raises the card to at least the palette's minimum alpha (1 in
    /// high contrast); with the floor off the card keeps 20%.
    /// </summary>
    private static async Task CheckOverlayReadableAsync(AppHost host, OverlayWindow overlay, string palette)
    {
        var o = host.Settings.Overlay;
        var (opacity, readable) = (o.BackgroundOpacity, o.EnsureReadableText);
        try
        {
            o.BackgroundOpacity = 0.2;
            o.EnsureReadableText = true;
            overlay.ApplySettings();
            await Pump();
            var minimum = palette == ThemeResolver.HighContrast ? 1.0 : ThemeManager.OverlayMinimumAlpha();
            var alpha = ((SolidColorBrush)overlay.CardBackground).Color.A / 255.0;
            Check(alpha + 0.5 / 255 >= minimum, $"overlay card alpha {alpha:0.00} is below the readable minimum {minimum:0.00} ({palette})");

            o.EnsureReadableText = false;
            overlay.ApplySettings();
            await Pump();
            alpha = ((SolidColorBrush)overlay.CardBackground).Color.A / 255.0;
            var expected = palette == ThemeResolver.HighContrast ? 1.0 : 0.2;
            Check(Math.Abs(alpha - expected) < 1.0 / 255 + 1e-9, $"overlay card alpha {alpha:0.00} is not {expected:0.00} with the floor off ({palette})");
        }
        finally
        {
            (o.BackgroundOpacity, o.EnsureReadableText) = (opacity, readable);
            overlay.ApplySettings();
        }
    }

    /// <summary>
    /// AC-OUXC-4 (REQ-OUXC-44, 45): the compact layout renders one text line per card under every palette and switches
    /// back to the full layout live; in edit mode every card shows its Copy button.
    /// </summary>
    private static async Task CheckOverlayCompactAsync(AppHost host, string palette, string? shots)
    {
        var o = host.Settings.Overlay;
        var compact = o.Compact;
        var overlay = new OverlayWindow(host);
        try
        {
            o.Compact = true;
            overlay.ApplySettings();
            overlay.Show();
            host.ShowDemoMessages();
            await Pump();
            Check(ReferenceEquals(overlay.Items.ItemTemplate, overlay.FindResource(OverlayWindow.CompactItemKey)), $"the compact overlay does not use CompactItem ({palette})");
            var count = overlay.Items.Items.Count;
            Check(count > 0, $"the compact overlay shows no demo messages ({palette})");
            for (var i = 0; i < count; i++)
            {
                var card = overlay.Items.ItemContainerGenerator.ContainerFromIndex(i)
                    ?? throw new InvalidOperationException($"compact overlay card {i} was not generated ({palette})");
                var lines = VisualDescendants(card).OfType<TextBlock>().Count(t => t.Name.EndsWith("Line", StringComparison.Ordinal) && t.IsVisible);
                Check(lines == 1, $"compact overlay card {i} shows {lines} text lines, not 1 ({palette})");
            }
            if (shots is not null) Save(overlay, Path.Combine(shots, $"{palette}-overlay-compact.png"), Backdrop());

            overlay.ToggleEditMode();
            await Pump();
            Check(overlay.IsEditing, $"the overlay is not in edit mode after Ctrl+Shift+E ({palette})");
            var copies = VisualDescendants(overlay).OfType<Button>().Count(b => b.Name == "CopyButton" && b.IsVisible);
            Check(copies == count, $"edit mode shows {copies} Copy buttons for {count} messages ({palette})");
            overlay.ToggleEditMode();
            await Pump();
            Check(!VisualDescendants(overlay).OfType<Button>().Any(b => b.Name == "CopyButton" && b.IsVisible), $"Copy buttons stay visible after edit mode ({palette})");

            o.Compact = false;
            overlay.ApplySettings();
            await Pump();
            Check(ReferenceEquals(overlay.Items.ItemTemplate, overlay.FindResource(OverlayWindow.FullItemKey)), $"turning Compact off does not restore FullItem ({palette})");
        }
        finally
        {
            o.Compact = compact;
            overlay.Close();
        }
    }

    /// <summary>
    /// AC-OUXC-2, 3: the counter is collapsed before any translation, and Up/Down through the real key handler recall a
    /// sent reply and restore the draft.
    /// </summary>
    private static void CheckComposeHistory(ComposeWindow compose)
    {
        Check(compose.CharCount.Visibility == Visibility.Collapsed, "the reply box's length counter shows before any translation");
        compose.History.Add("gg wp");
        compose.Input.Text = "dr";
        PressKey(compose.Input, Key.Up);
        Check(compose.Input.Text == "gg wp", $"Up in the reply box recalled \"{compose.Input.Text}\", not the sent reply");
        PressKey(compose.Input, Key.Down);
        Check(compose.Input.Text == "dr", $"Down past the newest reply restored \"{compose.Input.Text}\", not the draft");
        compose.Input.Text = "";
    }

    private static void PressKey(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("the reply box has no presentation source");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
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
            if (first) AuditWindow(wizard, $"guide step {step}");
            if (shots is not null && palette is ThemeCatalog.Herald or ThemeResolver.HighContrast) Save(wizard, Path.Combine(shots, $"{palette}-setup-{step.ToString().ToLowerInvariant()}.png"));
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
