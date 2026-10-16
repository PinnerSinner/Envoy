using Envoy.App.Services.Capture;
using Envoy.Core.Capture;
using Envoy.Core.Settings;

namespace Envoy.App.Services;

/// <summary>32-bit BGRA pixels, top-down.</summary>
public sealed record CapturedImage(byte[] Pixels, int Width, int Height)
{
    public int Stride => Width * 4;
}

/// <summary>
/// One-shot capture for Scan &amp; translate and the smoke test: a facade over <see cref="GdiCaptureBackend"/> and
/// <see cref="FrameProcessing"/>. The watcher uses the backend directly with pooled buffers (spec 05).
/// </summary>
public static class ScreenCapturer
{
    private static readonly FramePool OneShotPool = new();

    /// <summary>Copies <paramref name="region"/> at <paramref name="scale"/> into an exact-length image. Throws <see cref="CaptureException"/>.</summary>
    public static CapturedImage Capture(ScreenRegion region, double scale)
    {
        ArgumentNullException.ThrowIfNull(region);
        var w = Math.Max(1, (int)Math.Round(region.Width * scale));
        var h = Math.Max(1, (int)Math.Round(region.Height * scale));
        using var backend = new GdiCaptureBackend();
        using var frame = backend.Capture(new CaptureTarget(region, IntPtr.Zero, w, h), OneShotPool);
        return new CapturedImage(frame.ToArray(), frame.Width, frame.Height);
    }

    /// <summary>The v0.1 output size for <paramref name="region"/> with the user's enlargement (<see cref="RegionMath.OutputSize"/>).</summary>
    public static double ScaleFor(ScreenRegion region, double upscale) =>
        RegionMath.OutputSize(region, upscale, OcrService.MaxImageDimension).Scale;

    /// <summary>
    /// Makes chat text easier for OCR: greyscale, contrast stretch around mid-grey and optional inversion (Windows OCR
    /// prefers dark text on a light background; AoE chat is light text on dark).
    /// </summary>
    public static void Preprocess(CapturedImage image, bool grayscale, double contrast, bool invert)
    {
        ArgumentNullException.ThrowIfNull(image);
        FrameProcessing.Preprocess(image.Pixels, new PreprocessOptions(grayscale, contrast, invert));
    }

    /// <summary>Cheap fingerprint used to skip OCR when the chat area hasn't changed since the last frame.</summary>
    public static ulong Fingerprint(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return FrameProcessing.Analyze(image.Pixels).Hash;
    }
}
