using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Envoy.App.Services;
using Envoy.Core.Capture;
using Envoy.Core.Chat;
using Envoy.Core.Diagnostics;
using Envoy.Core.Languages;
using Envoy.Core.Ocr;
using Envoy.Core.Pipeline;
using Envoy.Core.Settings;
using Envoy.Core.Translation;

namespace Envoy.App;

/// <summary>How the rendered chat text is set off from the background, as games do.</summary>
public enum OcrTextEffect
{
    /// <summary>A 1.5 px dark outline around every glyph.</summary>
    Outline,

    /// <summary>A dark copy one pixel right and down.</summary>
    Shadow,
}

/// <summary>One chat line as drawn (<paramref name="Rendered"/>) and what chat understanding must make of it.</summary>
public sealed record OcrE2ELine(string Rendered, string Sender, string Text);

/// <summary>One OCR end-to-end case: lines in a font, size and effect, read with one OCR language.</summary>
public sealed record OcrE2ECase(string Id, string OcrLanguage, string Font, double SizePx, OcrTextEffect Effect,
    IReadOnlyList<OcrE2ELine> Lines, bool Required);

public enum OcrE2EOutcome
{
    Passed,
    Failed,

    /// <summary>The case's OCR language is not installed and the case is not required.</summary>
    SkippedNoRecognizer,
}

/// <summary>A case's outcome, the merged OCR lines and a one-line explanation.</summary>
public sealed record OcrE2EResult(string CaseId, OcrE2EOutcome Outcome, IReadOnlyList<string> Recognized, string Detail);

/// <summary>
/// The OCR end-to-end harness (REQ-QSC-44, contract §8.4; docs/specs/08b-ci-release.md §9). It renders known chat lines
/// the way a game shows them, runs them through the real capture-path enhancement, the real <c>Windows.Media.Ocr</c>,
/// <see cref="OcrMerger.MergeLines"/> and <see cref="ChatPipeline.Analyze"/>, and checks sender and text. It lives in the
/// app, next to <c>SmokeTest.cs</c>, so both <c>tests/Envoy.App.Tests</c> (every CI build) and
/// <c>Envoy.exe --ocr-e2e &lt;report.md&gt;</c> (the release pass on desktop Windows) run the same cases. Synthetic text
/// only; nothing is sent anywhere (the pipeline's translator throws if it is ever called).
/// </summary>
public static class OcrEndToEnd
{
    /// <summary>The start-up flag (contract §4 slot 3): <c>Envoy.exe --ocr-e2e &lt;report.md&gt;</c>.</summary>
    public const string Flag = "--ocr-e2e";

    /// <summary>The smallest <see cref="TextSimilarity.Similarity"/> of the normalised texts that counts as read.</summary>
    public const double MinSimilarity = 0.85;

    private static readonly OcrE2ELine[] EnglishLines =
    [
        new("[All] Henry: attack the gold at 3", "Henry", "attack the gold at 3"),
        new("[Team] Ada: need wood pls", "Ada", "need wood pls"),
        new("Bob_99: gg wp", "Bob_99", "gg wp"),
    ];

    /// <summary>
    /// Six required en-US cases (Segoe UI at 14, 16 and 18 px, outlined and shadowed), then one optional case each for
    /// ja-JP, zh-CN, ko-KR and ru-RU, which report <see cref="OcrE2EOutcome.SkippedNoRecognizer"/> when Windows has no
    /// recognizer for them.
    /// </summary>
    public static IReadOnlyList<OcrE2ECase> Cases { get; } = BuildCases();

    private static List<OcrE2ECase> BuildCases()
    {
        var cases = new List<OcrE2ECase>();
        foreach (var size in new[] { 14.0, 16.0, 18.0 })
        {
            foreach (var effect in new[] { OcrTextEffect.Outline, OcrTextEffect.Shadow })
            {
                cases.Add(new OcrE2ECase(
                    string.Create(CultureInfo.InvariantCulture, $"en-US-{size:0}px-{effect.ToString().ToLowerInvariant()}"),
                    "en-US", "Segoe UI", size, effect, EnglishLines, Required: true));
            }
        }
        cases.Add(Optional("ja-JP", "Yu Gothic UI", new("Taro: こんにちは、よろしく", "Taro", "こんにちは、よろしく")));
        cases.Add(Optional("zh-CN", "Microsoft YaHei UI", new("Wei: 我们去打金矿", "Wei", "我们去打金矿")));
        cases.Add(Optional("ko-KR", "Malgun Gothic", new("Minsu: 나무가 필요해", "Minsu", "나무가 필요해")));
        cases.Add(Optional("ru-RU", "Segoe UI", new("Ivan: нужно больше дерева", "Ivan", "нужно больше дерева")));
        return cases;

        static OcrE2ECase Optional(string language, string font, OcrE2ELine line) =>
            new($"{language}-16px-outline", language, font, 16, OcrTextEffect.Outline, [line], Required: false);
    }

    // Game-like colours: cream text on a dark, warm, slightly noisy panel.
    private static readonly Color TextColor = Color.FromRgb(240, 232, 214);
    private static readonly Color EdgeColor = Color.FromRgb(8, 6, 4);
    private static readonly Color PanelTop = Color.FromRgb(46, 38, 28);
    private static readonly Color PanelBottom = Color.FromRgb(24, 20, 16);
    private const int Padding = 12;
    private const int TextureCell = 4;

    /// <summary>
    /// UI (STA) thread. Draws the case's lines top to bottom as glyph geometry on a dark textured gradient, with a 1.5 px
    /// dark outline or a (+1,+1) shadow, at one pixel per DIP. Returns top-down BGRA as an exact-length copy. The same
    /// case always gives the same bytes.
    /// </summary>
    public static CapturedImage Render(OcrE2ECase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var typeface = new Typeface(new FontFamily(c.Font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var fill = Frozen(new SolidColorBrush(TextColor));
        var edge = Frozen(new SolidColorBrush(EdgeColor));
        var texts = c.Lines
            .Select(l => new FormattedText(l.Rendered, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
                c.SizePx, fill, pixelsPerDip: 1.0))
            .ToList();
        var lineHeight = Math.Ceiling(c.SizePx * 1.6);
        var width = (int)Math.Ceiling(texts.Max(t => t.WidthIncludingTrailingWhitespace)) + 2 * Padding;
        var height = (int)Math.Ceiling(lineHeight * texts.Count) + 2 * Padding;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var gradient = Frozen(new LinearGradientBrush(PanelTop, PanelBottom, 90));
            dc.DrawRectangle(gradient, null, new Rect(0, 0, width, height));
            DrawTexture(dc, width, height);

            var outline = Frozen(new Pen(edge, 3.0) { LineJoin = PenLineJoin.Round });
            for (var i = 0; i < texts.Count; i++)
            {
                var origin = new Point(Padding, Padding + i * lineHeight);
                var glyphs = texts[i].BuildGeometry(origin);
                if (c.Effect == OcrTextEffect.Outline)
                {
                    // The pen straddles the glyph edge; the fill drawn over it leaves 1.5 px of outline outside.
                    dc.DrawGeometry(null, outline, glyphs);
                }
                else
                {
                    dc.PushTransform(new TranslateTransform(1, 1));
                    dc.DrawGeometry(edge, null, glyphs);
                    dc.Pop();
                }
                dc.DrawGeometry(fill, null, glyphs);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Copy(bitmap);
    }

    /// <summary>
    /// UI thread. Applies the capture path's default enlargement (<see cref="RegionMath.OutputSize"/>) and
    /// <see cref="FrameProcessing.Preprocess"/> with default <see cref="CaptureOptions"/>, OCRs with
    /// <paramref name="c"/>'s language only, merges with <see cref="OcrMerger.MergeLines"/>(passes, "en"), and analyses
    /// with a default <see cref="ChatPipelineOptions"/>. Every expected line needs one message whose sender is equal
    /// (ordinal) and whose normalised text (<see cref="TextSimilarity.Normalize"/>) is at least
    /// <see cref="MinSimilarity"/> similar. A missing recognizer fails a required case and skips any other.
    /// </summary>
    public static async Task<OcrE2EResult> RunAsync(OcrE2ECase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = LanguageCatalog.Normalize(c.OcrLanguage);
        var installed = OcrService.InstalledLanguages().Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        var ocr = new OcrService();
        if (installed) ocr.Configure(code, [code]);
        // Configure falls back to the profile languages when nothing matches; that must not pass for this language.
        if (!installed || !ocr.ActiveLanguages.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            return new OcrE2EResult(c.Id, c.Required ? OcrE2EOutcome.Failed : OcrE2EOutcome.SkippedNoRecognizer, [],
                $"no Windows OCR recognizer for {c.OcrLanguage}");
        }

        var options = CaptureOptions.From(new AppSettings());
        var image = Enlarge(Render(c), options.Upscale);
        FrameProcessing.Preprocess(image.Pixels, options.Preprocess);
        var passes = await ocr.RecognizeAsync(image);
        var lines = OcrMerger.MergeLines(passes, "en");
        using var pipeline = new ChatPipeline(new RefusingTranslator(), new ChatPipelineOptions());
        var messages = pipeline.Analyze(lines);
        var recognized = lines.Select(l => l.Text).ToList();

        var misses = new List<string>();
        foreach (var expected in c.Lines)
        {
            var best = messages
                .Where(m => string.Equals(m.Sender, expected.Sender, StringComparison.Ordinal))
                .Select(m => (Message: m, Score: Score(m.Text, expected.Text)))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (best.Message is null)
                misses.Add($"no message from \"{expected.Sender}\"");
            else if (best.Score < MinSimilarity)
                misses.Add(string.Create(CultureInfo.InvariantCulture,
                    $"\"{expected.Sender}\": \"{best.Message.Text}\" is {best.Score:0.00} like \"{expected.Text}\""));
        }
        var size = string.Create(CultureInfo.InvariantCulture, $"{image.Width}x{image.Height} px");
        return misses.Count == 0
            ? new OcrE2EResult(c.Id, OcrE2EOutcome.Passed, recognized,
                string.Create(CultureInfo.InvariantCulture, $"{c.Lines.Count} of {c.Lines.Count} lines read ({size})"))
            : new OcrE2EResult(c.Id, OcrE2EOutcome.Failed, recognized,
                string.Join("; ", misses) + $" ({size}; OCR read: {string.Join(" / ", recognized)})");
    }

    /// <summary>The similarity of two texts after <see cref="TextSimilarity.Normalize"/>, as Envoy compares OCR readings.</summary>
    public static double Score(string actual, string expected) =>
        TextSimilarity.Similarity(TextSimilarity.Normalize(actual), TextSimilarity.Normalize(expected));

    /// <summary>How an outcome reads in the report.</summary>
    public static string Describe(OcrE2EOutcome outcome) => outcome switch
    {
        OcrE2EOutcome.Passed => "Passed",
        OcrE2EOutcome.Failed => "Failed",
        _ => "Skipped (no recognizer)",
    };

    /// <summary>One Markdown table row: <c>| case | outcome | detail |</c>.</summary>
    public static string ToMarkdownRow(OcrE2EResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"| {result.CaseId} | {Describe(result.Outcome)} | {result.Detail.Replace("|", "\\|", StringComparison.Ordinal)} |";
    }

    /// <summary>A Markdown table of <paramref name="results"/> under a header.</summary>
    public static string ToMarkdown(IEnumerable<OcrE2EResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var sb = new StringBuilder();
        sb.AppendLine("| case | outcome | detail |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var r in results) sb.AppendLine(ToMarkdownRow(r));
        return sb.ToString();
    }

    /// <summary>
    /// <c>Envoy.exe --ocr-e2e &lt;report.md&gt;</c> (REQ-QSC-46): runs every case on the UI thread, writes
    /// <see cref="ToMarkdown"/> to the path and shuts down with exit code 0 unless a required case failed. Runs before
    /// the single-instance mutex and uses no settings, data folder or network.
    /// </summary>
    public static async void Run(App app, string[] args, LogHub log)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(log);
        var ocrLog = log.For("OcrE2E");
        var index = Array.IndexOf(args, Flag);
        var report = index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : Path.Combine(Environment.CurrentDirectory, "ocr-e2e.md");
        var exitCode = 0;
        var results = new List<OcrE2EResult>();
        try
        {
            foreach (var c in Cases)
            {
                OcrE2EResult result;
                try
                {
                    result = await RunAsync(c);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    result = new OcrE2EResult(c.Id, OcrE2EOutcome.Failed, [], $"{ex.GetType().Name} while running the case");
                }
                results.Add(result);
                if (result.Outcome == OcrE2EOutcome.Failed && c.Required) exitCode = 1;
                ocrLog.Info("OCR end-to-end case", LogField.Of("case", c.Id), LogField.Of("outcome", result.Outcome.ToString()));
            }
            File.WriteAllText(report, "# Envoy OCR end-to-end\n\n" + ToMarkdown(results));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ocrLog.Error("Could not write the OCR end-to-end report", ex);
            exitCode = 1;
        }
        log.Flush();
        app.Shutdown(exitCode);
    }

    private static CapturedImage Enlarge(CapturedImage image, double upscale)
    {
        var (w, h, scale) = RegionMath.OutputSize(new ScreenRegion(0, 0, image.Width, image.Height), upscale, OcrService.MaxImageDimension);
        if (w == image.Width && h == image.Height) return image;
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        return Copy(scaled);
    }

    private static CapturedImage Copy(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return new CapturedImage(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    /// <summary>A fixed pattern of faint light and dark cells, so the panel is not one flat colour (no randomness).</summary>
    private static void DrawTexture(DrawingContext dc, int width, int height)
    {
        var shades = Enumerable.Range(0, 8)
            .Select(i => Frozen(new SolidColorBrush(i < 4 ? Color.FromArgb((byte)(6 + i * 4), 255, 255, 255)
                                                          : Color.FromArgb((byte)(6 + (i - 4) * 4), 0, 0, 0))))
            .ToArray();
        for (var y = 0; y < height; y += TextureCell)
        {
            for (var x = 0; x < width; x += TextureCell)
            {
                var hash = unchecked(((uint)(x * 73856093) ^ (uint)(y * 19349663)) * 2654435761u);
                dc.DrawRectangle(shades[hash >> 29], null, new Rect(x, y, TextureCell, TextureCell));
            }
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>The harness only analyses; a translation request means something is wired wrong.</summary>
    private sealed class RefusingTranslator : ITranslator
    {
        public string Id => "ocr-e2e";

        public bool IsMetered => false;

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The OCR end-to-end harness never translates.");
    }
}
