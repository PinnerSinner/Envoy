# 00 — Integration contract for the 1.0 specs

Status: **Accepted (binding)**

This file decides every cross-area question raised in the set review of specs 01–08. When it disagrees with an area
spec, this file wins. The first commit of each area's change edits that area's spec so it matches this file:
renames, demotions, removed requirements and new ones. The spec and the code then never disagree.

Specs 07 and 08 are split (the split files already exist):
- `07-onboarding-ux-accessibility.md` → `07a-onboarding-notices.md`, `07b-accessibility.md`, `07c-reply-overlay-polish.md`.
- `08-quality-security-ci.md` → `08a-quality-gates.md`, `08b-ci-release.md`.

The old files remain only as short "Superseded" pointers and are deleted in the first implementation commit of 08a
(for `08-…`) and 07a (for `07-…`). Requirement numbers are unchanged. 08a and 08b keep `REQ-QSC-n`; 07a, 07b and 07c
use the prefixes `REQ-OUXA-n`, `REQ-OUXB-n` and `REQ-OUXC-n` with the old `REQ-OUX-n` number, so `OUX-13` in §9 is
`REQ-OUXA-13`. `docs/specs/README.md` indexes the set and its process.

## 1. Implementation order

The steps run strictly one after another. Each lands on `main` with CI green before the next one starts. A step may
assume only what earlier steps have delivered, as listed below.

| # | Area | May assume exists | Delivers for later steps |
| --- | --- | --- | --- |
| 1 | 01-ui-foundation | today's code | `Pages/*`, `PageIds`, `MainViewModel.*` partials, `Envoy.Core.Mvvm`, `IShell` (01 members), `DialogService`, `CapturePreviewViewModel` + `ChatWatcher.BeginPreview()`, `HotkeyRegistrationResult` + `AppHost.HotkeyResults`, `TextTemplate` (§8.7), smoke loop over `PageIds.All`, smoke `WaitForExit(600000)` |
| 2 | 08a-quality-gates | 1 | `Directory.Build.props`, analyzers, `BannedSymbols.txt`, `tools/ci/check_policy.py` + `policy.json` (run in the existing `check` job), `Security/*` (`OutgoingText`, `SendTargetGuard`, `ShellLauncher`, `ReleaseUrlPolicy`, `PhraseLimits`), `.config/dotnet-tools.json`. The workflow layout is unchanged. |
| 3 | 02-reliability-observability | 1–2 | `ILog`/`LogHub`/`LogRedactor`, `AppHostOptions`, instance data paths, global handlers, `Forget`, `NotificationThrottle` + `ToastService`, diagnostics, safe mode, `StartupSentinel`, `DataInventory`, the `OnStartup`/`ExitApp` skeleton from §4, `[Personal]`/`[Location]`, `docs/privacy.md` with its "What Envoy stores on your PC" section |
| 4 | 03-translation-resilience | 1–3 | PR A: error kinds, retry, breaker, throttle, `TranslationRuntime`, `ProviderHealthRegistry`. PR B: cache persistence and the Translation page health UI. Each PR is a separate CI-green landing. |
| 5 | 05-capture-engine | 1–4 | `ChatWatcher` rewrite, the tick algorithm (§2.7), `CaptureFrame`, `OcrLine` with reserved `Left`/`Right`, `OcrMerger.MergeLines`, `GameWindowTracker` + `AppHost.GameWindow`, capture-options snapshot |
| 6 | 04-chat-understanding | 1–5 | stitching, dedupe, system lines, cleaner, the pending-message lifecycle (§2.6), "Understood as" in `CapturePreviewViewModel` |
| 7 | 06-distribution-updates | 1–6 | `Program.Main`, Velopack, `UpdateCoordinator`, `UpdateViewBuilder`, `AppHost.AppVersion`, `RestartToUpdate`, tag-only signing |
| 8 | 07a-onboarding-notices | 1–7 | setup guide, `NoticeCenter`, `TrayNotifier`, War Room layout (§8.6), privacy disclosure, the data-flow sections of `docs/privacy.md`, FAQ note |
| 9 | 07b-accessibility | 1–8 | focus ring, names, `check_a11y.py`, high-contrast palette, `ThemeResolver`, contrast fixes |
| 10 | 07c-reply-overlay-polish | 1–9 | reply history, counter, compact overlay |
| 11 | 08b-ci-release | 1–10 | workflow restructure, coverage gate, CodeQL, SBOM, OCR end-to-end test, compatibility matrix, release checklist |

The arrival rule is strict. No step writes code against a type that a later step introduces. Placeholder members
"for later" are not allowed. A later step adds its own members to `AppHostOptions`, `IShell` and the settings.

## 2. Single owners for shared concepts

| Concept | Owner | Consumers | Removed duplicates |
| --- | --- | --- | --- |
| Provider health | 03 `ProviderHealthRegistry` via `TranslationRuntime.Health` | 02 diagnostics, 07a notices | 02 `ProviderHealthTracker`/REQ-REL-26 and its `ProviderHealth` record; 07 `ProviderFailureMonitor`/REQ-OUX-15 |
| Notifications | 02 `NotificationThrottle` (Core) + `ToastService`; 07a `NoticeCenter` + `TrayNotifier` built on that throttle | all | 07's own rate-limit fields; `QuietWhileGameFocused` |
| Hotkey registration results | 01 `AppHost.HotkeyResults` | 02 diagnostics, 07a cheat sheet and hints | `App.HotkeyErrors`, `Host.HotkeyFailures`, `App.FailedHotkeyCombos`, `IShell.IsHotkeyTaken` |
| Game-window probing | 05 `GameWindowTracker` → `AppHost.GameWindow` | 06 game-running check, 07a display verdict and in-game policy | 07 `DisplayModeProbe` P/Invokes, `AppHost.IsGameInForeground` |
| Update state | 06 `UpdateCoordinator` | 07a update notice, About page, tray | 07 `CheckForUpdatesAsync` hook, `Notifications.LastAnnouncedUpdate` |
| Pending-message lifecycle | 04 `ChatPipeline` | overlay, 05 watcher | — |
| `CaptureFrame` and the tick | 05 | 04, `CapturePreviewViewModel`, 07a wizard | 04's `CaptureFrame` shape change |
| Live view state | 01 `CapturePreviewViewModel` + preview lease | 04, 05, 07a wizard | `ChatWatcher.PublishFrames` |
| Redaction | 02 `LogRedactor` | 03 `ProviderHealthText.Sanitize` (delegates; mask is `[redacted]`), 06, 07a | 03's `***` mask |
| Modal dialogs | 01 `Services/DialogService.cs` | 02 confirmations, phrase editing | `MessageBox.Show` anywhere else in `src/Envoy.App` |

### 2.1 Provider health (03)

- `ProviderState { Unknown, Healthy, Degraded, Throttled, CoolingDown, AuthFailed, QuotaExceeded }` and
  `ProviderHealthSnapshot` stay as 03 defines them.
- `TranslationRuntime` raises `ProviderHealthChanged(ProviderHealthSnapshot)` on every state transition, including a
  transition caused by a breaker-skipped call.
- Names that do not collide: `AppHost.TranslationHealth` (the registry) and `MainViewModel.ProviderHealthRows`. No
  other type or member may be called `ProviderHealth`.
- 02's diagnostics report reads `TranslationRuntime.Health.Snapshot()`. 02 logs `TranslationRuntime.Diagnostic`
  events at Info under category `Translation`. 03 lands after 02, so 03 adds that log subscription in AppHost.

### 2.2 Notifications

There are exactly three surfaces:

1. **Non-activating toast** (`ToastWindow`, 02). It is topmost, `ShowActivated=false` and
   `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, so it never takes focus.
2. **Tray balloon** (`TrayNotifier`, 07a).
3. **War Room notice list** (07a).

No surface is modal. The only modal boxes allowed are `DialogService` confirmations that the user starts from
`MainWindow`, plus the crash-loop dialog if REQ-REL-15 (now SHOULD) is built.

Every item has a class:

- **Problem** (02): capture error, settings recovered or reset, previous session crashed, reply send blocked, export
  failed.
- **Advisory** (07a): `OcrLanguageMissing`, `ProviderFailing`, `UpdateAvailable`.

There is one Core throttle. `Envoy.Core.Notifications.NotificationThrottle`, owned by 02, is a single instance on
`AppHost.Notifications`, and every surface asks it before showing anything. Its rules:

- the same key is shown at most once per 2 min;
- at most 3 toasts are visible at once;
- advisory balloons are at least 60 s apart, and at most 5 are shown per session.

`NoticeCenter` keeps the `Active` list, the dismissed keys and the deferral queue. It never keeps its own counters.

There is one in-game policy, keyed on `AppHost.GameWindow.GameForeground`:

| | Game window in foreground | Otherwise |
| --- | --- | --- |
| Problem | toast now | toast now |
| Advisory | **deferred** (queued, one per key, latest wins), never dropped | balloon, plus War Room list |

Deferred advisories are re-offered through `NoticeCenter.TakeDeferred` when `GameForeground` turns false or the game
exits. `AppHost.GameWindowChanged` triggers this, and a 5 s timer covers missed events. Further rules:

- While the setup guide is open, `Hold(true)` defers every advisory.
- In safe mode, `NoticeCenter` and `TrayNotifier` are not created, and only problem toasts appear.
- `UpdateAvailable` goes to a balloon only. The War Room shows updates through 06's banner, never through the notice
  list.
- `ProviderFailing` is raised from `ProviderHealthChanged` when the state becomes `AuthFailed`, `QuotaExceeded` or
  `CoolingDown`. It is resolved when the state returns to `Healthy`, which re-arms it.
- `Diagnostics.ShowErrorToasts=false` turns problem toasts off. `Notifications.Enabled=false` turns advisory balloons
  off. Both are still logged, and advisories still appear in the War Room list.

### 2.3 Hotkey registration results (01)

```csharp
public enum HotkeyStatus { Registered, NotSet, Taken, Invalid, SkippedSafeMode } // 01 ships the first four; 02 appends SkippedSafeMode (arrival rule)
public sealed record HotkeyRegistrationResult(string ActionId, string ActionName, string Combo, HotkeyStatus Status);
// AppHost: IReadOnlyList<HotkeyRegistrationResult> HotkeyResults { get; set; } = []; event Action? HotkeyResultsChanged;
```

`App.RegisterHotkeys` replaces the whole list after each registration pass. Nothing else writes it.

### 2.4 Game-window probing (05)

`GameWindowInfo` holds:

- `Handle`;
- `ClientRect` and `WindowRect` (physical pixels);
- `Minimized`, `GameForeground`, `EnvoyForeground`;
- `HasCaption`;
- `MonitorBounds`;
- `UniformFrameStreak` (05's blank-frame counter).

`AppHost.GameWindow` is the latest `GameWindowInfo?`, with `GameWindowChanged` raised on the UI thread. The watcher
tick and a 1 s UI timer update it while a game process is detected, and the timer runs even when capture is stopped.
07a's `DisplayModeAdvisor.Classify(GameWindowInfo?)` is a pure Core function. For exclusive fullscreen, 05 owns the
advice text (`CaptureText.LikelyExclusiveFullscreen`) and 07a reuses it.

### 2.5 Update state (06)

The update notice from 07a subscribes to `UpdateCoordinator.Changed` and submits `UpdateAvailable` when the state
becomes `Available` or `ReadyToRestart`. Its text comes from `UpdateView.Headline`. Its action runs
`UpdateView.Primary`: `RestartToUpdate` for an installed copy, otherwise the release page. REQ-OUX-17 is rewritten to
say this.

There is one persisted "announced" field, `Updates.LastAnnouncedVersion`. It lives in 06's `UpdateSettings` and 06 fixes
its meaning, but 07a adds the property, because 07a is its first reader (arrival rule). It only stops a second balloon for the
same version. `SkippedVersion` makes automatic checks report `Idle`, so a skipped version never reaches the notice
path at all.

`UpdateCoordinator.Changed` fires on a background thread. AppHost marshals it to the UI thread, and that includes the
`settingsChanged` callback (see §7).

### 2.6 Pending-message lifecycle (04)

The invariant: every message emitted as `Pending` reaches exactly one terminal state (`Translated`, `Failed` or
`Skipped`), and `MessageUpdated` is raised exactly once for that transition.

- Translations run under a pipeline-lifetime `CancellationTokenSource` owned by `ChatPipeline`, linked with 03's
  per-call deadline. The token passed to `ProcessAsync` cancels only analysis, never translation.
- `ChatPipeline.Reset()` (new match) and `Dispose()` cancel the lifetime token. Any `OperationCanceledException` in
  translation ends as `Failed("cancelled")` with `MessageUpdated` raised.
- The overlay shows the original line without the spinner.
- Required tests:
  - the watcher stops mid-translation and the message ends non-Pending;
  - `NewMatch` mid-translation ends in `Failed("cancelled")`;
  - `Dispose` mid-translation ends in `Failed("cancelled")`.
- 05 lands first and must already pass `CancellationToken.None`, never a loop or generation token, to `ProcessAsync`.

### 2.7 `CaptureFrame` and the tick (05)

```csharp
public sealed record OcrLine(string Text, double Top, double Bottom, double Left = 0, double Right = 0); // Left/Right reserved; 04 fills them (SHOULD)
public sealed record CaptureFrame(CapturedImage Image /* exact-length copy, never a pooled buffer */,
                                  IReadOnlyList<OcrLine> Lines, TimeSpan OcrTime);
```

The tick algorithm, in order:

1. Heartbeat.
2. Read the capture-options snapshot (§7).
3. Probe the game window and publish it.
4. Resolve the region and decide pause or preview override.
5. Capture into a pooled buffer.
6. Fingerprint. If the frame is unchanged and no preview lease is held, schedule the next tick and return.
7. Preprocess.
8. Run OCR passes, single-flight with a timeout.
9. `lines = OcrMerger.MergeLines(passes, preferredLanguage)`. 05 adds `MergeLines`, and `Merge` becomes
   `MergeLines(...).Select(l => l.Text)` with identical output.
10. If reading, call `pipeline.ProcessAsync(lines, CancellationToken.None).Forget(log, "pipeline")`.
11. If a preview lease is held, raise `FrameCaptured(new CaptureFrame(copy, lines, ocrTime))`.
12. Return the buffer to the pool, update the metrics, and compute the next delay.

REQ-CHAT-1 (`MergeLines`) and REQ-CHAT-3 (pass geometry) move to 05 as REQ-CAP-42. 04 makes no change to
`ChatWatcher`.

### 2.8 Live view state (01)

- `IDisposable ChatWatcher.BeginPreview()` replaces `PublishFrames`. It uses an `Interlocked` count, and
  `IsPreviewing => count > 0`.
- `CapturePreviewViewModel` holds a lease while `IsActive` is true, and releases it on deactivate or dispose.
- 05's preview override (REQ-CAP-13) keys off `IsPreviewing`.
- `CapturePreviewViewModel` is the only home for preview state:
  - 04's "Understood as" text, computed on the UI thread by `Pipeline.Describe(frame.Lines)`, which has no side effects;
  - the copy-as-test-capture command;
  - 05's `MetricsText`/`HealthText` refreshed by a 1 s `DispatcherTimer` in the view model.

  Page code-behind only toggles `IsActive`. The wizard's Chat-area step uses its own view-model instance.

## 3. Final shapes

```csharp
public sealed record AppHostOptions                  // 02 creates; 06 adds UpdateBackend
{
    public LogHub? Log { get; init; }                // null → LogHub with no sinks
    public MemoryLogSink? RecentLog { get; init; }
    public SafeModeReason? SafeMode { get; init; }
    public string? DataFolder { get; init; }         // null → Path.GetDirectoryName(settingsPath)
    public IUpdateBackend? UpdateBackend { get; init; } // null → UpdateBackendFactory.Create(...) in AppHost.cs
    public TimeProvider? TimeProvider { get; init; } // null → TimeProvider.System; passed to every timed Core type
}
public AppHost(string settingsPath, Dispatcher ui, AppHostOptions? options = null);
```

Instance paths replace the statics in 02. The static `AppHost.DataFolder`, `LogsFolder` and `UserPhrasesPath` are
deleted. Each path below is an instance property derived from `options.DataFolder`, added by the area named:

- `DataFolder`, `LogsFolder` (`logs/`), `UserPhrasesPath` and `CrashMarkerPath` (`last-crash.json`) come from 02;
- `TranslationCachePath` comes from 03;
- `ChatFormatsPath` comes from 04.

Every smoke-test host passes a fresh `%TEMP%\envoy-smoke-{guid}\data` as `DataFolder`, and the smoke test deletes
that folder at the end. No code path outside `SettingsStore.DefaultPath` may name `%APPDATA%`.

The constructor order is fixed, and each area inserts its line in this position:

1. settings `LoadWithReport` (02);
2. `ApplyDiagnosticsSettings` (02);
3. `TranslationRuntime` (03);
4. `TranslatorFactory` (03);
5. `ChatFormatCatalog` (04);
6. `ChatPipeline`;
7. `ChatWatcher` (05);
8. `UpdateCoordinator` (06);
9. `NoticeCenter` (07a, skipped in safe mode).

The `ApplySettings` order is fixed the same way:

1. diagnostics (02);
2. publish the immutable snapshots (§7);
3. `TranslationRuntime.Configure` (03);
4. `BuildTranslator`;
5. chat options and format re-resolve (04);
6. `Watcher.Invalidate` (05);
7. update settings (06);
8. OCR-readiness notice (07a);
9. theme through `ThemeResolver` (07b).

The `RefreshGame` order, on a change:

1. log (02);
2. format re-resolve (04);
3. `Updates.TickAsync` on game close (06);
4. `Notices.TakeDeferred` (07a).

Every `catch` in `AppHost.cs` logs. 02 adds this rule to `CLAUDE.md`.

Final `IShell`. Each area adds only its own block, in its own step:

```csharp
public interface IShell
{
    // 01
    void SelectRegion(); void ToggleOverlayEditMode(); void OpenCompose(); void ScanAndTranslate(); void ExitApp();
    void OpenUrl(string urlOrDataPath);          // → ShellLauncher.TryOpen (08a)
    void InstallOcrPack(string languageTag);     // → ShellLauncher.InstallOcrPack (08a)
    void ShowInfo(string title, string message); // → DialogService
    // 02
    bool Confirm(string title, string message);  // → DialogService
    void Restart(bool safeMode); void ResetSettingsAndRestart(); void DeleteAllDataAndRestart();
    string? ChooseExportPath(string defaultFileName); void CopyText(string text); void RevealFile(string path);
    // 06
    void RestartToUpdate();
    // 07a
    void OpenSetupWizard(string? stepId = null); void ShowPage(string pageId);
}
```

Versions:

- `AppHost.CurrentVersion` stays `Version` (assembly version), and 02's header and crash marker use it.
- 06 adds `AppHost.AppVersion`, a `ReleaseVersion` parsed from the informational version (with any prerelease
  suffix). All user-visible version text uses `AppVersion` after 06.
- The default `HttpClient` user agent stays browser-like: `Mozilla/5.0 (Windows NT 10.0; Win64; x64) Envoy/{x.y.z}`.
  06 only replaces the hard-coded `0.1` token. Update requests keep their per-request `Envoy/{version}` agent.

## 4. Start-up and exit sequence (02 writes the skeleton; later areas fill marked slots)

`App.OnStartup` runs in this order:

1. **`Program.Main`** (06) runs `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` before any WPF object exists.
   Until 06 lands, WPF's generated `Main` is used.
2. **Log bootstrap** and the global handlers: dispatcher, `TaskScheduler.UnobservedTaskException`,
   `AppDomain.UnhandledException` and WinForms `ThreadException`. The handler bodies are Core methods with unit tests.
3. **Smoke branch:** `--smoke-test`, `--ocr-e2e` (08b, SHOULD) and `--soak` (05, SHOULD) run and exit here.
4. **Single-instance mutex.** A second instance exits.
5. **Pending update apply** (06). This is skipped when `--safe-mode` is set or Shift is held, and it runs **before**
   the startup sentinel. Applying calls Velopack and exits.
6. **Safe-mode decision.** It is on for `--safe-mode`, Shift held, or (if REQ-REL-42 is built) two unfinished starts.
   Then `StartupSentinel.BeginStartup`.
7. **`AppHost`** with `AppHostOptions`.
8. **Notification services.**
   - Toasts and the throttle are always created.
   - `NoticeCenter` and `TrayNotifier` (07a) are skipped in safe mode.
   - High-contrast listeners are added here (07b).
9. **`Host.Start()`.** In safe mode there is no capture start, no automatic update check (`UpdateCoordinator.StartAsync`
   is not called) and no notices. A manual "Check for updates" still works.
10. **Overlay and hotkeys**, unless in safe mode. In safe mode, every `HotkeyResults` entry is `SkippedSafeMode`.
11. **Tray**, then the window choice:
    - in safe mode, `MainWindow`;
    - otherwise, with `--minimized`, tray only (the guide never auto-opens; the War Room callout remains);
    - otherwise, if `OnboardingGate.ShouldAutoOpen`, the setup guide (07a);
    - otherwise, the existing first-run and `--minimized` rule.
12. `StartupSentinel.MarkHealthy` after 30 s or first `MainWindow` render.

`ExitApp(restart)` runs in this order:

1. Hide the overlay and unregister hotkeys.
2. If `Host.SaveSuppressed` is false, apply pending edits and `SaveNow()`. `MainWindow.OnClosed` checks the same flag.
3. `Host.Dispose()`. This cancels the update CTS (06), saves the cache (03) and stops the watcher.
4. `StartupSentinel.EndSession`.
5. Dispose the notifier, toasts and tray.
6. Flush and dispose `LogHub`, which is always last before restart.
7. Release the mutex.
8. Launch the restart, if any, through `ShellLauncher.RestartSelf(flags)` or `UpdateCoordinator.RestartToUpdate`.
9. `Shutdown()`.

Reset and delete call `Host.SuppressSave()` **before** they touch files, and disposing never undoes them. A test
covers this: after `ResetToDefaults`, `Dispose` leaves default settings on disk. `RestartToUpdate` saves normally.

## 5. Process launching and policy allowlists

- **Process launching.** `BannedSymbols.txt` (08a) bans `Process.Start` and `ProcessStartInfo` everywhere except
  `src/Envoy.App/Security/ShellLauncher.cs`, which contains the only `#pragma warning disable RS0030`. Its members:
  - `TryOpen(string target, string allowedFolder)` opens absolute `http`/`https` URLs without user-info,
    `ms-settings:` URIs, and non-executable files or folders under `allowedFolder` (the caller passes
    `Host.DataFolder`).
  - `InstallOcrPack(string languageTag)` validates the tag against `OcrLanguages` and uses
    `OcrPackInstall.PowerShellArguments(tag)` (01). It runs `powershell.exe` with `Verb=runas`.
  - `RestartSelf(RestartFlags flags)` starts `Environment.ProcessPath` with `RestartFlags { None, SafeMode, Minimized }`
    mapped to `--safe-mode` and `--minimized`, and nothing else.
  - `RevealFile(string path)` checks that the path is absolute and the file exists, then runs
    `explorer.exe /select,"<path>"`. Use it for user-chosen export paths.

  Each member has unit tests of its validation; the validators live in Core as `LaunchTarget`. 01's
  `InstallOcrCommand`, 02's restart and export, and 07a's OCR step call these members. REQ-QSC-30 and AC-QSC-17 are
  reworded to "ShellLauncher is the only file that starts processes".

- **Native allowlist** (`tools/ci/policy.json` → `native`). 08a seeds it from today's `Native.cs`. Each area adds its
  entries to `Native.cs` **and** `policy.json` in the same change:

  | Area | Additions |
  | --- | --- |
  | 02 | `user32!GetAsyncKeyState`, `user32!MonitorFromPoint`, `user32!MonitorFromWindow`, `user32!GetMonitorInfoW`, `shcore!GetDpiForMonitor`; structs `POINT`, `MONITORINFO`. `MakeNoActivate` reuses the existing `GetWindowLongPtrW`/`SetWindowLongPtrW`. |
  | 05 | `user32!GetClientRect`, `ClientToScreen`, `WindowFromPoint`, `GetAncestor`, `EnumWindows`, `IsWindowVisible`, `GetWindow`, `MonitorFromRect`, `GetThreadDpiAwarenessContext`, `AreDpiAwarenessContextsEqual`; `user32!GetGuiResources` only with the soak mode; `dwmapi!DwmGetWindowAttribute` only if WGC (COULD) lands. It reuses 02's `POINT`/monitor functions. |
  | 07a, 07b, 07c, 03, 04, 06 | none. 07a uses `AppHost.GameWindow`. A need found during implementation is added to this table in the same PR. |

- **Network allowlist.** `allowedPaths` stays `src/Envoy.Core/Translation/`, `src/Envoy.Core/Updates/` and
  `src/Envoy.App/AppHost.cs`.
  - 06 puts `UpdateBackendFactory.Create(HttpClient)` in `AppHost.cs`. `VelopackUpdateBackend.cs` names no network
    type.
  - Hosts: 08a seeds the hosts used today. 03 and 06 add any new literal host in the same PR.
  - No other area adds network code.

## 6. Smoke test structure and budget

`SmokeTest.Run(App app, string[] args, LogHub log, MemoryLogSink recent)` has three phases. Every section logs
`[smoke] <section> ok elapsed_ms=<n>`.

1. **Setup and once-only work.** These steps run once, never per palette:
   - create the temp root, `DataFolder` and main host (with `SmokeUpdateBackend`);
   - list the OCR languages;
   - the translation-provider and compose waits;
   - seed 03 health;
   - the 04 `Pipeline.Describe` sample;
   - the 05 `CaptureEngineSmokeAsync` 10 s run;
   - 02 S2 (a safe host with its own temp `DataFolder`), S3 (bundle) and S4 (handler wiring only);
   - one 06 Portable host, which is reused across palettes.
2. **Per palette.** Loop `foreach (var id in ThemeManager.PaletteIds)`, in this order:
   1. Apply the palette and start the binding-error listener.
   2. Open `MainWindow` and, for each `PageIds.All` entry, navigate, assert the page and take a screenshot.
   3. Run every `MainWindow`-dependent check:
      - 02 safe-mode banner (a second `MainWindow` on the safe host) and S1 toast;
      - 03 health rows;
      - 06 update states, then a screenshot of the **War Room** as `{palette}-update.png`;
      - 07a notices and Finish-setup callout;
      - 07b focus-visual check.
   4. `main.Close()`.
   5. Open the other windows: guide steps (07a), overlay full and compact plus edit mode (07c), compose (07c history),
      lookup, region selector, toast and tray notifier.
   6. Stop the listener and assert zero binding errors.
3. **Teardown.** Dispose all hosts, delete the temp root, then run S5 (log tail) and write the total elapsed time.

Budget:

- `build.yml` uses `WaitForExit(600000)`, set by 01.
- The test logs a Warning when its total exceeds 420 s and when one palette exceeds 30 s.
- REQ-QSC-26 is restated: the `check` job takes ≤ 8 min, and the Windows job takes ≤ 20 min wall time excluding
  tag-only release steps.
- Acceptance criteria name `ThemeManager.PaletteIds`, never "5 themes". The expected settings-screenshot count is
  `PaletteIds.Count × PageIds.All.Count`. The same rewording applies to AC-UIF-11/12, AC-TRR-14, AC-CAP-19,
  AC-CHAT-19 and 02 S1.

## 7. Settings rules

- **Additive only.** New properties have defaults that reproduce today's behaviour, unless the spec says otherwise.
  `AppSettings.CurrentVersion` stays `1` until a real migration ships, so the `SettingsMigrator` (now SHOULD) has no
  steps.
- **Markers.**
  - `[Secret]` (exists) is encrypted and masked.
  - `[Personal]` (02) covers `MyPlayerName`, `MutedPlayers` and `Chat.SystemLinePatterns`; the 04 marker is a MUST.
    These are masked as `[set]`/`[N entries]`.
  - `[Location]` (02) covers `Capture.Regions` and 05's `Capture.WindowRegions`. They are redacted to width×height.
  - `SettingsRedactionTests` checks every marker by reflection. A new property holding names, chat text or positions
    must carry the marker in the same PR.
- **Thread safety.**
  - Only the UI thread mutates `AppSettings`.
  - Background code never reads `AppSettings` directly. Inside `ApplySettings`, AppHost builds immutable snapshot
    records (`CaptureOptions` for 05, `ChatPipelineOptions` for 04, the `TranslationRuntime.Configure` options for
    03). Their collections are copied with `ToArray()`/`ToFrozenDictionary()`, and AppHost publishes them with
    `Volatile.Write`. Consumers take a `Func<TSnapshot>` or read a volatile field.
  - Background writes, such as 06's `LastCheckUtc`, go through `Dispatcher.BeginInvoke`.
  - UI-only state (for example `Compose.PhraseUsage`) is documented as UI-thread-only.
  - Each snapshot type has a test: mutating the settings after the snapshot is built does not change the snapshot.
- **Durability.**
  - Save writes the current version.
  - `.bak` files keep DPAPI-encrypted secrets and are never bundled.
  - Reset and delete suppress saving (§4).
- **Bindings.** Settings controls bind in `Pages/*.xaml` (01 updates the `CLAUDE.md` rule).

## 8. Newly assigned concerns

1. **Privacy disclosure (07a, REQ-OUX-47, MUST).**
   - The guide's first step and a War Room callout show "What leaves your PC", from `PrivacyText`:
     - chat lines and replies go to the chosen provider (the default is Google's unofficial keyless endpoint);
     - update checks go to GitHub;
     - OCR and speech stay local.
   - No chat text is sent to any provider until `Onboarding.PrivacyAcknowledgedVersion >= PrivacyText.Version`.
     AppHost enforces this before `BuildTranslator` is used, and `TranslationGateTests` covers it. The smoke hosts set
     the acknowledgement.
   - 07a adds the data-flow sections ("What leaves your PC") to `docs/privacy.md`. 02 creates the file with the
     stored-data inventory (§8.2), because 02's inventory test reads it. It adds a FAQ entry "Is Envoy
     allowed in ranked/anti-cheat games?": what Envoy does and does not do, that no publisher has endorsed it, and
     links to each publisher's third-party-tool policy. This is docs only, with no claim of approval.
2. **Data inventory and "Delete all Envoy data" (02, REQ-REL-44, MUST).**
   - Core `DataInventory.Enumerate(dataFolder)` lists each file and its purpose:
     - `settings.json` and its `.bak`/`.v{N}.bak`/`.reset`/`.broken` copies;
     - `phrases.json`;
     - `logs/*`;
     - `last-crash.json`;
     - 03's `translation-cache.json` and 04's `chat-formats.json`, each added by its area to the inventory in the same
       PR.
   - About › Troubleshooting has "Delete all Envoy data…". It confirms, suppresses saving, deletes only the inventory
       entries, removes the Run value, and calls `RestartSelf(None)`.
   - A test asserts that every inventory file name appears in `docs/privacy.md`.
3. **Release compatibility matrix and manual pass (08b, folded into REQ-QSC-38).**
   - `docs/compatibility.md` is the matrix: supported titles (AoE I DE, II DE, III DE, IV, AoM Retold) × display mode
     (windowed, borderless, exclusive fullscreen) × DPI (100 %, 150 %) × Windows 10 22H2 / 11. Each cell records the
     version last verified.
   - `docs/releasing.md` gathers every spec's manual M-steps into one ordered pre-release pass. This includes 04's
     real-capture calibration: at least 3 anonymised "Copy as test capture" fixtures from 2 titles in `Golden/Captures`
     before 1.0, after which 04's thresholds stop being provisional.
4. **OCR end-to-end test on Windows CI (08b, REQ-QSC-44).**
   - `tests/Envoy.App.Tests` (net8.0-windows) renders known chat lines to bitmaps at 14–18 px, with outline and shadow
     and in system fonts. It runs them through `FrameProcessing.Preprocess`, the real `Windows.Media.Ocr`,
     `MergeLines` and `ChatPipeline` parsing, and asserts sender equality and `TextSimilarity ≥ 0.85`.
   - **MUST:** the harness, and the en-US case running on every CI build.
   - **SHOULD:** the workflow installs the `Language.OCR` capabilities for ja-JP, zh-CN, ko-KR and ru-RU on the runner.
     A language whose recognizer is missing reports `Skipped (no recognizer)` in the job summary rather than failing.
5. **Soak and resource checks (05, REQ-CAP-43, SHOULD).**
   - `Envoy.exe --soak <minutes>` drives the watcher on a synthetic chat window. Every minute it logs private bytes,
     handle count, GDI/USER objects, `FramePool` size and History/Players counts.
   - The run fails if any of these grows by more than 20 % between minute 5 and the end.
   - A 120-minute run is a step in the pre-release pass. It is not part of CI.
6. **War Room information architecture (07a, REQ-OUX-48, MUST).**
   - Top to bottom:
     1. the safe-mode banner (MainWindow-level, above every page);
     2. the update banner (06; it also hosts the one-time "Updated to vX");
     3. the notice list (at most 3, then "Show all (n)");
     4. hints (the Finish-setup callout first, then the page hint);
     5. the existing War Room content.

     Each slot shows at most one item, except the notice list.
   - The Appearance page is ordered Theme → Accessibility (07b) → Notifications (07a) → Startup & updates (06).
   - The About page is ordered Version & updates (06) → Setup guide (07a) → Troubleshooting (02, including Delete all
     data) → Privacy link (07a) → licences.
   - Page hints map 05's `WatcherState.Paused` to `CaptureCondition.AutoPaused(reason)`. This shows 05's pause text
     with **no** Resume action, except "Select chat area again" for `RegionOffScreen`. `PageHintsTests` covers each
     `PauseReason`.
7. **UI strings (rule, all areas).**
   - User-facing text built in code lives in Core `public static class *Text`, in the area's namespace (for example
     `ProviderHealthText`, `NoticeText`, `CaptureText`, `PrivacyText`). Each is a whole-sentence format string with
     named placeholders (`{count}`, never `{0}`) and no concatenated fragments, so it can move to `.resx` later.
     Templates are filled only by Core `Envoy.Core.Text.TextTemplate.Format(template, culture, (name, value)...)`,
     which 01 adds; an unknown or missing name throws. XAML labels (including `StringFormat` in bindings) stay inline
     for now.
   - Voice: short second-person sentences that say what happened, then what to do. No "Error:" prefixes. No raw
     exception text.
8. **Other decided fixes.**
   - **Accessibility names.** From 01 on, every new interactive control gets `AutomationProperties.Name` (a
     `CLAUDE.md` rule added by 01). `check_a11y.py` lands in 07b. `RegionSelectorWindow` literals become palette keys
     `Overlay.HitTest` and `Overlay.Dim` (07b).
   - **Reply-send target.** `SendTargetGuard` (08a) targets `Host.Game.MainWindow` when a game is detected, and
     treats `Shell_TrayWnd`/`explorer.exe` as no target.
   - **Character counter.** 07c's counter measures `OutgoingText.Sanitize(AppendOriginal(text))`.
   - **Compose throttling.** Compose requests get a separate low-priority throttle lane (03, REQ-TRR-29, SHOULD).
   - **Signing.** Signing runs only on `v*` tags, in a step gated by `environment: release`. Untagged builds are
     unsigned (06 implements this; 08b moves it into the release job).
   - **`vpk` pin.** 06 pins `vpk` in `.config/dotnet-tools.json`, and `check_velopack_pin.sh` reads it.
   - **ScreenCapturer.** 08a fixes or suppresses diagnostics in today's `ScreenCapturer`, and 05's rewrite must be
     clean under the same rules.
   - **Command errors.** `AsyncRelayCommand` requires an `onError`. `MainViewModel` routes it to one handler, which
     logs at Error and shows a problem toast after 02.
   - **Region migration.** REQ-CAP-6 becomes COULD (no installed base). REQ-CAP-4 remains only as the no-game-window
     fallback.

## 9. MUST budget (152 total)

Every requirement not listed here as MUST is SHOULD or COULD.

| Area | MUST ids | # | Demoted, folded or removed |
| --- | --- | --- | --- |
| 01 | UIF-1,2,3,4,5,7,8,9,10,11,13,15,18,19,21 | 15 | 6→folded into 5; 12→8; 14 SHOULD; 24→DoD. 21 is promoted (preview lease). |
| 02 | REL-1,2,3,4,5,11,12,13,16,18,19,23,27,29,33,34,39,40,43,44 | 20 | 14→11; 10,25→23; 41→40; 8,15,17,21,24,30,31 SHOULD; 26 removed; 38 SHOULD (with the §4 fix); 42 COULD |
| 03 | TRR-1,2,4,6,7,8,10,11,12,16,17,18,19,22,23,24 | 16 | 3→2; 9→8; 5,13,14,15 SHOULD; new 29 SHOULD |
| 05 | CAP-1,2,4,5,9,10,13,14,19,22,25,26,27,28,30,31,34,41,42 | 19 | 3→2; 32→31; 29→30; 12,15,16,18,20,21,35,36,38,39 SHOULD; 6 COULD; new 42 (MergeLines/geometry/token), 43 SHOULD |
| 04 | CHAT-5,7,9,15,16,18,20,23,25,26,27,35,36,40,44,46,47 | 17 | 1,3→CAP-42; 2,22→DoD (existing tests stay green); 6,11→5; 17→16; 24→23; 43→44; 10,12,29,30,31,32,33,39 SHOULD; new 47 (pending lifecycle) |
| 06 | DIST-1,2,3,5,7,8,9,10,11,12,13,14,15,16,19,20,27 | 17 | 17→16; 8 reworded to tag-only; 4,6,18,28 SHOULD; 29→DoD |
| 07a | OUX-1,2,3,6,7,10,13,16,17,23,47,48 | 12 | 4→1; 14,21→13 (throttle and hold); 15 removed; 19 removed (policy fixed in §2.2); 5,8,9,11,12,18,20,22 SHOULD |
| 07b | OUX-24,25,26,29,32,33,35,36 | 8 | 34→33; 38→35; 27,28,30,31,37 SHOULD |
| 07c | OUX-39,40,41,44 | 4 | 46→44; 42,43,45 SHOULD |
| 08a | QSC-1,2,3,4,6,8,9,11,28,29,30,31,32,34 | 14 | 5→4; 33→34; 7,10 SHOULD |
| 08b | QSC-14,17,20,21,23,26,36,38,40,44 | 10 | 15 SHOULD; 16→14 (gate: line coverage ≥ 70 %, branch ≥ 55 %); 19,39,43 SHOULD (43 is replaced by 44's project); 12,13,18,22,24,25,27,37,41,42 SHOULD |

Total: 15+20+16+19+17+17+12+8+4+14+10 = **152**.

Every MUST has an acceptance criterion of one of these kinds:

- a Core unit test;
- `tools/typecheck/typecheck.sh` (typecheck, stub or a11y lint);
- a named Windows smoke-test section;
- a CI check (policy, coverage, pin or CodeQL);
- a numbered step in the `docs/releasing.md` manual pass.

An area cannot close with an unverifiable MUST. It either changes the acceptance criterion to one of these kinds or
demotes the requirement in its spec, and records that under Deviations.

## 10. Definition of done (per area)

1. `dotnet test tests/Envoy.Core.Tests` and `tools/typecheck/typecheck.sh` pass locally. Existing tests are unchanged
   and green, except for edits that are listed under Deviations.
2. New logic is in `Envoy.Core` with unit tests. Each new window or state has a smoke section placed according to §6.
3. Allowlist additions (§5), settings markers (§7) and inventory entries (§8.2) are made in the same change.
4. `CHANGELOG.md` has an entry under `## Unreleased`. User-facing docs, `docs/architecture.md` and `CLAUDE.md` rules
   are updated.
5. The area's spec has `Status: Implemented` and a short `## Deviations` section listing every difference from the
   spec and from this contract, with the reason. If there are none, it says "None".
6. CI is green on `main` after the merge, including the Windows smoke test, before the next area starts.
