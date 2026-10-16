# Capture Engine: window-relative, adaptive, self-healing chat capture

Status: Implemented — revised against `00-integration.md` (binding). Landed as step 5, after 01, 08a, 02 and 03. See
[Deviations](#deviations).

Area: `05-capture-engine`, step 5 of the implementation order. It may assume everything delivered by 01
(ui-foundation), 08a (quality-gates), 02 (reliability-observability) and 03 (translation-resilience), and nothing from
later steps (contract §1).

This area covers how Envoy finds the chat pixels, when it reads them, how fast it reads, and what it does when
something stalls. In code that means:

- `ChatWatcher`, `ScreenCapturer` and `OcrService` (the call boundary only);
- `GameDetector`, plus the new `GameWindowTracker` and `AppHost.GameWindow`;
- the coordinate maths in the region selector;
- the `CaptureSettings` section of `AppSettings` and the `CaptureOptions` snapshot;
- `OcrMerger.MergeLines` and the `OcrLine`/`CaptureFrame` shapes. These move here from 04 as REQ-CAP-42.

Out of scope:

- chat stitching, parsing and the pending-message lifecycle (04);
- log sinks and diagnostics bundles (02);
- the page and view-model split (01, already landed);
- first-run region guidance and the display-mode verdict (07a, which consumes `AppHost.GameWindow`).

---

## Context

### What exists when this step starts

After steps 1–4, the code looks like this:

| Concern | State at step 5 | Where |
| --- | --- | --- |
| Loop | `ChatWatcher.RunAsync` runs on the thread pool. Each tick calls `TickAsync`, then `Task.Delay(max(200, IntervalMs) - elapsed)`. `IntervalMs` is fixed (default 700 ms, slider 250–3000). | `src/Envoy.App/Services/ChatWatcher.cs` |
| Settings access | The loop calls `settings()`, which returns the live `AppSettings`, and reads `Capture.*` and `MyLanguage` on the loop thread while the UI thread mutates them. `AppHost.SetRegion` mutates `Regions` in place. `_game` is a plain field. | `ChatWatcher.cs`, `src/Envoy.App/AppHost.cs` |
| Region | `CaptureSettings.Regions[gameId]` is an **absolute physical-pixel screen rectangle**. 02 has marked it `[Location]`. Resolution order: the current game's entry, then `"any"`. | `src/Envoy.Core/Settings/AppSettings.cs` |
| Capture | `ScreenCapturer.Capture` creates and destroys a DC and bitmap **every frame**, and allocates a **new `byte[w*h*4]` every frame**. A 600×200 region at 2× is about 1.9 MB of LOH garbage per tick. 08a fixed its CA1806 warnings. | `src/Envoy.App/Services/ScreenCapturer.cs` |
| Change detection | `Preprocess` (per-pixel `double` maths) runs **before** `Fingerprint`, so unchanged frames still pay for it. | `ScreenCapturer.cs`, `ChatWatcher.TickAsync` |
| Preview | 01 replaced `PublishFrames` with `IDisposable BeginPreview()` and `IsPreviewing`. `CapturePreviewViewModel` holds the lease while `IsActive` is true. While previewing, unchanged frames are still OCR'd. | `ChatWatcher.cs`, `ViewModels/CapturePreviewViewModel.cs` |
| Hand-off | `OcrMerger.Merge` returns `IReadOnlyList<string>`. `pipeline.ProcessAsync(lines, ct)` is fire-and-forget and receives the **loop's** token. `CaptureFrame(CapturedImage Image, IReadOnlyList<string> Lines, TimeSpan OcrTime)`. | `ChatWatcher.cs`, `src/Envoy.Core/Ocr/OcrResults.cs` |
| Game awareness | `GameDetector.Find()` is polled every 3 s on the UI thread and returns `RunningGame(Profile, ProcessId, MainWindow)`. The watcher only checks whether a game is running. Minimised, covered or background game windows are still captured, OCR'd and translated. | `Services/GameDetector.cs`, `AppHost.cs` |
| OCR | `OcrService.RecognizeAsync(CapturedImage)` has **no timeout and no cancellation**. | `Services/OcrService.cs` |
| Failure handling | An exception sets `WatcherState.Error` and `LastError = ex.Message` (raw text). A hung OCR or GDI call stops the loop silently. 02 logs `Error` transitions and raises the `capture:error` problem toast. | `ChatWatcher.cs`, `AppHost.cs` |
| DPI | The manifest declares PerMonitorV2. `RegionSelectorWindow` multiplies DIPs by a DPI value that can be stale. `OverlayWindow.Reposition` divides by the **overlay's** DPI. | `app.manifest`, `Windows/RegionSelectorWindow.xaml.cs`, `Windows/OverlayWindow.xaml.cs` |
| Logging, time | `ILog`/`LogHub.For(category)`, `Task.Forget(ILog, what)` and `AppHostOptions.TimeProvider` exist (02). | `src/Envoy.Core/Diagnostics/*` |
| Policy | `tools/ci/check_policy.py` and `policy.json` (08a) allowlist every P/Invoke and deny process-access identifiers, **including in comments**. `BannedSymbols.txt` bans `Process.Start` and `Process.MainModule`. | `tools/ci/` |
| Tests | Nothing in the capture path is unit tested. The smoke setup phase has a one-shot `capture-ocr` section. | `tests/Envoy.Core.Tests`, `SmokeTest.cs` |

### What is weak or missing for production

1. **Moving the game window breaks Envoy.** If the user moves or resizes a windowed game, or moves it to another
   monitor, Envoy keeps reading the old screen rectangle. Users report this as "it stopped working".
2. **False reads.** When the game is minimised, Envoy reads the desktop. When a browser covers the chat, Envoy OCRs the
   browser and sends unrelated on-screen text to a translation provider.
3. **CPU and GC cost while gaming.** The loop runs at a fixed 700 ms. It allocates on the LOH every frame, runs
   per-pixel `double` maths before the change check, and churns a DC on every frame.
4. **No self-healing.** One hung `OcrEngine.RecognizeAsync` stops chat reading for the session, and the status still
   says "Reading chat".
5. **Stuck "translating…" lines.** The loop token reaches `ProcessAsync`, so stopping capture leaves in-flight overlay
   lines pending forever. A watchdog restart would do the same. This is the critique's major issue on 05, and the fix
   is in contract §2.6.
6. **Settings race.** Background reads of `AppSettings` happen while the UI thread mutates it (contract §7).
7. **Mixed DPI.** A 150% laptop panel next to a 100% external monitor gives an off-by-scale selection or overlay.
8. **Nothing to tune with.** Nobody can see whether frames are skipped, how slow OCR is, or why Envoy is not reading.

---

## Goals / Non-goals

### Goals

- Store the chat region **relative to the game window's client area**. Resolve it on every tick, and fall back to the
  absolute regions when there is no game window. v0.1 settings files load unchanged.
- Own game-window probing for the whole app: `GameWindowTracker` → `AppHost.GameWindow` (contract §2.4).
- **Pause** capture, OCR and translation while the game is minimised or the chat is covered.
- **Adaptive polling** with failure back-off.
- An **OCR timeout**, **single-flight OCR** and a **watchdog** with a restart budget.
- Make the tick follow contract §2.7 exactly:
  - `MergeLines` with geometry;
  - an exact-length `CaptureFrame` copy;
  - `CancellationToken.None` passed to `ProcessAsync`;
  - background reads only through the `CaptureOptions` snapshot.
- Correct per-monitor DPI handling in region selection.
- All pure logic in `Envoy.Core/Capture/` with xUnit tests, and the Win32 glue exercised by one smoke section.

### Non-goals

- A working Windows.Graphics.Capture backend (REQ-CAP-37, COULD).
- Reading exclusive-fullscreen games. Envoy only explains the problem (REQ-CAP-40, SHOULD). The advice text is
  `CaptureText.LikelyExclusiveFullscreen`, which 07a reuses.
- Any game-process access beyond today's process-name enumeration and user32 window queries. There is no memory
  access, injection or hooking.
- Chat stitching, parsing, dedupe, translation, and the pending-message lifecycle. These belong to 04, and 04's
  lifecycle tests land with 04.
- Auto-detecting the chat region from pixels.
- Migrating v0.1 users' absolute regions (REQ-CAP-6, COULD). There is no installed base yet.

---

## Requirements

MUST count for this area: **19** (contract §9: CAP-1, 2, 4, 5, 9, 10, 13, 14, 19, 22, 25, 26, 27, 28, 30, 31, 34, 41,
42). Folded ids are kept so references stay valid.

### Window-relative region and game window

- **REQ-CAP-1 (MUST)** Selecting a region while a game window is detected stores the region in
  `Capture.WindowRegions[gameId]` as a `WindowRelativeRegion`, provided the rectangle lies inside the client area
  within 2 px. The absolute rectangle is also stored in `Capture.Regions[gameId]` as the fallback.
  `Capture.WindowRegions` carries `[Location]` (contract §7).
- **REQ-CAP-2 (MUST)** On every tick, `GameWindowTracker` probes the game window and publishes it as
  `AppHost.GameWindow` (contract §2.4). The relative region is then resolved against the current client rectangle.
  - Same client size: moving the window by (dx, dy) moves the captured rectangle by exactly (dx, dy).
  - Different client size: the region scales proportionally per axis and is clamped to the client.
  - (absorbs REQ-CAP-3)
- **REQ-CAP-3** Folded into REQ-CAP-2.
- **REQ-CAP-4 (MUST)** The region resolves to `Regions[gameId]`, then `Regions["any"]`, exactly as today, when either
  of these holds:
  - there is no game window;
  - there is no relative region.

  This is the no-game-window fallback. A v0.1 `settings.json` loads and captures the same rectangle.
- **REQ-CAP-5 (MUST)** A resolved region that intersects no monitor pauses with `PauseReason.RegionOffScreen` instead
  of capturing black pixels.
- **REQ-CAP-6 (COULD)** One-time derivation of a relative region from a v0.1 absolute region. This area does not build
  it.
- **REQ-CAP-7 (SHOULD)** `ChatWatcher.RegionChanged` fires when the resolved region moves. `AppHost.RegionMoved` then
  repositions the anchored overlay on the next UI dispatch.
- **REQ-CAP-8 (SHOULD)** The Reading Chat region row has two parts:
  - It says either "follows the {game} window" or "fixed screen position".
  - It shows a re-select hint when the current client size differs from the reference size.

### Pausing

- **REQ-CAP-9 (MUST)** While the game window is minimised, the watcher neither captures nor runs OCR. It reports
  `PauseReason.GameMinimized` and resumes within 1 s of restore.
- **REQ-CAP-10 (MUST)** The watcher pauses with `PauseReason.ChatCovered` when all of these hold:
  - `Capture.PauseWhenCovered` is true (the default);
  - the active backend cannot see occluded windows;
  - the region is window-relative;
  - at least 3 of 9 sample points hit a top-level window that is neither the game nor an Envoy window.
- **REQ-CAP-11 (SHOULD)** `Capture.PauseWhenNotForeground` (default false) pauses with `GameNotForeground` while the
  foreground window belongs to neither the game nor Envoy.
- **REQ-CAP-12 (SHOULD)** While paused, the loop wakes every 1000 ms and does no capture or OCR. `AppHost.CaptureStatus`
  names the reason using `CaptureText`.
- **REQ-CAP-13 (MUST)** While `ChatWatcher.IsPreviewing` is true (01's lease), the `NoGame`, `GameNotForeground` and
  `ChatCovered` pauses are overridden so the user can tune. Frames go to the Live view, but **not** to the pipeline.
  `NoRegion`, `RegionOffScreen` and `GameMinimized` are never overridden.

### Adaptive polling

- **REQ-CAP-14 (MUST)** With `Capture.AdaptivePolling` on (the default), polling works like this:
  - a changed frame sets the interval to `MinIntervalMs` (300);
  - it stays there for 3 more unchanged ticks;
  - it then multiplies by 1.5 per unchanged tick, up to `MaxIntervalMs` (1500).

  This intentionally changes today's fixed 700 ms behaviour (contract §7 "unless the spec says otherwise").
- **REQ-CAP-15 (SHOULD)** With `AdaptivePolling` off, the interval is the fixed `IntervalMs` (default 700).
- **REQ-CAP-16 (SHOULD)** Settings are clamped when the snapshot is built:
  - `MinIntervalMs` to [150, 2000];
  - `MaxIntervalMs` to [Min, 5000];
  - `IntervalMs` to [150, 5000];
  - `OcrTimeoutMs` to [1000, 30000].
- **REQ-CAP-17 (SHOULD)** The delay after a tick is at least the tick's own work time (≤ 50% duty cycle) and at least
  50 ms.
- **REQ-CAP-18 (SHOULD)** `Invalidate()` resets the interval to Min and forces the next frame through OCR. It is called
  by `ApplySettings` position 6, by `NewMatch`, by `SetRegion` and by `BeginPreview`.
- **REQ-CAP-19 (MUST)** Each consecutive failed tick doubles the interval, starting from Min, up to 5000 ms. The first
  successful tick returns to adaptive behaviour.

### Allocation and throughput

- **REQ-CAP-20 (SHOULD)** Pixel buffers are rented from a dedicated `FramePool` and returned at the end of each tick.
  In steady state, capture, fingerprint and preprocessing allocate less than 16 KB of managed memory per frame.
- **REQ-CAP-21 (SHOULD)** `FrameProcessing.Analyze` and `Preprocess` allocate nothing. The fingerprint is taken on
  **raw** pixels before preprocessing. An unchanged frame skips preprocessing and OCR unless a preview lease is held
  (contract §2.7 step 6).
- **REQ-CAP-22 (MUST)** `FrameProcessing.Preprocess` matches the v0.1 algorithm within ±1 per channel for every
  combination of greyscale, contrast in [0.5, 3] and invert. OCR accuracy must not regress.
- **REQ-CAP-23 (SHOULD)** The GDI backend reuses its memory DC and bitmap while the output size is unchanged.
- **REQ-CAP-24 (SHOULD)** OCR receives pooled pixels through a length-bounded `IBuffer` view with no managed copy.

### OCR timeout, watchdog, health

- **REQ-CAP-25 (MUST)** Every OCR call is bounded by `Capture.OcrTimeoutMs` (default 4000). A timeout has these
  effects:
  - it cancels the WinRT operation;
  - it increments `OcrTimeouts`;
  - it sets `LastError` to `CaptureText.OcrTimedOut`;
  - it counts as a failed tick;
  - it does not stop the loop.
- **REQ-CAP-26 (MUST)** OCR is single-flight. While an earlier call is still running, including one abandoned after a
  timeout, a tick does not start another call. Instead it:
  - counts an `OcrBusySkip`;
  - leaves the fingerprint unset, so the frame is retried.
- **REQ-CAP-27 (MUST)** A watchdog restarts the loop, on a new capture-backend instance, when no heartbeat arrives for
  `max(15 s, 2 × OcrTimeoutMs + MaxIntervalMs)`. A restart never cancels translations already in flight (contract
  §2.6).
- **REQ-CAP-28 (MUST)** After more than 3 restarts within 5 minutes, the watcher gives up:
  - it stops in `WatcherState.Error`;
  - `LastError` is set to `CaptureText.StoppedResponding`.

  A manual `Start()` resets the budget.
- **REQ-CAP-29** Folded into REQ-CAP-30.
- **REQ-CAP-30 (MUST)** No capture, OCR or Win32 exception escapes the loop. Every failure has these effects:
  - it is logged at Warning under category `Capture`, with the exception and no OCR text;
  - it sets `LastError` to a `CaptureText` sentence chosen by `CaptureErrors.Describe`, never raw exception text.

  Three consecutive failures set `WatcherState.Error`, and one successful tick clears it. `ChatWatcher.Health` is
  computed in this order:
  1. `Paused` when a pause reason is set;
  2. `Failing` after 3 consecutive failures or a give-up;
  3. `Degraded` if a timeout or restart happened in the last 60 s;
  4. `Healthy` otherwise.

  (absorbs REQ-CAP-29)

### DPI

- **REQ-CAP-31 (MUST)** `RegionSelectorWindow` converts both dragged corners with `Visual.PointToScreen`, and draws
  the existing region with `Visual.PointFromScreen`. The result is within ±1 px on every monitor at any scale.
  (absorbs REQ-CAP-32)
- **REQ-CAP-32** Folded into REQ-CAP-31.
- **REQ-CAP-33 (SHOULD)** `OverlayWindow.Reposition` places the anchored overlay in physical pixels with
  `SetWindowPos`, and repositions again on `DpiChanged`.
- **REQ-CAP-34 (MUST)** The capture engine uses physical pixels for every coordinate: client rects, sample points and
  blits. At loop start the watcher checks that its thread's DPI awareness context is PerMonitorV2. If it is not, it
  logs a Warning and sets `LastError = CaptureText.DpiUnaware` once.

### Backend abstraction

- **REQ-CAP-35 (SHOULD)** Capture goes through `ICaptureBackend`. `GdiCaptureBackend` is the default (`Capture.Backend
  = "gdi"`).
- **REQ-CAP-36 (SHOULD)** An unknown or unsupported backend id falls back to GDI. `Metrics.Backend` reports the backend
  actually in use.
- **REQ-CAP-37 (COULD)** A `WgcCaptureBackend` (Windows.Graphics.Capture) is selectable when `IsSupported()` is true.
  It sets `SupportsOccludedCapture = true`.

### Metrics, hints, policy

- **REQ-CAP-38 (SHOULD)** `ChatWatcher.Metrics` returns a snapshot with these fields:
  - average capture ms and average OCR ms over the last 50 samples;
  - frames captured and frames skipped;
  - OCR runs, timeouts and busy-skips;
  - restarts;
  - the current interval;
  - the backend;
  - health and pause reason.
- **REQ-CAP-39 (SHOULD)** `CapturePreviewViewModel` exposes `MetricsText` and `HealthText`, refreshed by a 1 s
  `DispatcherTimer` while `IsActive` is true (contract §2.8). The Live view header shows both.
- **REQ-CAP-40 (SHOULD)** After 5 consecutive uniform raw frames (sampled luma range ≤ 8) while not paused, two things
  happen:
  - `ChatWatcher.Hint` becomes `CaptureText.LikelyExclusiveFullscreen`;
  - `GameWindowInfo.UniformFrameStreak` carries the streak, saturating at 5.
- **REQ-CAP-41 (MUST)** The capture engine adds no network code. Its only game-process access is today's process-name
  enumeration and the user32 window queries listed in [Allowlist additions](#allowlist-additions). Each P/Invoke is
  added to `Native.cs` **and** to `tools/ci/policy.json` → `native.allowed` in the same change. No identifier on
  `native.denied` appears anywhere, including comments.

### Contract hand-off (moved from 04)

- **REQ-CAP-42 (MUST)** The tick follows contract §2.7, which requires all of the following.
  - It reads settings only through the immutable `CaptureOptions` snapshot. Mutating `AppSettings` after the snapshot
    is built does not change the snapshot.
  - `OcrLine` gains reserved `Left`/`Right` (default 0).
  - `OcrMerger.MergeLines(passes, preferredLanguage)` returns one `OcrLine` per row, top to bottom. Each row carries
    the picked text and the median `Top`/`Bottom` of the engines that read it.
  - `Merge` becomes `MergeLines(...).Select(l => l.Text)`, with identical output.
  - `ChatPipeline` gains `ProcessAsync(IReadOnlyList<OcrLine>, CancellationToken)`. In this step it forwards the texts,
    and 04 later uses the geometry.
  - The watcher passes `CancellationToken.None` to `ProcessAsync` and wraps the call in `.Forget(log, "pipeline")`.
  - `CaptureFrame` is `(CapturedImage Image, IReadOnlyList<OcrLine> Lines, TimeSpan OcrTime)`. `Image` is an
    exact-length copy, never a pooled array.
  - `FrameCaptured` is raised only while a preview lease is held.

  (absorbs REQ-CHAT-1 and REQ-CHAT-3)
- **REQ-CAP-43 (SHOULD)** `Envoy.exe --soak <minutes>` (contract §8.5) runs the watcher on a synthetic chat window.
  - Every minute it logs these values:
    - private bytes;
    - handle count;
    - GDI and USER objects;
    - `FramePool` outstanding buffers and retained bytes;
    - the `History` and `Players` counts.
  - It exits 1 if any of these grows by more than 20% between minute 5 and the end.
  - It is not part of CI. A 120-minute run is a step in the pre-release pass.

---

## Design

### Overview

```
 UI thread                                   │ loop thread (generation N)
 ────────────────────────────────────────────┼──────────────────────────────────────────────────────────
 ApplySettings ─► CaptureOptions (Volatile)──┼─► 2  options()
 GameDetector (3 s) ─► volatile _game ───────┼─► 3  GameWindowTracker.Observe ─► publish ─┐
 1 s _windowTimer ─► Observe ─► publish ─┐   │   4  RegionMath.ResolveForGame ─► CaptureGate (pause / preview)
 AppHost.GameWindow ◄─ BeginInvoke ◄─────┴───┼───────────────────────────────────────────┘
 GameWindowChanged (06, 07a)                 │   5  ICaptureBackend.Capture ─► FrameBuffer (FramePool)
                                             │   6  FrameProcessing.Analyze ─ unchanged & !preview ─► 12
                                             │   7  Preprocess   8  OcrService (SingleFlight + timeout)
                                             │   9  OcrMerger.MergeLines
                                             │  10  if reading: ProcessAsync(lines, None).Forget
 CapturePreviewViewModel ◄─ FrameCaptured ◄──┼─ 11  if IsPreviewing: CaptureFrame(copy, lines, ocrTime)
                                             │  12  dispose buffer, metrics, AdaptivePollPolicy delay
 LoopWatchdog timer (2 s) ── restart / give up ─► heartbeat (1)
```

### Envoy.Core (new folder `src/Envoy.Core/Capture/`, namespace `Envoy.Core.Capture`)

All types are public, `net8.0`, and have no Windows dependency. Every timed type takes a `TimeProvider`.

**Persisted record** (in `src/Envoy.Core/Settings/AppSettings.cs`, next to `ScreenRegion`):

```csharp
/// <summary>Chat region as an offset inside a game window's client area, plus the client size when it was chosen.</summary>
public sealed record WindowRelativeRegion(int OffsetX, int OffsetY, int Width, int Height, int ClientWidth, int ClientHeight)
{
    public bool IsEmpty => Width <= 0 || Height <= 0 || ClientWidth <= 0 || ClientHeight <= 0;
}
```

**`CaptureOptions.cs`.** This is the snapshot required by contract §7. AppHost builds it in `ApplySettings` position 2
and in `SetRegion`, and publishes it with `Volatile.Write`.

```csharp
public sealed record CaptureOptions(
    string ForcedGame, bool OnlyWhileGameRuns,
    FrozenDictionary<string, ScreenRegion> Regions,
    FrozenDictionary<string, WindowRelativeRegion> WindowRegions,
    double Upscale, PreprocessOptions Preprocess, PollingOptions Polling,
    int OcrTimeoutMs, bool PauseWhenCovered, bool PauseWhenNotForeground,
    string Backend, string MyLanguage)
{
    /// <summary>Copies every collection (ToFrozenDictionary) and clamps per REQ-CAP-16. UI thread only.</summary>
    public static CaptureOptions From(AppSettings settings);
    public string GameIdFor(string? detectedGameId);      // ForcedGame unless "any", else detected ?? "any" (as AppHost.CurrentGameId)
    public bool ShouldRead(bool gameRunning);             // !OnlyWhileGameRuns || gameRunning || ForcedGame != "any" (today's ShouldRead)
}
```

**`GameWindowInfo.cs`.** This is the shape fixed by contract §2.4. It lives in Core so that 07a's pure
`DisplayModeAdvisor.Classify(GameWindowInfo?)` can use it.

```csharp
public sealed record GameWindowInfo(
    IntPtr Handle, ScreenRegion ClientRect, ScreenRegion WindowRect,   // physical px
    bool Minimized, bool GameForeground, bool EnvoyForeground,
    bool HasCaption, ScreenRegion MonitorBounds,
    int UniformFrameStreak);                                           // 0..BlankFrames.Threshold
```

**`RegionMath.cs`**

```csharp
public enum RegionSource { None, WindowRelative, GameAbsolute, AnyAbsolute }
public sealed record RegionResolution(ScreenRegion? Region, RegionSource Source, bool ClientSizeDiffers);

public static class RegionMath
{
    public const int EdgeTolerancePx = 2;
    public static WindowRelativeRegion? ToWindowRelative(ScreenRegion absolute, ScreenRegion client); // null if outside by > tolerance; else clamped
    public static ScreenRegion Resolve(WindowRelativeRegion relative, ScreenRegion client);           // translate, or scale per axis (rounded) + clamp
    /// <summary>WindowRegions[gameId] (only when gameClient is known) → Regions[gameId] → Regions["any"].</summary>
    public static RegionResolution ResolveForGame(CaptureOptions options, string gameId, ScreenRegion? gameClient);
    public static ScreenRegion? Intersect(ScreenRegion a, ScreenRegion b);
    public static bool Contains(ScreenRegion outer, ScreenRegion inner, int tolerance = 0);
    /// <summary>The v0.1 formula: upscale clamped to [1, 4], limited so neither side exceeds maxDimension.</summary>
    public static (int Width, int Height, double Scale) OutputSize(ScreenRegion source, double upscale, uint maxDimension);
    public static IReadOnlyList<(int X, int Y)> SamplePoints(ScreenRegion region, int gridSize = 3); // inset 2 px
}
```

**`FramePool.cs`**

```csharp
public sealed class FramePool          // ArrayPool<byte>.Create(64 MiB, 2): deterministic reuse, bounded retention
{
    public FrameBuffer Rent(int width, int height);
    public int Outstanding { get; }    // rented and not yet returned (soak + tests)
    public long RetainedBytesHighWater { get; }
}

/// <summary>BGRA32, top-down, stride = width*4. Array may be longer than Length.</summary>
public sealed class FrameBuffer : IDisposable
{
    public int Width { get; } public int Height { get; }
    public int Length => Width * 4 * Height;
    public byte[] Array { get; }       // for GetDIBits and AsBuffer(0, Length)
    public Span<byte> Pixels { get; }  // Array.AsSpan(0, Length)
    public byte[] ToArray();           // exact-length copy (CaptureFrame, Scan & translate)
    public void Dispose();             // returns to pool; idempotent
}
```

**`FrameProcessing.cs`**

```csharp
public readonly record struct FrameSignature(ulong Hash, byte MinLuma, byte MaxLuma)
{
    public bool IsUniform(int tolerance = BlankFrames.LumaTolerance) => MaxLuma - MinLuma <= tolerance;
}
public readonly record struct PreprocessOptions(bool Grayscale, double Contrast, bool Invert);

public static class FrameProcessing
{
    public static FrameSignature Analyze(ReadOnlySpan<byte> bgra);                 // v0.1 sampling (every 7th px, G>>3, FNV-1a) + min/max G
    public static void Preprocess(Span<byte> bgra, in PreprocessOptions options);  // stackalloc LUT; integer luma (77r+150g+29b+128)>>8; alpha=255
}

public static class BlankFrames
{
    public const int Threshold = 5, LumaTolerance = 8;
    public static int Next(int streak, FrameSignature signature);  // uniform ? min(streak+1, Threshold) : 0
}
```

**`AdaptivePollPolicy.cs`**

```csharp
public enum FrameOutcome { Changed, Unchanged, Paused, Failed }

public sealed record PollingOptions(int MinIntervalMs, int MaxIntervalMs,
    double BackoffFactor = 1.5, int FastFramesAfterChange = 3, int PausedIntervalMs = 1000,
    int MaxErrorIntervalMs = 5000, double MaxDutyCycle = 0.5, int FloorMs = 50)
{
    public static PollingOptions From(CaptureSettings settings);   // clamps; AdaptivePolling=false ⇒ Min = Max = IntervalMs
}

public sealed class AdaptivePollPolicy(PollingOptions options)
{
    public PollingOptions Options { get; set; }       // swapped each tick from the snapshot (Current re-clamped)
    public TimeSpan Current { get; }
    public TimeSpan Next(FrameOutcome outcome);       // REQ-CAP-14/15/19; Paused ⇒ PausedIntervalMs, Current untouched
    public void Reset();                              // Current = Min, fast counter refilled
    public TimeSpan DelayAfter(TimeSpan interval, TimeSpan workTime); // max(interval − work, work × (1−duty)/duty, Floor)
}
```

**`CaptureGate.cs`**

```csharp
public enum PauseReason { None, NoRegion, NoGame, GameMinimized, GameNotForeground, ChatCovered, RegionOffScreen }
public enum SampleOwner { Game, Envoy, Other }

public sealed record CaptureObservation(bool HasRegion, bool RegionOnScreen, bool ShouldRead,
    bool WindowFound, bool Minimized, bool GameForeground, bool EnvoyForeground,
    RegionSource Source, IReadOnlyList<SampleOwner>? Samples);

public sealed record CaptureGateOptions(bool PauseWhenCovered, bool PauseWhenNotForeground,
    bool BackendSeesOccluded, bool PreviewOpen, int CoveredSampleThreshold = 3);

public static class CaptureGate
{
    /// <summary>NoRegion → RegionOffScreen → GameMinimized → (PreviewOpen ⇒ None) → NoGame → GameNotForeground →
    /// ChatCovered → None. ChatCovered only for Source == WindowRelative.</summary>
    public static PauseReason Evaluate(CaptureObservation observation, CaptureGateOptions options);
    public static int CountCovered(IReadOnlyList<SampleOwner> samples);   // Other only
}
```

**`SingleFlight.cs`.** This holds the OCR timeout and single-flight logic in Core, so REQ-CAP-25/26 are unit tested.

```csharp
public sealed class SingleFlightBusyException() : InvalidOperationException("A previous call is still running.");

public sealed class SingleFlight(TimeProvider time)
{
    public bool IsBusy { get; }   // true from start until the work task actually completes (even after a timeout)
    /// <summary>Throws SingleFlightBusyException if IsBusy. Runs work(linkedToken); on timeout cancels linkedToken and
    /// throws TimeoutException while IsBusy stays true until work finishes. Faults clear IsBusy and rethrow.</summary>
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan timeout, CancellationToken ct);
}
```

**`LoopWatchdog.cs`**

```csharp
public enum WatchdogVerdict { Healthy, Stalled, GiveUp }
public sealed class LoopWatchdog(TimeProvider time, TimeSpan stallAfter, int maxRestarts = 3, TimeSpan? restartWindow = null /* 5 min */)
{
    public TimeSpan StallAfter { get; set; }
    public void Heartbeat();              // Interlocked
    public WatchdogVerdict Check();
    public void RecordRestart();          // also a heartbeat
    public void ResetBudget();
    public int Restarts { get; }
    public static TimeSpan StallThreshold(int ocrTimeoutMs, int maxIntervalMs); // max(15 s, 2×ocr + max)
}
```

**`CaptureMetrics.cs`**

```csharp
public enum CaptureHealth { Healthy, Paused, Degraded, Failing }
public sealed record CaptureMetricsSnapshot(double AvgCaptureMs, double AvgOcrMs, long FramesCaptured, long FramesSkipped,
    long OcrRuns, long OcrTimeouts, long OcrBusySkips, long Restarts, int ConsecutiveFailures, TimeSpan CurrentInterval,
    string Backend, CaptureHealth Health, PauseReason PauseReason);

public sealed class CaptureMetrics(TimeProvider time, int window = 50)   // one lock; two fixed double[window] rings
{
    public void RecordCapture(TimeSpan elapsed, bool changed); public void RecordOcr(TimeSpan elapsed);
    public void RecordOcrTimeout(); public void RecordOcrBusy(); public void RecordFailure(); public void RecordSuccess();
    public void RecordRestart(); public void SetGaveUp(bool gaveUp); public void SetInterval(TimeSpan interval);
    public void SetBackend(string id); public void SetPause(PauseReason reason);
    public CaptureMetricsSnapshot Snapshot();    // Health per REQ-CAP-30
    public void Reset();
}
```

**`CaptureText.cs` and `CaptureErrors.cs`.** These hold the UI strings (contract §8.7). Each string is a whole
sentence with named placeholders. There is no "Error:" prefix and no raw exception text.

```csharp
public static class CaptureText
{
    public const string Reading = "Reading chat";
    public const string ReadingSlow = "Reading chat. Text recognition is slow right now.";
    public const string NoRegion = "Chat area not set";                     // unchanged
    public const string WaitingForGame = "Waiting for a game to start";     // unchanged
    public const string Stopped = "Chat reading is off";
    public const string PausedGameMinimized = "Paused while the game is minimised.";
    public const string PausedChatCovered = "Paused because another window covers the chat area. Move it away to resume.";
    public const string PausedGameNotForeground = "Paused while the game isn't the active window.";
    public const string PausedRegionOffScreen = "The chat area is off-screen. Select it again.";
    public const string ScreenUnreadable = "Envoy can't read the screen right now. It keeps trying, for example after you unlock Windows.";
    public const string OcrTimedOut = "Text recognition took too long. Envoy keeps trying.";
    public const string CaptureFailed = "Reading the chat area failed. Envoy keeps trying.";
    public const string StoppedResponding = "Chat reading stopped responding. Turn chat reading off and on to try again.";
    public const string DpiUnaware = "Display scaling awareness is off, so the chat area may be misaligned. Restart Envoy.";
    public const string LikelyExclusiveFullscreen = "The chat area is blank. Exclusive fullscreen can't be read. Switch the game to borderless or windowed mode, or select the chat area again.";
    public const string RegionFollowsFormat = "{width} × {height} px · follows the {game} window";
    public const string RegionFixedFormat = "{width} × {height} px at ({x}, {y}) · fixed screen position";
    public const string RegionResizedHint = "The game's size changed since you chose the chat area. Select it again if the Live view looks wrong.";
    public const string StatsFormat = "capture {captureMs} ms · OCR {ocrMs} ms · skipped {skipped}/{captured} ({skipPercent}%) · every {interval} s · {backend}";
    public static string Paused(PauseReason reason);                       // NoRegion/NoGame map to NoRegion/WaitingForGame
    public static string Health(CaptureMetricsSnapshot snapshot);          // one short sentence per CaptureHealth
    public static string Stats(CaptureMetricsSnapshot snapshot);           // invariant culture
    public static string RegionFollows(int width, int height, string game);
    public static string RegionFixed(ScreenRegion region);
}

public static class CaptureErrors
{
    /// <summary>TimeoutException → OcrTimedOut; CaptureException → ScreenUnreadable; anything else → CaptureFailed.
    /// Never returns exception text.</summary>
    public static string Describe(Exception ex);
}
```

**OCR shapes** (in `src/Envoy.Core/Ocr/OcrResults.cs`, REQ-CAP-42):

```csharp
public sealed record OcrLine(string Text, double Top, double Bottom, double Left = 0, double Right = 0); // Left/Right reserved; 04 fills them
public static IReadOnlyList<OcrLine> MergeLines(IReadOnlyList<OcrPass> passes, string preferredLanguage); // Left = Right = 0 in this step
public static IReadOnlyList<string> Merge(IReadOnlyList<OcrPass> passes, string preferredLanguage)
    => MergeLines(passes, preferredLanguage).Select(l => l.Text).ToList();
```

`MergeLines` reuses today's row grouping and engine choice unchanged. For each row it also collects the `Top`/`Bottom`
of every candidate in the group and takes the median of each.

**`ChatPipeline`** (in `src/Envoy.Core/Pipeline/ChatPipeline.cs`) gets one added overload. Arrival rule: 04 later
rewrites its body to use the geometry, and 04 owns every other pipeline change.

```csharp
public Task<IReadOnlyList<IncomingMessage>> ProcessAsync(IReadOnlyList<OcrLine> lines, CancellationToken ct = default)
    => ProcessAsync(lines.Select(l => l.Text), ct);
```

**`SoakBudget.cs`** (SHOULD, REQ-CAP-43):

```csharp
public sealed record SoakSample(int Minute, long PrivateBytes, int Handles, int GdiObjects, int UserObjects,
                                int PoolOutstanding, long PoolRetainedBytes, int History, int Players);
public static class SoakBudget
{
    /// <summary>Compares the sample at minute 5 with the last; returns one line per metric that grew > 20 %.</summary>
    public static IReadOnlyList<string> Violations(IReadOnlyList<SoakSample> samples, double maxGrowth = 0.20);
}
```

**Settings (`CaptureSettings`, shared `AppSettings.cs`).** Every change is additive. Missing JSON properties take the
defaults, and `CurrentVersion` stays 1.

| Property | Type | Default | Marker | UI (Reading Chat › Fine tuning) |
| --- | --- | --- | --- | --- |
| `WindowRegions` | `Dictionary<string, WindowRelativeRegion>` | `new()` | `[Location]` | written by region selection |
| `AdaptivePolling` | `bool` | `true` | | checkbox |
| `MinIntervalMs` | `int` | `300` | | slider 150–1000 (when adaptive) |
| `MaxIntervalMs` | `int` | `1500` | | slider 500–5000 (when adaptive) |
| `IntervalMs` (existing) | `int` | `700` | | existing slider, shown when adaptive is off |
| `OcrTimeoutMs` | `int` | `4000` | | none (file only) |
| `PauseWhenCovered` | `bool` | `true` | | checkbox |
| `PauseWhenNotForeground` | `bool` | `false` | | checkbox |
| `Backend` | `string` | `"gdi"` (`CaptureBackendIds.Gdi`) | | ComboBox, collapsed with one backend |

02's `SettingsRedaction` renders a `[Location]` dictionary as `W×H` values. That covers `WindowRelativeRegion` because
it has `Width`/`Height`. If the reflection walker only knows `ScreenRegion`, 05 extends it in the same change.
`SettingsRedactionTests.Every_marked_property_is_masked` must pass.

### Envoy.App

**`Services/Capture/ICaptureBackend.cs`**, **`GdiCaptureBackend.cs`** and **`CaptureBackendFactory.cs`** (new):

```csharp
public sealed record CaptureTarget(ScreenRegion ScreenRect, IntPtr Window, int OutputWidth, int OutputHeight);
public interface ICaptureBackend : IDisposable
{
    string Id { get; }
    bool SupportsOccludedCapture { get; }
    FrameBuffer Capture(CaptureTarget target, FramePool pool);   // throws CaptureException on failure
}
public sealed class CaptureException(string message, Exception? inner = null) : Exception(message, inner);
public static class CaptureBackendFactory
{
    public static IReadOnlyList<(string Id, string Label)> Available();  // gdi; wgc only if implemented && supported
    public static ICaptureBackend Create(string id);                      // unknown ⇒ GDI
}
```

`GdiCaptureBackend` takes over the logic of `ScreenCapturer.Capture`:

- It keeps the memory DC and bitmap for the last output size.
- It calls `GetDC(NULL)`/`ReleaseDC` per frame.
- `GetDIBits` writes into `buffer.Array`.
- A failed `StretchBlt` throws `CaptureException`.
- It checks every return value, so there are no CA1806 suppressions. 08a's analyzers apply to it unchanged (contract
  §8.8).
- One loop generation owns one instance.

`WgcCaptureBackend` is a COULD and is not planned for this step. If it lands, it adds `dwmapi!DwmGetWindowAttribute`.

**`Services/ScreenCapturer.cs`** becomes a facade for `LookupWindow` and the smoke `capture-ocr` section:

- `Capture` uses a one-shot `GdiCaptureBackend` and returns `CapturedImage(buffer.ToArray(), w, h)`.
- `Preprocess` and `Fingerprint` delegate to `FrameProcessing`.
- `LookupWindow` uses `RegionMath.OutputSize`.

**`Services/GameWindowTracker.cs`** (new, static, thread-safe):

```csharp
public static class GameWindowTracker
{
    /// <summary>Null when there is no game or its window is gone. Window = RunningGame.MainWindow if IsWindow, else
    /// GameDetector.FindMainWindow(pid). ClientRect = GetClientRect + ClientToScreen; WindowRect = GetWindowRect;
    /// HasCaption = WS_CAPTION in GetWindowLongPtrW(GWL_STYLE); MonitorBounds = MonitorFromWindow + GetMonitorInfoW (02).</summary>
    public static GameWindowInfo? Observe(RunningGame? game, int uniformFrameStreak);
    /// <summary>WindowFromPoint → GetAncestor(GA_ROOT): == game ⇒ Game (checked first); own process ⇒ Envoy; else Other.</summary>
    public static IReadOnlyList<SampleOwner> Sample(ScreenRegion region, IntPtr gameWindow);
    public static bool IsOnAnyMonitor(ScreenRegion region);   // MonitorFromRect(MONITOR_DEFAULTTONULL) != 0
    public static bool IsThreadPerMonitorV2();                // GetThreadDpiAwarenessContext + AreDpiAwarenessContextsEqual
}
```

**`Services/GameDetector.cs`** adds `FindMainWindow(int processId)`, which picks the largest visible, unowned top-level
window of that process using `EnumWindows`, `IsWindowVisible` and `GetWindow(GW_OWNER)`. `Find()` uses it when
`MainWindowHandle` is zero. The `EnumWindows` callback is a static `[UnmanagedCallersOnly]` function pointer, so no
delegate has to be kept alive.

**`Services/OcrService.cs`**:

```csharp
public bool IsBusy => _flight.IsBusy;
/// <summary>SingleFlight.RunAsync over the whole multi-engine pass; each engine call is
/// RecognizeAsync(bitmap).AsTask(linkedToken), so a timeout cancels the WinRT operation.</summary>
public Task<IReadOnlyList<OcrPass>> RecognizeAsync(FrameBuffer frame, TimeSpan timeout, CancellationToken ct);
public Task<IReadOnlyList<OcrPass>> RecognizeAsync(CapturedImage image);   // unchanged (Lookup, smoke)
```

The bitmap is made from `frame.Array.AsBuffer(0, frame.Length)` (REQ-CAP-24). If a single-flight busy exception
occurs, the watcher counts a busy-skip, not a failure.

**`Services/ChatWatcher.cs`** (rewritten; `CaptureFrame` stays in this file):

```csharp
public enum WatcherState { Stopped, NoRegion, Running, Idle, Paused, Error }   // Paused is new; Idle = NoGame
public sealed record CaptureFrame(CapturedImage Image, IReadOnlyList<OcrLine> Lines, TimeSpan OcrTime);

public sealed class ChatWatcher : IDisposable
{
    public ChatWatcher(OcrService ocr, ChatPipeline pipeline, Func<CaptureOptions> options,
                       Func<RunningGame?> currentGame, ILog log, TimeProvider time,
                       Action<GameWindowInfo?>? windowObserved = null);
    public WatcherState State { get; }
    public PauseReason PauseReason { get; }
    public string? LastError { get; }                 // always a CaptureText sentence
    public string? Hint { get; }                      // REQ-CAP-40
    public int UniformFrameStreak { get; }            // 0 when stopped
    public bool IsRunning { get; }
    public IDisposable BeginPreview();                // 01's lease API, unchanged
    public bool IsPreviewing { get; }
    public ScreenRegion? CurrentRegion { get; }       // last resolved; resolved on demand from options() when not running
    public RegionSource CurrentRegionSource { get; }
    public bool ClientSizeDiffers { get; }
    public CaptureMetricsSnapshot Metrics { get; }
    public CaptureHealth Health => Metrics.Health;
    public event Action<WatcherState>? StateChanged;  // background thread; raised when State, PauseReason or Health changes
    public event Action<CaptureFrame>? FrameCaptured; // background thread; only while IsPreviewing
    public event Action<ScreenRegion?>? RegionChanged;// background thread
    public void Start();       // resets watchdog budget, policy, metrics; PMv2 check once
    public void Stop();        // cancels the loop only; never in-flight translations
    public void Invalidate();  // fingerprint = 0, policy.Reset()
    public void Dispose();
}
```

`Func<AppSettings>`, `Func<string> currentGameId` and `ShouldRead` are removed. The snapshot provides
`GameIdFor`/`ShouldRead`.

**Tick** (contract §2.7, step numbers match). The loop belongs to generation `g`, and every state write checks
`g == _generation`.

1. `watchdog.Heartbeat()`.
2. `var o = options()`. This is the only settings read in the tick. Then `policy.Options = o.Polling`.
3. Probe and publish the game window:
   - `game = currentGame()`;
   - `info = GameWindowTracker.Observe(game, _streak)`;
   - if `info` differs from the last value this loop published, call `windowObserved?.Invoke(info)`.
4. Resolve the region and decide the pause:
   1. Compute `res = RegionMath.ResolveForGame(o, o.GameIdFor(game?.Profile.Id), info?.ClientRect)`. Raise
      `RegionChanged` if `res.Region` changed.
   2. Build the `CaptureObservation`. Take `Samples` only when all of these hold:
      - the source is `WindowRelative`;
      - `PauseWhenCovered` is on;
      - the backend cannot see occluded windows;
      - the game is not minimised.
   3. Compute `reason = Evaluate(obs, gate with PreviewOpen = IsPreviewing)` and
      `reading = Evaluate(obs, gate with PreviewOpen = false) == None`.
   4. If `reason != None`:
      - set the state (`NoRegion` → `NoRegion`, `NoGame` → `Idle`, anything else → `Paused`);
      - call `metrics.SetPause`;
      - set `outcome = Paused` and go to 12.
5. Capture: `RegionMath.OutputSize`, then `backend.Capture(target, pool)`, timed.
6. Fingerprint and skip:
   - `sig = FrameProcessing.Analyze(buffer.Pixels)` and `_streak = BlankFrames.Next(_streak, sig)`. `Hint` is set while
     `_streak == Threshold`.
   - If `sig.Hash == _lastHash` and `!IsPreviewing`: the frame is unchanged, so set `outcome = Unchanged` and go to 12.
   - If `ocr.IsBusy`: call `RecordOcrBusy`, leave `_lastHash` unchanged, set `outcome = Unchanged` and go to 12.
7. `FrameProcessing.Preprocess(buffer.Pixels, o.Preprocess)`.
8. `passes = await ocr.RecognizeAsync(buffer, o.OcrTimeoutMs, ct)`.
9. `lines = OcrMerger.MergeLines(passes, o.MyLanguage)`, then `_lastHash = sig.Hash`.
10. If `reading`, call `pipeline.ProcessAsync(lines, CancellationToken.None).Forget(log, "pipeline")`.
11. If `IsPreviewing`, raise `FrameCaptured(new CaptureFrame(new CapturedImage(buffer.ToArray(), w, h), lines,
    ocrTime))`.
12. In a `finally`:
    - `buffer?.Dispose()`;
    - record the metrics (`RecordSuccess` on `Changed`);
    - compute `delay = policy.DelayAfter(policy.Next(outcome), work)`;
    - `await Task.Delay(delay, time, ct)`.

**Errors.** Steps 5–11 are wrapped in one `catch` that excludes an `OperationCanceledException` for the loop's own
token. The catch does the following:

- calls `metrics.RecordFailure()`, plus `RecordOcrTimeout()` for a `TimeoutException`;
- sets `LastError = CaptureErrors.Describe(ex)`;
- calls `log.Warn("Capture tick failed", ex, kind)`;
- sets `outcome = Failed`;
- after 3 consecutive failures, calls `SetState(Error)`.

The pipeline's own faults are logged by `Forget` and are no longer copied into `LastError`.

**Watchdog.** A `TimeProvider` timer runs every 2 s while the loop runs and calls `Check()`.

- On `Stalled`, it restarts the loop:
  1. `_generation++`;
  2. cancel the old loop CTS;
  3. `CaptureBackendFactory.Create(o.Backend)`;
  4. start a new loop;
  5. `RecordRestart`;
  6. log a Warning.

  The old backend is disposed when its task finally completes. Translations are unaffected because they never saw the
  loop token.
- On `GiveUp`:
  1. stop the loop;
  2. `SetGaveUp(true)`;
  3. set `Error` with `LastError = CaptureText.StoppedResponding`;
  4. log an Error.

  02's existing `StateChanged` subscription then raises the `capture:error` problem toast.

**Logging** (category `Capture`, via `host.Log.For("Capture")`):

- Info: state and pause-reason transitions, and the backend in use at start.
- Warning: failures, timeouts, restarts, and the PMv2 check.
- Error: give-up.

OCR text is never logged.

### AppHost integration (shared `src/Envoy.App/AppHost.cs`, contract §3 positions)

- **Fields:** `private volatile RunningGame? _game;` and `private CaptureOptions _captureOptions;`. Accessors use
  `Volatile.Read` and `Volatile.Write`, and `public CaptureOptions CaptureOptions => Volatile.Read(ref
  _captureOptions);`.
- **Constructor position 7:** publish the initial `CaptureOptions.From(Settings)`, then:

  ```csharp
  Watcher = new ChatWatcher(Ocr, Pipeline, () => CaptureOptions, () => _game, Log.For("Capture"), Time, PublishGameWindow);
  Watcher.RegionChanged += _ => _ui.BeginInvoke(() => RegionMoved?.Invoke());
  ```
- **`ApplySettings`:** position 2 adds `Volatile.Write(ref _captureOptions, CaptureOptions.From(Settings))`, and
  position 6 is `Watcher.Invalidate()`.
- **Game window (contract §2.4):**

  ```csharp
  public GameWindowInfo? GameWindow { get; private set; }   // UI thread
  public event Action? GameWindowChanged;                   // UI thread
  private void PublishGameWindow(GameWindowInfo? info);     // any thread → _ui.BeginInvoke; sets and raises only if !Equals(GameWindow, info)
  ```

  A `_windowTimer` `DispatcherTimer` ticks every 1 s at `Background` priority for the host's lifetime, including while
  capture is stopped and in safe mode. Each tick does one of two things:
  - if `_game` is non-null, it calls `PublishGameWindow(GameWindowTracker.Observe(_game, Watcher.UniformFrameStreak))`;
  - otherwise, if `GameWindow` is non-null, it publishes `null`.

  Because `UniformFrameStreak` saturates at 5, a blank episode causes at most 5 extra change events. `RefreshGame` is
  not changed (contract §3 has no 05 position). `Dispose` stops the timer.
- **`CaptureStatus`** uses `CaptureText` and is notified on `StateChanged`:

  | Watcher state | Status |
  | --- | --- |
  | `Running` and `Health == Degraded` | `ReadingSlow` |
  | `Running` | `Reading` |
  | `Paused` | `CaptureText.Paused(PauseReason)` |
  | `Idle` | `WaitingForGame` |
  | `NoRegion` | `NoRegion` |
  | `Stopped` | `Stopped` |
  | `Error` | `LastError ?? CaptureFailed`, with no prefix |

  A non-null `Watcher.Hint` is appended on a new line.
- **`SetRegion(ScreenRegion absolute)`** does the following, in order:
  1. Writes `WindowRegions[CurrentGameId]` when `GameWindow` is known and `ToWindowRelative` is non-null.
  2. Always writes `Regions[CurrentGameId]`, and seeds `"any"` as today.
  3. Republishes `CaptureOptions`.
  4. Keeps today's sequence: `Invalidate`, `Start`, `SaveNow`, `SettingsApplied`.
- **`public event Action? RegionMoved;`** is raised on the UI thread.
- Every `catch` added here logs (contract §3).

**`App.xaml.cs`** (contract §4):

- Step 3, the smoke branch, adds `--soak <minutes>` → `SoakTest.Run(...)`, then exit (SHOULD).
- Step 10, overlay and hotkeys, adds `Host.RegionMoved += () => _overlay?.Reposition();` after the overlay is created.
  In safe mode there is no overlay and no subscription.

### UI (Reading Chat page = `PageIds.Capture`)

**`Pages/ReadingChatPage.xaml`** has these rows. Bindings live here (contract §7), and every new interactive control
gets `AutomationProperties.Name` (01 rule). Only existing `Text.*` styles and palette keys are used.

- **Region row:** `RegionText` shows the source. A `RegionHint` caption appears below it when the hint is non-empty
  (SHOULD).
- **Fine tuning:**
  - the checkbox "Adapt reading speed to chat activity";
  - "Fastest check" and "Slowest check" sliders, shown when adaptive polling is on;
  - the existing interval row, shown when it is off;
  - "Pause while another window covers the chat area";
  - "Pause when the game isn't the active window";
  - "Capture method", collapsed when `HasCaptureBackendChoice` is false.
- **Live view header:** `CapturePreview.MetricsText` and `CapturePreview.HealthText` (SHOULD).

**`ViewModels/MainViewModel.ReadingChat.cs`** contains:

- `RegionText`, built from `CaptureText.RegionFollows`/`RegionFixed`;
- `RegionHint`;
- `CaptureBackends`;
- `HasCaptureBackendChoice`.

`RegionText` and `RegionHint` are re-notified on `Host.RegionMoved` and `GameWindowChanged`.

**`ViewModels/CapturePreviewViewModel.cs`** changes in two ways:

- `Text` adapts to `OcrLine`, using `CapturePreviewText.Lines(frame.Lines.Select(l => l.Text).ToList())`.
- It adds `MetricsText` (`CaptureText.Stats(Watcher.Metrics)`) and `HealthText` (`CaptureText.Health(...)`). A 1 s
  `DispatcherTimer` runs only while `IsActive` is true and raises their change notifications. `Dispose` stops it.

The page code-behind is unchanged and still only toggles `IsActive`.

**`Windows/RegionSelectorWindow.xaml.cs`**:

- `OnUp` uses `Surface.PointToScreen` on both corners, normalised with min/max.
- `ShowCurrent` uses `Surface.PointFromScreen`.
- `Scale` is kept only for the size label.

**`Windows/OverlayWindow.xaml.cs`** (`Reposition` only, SHOULD): it places the window in physical pixels with
`SetWindowPos(…, SWP_NOSIZE|SWP_NOACTIVATE)` and repositions again on `DpiChanged`. Free mode keeps DIP `Left`/`Top`.

### Soak mode (SHOULD, `src/Envoy.App/SoakTest.cs`)

`SoakTest.Run(App app, string[] args, LogHub log)` works like this:

1. It builds an `AppHost` on `%TEMP%\envoy-soak-{guid}\data` and sets `host.Pipeline.Translator` to an in-file
   `SoakEchoTranslator`. That translator returns the text after 50 ms and makes no network call.
2. It sets these settings:
   - `OnlyWhileGameRuns = false`;
   - `Regions["any"]` set to the text area of a synthetic WPF window, which appends a line of rotating Latin, CJK,
     Hangul and Cyrillic chat every 3 s and scrolls.
3. It calls `Watcher.Start()`.
4. Every minute it logs a `SoakSample`. GDI and USER objects come from `user32!GetGuiResources` on the current process.
5. At the end it disposes everything, deletes the temp folder, and exits 1 when `SoakBudget.Violations` is non-empty.

### Allowlist additions

These must match contract §5. Each is added to `src/Envoy.App/Interop/Native.cs` **and** `tools/ci/policy.json` →
`native.allowed` in the same PR.

| Entry | Used by | Condition |
| --- | --- | --- |
| `user32!GetClientRect` | `GameWindowTracker.Observe` | always |
| `user32!ClientToScreen` | `Observe` (uses 02's `POINT`) | always |
| `user32!WindowFromPoint` | `GameWindowTracker.Sample` | always |
| `user32!GetAncestor` (`GA_ROOT`) | `Sample` | always |
| `user32!EnumWindows` | `GameDetector.FindMainWindow` | always |
| `user32!IsWindowVisible` | `FindMainWindow` | always |
| `user32!GetWindow` (`GW_OWNER`) | `FindMainWindow` | always |
| `user32!MonitorFromRect` (`MONITOR_DEFAULTTONULL`) | `IsOnAnyMonitor` | always |
| `user32!GetThreadDpiAwarenessContext` | `IsThreadPerMonitorV2` | always |
| `user32!AreDpiAwarenessContextsEqual` | `IsThreadPerMonitorV2` | always |
| `user32!GetGuiResources` | `SoakTest` | only if REQ-CAP-43 lands |
| `dwmapi!DwmGetWindowAttribute` | `WgcCaptureBackend` | only if REQ-CAP-37 lands |

Some functions are reused and not added. These come from 02: `POINT`, `MonitorFromWindow`, `GetMonitorInfoW` and
`MONITORINFO`. These already exist: `IsWindow`, `IsIconic`, `GetWindowRect`, `GetWindowThreadProcessId`,
`GetForegroundWindow`, `GetWindowLongPtrW`, `SetWindowPos`, and every GDI entry. The network and host allowlists get
**no additions**. No new data files are written, so there is no `DataInventory` change.

### Failure behaviour summary

| Situation | Behaviour |
| --- | --- |
| Game minimised | `Paused/GameMinimized`, 1 s wake-ups, no capture or OCR |
| Browser over the chat (GDI) | `Paused/ChatCovered` (can be turned off) |
| Windows locked / UAC secure desktop | `CaptureException` → back-off to 5 s; `Error` after 3; recovers on the first success |
| OCR hangs | Timeout after 4 s → `Degraded`; single-flight skips until the call ends; the watchdog covers a hung loop |
| Loop repeatedly hangs | More than 3 restarts in 5 min → `Error` with `StoppedResponding` and the 02 problem toast |
| Capture stopped or restarted mid-translation | Translations continue (token `None`); 04 owns their terminal state |
| Exclusive fullscreen (black) | `Hint` after 5 uniform frames; `UniformFrameStreak` feeds 07a |
| Window moved or resized | The region follows; re-select hint when the size changed |
| Region off every monitor | `Paused/RegionOffScreen` |
| Unknown backend id | GDI, reported in metrics |

---

## Acceptance criteria

Each MUST has at least one criterion of a kind allowed by contract §9: a unit test, typecheck, a smoke section, a CI
check, or a numbered manual step `M-CAP-n` in `docs/releasing.md`.

| ID | REQs | Kind | Verification |
| --- | --- | --- | --- |
| AC-CAP-1 | 1, 2 | Unit | `RegionMathTests`: `ToWindowRelative_*` (3), `Resolve_same_client_size_moves_with_window`, `Resolve_scales_proportionally_when_client_resized`, `Resolve_clamps_to_client`, `Resolve_handles_negative_monitor_coordinates`. |
| AC-CAP-2 | 1, 2 | Smoke | Section `capture-engine`, which runs in setup and once only: the test window is moved by (+50, +30) physical px. The resolved region moves by exactly (50, 30), and `GameWindowInfo` has `HasCaption = true`, `MonitorBounds ⊇ ClientRect` and `WindowRect ⊇ ClientRect`. |
| AC-CAP-3 | 4 | Unit | `RegionMathTests.ResolveForGame_order` (theory) and `CaptureSettingsTests.V01_capture_settings_load_with_new_defaults`. |
| AC-CAP-4 | 5, 9, 10, 13 | Unit | Every `CaptureGateTests` case. |
| AC-CAP-5 | 9 | Smoke | `capture-engine`: minimise the test window → `Observe(...).Minimized` and the gate returns `GameMinimized`. Restore → `None`. |
| AC-CAP-6 | 9, 10 | Manual | M-CAP-1. |
| AC-CAP-7 | 14, 19 (15–17) | Unit | `AdaptivePollPolicyTests` (all). |
| AC-CAP-8 | 22 (20, 21) | Unit | `FrameProcessingTests.Preprocess_matches_v01_within_one` (MUST). The SHOULD tests `Analyze_does_not_allocate`, `Preprocess_does_not_allocate` and `FramePoolTests.*` pass. |
| AC-CAP-9 | 25, 26 | Unit | `SingleFlightTests` (all five) and `CaptureMetricsTests.Timeout_marks_degraded_for_60s`. Typecheck: `OcrService.RecognizeAsync(FrameBuffer, TimeSpan, CancellationToken)` and `IsBusy` compile. |
| AC-CAP-10 | 27, 28 | Unit | `LoopWatchdogTests` (all) with `FakeTimeProvider`. |
| AC-CAP-11 | 30 | Unit | `CaptureMetricsTests.Failing_after_three_consecutive_failures`, `Success_clears_failing`, `Paused_when_pause_reason_set` and `GaveUp_is_failing`. `CaptureErrorsTests.Describe_never_returns_exception_text`. |
| AC-CAP-12 | 27, 30, 42 | Smoke | `capture-engine`, a 10 s cap. A dedicated `ChatWatcher` runs on the test window (fake `RunningGame`). It must reach `Metrics.FramesCaptured ≥ 3` with `Health != Failing`, `Backend == "gdi"` and `windowObserved` called with the test window's handle. With `BeginPreview()` held, one `FrameCaptured` arrives whose `Image.Pixels.Length == W×H×4`. Then it stops and disposes cleanly. |
| AC-CAP-13 | 31, 34 | Smoke + manual | `capture-engine` asserts `GameWindowTracker.IsThreadPerMonitorV2() == true`. It also opens `RegionSelectorWindow` on the 100% runner display: a DIP rectangle converted by the new code equals the expected physical rectangle ±1 px. Manual: M-CAP-2 (mixed DPI). |
| AC-CAP-14 | 41 | CI | `check_policy.py` (08a, `check` job) passes with the entries in [Allowlist additions](#allowlist-additions). `native-denied` finds nothing. BannedApiAnalyzers report no RS0030. |
| AC-CAP-15 | 42 | Unit | `OcrMergerTests.MergeLines_keeps_median_geometry_per_row`, `MergeLines_text_matches_Merge`, `MergeLines_left_right_reserved_zero`. Existing `OcrMergerTests` unchanged. `ChatPipelineTests.Lines_overload_matches_string_overload`. `CaptureOptionsTests.Snapshot_is_isolated_from_later_settings_edits`. |
| AC-CAP-16 | 42 | Typecheck | A new `check_ui_rules.py` rule (next free number, with a `test_check_ui_rules.py` case) fails when `src/Envoy.App/Services/ChatWatcher.cs` calls `ProcessAsync(` with anything other than `CancellationToken.None`, or reads `AppSettings`/`.Settings`. |
| AC-CAP-17 | 1 | Unit | 02's `SettingsRedactionTests.Every_marked_property_is_masked` covers `Capture.WindowRegions` (`W×H` only). |
| AC-CAP-18 | 35, 36 (SHOULD) | Smoke | `capture-engine`: `CaptureBackendFactory.Create("nonsense").Id == "gdi"`. |
| AC-CAP-19 | 20, 23 (SHOULD) | Smoke | `capture-engine` logs `steady-state alloc {n} B` for 20 captures of 320×120 at 2×. It logs a `WARNING` (not a failure) when n ≥ 307,200. |
| AC-CAP-20 | 38, 39, 8, 12 (SHOULD) | Unit + smoke | `CaptureMetricsTests.StatsLine_formats_expected_text` and `CaptureTextTests`. The per-palette `pages({id})` screenshots `{palette}-settings-capture.png` exist for every `ThemeManager.PaletteIds` entry and render the Fine tuning rows. Smoke binding-error check: zero errors. |
| AC-CAP-21 | 40 (SHOULD) | Unit + manual | `FrameProcessingTests.Analyze_reports_uniform_for_black_frame` and `BlankFramesTests`. Manual: M-CAP-5. |
| AC-CAP-22 | 43 (SHOULD) | Unit + manual | `SoakBudgetTests`. Manual: M-CAP-6. |
| AC-CAP-23 | all | DoD | `dotnet test tests/Envoy.Core.Tests` and `tools/typecheck/typecheck.sh` pass. Windows CI smoke prints `SMOKE TEST PASSED`. `[smoke] capture-engine ok elapsed_ms=<n>` with n ≤ 15000. |

---

## Test plan

### Unit tests (`tests/Envoy.Core.Tests/Capture/`, xUnit, `FakeTimeProvider`)

- **`RegionMathTests`:**
  - `ToWindowRelative_inside_client_stores_offset_and_reference_size`: client (100,50,1920,1080), region
    (120,900,600,150) → (20,850,600,150,1920,1080).
  - `ToWindowRelative_returns_null_when_region_leaves_client` (10 px overhang).
  - `ToWindowRelative_tolerates_2px_overhang_and_clamps`.
  - `Resolve_same_client_size_moves_with_window` (+500, −200).
  - `Resolve_scales_proportionally_when_client_resized`: 1920×1080 → 2560×1440, scaled by 4/3.
  - `Resolve_clamps_to_client`.
  - `Resolve_handles_negative_monitor_coordinates`.
  - `ResolveForGame_order` (theory over client known or unknown × which entries exist, including `ClientSizeDiffers`).
  - `OutputSize_matches_v01_formula` (theory).
  - `SamplePoints_returns_3x3_inset_grid_inside_region`.
- **`CaptureOptionsTests`:**
  - `From_copies_and_clamps`;
  - `Snapshot_is_isolated_from_later_settings_edits` (mutating `Regions`, `WindowRegions` and `IntervalMs` after
    `From` leaves the snapshot unchanged);
  - `GameIdFor_matches_AppHost_rule`;
  - `ShouldRead_matches_v01` (theory).
- **`FrameProcessingTests`:**
  - `Analyze_identical_frames_have_equal_hash`;
  - `Analyze_ignores_sub_quantisation_shimmer`;
  - `Analyze_detects_8px_text_run`;
  - `Analyze_reports_uniform_for_black_frame`;
  - `Analyze_does_not_allocate`;
  - `Preprocess_matches_v01_within_one` (theory over greyscale × contrast {0.5, 1, 1.3, 3} × invert, against a copy of
    the v0.1 `double` algorithm kept in the test file, on a seeded random 64×16 frame);
  - `Preprocess_does_not_allocate`;
  - `Preprocess_sets_alpha_opaque`.
- **`BlankFramesTests`:**
  - `Streak_counts_uniform_frames_and_saturates_at_threshold`;
  - `Non_uniform_frame_resets`.
- **`FramePoolTests`:**
  - `Rent_returns_exact_length_span_and_stride`;
  - `Dispose_returns_array_and_next_rent_reuses_it`;
  - `Double_dispose_is_safe`;
  - `ToArray_copies_exact_pixels`;
  - `Outstanding_tracks_rent_and_return`.
- **`AdaptivePollPolicyTests`:**
  - `Changed_frame_drops_to_min`;
  - `Stays_at_min_for_fast_frames_after_change`;
  - `Unchanged_frames_back_off_by_factor_to_max`;
  - `Failures_double_to_error_cap_and_success_recovers`;
  - `Paused_uses_paused_interval_and_preserves_current`;
  - `Disabled_adaptive_uses_fixed_interval`;
  - `Options_clamp_out_of_range_settings` (theory);
  - `DelayAfter_respects_duty_cycle_and_floor` (600/300 → 600; 0/300 → 300; 290/300 → 290);
  - `Reset_returns_to_min`.
- **`CaptureGateTests`:**
  - `No_region_always_pauses_even_with_preview`;
  - `Off_screen_region_pauses_even_with_preview`;
  - `Minimized_game_pauses_even_with_preview`;
  - `No_game_pauses_unless_preview_open`;
  - `Covered_region_pauses_when_enabled_and_backend_cannot_see_occluded` (3 of 9 → `ChatCovered`; 2 of 9 → `None`);
  - `Covered_ignored_when_backend_sees_occluded`;
  - `Covered_ignored_for_absolute_regions`;
  - `Envoy_windows_never_count_as_covering`;
  - `Not_foreground_pauses_only_when_setting_on`;
  - `Envoy_foreground_does_not_count_as_not_foreground`;
  - `Preview_overrides_no_game_covered_and_not_foreground`;
  - `Reading_is_false_when_only_preview_override_allows_capture`.
- **`SingleFlightTests`:**
  - `Completed_call_clears_busy`;
  - `Second_call_while_busy_throws_busy`;
  - `Timeout_throws_TimeoutException_and_cancels_work_token`;
  - `Busy_stays_true_after_timeout_until_work_completes`;
  - `Faulted_work_clears_busy_and_rethrows`.
- **`LoopWatchdogTests`:**
  - `Healthy_while_heartbeats_arrive`;
  - `Stalled_after_threshold`;
  - `Gives_up_after_more_than_three_restarts_in_five_minutes`;
  - `Restart_budget_recovers_after_window_and_ResetBudget`;
  - `StallThreshold_is_max_of_15s_and_formula` (theory).
- **`CaptureMetricsTests`:**
  - `Averages_use_last_50_samples`;
  - `Counts_captured_and_skipped_frames`;
  - `Timeout_marks_degraded_for_60s` (Degraded at t+59 s, Healthy at t+61 s);
  - `Failing_after_three_consecutive_failures`;
  - `Success_clears_failing`;
  - `Paused_when_pause_reason_set`;
  - `GaveUp_is_failing`;
  - `StatsLine_formats_expected_text` (under `de-DE`);
  - `Concurrent_recording_is_consistent`.
- **`CaptureTextTests` / `CaptureErrorsTests`:**
  - `Paused_has_text_for_every_reason`;
  - `No_text_starts_with_Error_prefix`;
  - `Describe_never_returns_exception_text` (an exception with message "secret-xyz" never appears in the output);
  - `Region_formats_fill_named_placeholders`.
- **`SoakBudgetTests`** (SHOULD):
  - `Growth_over_20_percent_is_reported`;
  - `Minute_5_is_the_baseline`;
  - `Short_runs_report_nothing`.
- **`OcrTests.cs` additions:**
  - `OcrMergerTests.MergeLines_keeps_median_geometry_per_row`;
  - `MergeLines_text_matches_Merge`;
  - `MergeLines_left_right_reserved_zero`.
- **Pipeline:** `ChatPipelineTests.Lines_overload_matches_string_overload`. The same input through both overloads
  emits the same messages.
- **`CaptureSettingsTests`:**
  - `V01_capture_settings_load_with_new_defaults`;
  - `Capture_settings_round_trip_window_regions_and_polling`.

### Typecheck

- A new `check_ui_rules.py` rule plus a test case (AC-CAP-16).
- `gen_stubs.py` regenerates for the new App types.
- `typecheck.sh` compiles `GameWindowTracker`, the backends, the `OcrService` overload and the `ChatWatcher`
  signature.

### Smoke (`src/Envoy.App/SmokeTest.cs`, contract §6)

- **Setup phase, once.** Add `CaptureEngineSmokeAsync(AppHost host, LogHub log)` as section `capture-engine`. It must
  finish within 15 s.
  1. Show a captioned 640×360 test window titled "Envoy smoke game", with a `TextBlock` of sample chat text, and get
     its HWND. Assert `IsThreadPerMonitorV2()`.
  2. Create `fake = new RunningGame(GameCatalog.Find("aoe2de")!, Environment.ProcessId, hwnd)`. Assert that
     `Observe(fake, 0)` is non-null, not minimised, `HasCaption`, and that its `MonitorBounds`/`WindowRect` contain
     `ClientRect` (AC-CAP-2).
  3. Relative round trip with a move of (+50, +30) via `Native.SetWindowPos` (AC-CAP-2).
  4. Minimise and restore (AC-CAP-5).
  5. The DIP-to-physical check on `RegionSelectorWindow` (AC-CAP-13).
  6. The steady-state allocation log (AC-CAP-19) and the factory fallback (AC-CAP-18).
  7. Run a dedicated `ChatWatcher` with these inputs:
     - `CaptureOptions.From` of an in-memory `AppSettings`, with `WindowRegions["aoe2de"]` = (20,20,300,100) and
       `OnlyWhileGameRuns = false`;
     - `currentGame = () => fake`;
     - a `windowObserved` recorder;
     - a separate `ChatPipeline` with a no-op translator, so no request leaves the runner.

     Hold a preview lease, then make the assertions of AC-CAP-12. Dispose.
  8. Close the test window.

  The existing `capture-ocr` section additionally calls `host.Ocr.RecognizeAsync(frame, 10 s, None)`.
- **Per palette.** No new block. The `{palette}-settings-capture.png` screenshots and the binding-error check cover
  the new rows.
- **Teardown.** Nothing new. The dedicated watcher is disposed inside its section.

### Manual pass (05 appends these to `docs/releasing.md` under "Capture engine"; 08b consolidates them)

1. **M-CAP-1** (REQ-CAP-9, 10). In AoE II: DE windowed or borderless, minimise the game. Within 2 s the War Room shows
   "Paused while the game is minimised." and Task Manager shows Envoy below 1% CPU. Restore it: "Reading chat" returns
   within 2 s. Drag a browser over the chat: the status shows the covered text. Move the browser away: reading
   resumes.
2. **M-CAP-2** (REQ-CAP-31, 34). Use two monitors, the primary at 150% and the secondary at 100%, then swap them.
   Select the chat area on each monitor. The Live view shows exactly the dragged area, within 1 px at 4× zoom, and
   reopening the selector draws the rectangle in the same place.
3. **M-CAP-3** (REQ-CAP-2, 7, 33). Drag a windowed game 300 px. The captured area follows, and the overlay anchored
   "Above chat" follows within about 1 s. Then move the game to the other-DPI monitor.
4. **M-CAP-4** (REQ-CAP-14, 19, 30). In a quiet match the Live view shows `every 1.5 s` with a skip rate of at least
   90%. When a friend types, it shows `every 0.3 s`. Then lock the PC (Win+L) for 20 s and unlock it: the status
   returns to "Reading chat" within 6 s without toggling.
5. **M-CAP-5** (REQ-CAP-40). Use exclusive fullscreen where the game offers it, or select an all-black area. The
   blank-area hint appears within 5 frames.
6. **M-CAP-6** (REQ-CAP-43). Run `Envoy.exe --soak 120`. It exits 0, and the log shows no metric growing by more than
   20%.
7. **M-CAP-7** (REQ-CAP-11). Alt-tab to a browser on a second monitor with the game still visible and
   `PauseWhenNotForeground` off. Chat is still read.

---

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Capture/CaptureOptions.cs`, `GameWindowInfo.cs`, `RegionMath.cs`, `FramePool.cs`, `FrameProcessing.cs`, `AdaptivePollPolicy.cs`, `CaptureGate.cs`, `SingleFlight.cs`, `LoopWatchdog.cs`, `CaptureMetrics.cs`, `CaptureText.cs`, `CaptureErrors.cs`, `CaptureBackendIds.cs`, `SoakBudget.cs` | new | |
| `src/Envoy.Core/Ocr/OcrResults.cs` | `OcrLine` `Left`/`Right` (reserved), `MergeLines`, `Merge` delegates | 04 extends later |
| `src/Envoy.Core/Pipeline/ChatPipeline.cs` | `ProcessAsync(IReadOnlyList<OcrLine>, ct)` forwarding overload | 04 rewrites later |
| `src/Envoy.Core/Settings/AppSettings.cs` | `WindowRelativeRegion`; 8 `CaptureSettings` properties; `[Location]` on `WindowRegions` | **shared** |
| `src/Envoy.Core/Diagnostics/SettingsRedaction.cs` | only if needed: `[Location]` handles `WindowRelativeRegion` | 02's file |
| `src/Envoy.App/Services/ChatWatcher.cs` | rewrite (§2.7 tick, gate, policy, pool, backend, single-flight, watchdog, metrics, `Paused`, `CaptureFrame` shape) | |
| `src/Envoy.App/Services/Capture/ICaptureBackend.cs`, `GdiCaptureBackend.cs`, `CaptureBackendFactory.cs` | new (`WgcCaptureBackend.cs` only if COULD lands) | |
| `src/Envoy.App/Services/ScreenCapturer.cs` | facade over GDI backend and `FrameProcessing` | |
| `src/Envoy.App/Services/OcrService.cs` | `FrameBuffer` overload, `SingleFlight`, `IsBusy`, `AsBuffer` | 04 fills Left/Right later |
| `src/Envoy.App/Services/GameWindowTracker.cs` | new | |
| `src/Envoy.App/Services/GameDetector.cs` | `FindMainWindow(pid)` | |
| `src/Envoy.App/Interop/Native.cs` | the 10 user32 entries in the allowlist table | **shared** |
| `tools/ci/policy.json` | the same 10 entries in `native.allowed` | **shared** |
| `src/Envoy.App/AppHost.cs` | `volatile _game`, `CaptureOptions` publish (ApplySettings pos. 2), watcher at pos. 7, `Invalidate` at pos. 6, `GameWindow`/`GameWindowChanged`/`_windowTimer`, `CaptureStatus` via `CaptureText`, `SetRegion`, `RegionMoved` | **shared** |
| `src/Envoy.App/App.xaml.cs` | `RegionMoved` → overlay at step 10; `--soak` at step 3 (SHOULD) | **shared** |
| `src/Envoy.App/Pages/ReadingChatPage.xaml` | region source and hint, Fine tuning rows, metrics and health lines, `AutomationProperties.Name` | |
| `src/Envoy.App/ViewModels/MainViewModel.ReadingChat.cs` | `RegionText`, `RegionHint`, `CaptureBackends`, `HasCaptureBackendChoice` | |
| `src/Envoy.App/ViewModels/CapturePreviewViewModel.cs` | `OcrLine` adaptation; `MetricsText`, `HealthText` and the 1 s timer | 04 adds "Understood as" later |
| `src/Envoy.App/Windows/RegionSelectorWindow.xaml.cs` | `PointToScreen`/`PointFromScreen` | 07b changes its XAML later |
| `src/Envoy.App/Windows/OverlayWindow.xaml.cs` | `Reposition` in physical px and on `DpiChanged` (SHOULD) | 07c later |
| `src/Envoy.App/Windows/LookupWindow.xaml.cs` | `RegionMath.OutputSize` | |
| `src/Envoy.App/SmokeTest.cs` | setup section `capture-engine`; `capture-ocr` timeout overload | **shared** |
| `src/Envoy.App/SoakTest.cs` | new (SHOULD) | |
| `tools/typecheck/check_ui_rules.py`, `test_check_ui_rules.py` | capture-token/settings rule | |
| `tests/Envoy.Core.Tests/Capture/*.cs`, `OcrTests.cs`, pipeline tests | tests above | |
| `docs/architecture.md` | loop diagram, `CaptureOptions` threading, `GameWindow` ownership | |
| `docs/troubleshooting.md` | pause reasons, blank-area hint, turning off "covered" | |
| `docs/releasing.md` | M-CAP-1…7 | 08b consolidates |
| `CLAUDE.md` | rule: "Background capture code reads `CaptureOptions`, never `AppSettings`; `ProcessAsync` gets `CancellationToken.None`" | |
| `CHANGELOG.md` | `## Unreleased` entry | |

Estimated size without WGC and soak: Core about 600 lines, tests about 500, App about 600.

---

## Risks & open questions

- **Proportional scaling on resize is a guess.** Game UIs may anchor the chat to an edge or scale it with a UI-scale
  setting. Mitigations:
  - exact translation when the size is unchanged, which is the common case;
  - the re-select hint (REQ-CAP-8);
  - re-selecting overwrites the stored region.

  *Open:* should we store one region per client size?
- **False "covered" pauses.** These can come from invisible, non-click-through windows such as recorders and vendor
  overlays. Mitigations:
  - the 3-of-9 threshold;
  - a clear status;
  - a one-click opt-out.
- **Preview OCRs unchanged frames.** Contract §2.7 step 6 requires this while the Live view is open. It costs CPU only
  while the page is open, and single-flight and the adaptive interval still bound it.
- **Envoy's overlay over the chat.** GDI with `CAPTUREBLT` captures our own overlay. The default anchors avoid this.
  `WDA_EXCLUDEFROMCAPTURE` would also hide the overlay from streams, so 07c decides.
- **Abandoned OCR operations.** WinRT cancellation is best-effort, so a permanently hung engine keeps `IsBusy` true.
  The status then reads "slow". Toggling capture recreates the engines through `OcrService.Configure`. A SHOULD
  follow-up recreates them automatically after 3 timeouts in a row.
- **`GameWindowChanged` churn.** The watcher and the timer both publish, but equality dedupe and the saturating streak
  bound the events to real changes. 06 and 07a must treat the event as "re-read `GameWindow`".
- **Forwarding `ProcessAsync` overload before 04.** It is deliberately trivial, and its test pins behaviour so that
  04's rewrite is a visible diff.
- **ArrayPool retention.** At most 2 arrays per bucket are retained, which is under 5 MB for a typical region and about
  54 MB in the worst case. The soak run watches it.
- **`MainWindowHandle` vs. the real game window.** `FindMainWindow` picks the largest visible unowned window, and the
  3 s refresh corrects a launcher choice.
- **Mixed-DPI verification needs hardware.** CI has one 100% display, so M-CAP-2 is the gate for that case.
- *Open:* is `MaxIntervalMs = 1500` right for every title? It is configurable. Revisit it with Live view metrics.

---

## Revision notes

These changes were made against `00-integration.md` and the set critique.

- **Scope and MUST budget.** MUSTs went from 31 to the contract's 19.
  - REQ-CAP-3 was folded into 2, 32 into 31, and 29 into 30.
  - REQ-CAP-12, 15, 16, 18, 20, 21, 35, 36, 38 and 39 became SHOULD.
  - REQ-CAP-6 became COULD and is not built, so the `RefreshGame` upgrade and old AC-CAP-21 are removed. This follows
    the critique minor: there is no installed base.
  - New REQ-CAP-42 (MUST) and REQ-CAP-43 (SHOULD) were added.
- **Pending-message bug** (critique major on 05). The watcher passes `CancellationToken.None` with `.Forget(log,
  "pipeline")`. Stop and watchdog restarts no longer strand translations. A typecheck rule enforces the token. 04 owns
  the lifecycle and its tests (contract §2.6).
- **04/05 watcher conflict** (critique major). REQ-CHAT-1 and REQ-CHAT-3 moved here as REQ-CAP-42, which covers:
  - `MergeLines`;
  - `OcrLine` with reserved `Left`/`Right`;
  - the contract `CaptureFrame` shape with an exact-length copy;
  - the tick rewritten as contract §2.7's 12 steps.

  To respect the arrival rule, a forwarding `ProcessAsync(IReadOnlyList<OcrLine>)` overload is added for 04 to fill.
- **Preview** (contract §2.8). `PublishFrames` is gone, and the gate and step 11 use 01's `BeginPreview`/`IsPreviewing`.
  Under preview, unchanged frames are OCR'd, as contract step 6 requires. This replaces the old REQ-CAP-21 rule. Frames
  allowed only by the preview override go to the Live view, not to the pipeline. The metrics timer moved from
  `MainWindow.xaml.cs` into `CapturePreviewViewModel` (critique major).
- **Game-window ownership** (critique minor; contract §2.4).
  - `GameWindowInfo` moved to Core and gained `WindowRect`, `HasCaption`, `MonitorBounds` and `UniformFrameStreak`.
  - `AppHost.GameWindow` and `GameWindowChanged` are published from both the tick and a 1 s UI timer that runs even
    when capture is stopped.
  - `CaptureText.LikelyExclusiveFullscreen` is the single advice text that 07a reuses.
- **Settings thread safety** (critique missing area; contract §7). The new `CaptureOptions` snapshot is built in
  `ApplySettings` position 2 and published with `Volatile.Write`, and it has an isolation test. It replaces
  `Func<AppSettings>`, `currentGameId`, `ShouldRead` and copy-on-write dictionaries. `_game` is volatile.
- **Contract positions.** The watcher is at constructor position 7 and `Invalidate` at `ApplySettings` position 6.
  There is no `RefreshGame` change. `RegionMoved` is wired at `OnStartup` step 10 and `--soak` at step 3.
- **Logging and time.** The old "ILog optional" wording was removed because 02 has landed. The spec now uses
  `Log.For("Capture")`, `Forget`, and `AppHostOptions.TimeProvider` for every timed type.
- **UI strings** (contract §8.7). All status, error and region text is in `CaptureText`. `LastError` is never raw
  exception text, and the status no longer has an "Error:" prefix. `Stopped` now reads "Chat reading is off", which
  separates it from the automatic pauses that 07a maps to `AutoPaused`.
- **Testability.** OCR timeout and single-flight logic moved into Core `SingleFlight`, so REQ-CAP-25 and 26 have unit
  tests. Error-to-text mapping is in `CaptureErrors` with a unit test. Every MUST now has a unit, typecheck, smoke, CI
  or numbered M-step criterion.
- **Smoke structure** (contract §6). `capture-engine` runs once in the setup phase with a 15 s budget. Its watcher
  uses a separate offline pipeline. AC wording uses `ThemeManager.PaletteIds` and `{palette}-settings-capture.png`
  instead of "5 themes" (critique minor). The allocation check only warns, because it is a SHOULD.
- **Policy** (critique blocker on allowlists). The exact `user32!` entries are listed for `Native.cs` and `policy.json`.
  `GetGuiResources` and `DwmGetWindowAttribute` are conditional. There are no network or host additions. The old grep
  AC was replaced by 08a's `check_policy.py`. `GdiCaptureBackend` must be analyzer-clean (contract §8.8).
- **Privacy and a11y.** `WindowRegions` is `[Location]`. New controls get `AutomationProperties.Name`.
- **Newly assigned.** Soak mode was added as REQ-CAP-43 (SHOULD, contract §8.5), with Core `SoakBudget` and the
  `FramePool` counters. The manual M-steps were renumbered as M-CAP-1…7 for `docs/releasing.md`.
- **Adaptive polling default.** The spec now states explicitly that `AdaptivePolling = true` changes today's
  fixed-interval behaviour, as contract §7 allows (critique minor).

---

## Deviations

Every MUST and SHOULD is built. REQ-CAP-6 and REQ-CAP-37 (both COULD) are not, as planned. Differences from this spec
and the contract:

1. **COULDs not built.** There is no `WgcCaptureBackend` (REQ-CAP-37), so `dwmapi!DwmGetWindowAttribute` is not
   allowlisted, `CaptureBackendFactory.Available()` lists GDI only and the "Capture method" row stays collapsed. There
   is no one-time derivation of relative regions from v0.1 absolute ones (REQ-CAP-6). The SHOULD follow-up in Risks
   (recreate the OCR engines after 3 timeouts in a row) is not built either; toggling capture still recreates them.
2. **`SetRegion` probes the window itself.** It calls `GameWindowTracker.Observe(_game, …)` at the moment of
   selection instead of reading the cached `AppHost.GameWindow`, which can be up to 1 s old. A minimised window gives
   no relative region. When the new area lies outside the game's client area, `SetRegion` also **removes**
   `WindowRegions[gameId]`, so a stale relative region cannot override the fixed position the user just chose. It
   logs the selection (size only, `[Location]` rules) under `Capture`.
3. **Minimised game keeps its last client rect.** While the window is minimised (its client rect is meaningless),
   the tick resolves the region against the last client rect it saw, so the region does not jump and `RegionChanged`
   does not fire twice per minimise.
4. **`CaptureGate.IsReading(observation, options)`** is a small Core helper for step 4.3's "`reading` = evaluate
   with `PreviewOpen = false`"; behaviour is as specified.
5. **Extra public members.** `ChatWatcher.Pool` (the soak run reads its counters); `ChatWatcher.StateChanged` is also
   raised when `Hint` appears or clears, so the status line's second line updates. `OcrService` takes an optional
   `TimeProvider` (for its `SingleFlight`). `ScreenCapturer.ScaleFor` wraps `RegionMath.OutputSize` for
   `LookupWindow`, which also shows `CaptureText.ScreenUnreadable` for a `CaptureException`.
   `RegionSelectorWindow.ToScreen(Rect)` is internal so the smoke test can check the DIP-to-physical conversion.
   `MainViewModel.HasRegionHint` drives the hint caption's visibility. `CaptureText` gains `RegionNotSet`,
   `BackendGdi` and the three `Health*` sentences; `CaptureMetrics` exposes `FailingAfter`, `DegradedFor` and
   `CaptureMetricsSnapshot.Empty`; `RegionMath` exposes `SampleInsetPx` and `RegionResolution.None`.
6. **`HealthText` while stopped** reads `CaptureText.Stopped` rather than "working normally".
7. **Soak details.** The offline translator is `EchoTranslator` (shared with the smoke test's dedicated watcher)
   rather than an in-file `SoakEchoTranslator`. `--soak <minutes> [log-path]` writes `soak-test.log` (default: the
   current directory), like the smoke test's log, and nothing under `%APPDATA%`. It uses `SetRegion` on a fresh temp
   host, so the region is stored as `Regions["any"]` exactly as in the design.
8. **Smoke allocation figure** uses `GC.GetAllocatedBytesForCurrentThread()` around 20 synchronous capture +
   fingerprint + preprocess frames; it warns, never fails (AC-CAP-19). The dedicated smoke watcher uses
   `TimeProvider.System`.
9. **Extra tests** beyond the plan: `Reading_when_everything_is_fine`, `Swapping_options_reclamps_current`,
   `Health_has_text_for_every_state`, `StallAfter_can_change_while_running`,
   `Null_entries_from_a_hand_edited_file_are_skipped`, `Caller_cancellation_cancels_the_work`,
   `ResolveForGame_reports_a_different_client_size`, `ResolveForGame_any_game_uses_the_any_region` and
   `Intersect_and_Contains`. The `CaptureText`/`CaptureErrors`/`SoakBudget` tests live in `CaptureMetricsTests.cs`,
   and `BlankFramesTests`/`FramePoolTests` in `FrameProcessingTests.cs`; `CaptureSettingsTests` is in
   `CaptureOptionsTests.cs`.
10. **Typecheck rule R11** also treats any `.Settings` member access in `ChatWatcher.cs` (outside `using` lines and
    comments) as a settings read, which is stricter than "reads `AppSettings`".
11. **The Windows-only criteria** (smoke `capture-engine`, the per-palette `settings-capture` screenshots, AC-CAP-23's
    `SMOKE TEST PASSED`) are verified by CI on push; this Linux environment ran the Core tests, the typecheck and the
    policy check.
