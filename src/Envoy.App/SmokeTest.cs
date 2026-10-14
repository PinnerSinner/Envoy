using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Envoy.App.Controls;
using Envoy.App.Pages;
using Envoy.App.Services;
using Envoy.App.ViewModels;
using Envoy.App.Windows;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Envoy.Core.Translation;

namespace Envoy.App;

/// <summary>
/// `Envoy.exe --smoke-test [log-path] [--screenshots dir]`: builds every window and settings page under every palette,
/// then exits 0 (or 1 with the error in the log). CI runs this on a Windows runner so XAML resource and binding
/// mistakes fail the build. With `--screenshots <dir>` it also saves PNGs of each page and window under each palette.
/// </summary>
/// <remarks>
/// Three phases (contract §6): setup and once-only work; one pass per <see cref="ThemeManager.PaletteIds"/> entry
/// (pages, MainWindow-dependent checks, close, other windows, binding errors); teardown. Every section logs
/// <c>[smoke] {section} ok elapsed_ms={n}</c>. New windows and pages are exercised in the per-palette phase.
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

    public static bool IsRunning { get; private set; }

    public static async void Run(App app, string[] args)
    {
        IsRunning = true;
        _logPath = ArgValue(args, "--smoke-test") ?? Path.Combine(Environment.CurrentDirectory, "smoke-test.log");
        var shots = ArgValue(args, "--screenshots");
        if (shots is not null) Directory.CreateDirectory(shots);
        try { File.WriteAllText(_logPath, ""); }
        catch (IOException) { }

        var exitCode = 0;
        var total = Stopwatch.StartNew();
        string? root = null;
        AppHost? host = null;
        try
        {
            // ================= 1. Setup and once-only work =================
            var sw = Stopwatch.StartNew();
            root = Path.Combine(Path.GetTempPath(), $"envoy-smoke-{Guid.NewGuid():N}");
            var settingsPath = Path.Combine(root, "settings.json");
            host = new AppHost(settingsPath, app.Dispatcher);
            App.Host = host;
            Section("host", sw);

            Log("ocr languages: " + string.Join(", ", OcrService.InstalledLanguages().Select(l => l.WindowsTag)));
            Section("ocr-languages", sw);

            await WarmUpProviderAsync(host);
            Section("provider-warmup", sw);

            // Capture + OCR once on a small screen area (the runner's desktop).
            var image = ScreenCapturer.Capture(new ScreenRegion(0, 0, 320, 120), 2.0);
            ScreenCapturer.Preprocess(image, true, 1.3, false);
            var passes = await host.Ocr.RecognizeAsync(image);
            Log($"capture+ocr: {image.Width}x{image.Height}, {passes.Count} engines");
            Section("capture-ocr", sw);

            Check(PageIds.All.SequenceEqual(LegacyPageNames), $"PageIds.All is [{string.Join(", ", PageIds.All)}], expected the legacy names in order");
            Section("page-ids", sw);

            // Binding errors fail the run. Refresh() turns WPF tracing on in release builds; set the level after it.
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            // ================= 2. Per palette =================
            var first = true;
            foreach (var palette in ThemeManager.PaletteIds)
            {
                await RunPaletteAsync(host, settingsPath, palette, first, shots);
                first = false;
            }

            // ================= 3. Teardown =================
            sw.Restart();
            host.Dispose();
            host = null;
            if (shots is not null)
            {
                var expected = ThemeManager.PaletteIds.Count * PageIds.All.Count;
                var actual = Directory.GetFiles(shots, "*-settings-*.png").Length;
                Check(actual == expected, $"expected {expected} settings screenshots, found {actual}");
            }
            Section("teardown", sw);
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
            try { host?.Dispose(); }
            catch (Exception ex) { Warn($"host dispose failed: {ex.Message}"); }
            if (root is not null) DeleteTempRoot(root);
            Log($"[smoke] total elapsed_ms={total.ElapsedMilliseconds}");
            if (total.Elapsed > TotalBudget) Warn($"total {total.Elapsed.TotalSeconds:0} s exceeds the {TotalBudget.TotalSeconds:0} s budget");
        }

        app.Shutdown(exitCode);
    }

    private static async Task RunPaletteAsync(AppHost host, string settingsPath, string palette, bool first, string? shots)
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

    private static void Warn(string message) => Log($"[smoke] WARNING {message}");

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
