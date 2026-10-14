# Capture Engine: window-relative, adaptive, self-healing chat capture

Status: Proposed

Area: `05-capture-engine`. This area covers how Envoy finds the chat pixels, when it reads them, how fast, and what it
does when something stalls. In code that means `ChatWatcher`, `ScreenCapturer`, `OcrService` (the call boundary only),
`GameDetector`, the region selector's coordinate maths and the `CaptureSettings` section of `AppSettings`. Out of scope:
OCR line merging and chat parsing (chat-understanding), logging sinks and diagnostics bundles (reliability-observability),
the page/view-model split of `MainWindow` (ui-foundation) and first-run region guidance (onboarding-ux-accessibility).

---

## Context

### What exists today

| Concern | Today | Where |
| --- | --- | --- |
| Loop | `ChatWatcher.RunAsync` runs on the thread pool. Each tick: `TickAsync`, then `Task.Delay(max(200, IntervalMs) - elapsed)`. The interval is fixed at `CaptureSettings.IntervalMs` (default 700 ms, slider 250–3000 on Reading Chat). | `src/Envoy.App/Services/ChatWatcher.cs` |
| Region | `CaptureSettings.Regions` is a `Dictionary<string, ScreenRegion>` keyed by game id. Each entry is an **absolute physical-pixel screen rectangle**. `ChatWatcher.CurrentRegion` returns the current game's entry, then `"any"`. `AppHost.SetRegion` writes the current game's entry and seeds `"any"` if it is missing. | `src/Envoy.Core/Settings/AppSettings.cs`, `ChatWatcher.cs`, `src/Envoy.App/AppHost.cs` |
| Capture | `ScreenCapturer.Capture` uses `GetDC(NULL)` + `CreateCompatibleDC/Bitmap` + `StretchBlt(HALFTONE, SRCCOPY\|CAPTUREBLT)` + `GetDIBits`. It creates and destroys the DC and bitmap **every frame** and allocates a **new `byte[w*h*4]` every frame**. A typical 600×200 region at 2× upscale is about 1.9 MB of LOH garbage per tick. | `src/Envoy.App/Services/ScreenCapturer.cs` |
| Change detection | `Preprocess` (greyscale/contrast/invert, double maths per pixel) runs **before** `Fingerprint` (FNV over every 7th pixel's green >> 3). Unchanged frames therefore still pay for preprocessing. `PublishFrames` (Live view open) bypasses the fingerprint entirely, so with the Live view open every frame is OCR'd. | `ScreenCapturer.cs`, `ChatWatcher.TickAsync` |
| Game awareness | `GameDetector.Find()` is polled every 3 s on the UI thread (`AppHost._gameTimer`) and returns `RunningGame(Profile, ProcessId, MainWindow)`. The watcher only uses it through `ShouldRead` (game running or game forced), which sets `WatcherState.Idle`. Minimised, covered or background game windows are still captured and OCR'd. | `src/Envoy.App/Services/GameDetector.cs`, `AppHost.cs` |
| OCR | `OcrService.RecognizeAsync(CapturedImage)` copies the array into an `IBuffer` (`CryptographicBuffer.CreateFromByteArray`), creates a `SoftwareBitmap` and awaits each engine in turn. It has **no timeout and no cancellation**. | `src/Envoy.App/Services/OcrService.cs` |
| Failure handling | Exceptions in a tick set `WatcherState.Error` + `LastError`. A hung OCR call or blocked GDI call stops the loop silently. Nothing notices and nothing restarts it. | `ChatWatcher.cs` |
| DPI | The manifest declares PerMonitorV2. `RegionSelectorWindow` converts DIPs to pixels by multiplying with `VisualTreeHelper.GetDpi(this)`. That value can be stale right after the window is moved to a monitor with a different scale. `OverlayWindow.Reposition` divides the physical region by **the overlay's own** DPI, which is wrong when the overlay and the chat are on monitors with different scaling. | `src/Envoy.App/app.manifest`, `Windows/RegionSelectorWindow.xaml.cs`, `Windows/OverlayWindow.xaml.cs` |
| Metrics | The Live view header shows `"{n} lines · OCR {ms} ms"` for published frames only. | `Windows/MainWindow.xaml.cs` (`OnFrame`), `MainWindow.xaml` (`PreviewStats`) |
| Other callers | `LookupWindow.RunAsync` (Scan & translate) and `SmokeTest.cs` call `ScreenCapturer.Capture/Preprocess` directly. `OverlayWindow`, `MainViewModel.RegionText/HasRegion` and `App.SelectRegion` read `Watcher.CurrentRegion`. | `Windows/LookupWindow.xaml.cs`, `SmokeTest.cs`, `ViewModels/MainViewModel.cs`, `App.xaml.cs` |
| Tests | Nothing in the capture path is unit tested. The smoke test captures `(0,0,320,120)` once and OCRs it. | `tests/Envoy.Core.Tests`, `SmokeTest.cs` |

### What is weak or missing for production

1. **Moving the game window breaks Envoy.** If the user drags a windowed game, changes resolution or moves the game
   to another monitor, Envoy keeps reading the old screen rectangle. It translates whatever is now there, or reads
   nothing, and the overlay stays anchored to the wrong place. Users will report this as "it stopped working".
2. **Wasted work and false reads.** Envoy reads a minimised game's region, which now shows the desktop. When a browser
   or Discord covers the chat area, Envoy OCRs that content and pushes it through the translator, possibly sending
   unrelated on-screen text to a paid provider. This also breaks the "chat goes only where you chose" spirit for
   non-chat text.
3. **CPU and GC cost while gaming.** A fixed 700 ms loop with per-frame LOH allocations, per-pixel `double` maths before
   the change check, and full DC/bitmap churn is noticeable on low-end machines running a CPU-heavy RTS.
4. **No self-healing.** One hung `OcrEngine.RecognizeAsync` freezes chat reading for the rest of the session, and the
   status line still says "Reading chat".
5. **Mixed-DPI setups** (a 150% laptop panel plus a 100% external monitor, which is common) can give an off-by-scale
   selection or a misplaced overlay.
6. **GDI is the only path.** GDI cannot see a window that is covered or minimised. There is no seam to add
   `Windows.Graphics.Capture` later.
7. **Nothing to tune with.** Users and support cannot see whether Envoy is skipping frames, how slow OCR is, or why it
   is not reading.

---

## Goals / Non-goals

### Goals

- Store the chat region **relative to the game window's client area**. Resolve it every tick so window moves,
  resolution changes and monitor switches keep working. Fall back to the existing absolute regions when there is no
  game window. Existing `settings.json` files must keep working **without a migration step**.
- **Pause** (no capture, no OCR, no translation) while the game is minimised or the chat area is covered by another
  app's window. Optionally pause when the game is not in the foreground.
- **Adaptive polling**: read quickly right after chat changes and back off while it is static, with configurable
  min/max and a CPU duty-cycle cap.
- **Allocation-free steady state** for capture, fingerprint and preprocessing: pooled pixel buffers, reused GDI
  objects, span-based maths, and a fingerprint taken on raw pixels **before** preprocessing.
- **OCR timeout, single-flight OCR, and a watchdog** that restarts a stalled loop within a restart budget. The loop
  reports a health state.
- **Per-monitor DPI correctness** in region selection, region display and overlay anchoring.
- An **`ICaptureBackend` seam** with GDI as default and a `Capture.Backend` setting, so Windows.Graphics.Capture can be
  added.
- **Metrics** (average capture ms, average OCR ms, frames captured/skipped, timeouts, restarts, current interval,
  backend, health, pause reason) shown on the Reading Chat Live view.
- All pure logic in `Envoy.Core/Capture/` with xUnit tests. The Win32 glue is exercised by the Windows smoke test.

### Non-goals

- Shipping a working Windows.Graphics.Capture backend is a COULD (REQ-CAP-37), not a MUST. Its D3D11 interop cannot be
  verified on Linux and is too large for this sitting alongside the rest.
- Reading exclusive-fullscreen games. GDI captures them as black. We only **detect and explain** this (REQ-CAP-40).
- Any form of game process access beyond what exists today (process-name enumeration). No `OpenProcess` for memory,
  no DLL injection, no hooks, no reading game files.
- Changing OCR merging, chat parsing or translation.
- Auto-detecting the chat region from pixels. Envoy does not know the games' chat colours or layouts. The user still
  selects it.
- A log sink. If reliability-observability's `ILog` (REQ-REL-1) has landed, the watcher writes to it (see Design).
  Otherwise it only exposes `LastError` and metrics.

---

## Requirements

### Window-relative region

- **REQ-CAP-1 (MUST)** When the user selects a region while a game window is detected, and the rectangle lies inside
  that window's client area (2 px tolerance), Envoy stores it in `Capture.WindowRegions[gameId]` as a
  `WindowRelativeRegion`. It also keeps the absolute rectangle in `Capture.Regions[gameId]` as the fallback.
- **REQ-CAP-2 (MUST)** Each tick re-reads the game window's client rectangle and resolves the relative region against
  it. When the client size is unchanged, moving the window by (dx, dy) moves the captured rectangle by exactly
  (dx, dy).
- **REQ-CAP-3 (MUST)** When the client size differs from the size recorded at selection, the region is scaled
  proportionally on each axis and clamped to the client rectangle.
- **REQ-CAP-4 (MUST)** Without a game window or a relative region, the region resolves to `Regions[gameId]`, then
  `Regions["any"]`, exactly as today. A settings file written by v0.1 loads and captures the same rectangle with no
  migration step.
- **REQ-CAP-5 (MUST)** A resolved region that intersects no monitor pauses capture with `PauseReason.RegionOffScreen`
  instead of capturing black pixels.
- **REQ-CAP-6 (SHOULD)** If a game window is detected, the current game has only an absolute region, and that region
  lies fully inside the client area, Envoy derives and saves the relative region once. This silently upgrades v0.1
  users.
- **REQ-CAP-7 (SHOULD)** When the resolved region changes (because the window moved), `ChatWatcher.RegionChanged`
  fires. An overlay anchored to the chat repositions on the next UI dispatch.
- **REQ-CAP-8 (SHOULD)** The Reading Chat region row says whether the region "follows the {game} window" or is a
  "fixed screen position". When the current client size differs from the reference size, it shows a re-select hint.

### Pausing

- **REQ-CAP-9 (MUST)** While the game window is minimised (`IsIconic`), the watcher neither captures nor runs OCR,
  reports `PauseReason.GameMinimized`, and resumes within one paused interval (≤ 1 s) after restore.
- **REQ-CAP-10 (MUST)** Pause with `PauseReason.ChatCovered` when all of these hold: `Capture.PauseWhenCovered` is true
  (default true), the active backend cannot see occluded windows, the region is window-relative, and at least 3 of the 9
  sample points in the region hit a top-level window that is neither the game nor an Envoy window.
- **REQ-CAP-11 (SHOULD)** When `Capture.PauseWhenNotForeground` is true (default false), the watcher pauses with
  `PauseReason.GameNotForeground` while the foreground window belongs to neither the game nor Envoy.
- **REQ-CAP-12 (MUST)** While paused, the loop wakes every `PausedIntervalMs` (1000 ms), does no capture or OCR, and
  `AppHost.CaptureStatus` names the reason in plain language.
- **REQ-CAP-13 (MUST)** While the Live view is open (`PublishFrames`), the pause reasons `NoGame`, `GameNotForeground`
  and `ChatCovered` are overridden so the user can tune. `NoRegion`, `RegionOffScreen` and `GameMinimized` are not.

### Adaptive polling

- **REQ-CAP-14 (MUST)** With `Capture.AdaptivePolling` on (default), a changed frame sets the interval to
  `MinIntervalMs` (default 300). The interval stays there for the next `FastFramesAfterChange` (3) unchanged ticks,
  then multiplies by 1.5 per unchanged tick up to `MaxIntervalMs` (default 1500).
- **REQ-CAP-15 (MUST)** With `AdaptivePolling` off, the interval is the fixed `IntervalMs` (existing setting, default
  700).
- **REQ-CAP-16 (MUST)** Polling settings are clamped when read: `MinIntervalMs` to [150, 2000], `MaxIntervalMs` to
  [Min, 5000], `IntervalMs` to [150, 5000].
- **REQ-CAP-17 (SHOULD)** The delay after a tick is never less than the tick's own work time (≤ 50% duty cycle) and
  never less than 50 ms.
- **REQ-CAP-18 (MUST)** `Invalidate()` (called on settings change, new match and region set) resets the interval to
  the minimum and forces the next captured frame through OCR.
- **REQ-CAP-19 (MUST)** Consecutive failed ticks double the interval, starting from Min, up to 5000 ms. The first
  successful tick returns to normal adaptive behaviour.

### Allocation and throughput

- **REQ-CAP-20 (MUST)** Pixel buffers are rented from a dedicated `FramePool` (`ArrayPool<byte>`) and returned at the
  end of each tick. In steady state, managed allocation for capture + fingerprint + preprocess is under 16 KB per frame,
  excluding the first rent.
- **REQ-CAP-21 (MUST)** `FrameProcessing.Analyze` (fingerprint) and `FrameProcessing.Preprocess` work on spans and
  allocate nothing. The fingerprint is taken on **raw** pixels before preprocessing, and unchanged frames skip both
  preprocessing and OCR, including while the Live view is open.
- **REQ-CAP-22 (MUST)** `FrameProcessing.Preprocess` output matches the v0.1 algorithm (`ScreenCapturer.Preprocess`)
  within ±1 per channel for any combination of greyscale, contrast in [0.5, 3] and invert.
- **REQ-CAP-23 (SHOULD)** The GDI backend reuses its memory DC and DIB section while the output size is unchanged, and
  recreates them only when the size changes.
- **REQ-CAP-24 (SHOULD)** OCR receives the pooled pixels through a length-bounded `IBuffer` view (`AsBuffer(0,
  length)`), with no intermediate managed copy.

### OCR timeout, watchdog, health

- **REQ-CAP-25 (MUST)** Every OCR call is bounded by `Capture.OcrTimeoutMs` (default 4000). A timeout increments
  `OcrTimeouts`, sets `LastError`, counts as a failed tick and does not stop the loop.
- **REQ-CAP-26 (MUST)** OCR is single-flight. While a timed-out OCR call is still running, later ticks do not start
  another one. They count as `OcrBusySkips` and leave the fingerprint unset so the frame is retried.
- **REQ-CAP-27 (MUST)** A watchdog restarts the loop on a new capture backend instance when no heartbeat arrives for
  `max(15 s, 2 × OcrTimeoutMs + MaxIntervalMs)`.
- **REQ-CAP-28 (MUST)** After more than 3 restarts within 5 minutes, the watcher stops in `WatcherState.Error` with
  "Chat reading stopped responding. Toggle capture to retry." A manual `Start()` resets the budget.
- **REQ-CAP-29 (MUST)** `ChatWatcher.Health` is `Paused` when a pause reason is set, `Failing` after 3 consecutive
  failed ticks or a watchdog give-up, `Degraded` if an OCR timeout or restart happened in the last 60 s, and `Healthy`
  otherwise.
- **REQ-CAP-30 (MUST)** No capture, OCR or Win32 exception escapes the loop. 3 consecutive failures set
  `WatcherState.Error` with a user-readable `LastError`. One successful tick clears it.

### DPI

- **REQ-CAP-31 (MUST)** `RegionSelectorWindow` converts the dragged rectangle to physical screen pixels with
  `Visual.PointToScreen` on both corners. This is accurate within ±1 px on every monitor at any scale, including
  non-primary monitors whose DPI differs from the primary's.
- **REQ-CAP-32 (MUST)** `RegionSelectorWindow` draws the existing region using `Visual.PointFromScreen` on both corners.
- **REQ-CAP-33 (SHOULD)** `OverlayWindow.Reposition` computes the anchored position in physical pixels from the
  resolved region and the overlay's physical size, then places the window with `SetWindowPos`. This makes it correct
  when the overlay and the chat are on monitors with different DPI.
- **REQ-CAP-34 (MUST)** The capture engine uses physical pixels for all coordinates (client rects, sample points,
  blits). At loop start, the watcher verifies that its thread's DPI awareness context is PerMonitorV2. If it is not, it
  reports `LastError` "Display scaling awareness is off; capture may be misaligned" (once).

### Backend abstraction

- **REQ-CAP-35 (MUST)** Capture goes through `ICaptureBackend`. `GdiCaptureBackend` is the default and is selected by
  `Capture.Backend = "gdi"`.
- **REQ-CAP-36 (MUST)** An unknown or unsupported backend id falls back to GDI, and `Metrics.Backend` reports the
  backend actually in use.
- **REQ-CAP-37 (COULD)** A `WgcCaptureBackend` (Windows.Graphics.Capture, window capture) is selectable when
  `GraphicsCaptureSession.IsSupported()` is true. It reports `SupportsOccludedCapture = true`, so the cover check is
  skipped for it.

### Metrics and observability

- **REQ-CAP-38 (MUST)** `ChatWatcher.Metrics` returns a snapshot with: average capture ms and average OCR ms (last 50
  samples), frames captured, frames skipped as unchanged, OCR runs, OCR timeouts, OCR-busy skips, restarts, current
  interval, backend id, health and pause reason.
- **REQ-CAP-39 (MUST)** The Live view header shows the metrics line and the health/pause line. It refreshes at least
  once per second while the page is visible, including while every frame is skipped.
- **REQ-CAP-40 (SHOULD)** When 5 consecutive captured raw frames are uniform (sampled luma range ≤ 8) and not paused,
  the status shows "The chat area is blank — exclusive fullscreen can't be read; use borderless or windowed mode, or
  re-select the area."
- **REQ-CAP-41 (MUST)** The capture engine adds no network calls and no process access beyond today's process-name
  enumeration and user32 window queries (`GetClientRect`, `ClientToScreen`, `IsIconic`, `WindowFromPoint`,
  `GetAncestor`, `GetWindowThreadProcessId`, `EnumWindows`, `MonitorFromRect`).

---

## Design

### Overview

```
                   ┌──────────── ChatWatcher (thread pool loop, generation N) ───────────────┐
 RunningGame? ───► │ GameWindowTracker.Observe ─► RegionMath.ResolveForGame ─► CaptureGate  │
 (AppHost, 3 s)    │      (user32)                    (Core)                   (Core)      │
                   │        │ paused? ──► AdaptivePollPolicy.Next(Paused)                   │
                   │        ▼ capture                                                      │
                   │ ICaptureBackend.Capture(target, FramePool) ─► FrameBuffer (pooled)     │
                   │        ▼                                                              │
                   │ FrameProcessing.Analyze(raw) ─ unchanged? ─► skip (metrics, policy)    │
                   │        ▼ changed                                                      │
                   │ FrameProcessing.Preprocess ─► OcrService.RecognizeAsync (timeout,      │
                   │                                single-flight) ─► OcrMerger ─► pipeline │
                   │ heartbeat ◄──────────── LoopWatchdog (Timer, 2 s) ── restart / give up │
                   └────────────────────────────────────────────────────────────────────────┘
```

### Envoy.Core: new folder `src/Envoy.Core/Capture/`

All types are public, `net8.0`, and have no Windows dependency.

**`WindowRelativeRegion`** goes in `src/Envoy.Core/Settings/AppSettings.cs`, next to `ScreenRegion`, because it is
persisted.

```csharp
/// <summary>Chat region as an offset inside a game window's client area, plus the client size when it was chosen.</summary>
public sealed record WindowRelativeRegion(int OffsetX, int OffsetY, int Width, int Height, int ClientWidth, int ClientHeight)
{
    public bool IsEmpty => Width <= 0 || Height <= 0 || ClientWidth <= 0 || ClientHeight <= 0;
}
```

**`RegionMath.cs`**

```csharp
namespace Envoy.Core.Capture;

public enum RegionSource { None, WindowRelative, GameAbsolute, AnyAbsolute }

public sealed record RegionResolution(ScreenRegion? Region, RegionSource Source, bool ClientSizeDiffers);

public static class RegionMath
{
    public const int EdgeTolerancePx = 2;

    /// <summary>Null when <paramref name="absolute"/> leaves <paramref name="client"/> by more than the tolerance.
    /// Within tolerance the result is clamped to the client.</summary>
    public static WindowRelativeRegion? ToWindowRelative(ScreenRegion absolute, ScreenRegion client);

    /// <summary>Same client size: pure translation. Otherwise proportional scale per axis (rounded), then clamped to client.</summary>
    public static ScreenRegion Resolve(WindowRelativeRegion relative, ScreenRegion client);

    /// <summary>Order: WindowRegions[gameId] (only when <paramref name="gameClient"/> is known) → Regions[gameId] → Regions["any"].</summary>
    public static RegionResolution ResolveForGame(CaptureSettings settings, string gameId, ScreenRegion? gameClient);

    public static ScreenRegion? Intersect(ScreenRegion a, ScreenRegion b);
    public static bool Contains(ScreenRegion outer, ScreenRegion inner, int tolerance = 0);

    /// <summary>The v0.1 formula: upscale clamped to [1, 4] and limited so neither side exceeds maxDimension.</summary>
    public static (int Width, int Height, double Scale) OutputSize(ScreenRegion source, double upscale, uint maxDimension);

    /// <summary>gridSize × gridSize points, inset 2 px, inside the region (for cover detection).</summary>
    public static IReadOnlyList<(int X, int Y)> SamplePoints(ScreenRegion region, int gridSize = 3);
}
```

`SamplePoints` allocates a small list. It runs only on ticks where the cover check applies, which is acceptable. It is
not part of REQ-CAP-20's budget (capture + fingerprint + preprocess).

**`FramePool.cs`**

```csharp
public sealed class FramePool
{
    /// <summary>Dedicated pool (ArrayPool.Create(maxArrayLength: 64 MiB, maxArraysPerBucket: 2)) so reuse is deterministic
    /// and retention is bounded.</summary>
    public FramePool();
    public FrameBuffer Rent(int width, int height);
}

/// <summary>BGRA32, top-down, stride = width*4. The array may be longer than Length.</summary>
public sealed class FrameBuffer : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public int Length => Stride * Height;
    public byte[] Array { get; }            // for P/Invoke (GetDIBits) and AsBuffer(0, Length)
    public Span<byte> Pixels { get; }       // Array.AsSpan(0, Length)
    public byte[] ToArray();                // exact-length copy (Live view, Scan & translate)
    public void Dispose();                  // returns to pool; idempotent
}
```

**`FrameProcessing.cs`**

```csharp
public readonly record struct FrameSignature(ulong Hash, byte MinLuma, byte MaxLuma)
{
    public bool IsUniform(int tolerance = 8) => MaxLuma - MinLuma <= tolerance;
}

public readonly record struct PreprocessOptions(bool Grayscale, double Contrast, bool Invert);

public static class FrameProcessing
{
    /// <summary>Same sampling as v0.1 (every 7th pixel, green >> 3, FNV-1a) plus the min/max sampled green as a luma
    /// proxy. Allocation-free.</summary>
    public static FrameSignature Analyze(ReadOnlySpan<byte> bgra);

    /// <summary>LUT-based (stackalloc byte[256]) greyscale → contrast → invert. Matches the v0.1 double formula within ±1.
    /// Allocation-free. Sets alpha to 255.</summary>
    public static void Preprocess(Span<byte> bgra, in PreprocessOptions options);
}
```

Greyscale uses integer luma `(77*r + 150*g + 29*b + 128) >> 8`. The 256-entry LUT folds in contrast and invert. Both
the LUT and the integer luma are why the tolerance is ±1 rather than exact.

**`AdaptivePollPolicy.cs`**

```csharp
public enum FrameOutcome { Changed, Unchanged, Paused, Failed }

public sealed record PollingOptions(
    int MinIntervalMs, int MaxIntervalMs,
    double BackoffFactor = 1.5, int FastFramesAfterChange = 3,
    int PausedIntervalMs = 1000, int MaxErrorIntervalMs = 5000,
    double MaxDutyCycle = 0.5, int FloorMs = 50)
{
    /// <summary>Clamps per REQ-CAP-16. AdaptivePolling=false ⇒ Min = Max = IntervalMs.</summary>
    public static PollingOptions From(CaptureSettings settings);
}

public sealed class AdaptivePollPolicy(PollingOptions options)
{
    public PollingOptions Options { get; set; }      // swapped on settings apply (keeps Current, re-clamped)
    public TimeSpan Current { get; }
    public TimeSpan Next(FrameOutcome outcome);       // rules of REQ-CAP-14/15/19, Paused ⇒ PausedIntervalMs (Current untouched)
    public void Reset();                              // Current = Min, fast counter = FastFramesAfterChange
    /// <summary>max(interval − work, work × (1 − duty) / duty, Floor).</summary>
    public TimeSpan DelayAfter(TimeSpan interval, TimeSpan workTime);
}
```

**`CaptureGate.cs`**

```csharp
public enum PauseReason { None, NoRegion, NoGame, GameMinimized, GameNotForeground, ChatCovered, RegionOffScreen }
public enum SampleOwner { Game, Envoy, Other }

public sealed record CaptureObservation(
    bool HasRegion, bool RegionOnScreen, bool GameRunning,
    bool WindowFound, bool Minimized, bool GameForeground, bool EnvoyForeground,
    RegionSource Source, IReadOnlyList<SampleOwner>? Samples);

public sealed record CaptureGateOptions(bool PauseWhenCovered, bool PauseWhenNotForeground,
    bool BackendSeesOccluded, bool PreviewOpen, int CoveredSampleThreshold = 3);

public static class CaptureGate
{
    /// <summary>Precedence: NoRegion → RegionOffScreen → GameMinimized → (preview open ⇒ None) → NoGame →
    /// GameNotForeground → ChatCovered → None. The cover check runs only for Source == WindowRelative.</summary>
    public static PauseReason Evaluate(CaptureObservation observation, CaptureGateOptions options);

    public static int CountCovered(IReadOnlyList<SampleOwner> samples);   // Other only; Envoy and Game never count
}
```

**`LoopWatchdog.cs`**

```csharp
public enum WatchdogVerdict { Healthy, Stalled, GiveUp }

public sealed class LoopWatchdog(TimeProvider clock, TimeSpan stallAfter, int maxRestarts = 3, TimeSpan? restartWindow = null /* 5 min */)
{
    public TimeSpan StallAfter { get; set; }
    public void Heartbeat();                 // thread-safe (Interlocked on ticks)
    public WatchdogVerdict Check();          // Stalled when now − lastBeat > StallAfter; GiveUp when the restart budget is spent
    public void RecordRestart();             // also counts as a heartbeat
    public void ResetBudget();               // manual Start()
    public int Restarts { get; }             // lifetime count, for metrics
    public static TimeSpan StallThreshold(int ocrTimeoutMs, int maxIntervalMs); // max(15 s, 2×ocr + max)
}
```

**`CaptureMetrics.cs`**

```csharp
public enum CaptureHealth { Healthy, Paused, Degraded, Failing }

public sealed record CaptureMetricsSnapshot(
    double AvgCaptureMs, double AvgOcrMs,
    long FramesCaptured, long FramesSkipped, long OcrRuns, long OcrTimeouts, long OcrBusySkips,
    long Restarts, int ConsecutiveFailures, TimeSpan CurrentInterval,
    string Backend, CaptureHealth Health, PauseReason PauseReason)
{
    public double SkipRate => FramesCaptured == 0 ? 0 : (double)FramesSkipped / FramesCaptured;
    /// <summary>"capture 4 ms · OCR 85 ms · skipped 37/40 (93%) · every 0.9 s · gdi" (invariant culture).</summary>
    public string ToStatsLine();
}

public sealed class CaptureMetrics(TimeProvider? clock = null, int window = 50)
{
    public void RecordCapture(TimeSpan elapsed, bool changed);
    public void RecordOcr(TimeSpan elapsed);
    public void RecordOcrTimeout();
    public void RecordOcrBusy();
    public void RecordFailure();
    public void RecordSuccess();                 // resets ConsecutiveFailures
    public void RecordRestart();
    public void SetGaveUp(bool gaveUp);
    public void SetInterval(TimeSpan interval);
    public void SetBackend(string id);
    public void SetPause(PauseReason reason);
    public CaptureMetricsSnapshot Snapshot();    // Health per REQ-CAP-29
    public void Reset();
}
```

It is thread-safe through a single `lock`. Averages use two fixed `double[window]` rings allocated in the constructor.

**Settings (`CaptureSettings` in `src/Envoy.Core/Settings/AppSettings.cs`, shared file).** These additions are
additive. Missing JSON properties take the defaults, so no migration step is needed. If the reliability spec's
`SettingsSanitizer` lands, it can rely on `PollingOptions.From` for clamping.

| Property | Type | Default | UI |
| --- | --- | --- | --- |
| `WindowRegions` | `Dictionary<string, WindowRelativeRegion>` | `new()` | written by region selection |
| `AdaptivePolling` | `bool` | `true` | Reading Chat → Fine tuning, checkbox |
| `MinIntervalMs` | `int` | `300` | slider 150–1000 (visible when adaptive) |
| `MaxIntervalMs` | `int` | `1500` | slider 500–5000 (visible when adaptive) |
| `IntervalMs` (existing) | `int` | `700` | existing slider, now visible only when adaptive is off |
| `OcrTimeoutMs` | `int` | `4000` | none (settings file only), clamped to [1000, 30000] |
| `PauseWhenCovered` | `bool` | `true` | checkbox |
| `PauseWhenNotForeground` | `bool` | `false` | checkbox |
| `Backend` | `string` | `CaptureBackendIds.Gdi` (`"gdi"`) | ComboBox, hidden when only one backend is available |

`CaptureBackendIds` (Core, `Capture/CaptureBackendIds.cs`): `Gdi = "gdi"`, `WindowsGraphicsCapture = "wgc"`.

### Envoy.App changes

**`Services/Capture/ICaptureBackend.cs`** (new)

```csharp
public sealed record CaptureTarget(ScreenRegion ScreenRect, IntPtr Window, ScreenRegion? ClientRect, int OutputWidth, int OutputHeight);

public interface ICaptureBackend : IDisposable
{
    string Id { get; }
    bool SupportsOccludedCapture { get; }
    /// <summary>Fills a pooled buffer of OutputWidth×OutputHeight; throws CaptureException on failure.</summary>
    FrameBuffer Capture(CaptureTarget target, FramePool pool);
}

public sealed class CaptureException(string message, Exception? inner = null) : Exception(message, inner);
```

**`Services/Capture/GdiCaptureBackend.cs`** (new, logic moved from `ScreenCapturer.Capture`)
- It keeps `_memDc` and `_bitmap` (`CreateCompatibleBitmap`) for the last output size and recreates them on size
  change (REQ-CAP-23). It calls `GetDC(NULL)`/`ReleaseDC` per frame because screen DCs must not be held. `Dispose`
  frees the GDI objects. The instance is not thread-safe and is owned by one loop generation.
- `GetDIBits` writes into `buffer.Array` (DllImport `byte[]` is pinned and not copied).
- When `StretchBlt` returns false, it throws `CaptureException("Can't read the screen (is Windows locked or a UAC prompt open?)")`.

**`Services/Capture/CaptureBackendFactory.cs`** (new)

```csharp
public static class CaptureBackendFactory
{
    public static IReadOnlyList<(string Id, string Label)> Available();   // gdi always; wgc only if implemented && IsSupported()
    public static ICaptureBackend Create(string id);                       // unknown/unsupported ⇒ GdiCaptureBackend (REQ-CAP-36)
}
```

**`Services/Capture/WgcCaptureBackend.cs`** (COULD, REQ-CAP-37). It uses `GraphicsCaptureItem` created for the HWND
via `IGraphicsCaptureItemInterop`, a `Direct3D11CaptureFramePool` (B8G8R8A8, 1 buffer), and a staging texture `Map`
copied into the `FrameBuffer`. It crops from window coordinates to the client rectangle using
`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`, then applies `StretchBlt`-equivalent scaling by nearest/box
filter in managed code. If this is not done in this sitting, `Available()` returns GDI only and the ComboBox row is
collapsed.

**`Services/ScreenCapturer.cs`** (kept as a facade for `LookupWindow` and `SmokeTest`)
- `Capture(ScreenRegion, double scale)` → one-shot `GdiCaptureBackend` + `FramePool`, returns
  `CapturedImage(buffer.ToArray(), w, h)`.
- `Preprocess(CapturedImage, …)` → `FrameProcessing.Preprocess(image.Pixels, new(...))`.
- `Fingerprint(CapturedImage)` → `FrameProcessing.Analyze(image.Pixels).Hash`.
- `LookupWindow` uses `RegionMath.OutputSize` in place of its inline formula (one-line change).

**`Services/GameWindowTracker.cs`** (new)

```csharp
public sealed record GameWindowInfo(IntPtr Handle, ScreenRegion ClientRect, bool Minimized, bool GameForeground, bool EnvoyForeground);

public static class GameWindowTracker
{
    /// <summary>Null when there is no game or its window is gone. Uses RunningGame.MainWindow if IsWindow, otherwise
    /// GameDetector.FindMainWindow(pid). Client rect = GetClientRect + ClientToScreen (physical px under PMv2).</summary>
    public static GameWindowInfo? Observe(RunningGame? game);

    /// <summary>WindowFromPoint → GetAncestor(GA_ROOT) per point; own process ⇒ Envoy, == game ⇒ Game, else Other.</summary>
    public static IReadOnlyList<SampleOwner> Sample(ScreenRegion region, IntPtr gameWindow);

    public static bool IsOnAnyMonitor(ScreenRegion region);     // MonitorFromRect(..., MONITOR_DEFAULTTONULL) != 0
    public static bool IsThreadPerMonitorV2();                  // GetThreadDpiAwarenessContext + AreDpiAwarenessContextsEqual
}
```

**`Services/GameDetector.cs`**: adds `public static IntPtr FindMainWindow(int processId)`. It uses `EnumWindows` to
pick the largest visible, unowned, non-tool top-level window of that process. `Find()` uses it when
`Process.MainWindowHandle` is zero. This handles games whose main handle is a splash or launcher window.

**`Interop/Native.cs`** (possibly shared with other areas): add `GetClientRect`, `ClientToScreen`, `WindowFromPoint`,
`GetAncestor` (+`GA_ROOT`), `EnumWindows` (+delegate), `IsWindowVisible`, `GetWindow` (+`GW_OWNER`),
`MonitorFromRect` (+`MONITOR_DEFAULTTONULL`), `GetThreadDpiAwarenessContext`, `AreDpiAwarenessContextsEqual`,
`DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4`, `POINT`. Add `DwmGetWindowAttribute` only if WGC lands.

**`Services/OcrService.cs`**

```csharp
public bool IsBusy { get; }   // a call (possibly abandoned after timeout) is still running
/// <summary>Single-flight: throws OcrBusyException if IsBusy. Times out with TimeoutException after <paramref name="timeout"/>;
/// the underlying WinRT operation is cancelled via IAsyncOperation.Cancel() and IsBusy clears when it actually completes.</summary>
public Task<IReadOnlyList<OcrPass>> RecognizeAsync(FrameBuffer frame, TimeSpan timeout, CancellationToken ct);
public Task<IReadOnlyList<OcrPass>> RecognizeAsync(CapturedImage image);   // unchanged signature (Lookup, smoke)
```

`SoftwareBitmap.CreateCopyFromBuffer(frame.Array.AsBuffer(0, frame.Length), …)` uses
`System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions` (REQ-CAP-24). The timeout wraps the
whole multi-engine pass with `Task.WaitAsync(timeout, ct)`.

**`Services/ChatWatcher.cs`** (rewritten)

```csharp
public enum WatcherState { Stopped, NoRegion, Running, Idle, Paused, Error }   // Paused is new; Idle stays = NoGame

public sealed class ChatWatcher : IDisposable
{
    public ChatWatcher(OcrService ocr, ChatPipeline pipeline, Func<AppSettings> settings,
                       Func<string> currentGameId, Func<RunningGame?> currentGame, TimeProvider? clock = null);

    public WatcherState State { get; }
    public PauseReason PauseReason { get; }
    public string? LastError { get; }
    public string? Hint { get; }                          // e.g. blank-frame hint (REQ-CAP-40); null when none
    public bool IsRunning { get; }
    public bool PublishFrames { get; set; }
    public Func<bool> ShouldRead { get; init; }           // unchanged meaning: false ⇒ PauseReason.NoGame
    public ScreenRegion? CurrentRegion { get; }           // last resolved absolute region; resolves on demand when not running
    public RegionSource CurrentRegionSource { get; }
    public bool ClientSizeDiffers { get; }
    public CaptureMetricsSnapshot Metrics { get; }
    public CaptureHealth Health => Metrics.Health;

    public event Action<WatcherState>? StateChanged;      // background thread (as today)
    public event Action<CaptureFrame>? FrameCaptured;     // background thread; CaptureFrame.Image is an exact-length copy
    public event Action<ScreenRegion?>? RegionChanged;    // background thread

    public void Start();     // resets watchdog budget, policy and metrics; checks PMv2 once
    public void Stop();
    public void Invalidate();  // fingerprint = 0, policy.Reset()
    public void Dispose();
}
```

Tick algorithm (generation `g`; every state write is guarded by `g == _generation`):

1. `watchdog.Heartbeat()`. Read `cfg = settings().Capture`, update `policy.Options = PollingOptions.From(cfg)`, and
   observe `info = GameWindowTracker.Observe(currentGame())`.
2. `res = RegionMath.ResolveForGame(cfg, currentGameId(), info?.ClientRect)`. If `res.Region` changed, update
   `CurrentRegion` and raise `RegionChanged`. The REQ-CAP-6 upgrade is **not** done here; see AppHost.
3. Build a `CaptureObservation`. `Samples` are taken only when `Source == WindowRelative`, `PauseWhenCovered` is on
   and the backend cannot see occluded windows. Evaluate `CaptureGate`. If paused: `SetState(Paused or Idle or
   NoRegion)`, `metrics.SetPause`, `delay = policy.Next(Paused)`, and continue.
4. `OutputSize`, then `backend.Capture(target, pool)` timed into `metrics.RecordCapture`.
5. `sig = FrameProcessing.Analyze(raw)`. Track the uniform streak for REQ-CAP-40. If `sig.Hash == _lastHash`:
   `metrics.RecordCapture(…, changed:false)`, `policy.Next(Unchanged)`, dispose the buffer, and continue.
6. If `ocr.IsBusy`: `metrics.RecordOcrBusy()`, leave `_lastHash` unchanged, `policy.Next(Unchanged)`, continue.
7. `Preprocess` in place, then `RecognizeAsync(frame, OcrTimeoutMs, ct)`, then `OcrMerger.Merge`. Set
   `_lastHash = sig.Hash` only after OCR succeeds. If `PublishFrames`, raise `FrameCaptured` with `buffer.ToArray()`.
   Fire-and-forget `pipeline.ProcessAsync` as today.
8. `metrics.RecordSuccess()`, `policy.Next(Changed)`, and `finally buffer.Dispose()`.
9. On exception (`CaptureException`, `TimeoutException`, `COMException`, `Win32Exception`, `InvalidOperationException`,
   or any other non-cancellation exception): `metrics.RecordFailure()` (and `RecordOcrTimeout` for timeouts), set
   `LastError`, and after 3 consecutive failures `SetState(Error)`. Then `policy.Next(Failed)`.
10. `await Task.Delay(policy.DelayAfter(interval, work), ct)`.

**Watchdog.** A `System.Threading.Timer` runs every 2 s while running and calls `watchdog.Check()`. On `Stalled`:
increment `_generation`, cancel the old CTS, create a new backend through `CaptureBackendFactory.Create(cfg.Backend)`,
start a new loop task, and call `metrics.RecordRestart()`. The old loop's backend is disposed in a continuation when
the old task finally completes. On `GiveUp`: stop the loop, call `metrics.SetGaveUp(true)`, and set `Error` with the
REQ-CAP-28 text. `StallAfter` is recomputed from settings each check.

**Threading.** Settings are read on the loop thread while the UI thread may mutate them. `AppHost.SetRegion` replaces
`Regions`/`WindowRegions` with **new** dictionary instances (copy-on-write) instead of mutating them in place. This
removes today's concurrent-read/write race. `AppHost._game` becomes `volatile`.

**Logging.** If `ILog` from reliability-observability exists when this is implemented, `ChatWatcher` takes an optional
`ILog? log` constructor parameter. It logs state/pause-reason transitions at Info, timeouts/restarts at Warning, and
give-up at Error. It never logs OCR text. Otherwise this is left out.

### Shared-file changes (for the integrator)

**`src/Envoy.Core/Settings/AppSettings.cs`**: add `WindowRelativeRegion` (record above) and the `CaptureSettings`
properties in the settings table. Update the `Regions` doc comment to "absolute fallback (physical px)". No other
sections change.

**`src/Envoy.App/AppHost.cs`**
- Constructor: `new ChatWatcher(Ocr, Pipeline, () => Settings, () => CurrentGameId, () => _game)` (adds the 5th
  argument). Subscribe `Watcher.RegionChanged += _ => _ui.BeginInvoke(() => RegionMoved?.Invoke())`.
- New `public event Action? RegionMoved;`.
- `private volatile RunningGame? _game;`
- `CaptureStatus` mapping:
  - `Running` + `Health.Degraded` → "Reading chat (text recognition is slow)"
  - `Running` → "Reading chat"
  - `Paused`: GameMinimized → "Paused — game minimised"; ChatCovered → "Paused — another window covers the chat
    area"; GameNotForeground → "Paused — game isn't the active window"; RegionOffScreen → "Chat area is off-screen —
    select it again"
  - `Idle` → "Waiting for a game to start" (unchanged)
  - `NoRegion` → unchanged
  - `Error` → `"Error: " + LastError` (unchanged)
  - When `Watcher.Hint` is non-null, it is appended on a new line.
- `SetRegion(ScreenRegion absolute)`: observe the game window. If `RegionMath.ToWindowRelative(absolute, client)` is
  non-null, write `WindowRegions[CurrentGameId]`. Always write `Regions[CurrentGameId]` and seed `Regions["any"]`.
  Both dictionaries are copied on write.
- `RefreshGame()`: after a game is newly detected, apply the REQ-CAP-6 one-time upgrade (same copy-on-write) and
  `ScheduleSave()`.

**`src/Envoy.App/App.xaml.cs`**: in start-up wiring, `Host.RegionMoved += () => _overlay?.Reposition();`.
`SelectRegion` is unchanged (it already passes `Watcher.CurrentRegion`, which is now the resolved region).

**`src/Envoy.App/Windows/MainWindow.xaml`** (Reading Chat page, or `Pages/ReadingChatPage.xaml` if ui-foundation's
split has landed)
- Region row: `RegionText` now includes the source, and a caption bound to `RegionHint` (visible when non-empty)
  appears under it.
- Fine tuning, replacing the single interval slider:
  - `CheckBox` "Adapt reading speed to chat activity" → `Settings.Capture.AdaptivePolling`.
  - Two slider rows "Fastest check" (`MinIntervalMs`, 150–1000, step 50) and "Slowest check" (`MaxIntervalMs`,
    500–5000, step 100), visible via `BoolToVisibility` on `AdaptivePolling`.
  - The existing "Check for new messages every" row, visible via `InverseBoolToVisibility`.
  - `CheckBox` "Pause while another window covers the chat area" → `PauseWhenCovered`.
  - `CheckBox` "Pause when the game isn't the active window" → `PauseWhenNotForeground`.
  - Row "Capture method" `ComboBox` → `ItemsSource={Binding CaptureBackends}`, `SelectedValue={Binding
    Settings.Capture.Backend}`, collapsed when `HasCaptureBackendChoice` is false.
- Live view header: `PreviewStats` shows `MetricsText`, and a second caption `HealthText` sits below the title. Only
  existing `Text.*` styles and palette keys are used. No new colours.

**`src/Envoy.App/Windows/MainWindow.xaml.cs` / `ViewModels/MainViewModel.cs`** (or `CapturePreviewViewModel` after
ui-foundation)
- VM: `RegionText` ("412 × 180 px · follows the AoE II: DE window" / "412 × 180 px at (20, 700) · fixed screen
  position"), `RegionHint` ("The game's size changed since you chose the chat area. Select it again if the Live view
  looks wrong."), `CaptureBackends`, `HasCaptureBackendChoice`, `MetricsText` (`Metrics.ToStatsLine()` plus " · {n}
  lines" from the last frame), and `HealthText`.
- A `DispatcherTimer` (1 s) runs while the Live view is visible and raises `MetricsText`/`HealthText`/`RegionText`
  change notifications (REQ-CAP-39). `OnFrame` no longer writes `PreviewStats`.

**`src/Envoy.App/Windows/RegionSelectorWindow.xaml.cs`**: `OnUp` computes physical corners with
`Surface.PointToScreen(start)` and `Surface.PointToScreen(end)`, normalised with min/max. `ShowCurrent` uses
`Surface.PointFromScreen` for both corners. `Scale` stays only for the size label. (REQ-CAP-31/32)

**`src/Envoy.App/Windows/OverlayWindow.xaml.cs`** (`Reposition` only): compute the target top-left in physical pixels
(region + anchor + gap scaled by the region monitor's DPI), then `SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0,
SWP_NOSIZE|SWP_NOACTIVATE)`. Free mode keeps DIP `Left/Top`. Reposition again on `DpiChanged`. (REQ-CAP-33)

**`src/Envoy.App/SmokeTest.cs`**: see Test plan.

### Failure behaviour summary

| Situation | Behaviour |
| --- | --- |
| Game minimised | `Paused/GameMinimized`, 1 s wake-ups, no capture or OCR |
| Browser over the chat (GDI) | `Paused/ChatCovered` (can be disabled) |
| Windows locked / UAC secure desktop | `StretchBlt` fails → backoff to 5 s; `Error` after 3 failures; recovers on the first success |
| OCR hangs | Timeout after 4 s → `Degraded`, single-flight skips until the call completes; the watchdog restarts the loop if the loop itself hangs |
| Loop repeatedly hangs | Give up after more than 3 restarts in 5 min → `Error` with a toggle-to-retry message |
| Exclusive fullscreen (black) | Blank-frame hint after 5 uniform frames |
| Window moved / resized | Region follows; re-select hint if the client size changed |
| Region off every monitor | `Paused/RegionOffScreen` |
| Unknown backend id in settings | GDI, reported in metrics |

---

## Acceptance criteria

| ID | REQs | Verification |
| --- | --- | --- |
| AC-CAP-1 | REQ-CAP-1, 2, 3 | Unit: `RegionMathTests.ToWindowRelative_inside_client_stores_offset_and_reference_size`, `Resolve_same_client_size_moves_with_window`, `Resolve_scales_proportionally_when_client_resized`, `Resolve_clamps_to_client`, `Resolve_handles_negative_monitor_coordinates` pass. |
| AC-CAP-2 | REQ-CAP-1, 2 | Smoke: the capture-engine block creates a test window, derives a relative region, moves the window by (+50, +30) physical px, and asserts the resolved region moved by exactly (50, 30). The log contains `capture-engine: relative region ok`. |
| AC-CAP-3 | REQ-CAP-4 | Unit: `RegionMathTests.ResolveForGame_order` (theory) and `SettingsStoreTests.V01_capture_settings_load_with_new_defaults` (v0.1 JSON without the new keys loads; `Regions` intact; `WindowRegions` empty; `AdaptivePolling` true). |
| AC-CAP-4 | REQ-CAP-5, 9, 10, 11, 13 | Unit: every `CaptureGateTests` case listed in the Test plan passes. |
| AC-CAP-5 | REQ-CAP-9 | Smoke: minimising the test window makes `GameWindowTracker.Observe` report `Minimized = true`, and the gate returns `GameMinimized`. Restoring returns `None`. |
| AC-CAP-6 | REQ-CAP-9, 10, 12 | Manual in-game (AoE II: DE windowed or borderless): minimise the game → War Room shows "Paused — game minimised" within 2 s and Task Manager shows Envoy below 1% CPU. Restore → "Reading chat" within 2 s. Drag a browser over the chat → "Paused — another window covers the chat area". Move it away → resumes. |
| AC-CAP-7 | REQ-CAP-14–19 | Unit: all `AdaptivePollPolicyTests` pass. |
| AC-CAP-8 | REQ-CAP-14, 38, 39 | Manual in-game: open Reading Chat during a match with quiet chat for 30 s → the Live view shows `every 1.5 s` and a skip rate of at least 90%. Have a friend type → within 2 s it shows `every 0.3 s`. |
| AC-CAP-9 | REQ-CAP-20, 21, 22 | Unit: `FrameProcessingTests.Analyze_does_not_allocate`, `Preprocess_does_not_allocate`, `Preprocess_matches_v01_within_one` and `FramePoolTests.Dispose_returns_array_and_next_rent_reuses_it` pass. |
| AC-CAP-10 | REQ-CAP-20, 23 | Smoke: 20 consecutive `GdiCaptureBackend.Capture` calls of the same 320×120 region at 2× (with pool return) allocate in total less than one frame's byte size (`GC.GetAllocatedBytesForCurrentThread` delta < 307,200). The log contains `capture-engine: steady-state alloc {n} B`. |
| AC-CAP-11 | REQ-CAP-25, 26 | Unit: `CaptureMetricsTests.Timeout_marks_degraded_for_60s`. Typecheck: `OcrService.RecognizeAsync(FrameBuffer, TimeSpan, CancellationToken)` compiles and `IsBusy` exists. Smoke: OCR via the new overload with a 10 s timeout completes. |
| AC-CAP-12 | REQ-CAP-27, 28 | Unit: `LoopWatchdogTests` (all 4) pass using `FakeTimeProvider`. |
| AC-CAP-13 | REQ-CAP-27, 29, 30, 38 | Smoke: a dedicated `ChatWatcher` pointed at the test window (as a fake `RunningGame`) runs until `Metrics.FramesCaptured >= 3` within 10 s. It asserts `Health != Failing` and `Metrics.Backend == "gdi"`, then stops cleanly. |
| AC-CAP-14 | REQ-CAP-29 | Unit: `CaptureMetricsTests.Failing_after_three_consecutive_failures`, `Paused_when_pause_reason_set`, `Success_clears_failing`. |
| AC-CAP-15 | REQ-CAP-31, 32, 34 | Manual (two monitors, primary at 150%, secondary at 100%, then swapped): select the chat area on each monitor. The Live view image contains exactly the dragged area (edges within 1 px by eye at 4× zoom), and re-opening the selector draws the rectangle in the same place. Smoke: the log contains `capture-engine: thread PMv2 = True`. |
| AC-CAP-16 | REQ-CAP-7, 33 | Manual: with overlay anchor "Above chat", drag the windowed game 300 px right → the overlay follows within about 1 s. Move the game to the other-DPI monitor → the overlay sits above the chat, not offset by the scale ratio. |
| AC-CAP-17 | REQ-CAP-35, 36 | Smoke: `CaptureBackendFactory.Create("nonsense").Id == "gdi"` and `Create("gdi").Id == "gdi"`. Typecheck: `ICaptureBackend` is implemented by `GdiCaptureBackend`. |
| AC-CAP-18 | REQ-CAP-37 | (Only if implemented.) Manual on Windows 11: set Capture method to Window capture, cover the chat with a browser → reading continues (no ChatCovered pause) and the Live view shows game chat. Smoke: if `IsSupported()`, one capture of the test window returns non-uniform pixels; otherwise the log contains `wgc: unsupported`. |
| AC-CAP-19 | REQ-CAP-38, 39 | Unit: `CaptureMetricsTests.StatsLine_formats_expected_text`, `Averages_use_last_50_samples`. Smoke: the `{theme}-settings-capture.png` screenshots in all 5 themes render the new Fine tuning rows and the stats line (CI artifact review). |
| AC-CAP-20 | REQ-CAP-40 | Unit: `FrameProcessingTests.Analyze_reports_uniform_for_black_frame`. Manual: in AoE II: DE switch to exclusive fullscreen (if offered) or select an all-black area → the blank-area hint appears in the War Room status within 5 frames. |
| AC-CAP-21 | REQ-CAP-6, 8 | Manual: start with a v0.1 `settings.json` (absolute region only), launch the game → after detection `settings.json` contains `windowRegions.<game>`, and Reading Chat says "follows the … window". Change the game resolution → the re-select hint shows. |
| AC-CAP-22 | REQ-CAP-41 | CI/typecheck review: `git grep -nE "OpenProcess\|ReadProcessMemory\|SetWindowsHookEx\|CreateRemoteThread" src/` returns nothing (add as a step in the PR checklist; quality-security-ci may automate it). |
| AC-CAP-23 | all | `dotnet test tests/Envoy.Core.Tests` and `tools/typecheck/typecheck.sh` pass. Windows CI `Envoy.exe --smoke-test` prints `SMOKE TEST PASSED`. |

---

## Test plan

### Unit tests (`tests/Envoy.Core.Tests/Capture/`, xUnit; `FakeTimeProvider` is already referenced)

**`RegionMathTests.cs`**
- `ToWindowRelative_inside_client_stores_offset_and_reference_size`: client (100,50,1920,1080), region
  (120,900,600,150) → (20,850,600,150,1920,1080).
- `ToWindowRelative_returns_null_when_region_leaves_client`: 10 px overhang → null.
- `ToWindowRelative_tolerates_2px_overhang_and_clamps`: 2 px overhang → clamped inside.
- `Resolve_same_client_size_moves_with_window`: same rel, client moved (+500, −200) → region moved by exactly that.
- `Resolve_scales_proportionally_when_client_resized`: 1920×1080 → 2560×1440 scales offset and size by 4/3 (rounded).
- `Resolve_clamps_to_client`: scaled region exceeding the client is clamped.
- `Resolve_handles_negative_monitor_coordinates`: client at (−1920, 0) works.
- `ResolveForGame_order` (theory): with/without client × with/without `WindowRegions[game]` / `Regions[game]` /
  `Regions["any"]` → expected `RegionSource` and rectangle. `ClientSizeDiffers` is set correctly.
- `OutputSize_matches_v01_formula` (theory): upscale 0.5/2/9 and large regions vs `maxDimension` 2600.
- `SamplePoints_returns_3x3_inset_grid_inside_region`.

**`FrameProcessingTests.cs`**
- `Analyze_identical_frames_have_equal_hash`.
- `Analyze_ignores_sub_quantisation_shimmer`: green +3 on all pixels with values aligned so `>>3` is unchanged → same
  hash.
- `Analyze_detects_8px_text_run`: a horizontal run of 8 white pixels on black → different hash.
- `Analyze_reports_uniform_for_black_frame`: all zeros → `IsUniform()`. A frame with a text run → not uniform.
- `Analyze_does_not_allocate`: warm up, then `GC.GetAllocatedBytesForCurrentThread()` delta == 0 over 100 calls.
- `Preprocess_matches_v01_within_one` (theory over greyscale × contrast {0.5, 1, 1.3, 3} × invert): compare against a
  copy of the v0.1 double algorithm kept in the test file on a random-seeded 64×16 frame.
- `Preprocess_does_not_allocate`.
- `Preprocess_sets_alpha_opaque`.

**`FramePoolTests.cs`**
- `Rent_returns_exact_length_span_and_stride`.
- `Dispose_returns_array_and_next_rent_reuses_it`: same `Array` reference on the second rent of the same size.
- `Double_dispose_is_safe`.
- `ToArray_copies_exact_pixels`: length == `Length`, contents equal.

**`AdaptivePollPolicyTests.cs`**
- `Changed_frame_drops_to_min`.
- `Stays_at_min_for_fast_frames_after_change`: 3 unchanged ticks at Min, the 4th is Min × 1.5.
- `Unchanged_frames_back_off_by_factor_to_max`: the sequence is capped at Max.
- `Failures_double_to_error_cap_and_success_recovers`.
- `Paused_uses_paused_interval_and_preserves_current`.
- `Disabled_adaptive_uses_fixed_interval`.
- `Options_clamp_out_of_range_settings` (theory: Min 10 → 150, Max < Min → Min, Max 99999 → 5000, IntervalMs 0 → 150).
- `DelayAfter_respects_duty_cycle_and_floor`: work 600 ms with interval 300 → 600 ms. Work 0 with interval 300 → 300 ms.
  Work 290 with interval 300 → 290 ms, because the duty-cycle cap wins over the 10 ms remainder.
- `Reset_returns_to_min`.

**`CaptureGateTests.cs`**
- `No_region_always_pauses_even_with_preview`.
- `Off_screen_region_pauses_even_with_preview`.
- `Minimized_game_pauses_even_with_preview`.
- `No_game_pauses_unless_preview_open`.
- `Covered_region_pauses_when_enabled_and_backend_cannot_see_occluded`: 3 of 9 `Other` → `ChatCovered`. 2 of 9 →
  `None`.
- `Covered_ignored_when_backend_sees_occluded`.
- `Covered_ignored_for_absolute_regions`.
- `Envoy_windows_never_count_as_covering`: 9 × `Envoy` → `None`.
- `Not_foreground_pauses_only_when_setting_on`.
- `Envoy_foreground_does_not_count_as_not_foreground`.
- `Preview_overrides_no_game_covered_and_not_foreground`.

**`LoopWatchdogTests.cs`** (`FakeTimeProvider`)
- `Healthy_while_heartbeats_arrive`.
- `Stalled_after_threshold`.
- `Gives_up_after_more_than_three_restarts_in_five_minutes`.
- `Restart_budget_recovers_after_window_and_ResetBudget`.
- `StallThreshold_is_max_of_15s_and_formula` (theory).

**`CaptureMetricsTests.cs`** (`FakeTimeProvider`)
- `Averages_use_last_50_samples`.
- `Counts_captured_and_skipped_frames`.
- `Timeout_marks_degraded_for_60s`: Degraded at t+59 s, Healthy at t+61 s.
- `Failing_after_three_consecutive_failures`.
- `Success_clears_failing`.
- `Paused_when_pause_reason_set` (takes precedence over Degraded).
- `GaveUp_is_failing`.
- `StatsLine_formats_expected_text`: exact string under `CultureInfo("de-DE")` to prove invariant formatting.
- `Concurrent_recording_is_consistent`: `Parallel.For` 10,000 `RecordCapture` → `FramesCaptured == 10000`.

**`SettingsStoreTests` additions** (in `tests/Envoy.Core.Tests/TranslationTests.cs`, where `SettingsStoreTests` lives,
or a new `Capture/CaptureSettingsTests.cs`)
- `V01_capture_settings_load_with_new_defaults`.
- `Capture_settings_round_trip_window_regions_and_polling`.

### Smoke-test additions (`src/Envoy.App/SmokeTest.cs`, after the per-theme loop, before the existing capture+OCR block)

A `CaptureEngineSmokeAsync(AppHost host, List<string> log)` method:
1. Create a plain topmost `Window` (640×360, `Background` = `DynamicResource Bg.Window`, title "Envoy smoke game"),
   show it, and get its HWND. Log `thread PMv2 = {GameWindowTracker.IsThreadPerMonitorV2()}`.
2. `fake = new RunningGame(GameCatalog.Find("aoe2de")!, Environment.ProcessId, hwnd)`. Call
   `GameWindowTracker.Observe(fake)` and assert non-null, a non-empty client, and `!Minimized`.
3. Relative round trip with a move of (+50, +30) via `Native.SetWindowPos`, asserting exact translation (AC-CAP-2).
4. Minimise, assert `Minimized` and `CaptureGate` → `GameMinimized`. Restore (AC-CAP-5).
5. Steady-state allocation check on `GdiCaptureBackend` (AC-CAP-10).
6. `CaptureBackendFactory` fallback checks (AC-CAP-17). If WGC is implemented, run its capture check, or log
   unsupported.
7. A dedicated `ChatWatcher` with an in-memory `AppSettings` copy (`WindowRegions["aoe2de"]` = (20,20,300,100) on the
   test window, `OnlyWhileGameRuns = false`) and `currentGame = () => fake`. Start, wait for `FramesCaptured >= 3`
   (10 s cap), assert health and backend, stop, dispose (AC-CAP-13).
8. Close the test window. Log `capture-engine: ok`.
9. Change the existing capture+OCR block to also call `host.Ocr.RecognizeAsync(frame, TimeSpan.FromSeconds(10),
   CancellationToken.None)` (AC-CAP-11).

The existing `{theme}-settings-capture.png` screenshots cover the Reading Chat UI changes in every theme.

### Manual in-game checks (record in the PR description)

1. AC-CAP-6: minimise / cover / restore, with the War Room status and Task Manager CPU.
2. AC-CAP-8: adaptive interval in the Live view during quiet chat and active chat.
3. AC-CAP-15: mixed-DPI selection on both monitors, swapping the primary.
4. AC-CAP-16: overlay follows a moved windowed game, including across monitors.
5. AC-CAP-20: blank-frame hint.
6. AC-CAP-21: v0.1 settings upgrade and the resolution-change hint.
7. Lock the PC (Win+L) for 20 s during capture, then unlock → back to "Reading chat" within 6 s without toggling.
8. Alt-tab to a browser on a second monitor with the game still visible on the first, `PauseWhenNotForeground` off →
   chat is still read.

---

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Capture/RegionMath.cs` | new | |
| `src/Envoy.Core/Capture/FramePool.cs` | new (`FramePool`, `FrameBuffer`) | |
| `src/Envoy.Core/Capture/FrameProcessing.cs` | new | |
| `src/Envoy.Core/Capture/AdaptivePollPolicy.cs` | new (`FrameOutcome`, `PollingOptions`, policy) | |
| `src/Envoy.Core/Capture/CaptureGate.cs` | new (`PauseReason`, `SampleOwner`, observation, options) | |
| `src/Envoy.Core/Capture/LoopWatchdog.cs` | new | |
| `src/Envoy.Core/Capture/CaptureMetrics.cs` | new (`CaptureHealth`, snapshot, metrics) | |
| `src/Envoy.Core/Capture/CaptureBackendIds.cs` | new | |
| `src/Envoy.Core/Settings/AppSettings.cs` | add `WindowRelativeRegion`; `CaptureSettings` gains `WindowRegions`, `AdaptivePolling`, `MinIntervalMs`, `MaxIntervalMs`, `OcrTimeoutMs`, `PauseWhenCovered`, `PauseWhenNotForeground`, `Backend` | **shared (AppSettings.cs)** |
| `src/Envoy.App/Services/ChatWatcher.cs` | rewrite (gate, policy, pool, backend, single-flight OCR, watchdog, metrics, `RegionChanged`, `Paused` state) | |
| `src/Envoy.App/Services/Capture/ICaptureBackend.cs` | new | |
| `src/Envoy.App/Services/Capture/GdiCaptureBackend.cs` | new (moved from `ScreenCapturer`) | |
| `src/Envoy.App/Services/Capture/CaptureBackendFactory.cs` | new | |
| `src/Envoy.App/Services/Capture/WgcCaptureBackend.cs` | new (COULD) | |
| `src/Envoy.App/Services/ScreenCapturer.cs` | becomes a facade over GDI backend + `FrameProcessing` | |
| `src/Envoy.App/Services/OcrService.cs` | `FrameBuffer` overload with timeout/cancel, `IsBusy`, zero-copy `AsBuffer` | |
| `src/Envoy.App/Services/GameWindowTracker.cs` | new | |
| `src/Envoy.App/Services/GameDetector.cs` | `FindMainWindow(pid)` via `EnumWindows`; used when `MainWindowHandle` is 0 | |
| `src/Envoy.App/Interop/Native.cs` | add the user32 functions listed in Design | possibly shared (other areas add interop) |
| `src/Envoy.App/AppHost.cs` | 5th watcher argument, `volatile _game`, `RegionMoved`, `CaptureStatus` mapping, copy-on-write `SetRegion`, REQ-CAP-6 upgrade in `RefreshGame` | **shared (AppHost.cs)** |
| `src/Envoy.App/App.xaml.cs` | `Host.RegionMoved += () => _overlay?.Reposition();` | **shared (App.xaml.cs)** |
| `src/Envoy.App/Windows/MainWindow.xaml` | Reading Chat: region source/hint, adaptive sliders, pause checkboxes, capture-method row, stats/health lines | **shared (MainWindow\*)** |
| `src/Envoy.App/Windows/MainWindow.xaml.cs` | 1 s metrics refresh timer while the Live view is visible; `OnFrame` stops writing `PreviewStats` | **shared (MainWindow\*)** |
| `src/Envoy.App/ViewModels/MainViewModel.cs` (or `CapturePreviewViewModel.cs`) | `RegionText`, `RegionHint`, `CaptureBackends`, `HasCaptureBackendChoice`, `MetricsText`, `HealthText` | shared with ui-foundation |
| `src/Envoy.App/Windows/RegionSelectorWindow.xaml.cs` | `PointToScreen`/`PointFromScreen` conversion | |
| `src/Envoy.App/Windows/OverlayWindow.xaml.cs` | `Reposition` in physical px + `DpiChanged` | possibly shared (ui-foundation / onboarding) |
| `src/Envoy.App/Windows/LookupWindow.xaml.cs` | use `RegionMath.OutputSize` | |
| `src/Envoy.App/SmokeTest.cs` | `CaptureEngineSmokeAsync` + OCR timeout overload call | **shared (SmokeTest.cs)** |
| `tests/Envoy.Core.Tests/Capture/*.cs` | new test files listed above | |
| `tests/Envoy.Core.Tests/TranslationTests.cs` (`SettingsStoreTests`) | 2 new tests (or a new file) | |
| `docs/architecture.md` | update loop diagram and Threading section | |
| `docs/troubleshooting.md` | pause reasons, blank-area hint, "covered" false positives (turn the option off) | |
| `CHANGELOG.md` | entry | |

Estimated size: Core about 550 lines, tests about 450, App about 600 (without WGC).

---

## Risks & open questions

- **Proportional scaling on resize is a guess.** Game UIs may anchor chat to a screen edge or scale it with a separate
  UI-scale setting. Envoy does not know each game's layout. Mitigation: exact translation when the size is unchanged
  (the common case), the re-select hint when it changed (REQ-CAP-8), and re-selection overwrites. *Open:* should we
  store one relative region per client size, so switching between two resolutions keeps both?
- **False "covered" pauses.** Some apps create invisible, non-click-through top-level windows (recorders,
  vendor-overlay helpers). `WindowFromPoint` skips `WS_EX_TRANSPARENT` layered windows, but not every overlay sets that
  flag. Mitigation: the 3-of-9 threshold, a clear status reason, and a one-click opt-out. COULD: name the covering
  process in the status (`Process.GetProcessById(pid).ProcessName`, no memory access).
- **Envoy's own overlay inside the chat area.** GDI with `CAPTUREBLT` captures our own layered overlay. If the user
  drags it over the chat, Envoy can read its own translations. Today's default anchors avoid this. *Open:* apply
  `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` to Envoy's windows? That would also hide the overlay from
  OBS/streams, so it would need to be a setting. That decision belongs to the overlay owner.
- **Abandoned OCR operations.** WinRT `OcrEngine.RecognizeAsync` cancellation is best-effort. Single-flight prevents
  pile-up, but a permanently hung engine keeps `IsBusy` true. The watchdog cannot fix that because the loop is still
  alive. Mitigation: after 3 timeouts in a row, `OcrService.Configure` re-creates the engines (cheap). Implement this
  if time allows; otherwise the status reads "Degraded" and toggling capture recreates them.
- **ArrayPool retention.** At most 2 arrays per size bucket are retained. For a typical region that is under 5 MB. The
  worst case near the OCR max dimension is about 54 MB. That is acceptable but worth watching in reliability's
  diagnostics.
- **`Process.MainWindowHandle` vs. the real game window.** Some titles show a launcher or splash first.
  `FindMainWindow` picks the largest visible unowned window. It could pick a launcher if the launcher stays open.
  `GameDetector` refreshes every 3 s, so this self-corrects once the game window is larger.
- **Windows.Graphics.Capture.** It needs Windows 10 1903+. On Windows 10 it draws a yellow capture border
  (`IsBorderRequired = false` only on Windows 11 with the `graphicsCaptureWithoutBorder` capability). It requires D3D11
  interop that cannot be compiled or exercised on Linux beyond the typecheck. It is a COULD for that reason. WGC is
  still pixels-in (DWM composition), so the anti-cheat promise holds.
- **Mixed-DPI verification depends on hardware.** CI runners have one 100% display, so REQ-CAP-31/33 rely on manual
  checks (AC-CAP-15/16). The pure parts are unit tested.
- **Dependencies on parallel areas.** ui-foundation may move the Reading Chat page and preview logic into
  `Pages/ReadingChatPage.xaml` + `CapturePreviewViewModel`. The UI additions here apply to whichever exists at
  merge time. reliability-observability's `ILog` is optional. onboarding-ux-accessibility may reuse
  `ChatWatcher.CurrentRegionSource` and `Hint` in first-run guidance.
- *Open:* is `MaxIntervalMs = 1500` slow enough to matter for CPU and fast enough not to miss short-lived chat lines?
  How long chat lines stay on screen is not known per game, so the value is configurable. Revisit with users' Live
  view metrics.
- *Open:* should `PauseWhenNotForeground` default to true for players with a single monitor? It is off by default
  because the cover check already handles the single-monitor case and the setting would break second-monitor use.
