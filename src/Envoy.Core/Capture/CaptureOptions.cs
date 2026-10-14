using System.Collections.Frozen;
using Envoy.Core.Games;
using Envoy.Core.Settings;

namespace Envoy.Core.Capture;

/// <summary>
/// The immutable capture settings the watcher's loop reads (contract §7, REQ-CAP-42). AppHost builds one on the UI
/// thread in <c>ApplySettings</c> and <c>SetRegion</c> and publishes it with <c>Volatile.Write</c>; background code never
/// reads <see cref="AppSettings"/>. Every collection is copied, so later settings edits never change a snapshot.
/// </summary>
public sealed record CaptureOptions(
    string ForcedGame, bool OnlyWhileGameRuns,
    FrozenDictionary<string, ScreenRegion> Regions,
    FrozenDictionary<string, WindowRelativeRegion> WindowRegions,
    double Upscale, PreprocessOptions Preprocess, PollingOptions Polling,
    int OcrTimeoutMs, bool PauseWhenCovered, bool PauseWhenNotForeground,
    string Backend, string MyLanguage)
{
    public const int MinOcrTimeoutMs = 1000;
    public const int MaxOcrTimeoutMs = 30000;

    /// <summary>Copies every collection and clamps per REQ-CAP-16. UI thread only (it reads the live settings).</summary>
    public static CaptureOptions From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var c = settings.Capture;
        return new CaptureOptions(
            string.IsNullOrWhiteSpace(settings.Game) ? GameCatalog.Any : settings.Game,
            c.OnlyWhileGameRuns,
            Copy(c.Regions),
            Copy(c.WindowRegions),
            c.Upscale,
            new PreprocessOptions(c.Grayscale, c.Contrast, c.Invert),
            PollingOptions.From(c),
            Math.Clamp(c.OcrTimeoutMs, MinOcrTimeoutMs, MaxOcrTimeoutMs),
            c.PauseWhenCovered,
            c.PauseWhenNotForeground,
            string.IsNullOrWhiteSpace(c.Backend) ? CaptureBackendIds.Gdi : c.Backend,
            string.IsNullOrWhiteSpace(settings.MyLanguage) ? "en" : settings.MyLanguage);
    }

    /// <summary>The game whose region applies: the forced game unless "any", else the detected one, else "any" (as <c>AppHost.CurrentGameId</c>).</summary>
    public string GameIdFor(string? detectedGameId) =>
        ForcedGame != GameCatalog.Any ? ForcedGame : detectedGameId ?? GameCatalog.Any;

    /// <summary>v0.1's rule: read unless "only while a game runs" is on, no game is running and none is forced.</summary>
    public bool ShouldRead(bool gameRunning) => !OnlyWhileGameRuns || gameRunning || ForcedGame != GameCatalog.Any;

    /// <summary>A frozen copy without null entries (a hand-edited file can hold <c>"aoe2de": null</c>).</summary>
    private static FrozenDictionary<string, T> Copy<T>(Dictionary<string, T>? source) where T : class =>
        (source ?? []).Where(kv => kv.Value is not null).ToFrozenDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}
