using System.Runtime.InteropServices.WindowsRuntime;
using Envoy.Core.Capture;
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
public sealed class OcrService(TimeProvider? time = null)
{
    private readonly List<(string Code, OcrEngine Engine)> _engines = [];
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private SingleFlight _flight = new(time ?? TimeProvider.System); // replaced by Recover; guarded by _gate
    private (string MyLanguage, List<string> Wanted)? _configured;

    /// <summary>
    /// True while a watcher OCR pass runs, including one abandoned after its timeout (REQ-CAP-26): the watcher skips OCR
    /// until it really ends, or until <see cref="Recover"/> abandons it.
    /// </summary>
    public bool IsBusy => Flight.IsBusy;

    /// <summary>How long the running (or abandoned) watcher OCR pass has been going; zero when idle.</summary>
    public TimeSpan BusyFor => Flight.BusyFor;

    private SingleFlight Flight
    {
        get { lock (_gate) return _flight; }
    }

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
        var wantedList = wanted.ToList();
        lock (_gate) _configured = (myLanguage, wantedList);
        var wantedSet = new HashSet<string>(wantedList.Select(LanguageCatalog.Normalize), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Abandons a stuck watcher OCR pass (Risks "Abandoned OCR operations"): a fresh single-flight lets the next pass
    /// run, on engines recreated from the last <see cref="Configure"/>, so it never shares an engine with the hung call.
    /// The hung call keeps its own engines and clears only its old single-flight if it ever ends.
    /// </summary>
    public void Recover()
    {
        (string MyLanguage, List<string> Wanted)? configured;
        lock (_gate)
        {
            _flight = new SingleFlight(_time);
            configured = _configured;
        }
        if (configured is { } c) Configure(c.MyLanguage, c.Wanted);
    }

    /// <summary>
    /// The watcher's OCR pass (REQ-CAP-24..26): single-flight over all engines and bounded by <paramref name="timeout"/>,
    /// which cancels the WinRT operations. The pooled pixels are wrapped in a length-bounded <c>IBuffer</c> view (no
    /// managed copy) and copied into the bitmap before this returns, so <paramref name="frame"/> may be returned to its
    /// pool as soon as the call ends, even after a timeout. Throws <see cref="SingleFlightBusyException"/> while busy and
    /// <see cref="TimeoutException"/> on timeout.
    /// </summary>
    public Task<IReadOnlyList<OcrPass>> RecognizeAsync(FrameBuffer frame, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Flight.RunAsync(token =>
        {
            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(frame.Array.AsBuffer(0, frame.Length),
                BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Premultiplied);
            return RecognizeAndDisposeAsync(bitmap, token);
        }, timeout, ct);
    }

    /// <summary>One-shot OCR of a copied image (Scan &amp; translate, the smoke test). Not single-flight and not bounded.</summary>
    public Task<IReadOnlyList<OcrPass>> RecognizeAsync(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(image.Pixels),
            BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
        return RecognizeAndDisposeAsync(bitmap, CancellationToken.None);
    }

    private async Task<IReadOnlyList<OcrPass>> RecognizeAndDisposeAsync(SoftwareBitmap bitmap, CancellationToken ct)
    {
        using var owned = bitmap;
        List<(string Code, OcrEngine Engine)> engines;
        lock (_gate) engines = [.. _engines];
        if (engines.Count == 0) return [];

        var passes = new List<OcrPass>(engines.Count);
        foreach (var (code, engine) in engines)
        {
            var result = await engine.RecognizeAsync(owned).AsTask(ct);
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
