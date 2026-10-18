using System.Windows;
using System.Windows.Input;
using Envoy.App.Services;
using Envoy.Core.Capture;
using Envoy.Core.Languages;
using Envoy.Core.Ocr;
using Envoy.Core.Settings;
using Envoy.Core.Translation;

namespace Envoy.App.Windows;

/// <summary>One-shot "scan this part of the screen and translate it" popup (lobby names, map text, tooltips).</summary>
public partial class LookupWindow : Window
{
    private readonly AppHost _host;

    public LookupWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => { if (!SmokeTest.IsRunning && IsLoaded) Close(); };
    }

    /// <summary>Asks the user for a region, reads and translates it, and shows the result.</summary>
    public static async Task ScanAsync(AppHost host)
    {
        var region = await RegionSelectorWindow.SelectAsync(null, "Scan & translate",
            "Drag a box around any text on screen (names, map descriptions, menus) to translate it. Esc cancels.");
        if (region is null) return;

        var window = new LookupWindow(host);
        window.PlaceNear(region);
        window.Show();
        window.Activate();
        await window.RunAsync(region);
    }

    /// <summary>Shows a ready result (used by the smoke test and screenshots).</summary>
    public void ShowResult(string original, string translation, string? from)
    {
        Original.Text = original;
        Translation.Text = translation;
        Languages.Text = from is null ? "" : $"{LanguageCatalog.NameOf(from)} → {LanguageCatalog.NameOf(_host.Settings.MyLanguage)}";
        Status.Text = "";
    }

    private async Task RunAsync(ScreenRegion region)
    {
        Status.Text = "Reading…";
        try
        {
            var cfg = _host.Settings.Capture;
            var scale = ScreenCapturer.ScaleFor(region, cfg.Upscale);
            var image = await Task.Run(() =>
            {
                var img = ScreenCapturer.Capture(region, scale);
                ScreenCapturer.Preprocess(img, cfg.Grayscale, cfg.Contrast, cfg.Invert);
                return img;
            });
            var lines = OcrMerger.Merge(await _host.Ocr.RecognizeAsync(image), _host.Settings.MyLanguage);
            if (lines.Count == 0)
            {
                Status.Text = "No text found. Try a larger area, or install the OCR language (Reading Chat).";
                return;
            }

            var original = string.Join(Environment.NewLine, lines);
            Original.Text = original;
            Status.Text = "Translating…";
            var result = await _host.Translator.TranslateAsync(new TranslationRequest(original, _host.Settings.MyLanguage));
            ShowResult(original, result.Text, result.DetectedSourceLanguage);
        }
        catch (CaptureException)
        {
            Status.Text = "⚠ " + CaptureText.ScreenUnreadable;
        }
        catch (Exception ex) when (ex is TranslationException or InvalidOperationException or HttpRequestException)
        {
            Status.Text = "⚠ " + ex.Message;
        }
    }

    private void PlaceNear(ScreenRegion region)
    {
        // Region is in physical pixels; approximate DIPs with the primary monitor's scale.
        var scale = SystemParameters.PrimaryScreenWidth > 0 ? System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width / SystemParameters.PrimaryScreenWidth : 1.0;
        var area = SystemParameters.VirtualScreenWidth;
        Left = Math.Min(region.X / scale, SystemParameters.VirtualScreenLeft + area - Width - 20);
        Top = Math.Max(SystemParameters.VirtualScreenTop, (region.Y + region.Height) / scale + 8);
    }

    private void OnCopyOriginal(object sender, RoutedEventArgs e) => TryCopy(Original.Text);
    private void OnCopyTranslation(object sender, RoutedEventArgs e) => TryCopy(Translation.Text);

    private void TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Status.Text = ClipboardHelper.TrySetText(text) ? "Copied." : "Clipboard is busy. Try again.";
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
