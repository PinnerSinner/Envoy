namespace Envoy.Core.Capture;

/// <summary>A captured frame's change fingerprint and the range of its sampled brightness (REQ-CAP-21, 40).</summary>
public readonly record struct FrameSignature(ulong Hash, byte MinLuma, byte MaxLuma)
{
    /// <summary>True when every sampled pixel has about the same brightness (a black or blank capture).</summary>
    public bool IsUniform(int tolerance = BlankFrames.LumaTolerance) => MaxLuma - MinLuma <= tolerance;
}

/// <summary>How a frame is enhanced before OCR (the Fine tuning settings).</summary>
public readonly record struct PreprocessOptions(bool Grayscale, double Contrast, bool Invert);

/// <summary>
/// Allocation-free pixel work on BGRA32 top-down frames (REQ-CAP-21, 22): the change fingerprint, taken on the raw
/// pixels before any enhancement, and the OCR enhancement itself.
/// </summary>
public static class FrameProcessing
{
    private const ulong FnvPrime = 1099511628211;
    private const ulong FnvOffset = 14695981039346656037;

    /// <summary>Bytes between fingerprint samples: every 7th pixel, as in v0.1.</summary>
    private const int SampleStride = 28;

    /// <summary>Fractional bits of the greyscale lookup index (4096 entries).</summary>
    private const int LumaFractionBits = 4;

    /// <summary>
    /// The v0.1 fingerprint (every 7th pixel's green channel quantised to 5 bits, FNV-1a) plus the minimum and maximum
    /// sampled green value. Allocates nothing.
    /// </summary>
    public static FrameSignature Analyze(ReadOnlySpan<byte> bgra)
    {
        var hash = FnvOffset;
        byte min = 255, max = 0;
        var any = false;
        for (var i = 1; i < bgra.Length; i += SampleStride)
        {
            var g = bgra[i];
            hash ^= (ulong)(g >> 3);
            hash *= FnvPrime;
            if (g < min) min = g;
            if (g > max) max = g;
            any = true;
        }
        return any ? new FrameSignature(hash, min, max) : new FrameSignature(hash, 0, 0);
    }

    /// <summary>
    /// Greyscale, contrast stretch around mid-grey and optional inversion, in place; alpha becomes 255. Matches the v0.1
    /// <c>double</c> algorithm within ±1 per channel: the per-pixel work is integer only, through a stack lookup table
    /// computed with the v0.1 formula (indexed by luma with 4 fractional bits in greyscale mode). Allocates nothing.
    /// </summary>
    public static void Preprocess(Span<byte> bgra, in PreprocessOptions options)
    {
        var length = bgra.Length - bgra.Length % 4;
        if (options.Grayscale)
        {
            const int size = (255 << LumaFractionBits) + 1;
            Span<byte> lut = stackalloc byte[size];
            for (var i = 0; i < size; i++) lut[i] = Map((double)i / (1 << LumaFractionBits), options);

            // Luma in 16.16 fixed point (0.299, 0.587, 0.114 to 5 decimals), rounded to 4 fractional bits.
            const int shift = 16 - LumaFractionBits;
            const int half = 1 << (shift - 1);
            for (var i = 0; i < length; i += 4)
            {
                var y = lut[(19595 * bgra[i + 2] + 38470 * bgra[i + 1] + 7471 * bgra[i] + half) >> shift];
                bgra[i] = y;
                bgra[i + 1] = y;
                bgra[i + 2] = y;
                bgra[i + 3] = 255;
            }
            return;
        }

        Span<byte> map = stackalloc byte[256];
        for (var i = 0; i < 256; i++) map[i] = Map(i, options);
        for (var i = 0; i < length; i += 4)
        {
            bgra[i] = map[bgra[i]];
            bgra[i + 1] = map[bgra[i + 1]];
            bgra[i + 2] = map[bgra[i + 2]];
            bgra[i + 3] = 255;
        }
    }

    /// <summary>The v0.1 per-channel formula for one value: contrast, then invert, then clamp and truncate.</summary>
    private static byte Map(double v, in PreprocessOptions options)
    {
        if (options.Contrast != 1.0) v = (v - 128) * options.Contrast + 128;
        if (options.Invert) v = 255 - v;
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}

/// <summary>Counts consecutive blank captures, the sign of an exclusive-fullscreen game (REQ-CAP-40).</summary>
public static class BlankFrames
{
    /// <summary>Blank frames in a row before the hint shows; the streak saturates here.</summary>
    public const int Threshold = 5;

    /// <summary>Largest sampled brightness range that still counts as blank.</summary>
    public const int LumaTolerance = 8;

    /// <summary>The streak after a frame with <paramref name="signature"/>.</summary>
    public static int Next(int streak, FrameSignature signature) =>
        signature.IsUniform() ? Math.Min(Math.Max(streak, 0) + 1, Threshold) : 0;
}
