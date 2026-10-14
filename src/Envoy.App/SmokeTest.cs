using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Envoy.App.Services;
using Envoy.App.Windows;

namespace Envoy.App;

/// <summary>
/// `Envoy.exe --smoke-test [log-path]`: builds every window under every theme, then exits 0 (or 1 with the error
/// in the log). CI runs this on a Windows runner so XAML resource and binding mistakes fail the build.
/// With `--screenshots <dir>` it also saves PNGs of each window under each theme (used for the docs).
/// </summary>
internal static class SmokeTest
{
    public static bool IsRunning { get; private set; }

    public static async void Run(App app, string[] args)
    {
        IsRunning = true;
        var logPath = ArgValue(args, "--smoke-test") ?? Path.Combine(Environment.CurrentDirectory, "smoke-test.log");
        var shots = ArgValue(args, "--screenshots");
        if (shots is not null) Directory.CreateDirectory(shots);
        var log = new List<string>();
        var exitCode = 0;
        try
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), $"envoy-smoke-{Guid.NewGuid():N}", "settings.json");
            var host = new AppHost(settingsPath, app.Dispatcher);
            App.Host = host;
            log.Add("host created");

            log.Add("ocr languages: " + string.Join(", ", OcrService.InstalledLanguages().Select(l => l.WindowsTag)));

            foreach (var theme in ThemeManager.Themes)
            {
                host.Settings.Theme = theme.Id; // so a settings re-apply doesn't switch back to "match game"
                ThemeManager.Apply(theme.Id);
                var main = new MainWindow(host);
                main.Show();
                foreach (var page in main.PageNames) main.Navigate(page);
                await Pump();
                if (shots is not null)
                {
                    host.ShowDemoMessages();
                    foreach (var page in new[] { "Dashboard", "Translation", "Capture", "Appearance" })
                    {
                        main.Navigate(page);
                        await Pump();
                        Save(main, Path.Combine(shots, $"{theme.Id}-settings-{page.ToLowerInvariant()}.png"));
                    }
                }
                main.Close();

                var overlay = new OverlayWindow(host);
                overlay.Show();
                host.ShowDemoMessages();
                await Pump();
                if (shots is not null) Save(overlay, Path.Combine(shots, $"{theme.Id}-overlay.png"), Backdrop());
                overlay.ToggleEditMode();
                await Pump();
                overlay.ToggleEditMode();
                overlay.Close();

                var compose = new ComposeWindow(host);
                compose.Open(IntPtr.Zero);
                if (shots is not null)
                {
                    compose.Input.Text = "good game! one more?";
                    await Task.Delay(2500); // let the free provider answer for the preview
                }
                await Pump();
                if (shots is not null) Save(compose, Path.Combine(shots, $"{theme.Id}-reply.png"), Backdrop());
                compose.Close();

                var lookup = new LookupWindow(host);
                lookup.Show();
                lookup.ShowResult("[全部] 天下第一：这张地图的金矿在中间\n小心早期快攻", "[All] Number One Under Heaven: the gold on this map is in the middle\nWatch out for an early rush", "zh-CN");
                await Pump();
                if (shots is not null) Save(lookup, Path.Combine(shots, $"{theme.Id}-scan.png"), Backdrop());
                lookup.Close();

                var selector = new RegionSelectorWindow(new System.Drawing.Rectangle(0, 0, 800, 600), null);
                selector.Show();
                await Pump();
                selector.Close();

                log.Add($"theme {theme.Id}: ok");
            }

            // Capture + OCR pipeline once on a small screen area (the runner's desktop).
            var image = ScreenCapturer.Capture(new Core.Settings.ScreenRegion(0, 0, 320, 120), 2.0);
            ScreenCapturer.Preprocess(image, true, 1.3, false);
            var passes = await host.Ocr.RecognizeAsync(image);
            log.Add($"capture+ocr ok ({image.Width}x{image.Height}, {passes.Count} engines)");

            host.Dispose();
            log.Add("SMOKE TEST PASSED");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            log.Add("SMOKE TEST FAILED");
            log.Add(ex.ToString());
        }

        File.WriteAllLines(logPath, log);
        app.Shutdown(exitCode);
    }

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
