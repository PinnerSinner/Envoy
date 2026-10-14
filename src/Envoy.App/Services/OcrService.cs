using Envoy.Core.Languages;
using Envoy.Core.Ocr;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace Envoy.App.Services;

/// <summary>Installed OCR language and whether Envoy is using it.</summary>
public sealed record OcrLanguageStatus(string Code, string Name, string WindowsTag, bool Installed);

/// <summary>
/// Wraps the OCR engine built into Windows 10/11. It is free, offline and fast, and supports every language the
/// user has the Windows "Optical character recognition" feature installed for (Settings → Time &amp; language).
/// </summary>
public sealed class OcrService
{
    private readonly List<(string Code, OcrEngine Engine)> _engines = [];
    private readonly object _gate = new();

    public IReadOnlyList<string> ActiveLanguages
    {
        get { lock (_gate) return _engines.Select(e => e.Code).ToList(); }
    }

    /// <summary>Every OCR language Windows has installed, mapped to Envoy codes.</summary>
    public static IReadOnlyList<OcrLanguageStatus> InstalledLanguages() =>
        OcrEngine.AvailableRecognizerLanguages
            .Select(l => new OcrLanguageStatus(LanguageCatalog.Normalize(l.LanguageTag), l.DisplayName, l.LanguageTag, true))
            .ToList();

    /// <summary>Status for every language Envoy knows, so the UI can say which ones still need installing.</summary>
    public static IReadOnlyList<OcrLanguageStatus> AllLanguageStatuses()
    {
        var installed = InstalledLanguages().ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
        return LanguageCatalog.All
            .Select(l => installed.TryGetValue(l.Code, out var i) ? i with { Name = l.DisplayName } : new OcrLanguageStatus(l.Code, l.DisplayName, l.OcrTag, false))
            .ToList();
    }

    /// <summary>Creates one engine per wanted language that's installed. Empty <paramref name="wanted"/> = all installed.</summary>
    public void Configure(string myLanguage, IEnumerable<string> wanted)
    {
        var wantedSet = new HashSet<string>(wanted.Select(LanguageCatalog.Normalize), StringComparer.OrdinalIgnoreCase);
        var useAll = wantedSet.Count == 0;
        wantedSet.Add(LanguageCatalog.Normalize(myLanguage));

        var engines = new List<(string, OcrEngine)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
        {
            var code = LanguageCatalog.Normalize(lang.LanguageTag);
            if (!useAll && !wantedSet.Contains(code)) continue;
            if (!seen.Add(code)) continue;
            if (OcrEngine.TryCreateFromLanguage(lang) is { } engine) engines.Add((code, engine));
        }

        // Fall back to the user's profile languages so OCR always works out of the box.
        if (engines.Count == 0 && OcrEngine.TryCreateFromUserProfileLanguages() is { } fallback)
            engines.Add((LanguageCatalog.Normalize(fallback.RecognizerLanguage.LanguageTag), fallback));

        lock (_gate)
        {
            _engines.Clear();
            _engines.AddRange(engines);
        }
    }

    public static uint MaxImageDimension => OcrEngine.MaxImageDimension;

    public async Task<IReadOnlyList<OcrPass>> RecognizeAsync(CapturedImage image)
    {
        List<(string Code, OcrEngine Engine)> engines;
        lock (_gate) engines = [.. _engines];
        if (engines.Count == 0) return [];

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(image.Pixels),
            BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);

        var passes = new List<OcrPass>(engines.Count);
        foreach (var (code, engine) in engines)
        {
            var result = await engine.RecognizeAsync(bitmap);
            var lines = result.Lines
                .Where(l => l.Words.Count > 0)
                .Select(l =>
                {
                    var top = l.Words.Min(w => w.BoundingRect.Top);
                    var bottom = l.Words.Max(w => w.BoundingRect.Bottom);
                    return new Core.Ocr.OcrLine(l.Text, top, bottom);
                })
                .ToList();
            passes.Add(new OcrPass(code, lines));
        }
        return passes;
    }
}
