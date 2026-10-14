using Envoy.App.Interop;
using Envoy.Core.Settings;

namespace Envoy.App.Services;

/// <summary>32-bit BGRA pixels, top-down.</summary>
public sealed record CapturedImage(byte[] Pixels, int Width, int Height)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Copies a rectangle of the screen with GDI. Works with games in windowed or borderless-windowed mode
/// (exclusive fullscreen bypasses the desktop compositor and captures as black).
/// </summary>
public static class ScreenCapturer
{
    public static CapturedImage Capture(ScreenRegion region, double scale)
    {
        var w = Math.Max(1, (int)Math.Round(region.Width * scale));
        var h = Math.Max(1, (int)Math.Round(region.Height * scale));

        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var bitmap = Native.CreateCompatibleBitmap(screenDc, w, h);
        var old = Native.SelectObject(memDc, bitmap);
        try
        {
            Native.SetStretchBltMode(memDc, Native.HALFTONE);
            if (!Native.StretchBlt(memDc, 0, 0, w, h, screenDc, region.X, region.Y, region.Width, region.Height, Native.SRCCOPY | Native.CAPTUREBLT))
                throw new InvalidOperationException("Screen capture failed.");

            var header = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            var pixels = new byte[w * h * 4];
            Native.SelectObject(memDc, old);
            if (Native.GetDIBits(memDc, bitmap, 0, (uint)h, pixels, ref header, Native.DIB_RGB_COLORS) == 0)
                throw new InvalidOperationException("Reading captured pixels failed.");
            old = IntPtr.Zero;
            return new CapturedImage(pixels, w, h);
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Makes chat text easier for OCR: grayscale, contrast stretch around mid-grey and optional inversion
    /// (Windows OCR prefers dark text on a light background; AoE chat is light text on dark).
    /// </summary>
    public static void Preprocess(CapturedImage image, bool grayscale, double contrast, bool invert)
    {
        var p = image.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            double b = p[i], g = p[i + 1], r = p[i + 2];
            if (grayscale)
            {
                var y = 0.299 * r + 0.587 * g + 0.114 * b;
                r = g = b = y;
            }
            if (contrast != 1.0)
            {
                r = (r - 128) * contrast + 128;
                g = (g - 128) * contrast + 128;
                b = (b - 128) * contrast + 128;
            }
            if (invert)
            {
                r = 255 - r;
                g = 255 - g;
                b = 255 - b;
            }
            p[i] = Clamp(b);
            p[i + 1] = Clamp(g);
            p[i + 2] = Clamp(r);
            p[i + 3] = 255;
        }
    }

    /// <summary>Cheap fingerprint used to skip OCR when the chat area hasn't changed since the last frame.</summary>
    public static ulong Fingerprint(CapturedImage image)
    {
        const ulong prime = 1099511628211;
        var hash = 14695981039346656037;
        var p = image.Pixels;
        // Sample every 7th pixel's green channel, quantised to ignore tiny shimmer from animated backgrounds.
        for (var i = 1; i < p.Length; i += 28)
        {
            hash ^= (ulong)(p[i] >> 3);
            hash *= prime;
        }
        return hash;
    }

    private static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
