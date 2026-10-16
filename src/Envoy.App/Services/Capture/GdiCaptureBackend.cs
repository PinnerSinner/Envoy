using System.Runtime.InteropServices;
using Envoy.App.Interop;
using Envoy.Core.Capture;

namespace Envoy.App.Services.Capture;

/// <summary>
/// Copies a rectangle of the screen with GDI (REQ-CAP-23). Works for games in windowed or borderless-windowed mode;
/// exclusive fullscreen bypasses the desktop compositor and captures as black, and a window covering the chat is
/// captured instead of the chat. The memory DC and bitmap are kept while the output size is unchanged; the screen DC is
/// taken and released per frame. Not thread-safe: one loop generation owns one instance.
/// </summary>
public sealed class GdiCaptureBackend : ICaptureBackend
{
    private IntPtr _memDc;
    private IntPtr _bitmap;
    private int _width;
    private int _height;
    private bool _disposed;

    public string Id => CaptureBackendIds.Gdi;

    public bool SupportsOccludedCapture => false;

    public FrameBuffer Capture(CaptureTarget target, FramePool pool)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pool);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rect = target.ScreenRect;
        if (rect.IsEmpty) throw new CaptureException("The capture rectangle is empty.");
        var w = Math.Max(1, target.OutputWidth);
        var h = Math.Max(1, target.OutputHeight);

        var screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new CaptureException("GetDC failed.");
        try
        {
            EnsureSurface(screenDc, w, h);
            var old = Native.SelectObject(_memDc, _bitmap);
            if (old == IntPtr.Zero) throw new CaptureException("SelectObject failed.");
            bool copied;
            try
            {
                // 0 means the mode could not be set; StretchBlt still works, with a lower-quality filter.
                _ = Native.SetStretchBltMode(_memDc, Native.HALFTONE);
                copied = Native.StretchBlt(_memDc, 0, 0, w, h, screenDc, rect.X, rect.Y, rect.Width, rect.Height,
                    Native.SRCCOPY | Native.CAPTUREBLT);
            }
            finally
            {
                // GetDIBits needs the bitmap deselected; restoring the DC's own bitmap cannot fail in practice.
                _ = Native.SelectObject(_memDc, old);
            }
            if (!copied) throw new CaptureException("StretchBlt failed.");

            var header = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            var buffer = pool.Rent(w, h);
            if (Native.GetDIBits(_memDc, _bitmap, 0, (uint)h, buffer.Array, ref header, Native.DIB_RGB_COLORS) != h)
            {
                buffer.Dispose();
                throw new CaptureException("GetDIBits failed.");
            }
            return buffer;
        }
        finally
        {
            _ = Native.ReleaseDC(IntPtr.Zero, screenDc); // 0 = not released; nothing more can be done
        }
    }

    private void EnsureSurface(IntPtr screenDc, int w, int h)
    {
        if (_memDc == IntPtr.Zero)
        {
            _memDc = Native.CreateCompatibleDC(screenDc);
            if (_memDc == IntPtr.Zero) throw new CaptureException("CreateCompatibleDC failed.");
        }
        if (_bitmap != IntPtr.Zero && _width == w && _height == h) return;
        DeleteBitmap();
        _bitmap = Native.CreateCompatibleBitmap(screenDc, w, h);
        if (_bitmap == IntPtr.Zero) throw new CaptureException("CreateCompatibleBitmap failed.");
        _width = w;
        _height = h;
    }

    private void DeleteBitmap()
    {
        if (_bitmap == IntPtr.Zero) return;
        _ = Native.DeleteObject(_bitmap); // false only for an invalid handle; nothing more can be done
        _bitmap = IntPtr.Zero;
        _width = _height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeleteBitmap();
        if (_memDc != IntPtr.Zero) _ = Native.DeleteDC(_memDc);
        _memDc = IntPtr.Zero;
    }
}
