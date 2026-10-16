using Envoy.Core.Capture;
using Envoy.Core.Settings;

namespace Envoy.App.Services.Capture;

/// <summary>What to capture: a physical-pixel screen rectangle, scaled to the output size.</summary>
/// <param name="ScreenRect">The chat area on screen (physical pixels).</param>
/// <param name="Window">The game window, when known (a window-based backend would capture it directly).</param>
/// <param name="OutputWidth">Width of the captured frame after scaling.</param>
/// <param name="OutputHeight">Height of the captured frame after scaling.</param>
public sealed record CaptureTarget(ScreenRegion ScreenRect, IntPtr Window, int OutputWidth, int OutputHeight);

/// <summary>
/// A way of copying screen pixels (REQ-CAP-35). One watcher loop generation owns one instance and calls it from its
/// own thread only.
/// </summary>
public interface ICaptureBackend : IDisposable
{
    /// <summary>The <see cref="CaptureBackendIds"/> value of this backend.</summary>
    string Id { get; }

    /// <summary>True when the backend sees the game even under other windows (so "covered" never pauses).</summary>
    bool SupportsOccludedCapture { get; }

    /// <summary>A BGRA32 frame of the target from <paramref name="pool"/>; the caller disposes it. Throws <see cref="CaptureException"/>.</summary>
    FrameBuffer Capture(CaptureTarget target, FramePool pool);
}

/// <summary>Creates capture backends by id (REQ-CAP-35, 36). Only GDI exists today; anything else falls back to it.</summary>
public static class CaptureBackendFactory
{
    /// <summary>The backends this machine can use, with their labels for the "Capture method" list.</summary>
    public static IReadOnlyList<(string Id, string Label)> Available() => [(CaptureBackendIds.Gdi, CaptureText.BackendGdi)];

    /// <summary>The backend for <paramref name="id"/>; an unknown or unsupported id gives GDI (check <see cref="ICaptureBackend.Id"/>).</summary>
    public static ICaptureBackend Create(string? id) => id switch
    {
        CaptureBackendIds.Gdi => new GdiCaptureBackend(),
        _ => new GdiCaptureBackend(), // a Windows.Graphics.Capture backend (REQ-CAP-37, COULD) would be matched above
    };
}
