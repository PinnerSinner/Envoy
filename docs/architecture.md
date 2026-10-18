# Architecture

Envoy is two projects. A platform-independent core holds all the logic worth testing. A thin Windows app talks to
the screen, the keyboard and the user.

```
┌──────────────────────── Envoy.App (WPF, Windows only) ────────────────────────┐
│                                                                               │
│  ChatWatcher loop (background)                                                │
│   ScreenCapturer ──► preprocess ──► fingerprint ──(changed?)──► OcrService    │
│   (GDI StretchBlt)   (grey/contrast/  (skip OCR when            (one engine   │
│                       invert)          chat is static)           per lang)    │
│                                                                     │         │
└─────────────────────────────────────────────────────────────────────┼─────────┘
                                                                      ▼
┌──────────────────────── Envoy.Core (net8.0, any OS) ──────────────────────────┐
│  OcrMerger ── pick the best engine per line, strip CJK spaces                 │
│      │                                                                        │
│  ChatPipeline (options snapshot, pipeline-lifetime token)                     │
│   ChatCaptureAnalyzer: clean ─► notice patterns ─► parse ─► stitch wraps      │
│   ─► ChatDeduplicator.AcceptNew (sender+text, fragments) ─► filters (me, …)   │
│        ─► SlangGlossary ─► ScriptDetector ─► ITranslator ─► PlayerDirectory   │
│                                                                               │
│  ITranslator stack:  Caching → Coalescing → Fallback(deadline)[               │
│      Budgeted( Resilient(provider) ) … , Budgeted( Resilient(GoogleFree) ) ]  │
│  TranslationRuntime: breakers, throttles, provider health, cache (long-lived) │
│  OutgoingTranslator: PhraseBook first, then the translator stack              │
└───────────────────────────────┬───────────────────────────────────────────────┘
                                ▼ events (marshalled to the UI thread by AppHost)
┌──────────────────────── Envoy.App ────────────────────────────────────────────┐
│  OverlayWindow (click-through, topmost)   MainWindow (settings / War Room)    │
│  ComposeWindow (hotkey) ──► KeyboardSender (SendInput) ──► game chat          │
└───────────────────────────────────────────────────────────────────────────────┘
```

## Envoy.Core

| Folder | What's there |
| --- | --- |
| `Chat/` | `ChatCaptureAnalyzer` (one capture's lines → chat messages, notices and fragments; joins wrapped lines by geometry), `ChatTextCleaner` (invisible characters and icon junk), `SystemLinePatterns` (the user's game-notice patterns), `ChatLineParser` (channel, sender and text, driven by a `ChatFormatProfile`), `ChatFormatCatalog` + embedded `chat-formats.json` (the `generic` profile plus the user's file), `ChatDeduplicator` (identity = sender + text; fragment suppression; one lock per capture), `TextSimilarity` (`OcrKey`, `ContainsFuzzy`), `PlayerDirectory` (per-player language memory, `Canonicalize`), `MuteList`, `TranscriptWriter`, `ChatUnderstandingText` |
| `Ocr/` | `OcrMerger` combines several engines' readings line by line (`MergeLines` keeps each row's median `Top`/`Bottom`/`Left`/`Right`; `Merge` is its text); `OcrText.Clean` removes the spaces Windows OCR puts between CJK characters; `CaptureFixture`, the recorded-capture text format behind the golden tests and "Copy as test capture" |
| `Capture/` | The capture engine's pure logic (spec 05): `CaptureOptions` (the immutable snapshot the loop reads), `GameWindowInfo`, `RegionMath` (window-relative regions, output size, sample points), `CaptureGate` (pause reasons), `AdaptivePollPolicy`, `FramePool`/`FrameBuffer`, `FrameProcessing` (fingerprint and preprocessing without allocation) and `BlankFrames`, `SingleFlight` (OCR timeout, one call at a time), `LoopWatchdog`, `CaptureMetrics`, `CaptureText`/`CaptureErrors`, `SoakBudget` |
| `Languages/` | `LanguageCatalog` (codes, names, OCR tags, normalising provider codes), `ScriptDetector` (offline language guess from Unicode blocks), `OcrReadiness` (which wanted OCR packs are missing; the guide's recommendations) |
| `Onboarding/` | Spec 07a. `OnboardingFlow` (the setup guide's steps, navigation and step status), `OnboardingGate` (when the guide opens by itself; resume step), `OnboardingOutcome` (Finish vs Skip), `DisplayModeAdvisor` (windowed / borderless / likely exclusive fullscreen from 05's `GameWindowInfo`), `OnboardingText` |
| `Privacy/` | `PrivacyText`: "What leaves your PC", its `Version` and the acknowledgement rule |
| `Ux/` | `PageHints` (every page callout and the War Room capture hint, incl. automatic pauses), `PageHintText`, `WarRoomSlots` (which callout the War Room hint slot shows), `ProviderCredentials` |
| `Translation/` | `ITranslator` and providers (`Providers/`, all HTTP through `ProviderHttp`), `TranslationException` with a `TranslationErrorKind` and its `TranslationErrorText`; `FallbackTranslator` (overall deadline), `CoalescingTranslator`, `CachingTranslator` + `TranslationCache` (+ the opt-in `TranslationCacheStore`), `BudgetedTranslator` + `UsageTracker`, `TranslatorFactory`, `SlangGlossary`, `PrivacyGatedTranslator` (the outermost wrapper: no text reaches any provider until "What leaves your PC" is confirmed); `TranslationRuntime` (long-lived: per-provider guards, `ProviderHealthRegistry`, the cache; survives rebuilds) and `TranslationLogging` |
| `Translation/Resilience/` | `ResilienceOptions` (per-provider defaults, chat and compose lanes), `RetryPolicy`, `CircuitBreaker`, `TokenBucket`, `ResilientTranslator`, `ProviderHealthRegistry` (the single provider-health source, contract §2.1) and `ProviderHealthText` |
| `Pipeline/` | `ChatPipeline` (OCR lines → `IncomingMessage` events; owns the pending-message lifecycle) with its immutable `ChatPipelineOptions`, `PipelineText`, `OutgoingTranslator` (your text → their language) |
| `Phrases/` | `PhraseBook` + embedded `phrases.json`; `PhrasePreview` (the Phrasebook page's rows and starter file) |
| `Hotkeys/` | `HotkeyStatus`, `HotkeyRegistrationResult` (one per action, published as `AppHost.HotkeyResults`), `HotkeyText`, `HotkeyCheatSheet` |
| `Mvvm/` | `ObservableObject`, `RelayCommand`, `RelayCommand<T>`, `AsyncRelayCommand` (required `onError`; never rethrows) |
| `Text/` | `TextTemplate.Format`, the one formatter for the `*Text` classes (named placeholders such as `{count}`) |
| `Threading/` | `LeaseCounter` (backs the watcher's preview leases) |
| `Settings/` | `AppSettings` (the whole settings model; `[Secret]`, `[Personal]` and `[Location]` markers) and `SettingsStore` (JSON, `[Secret]` properties encrypted via `ISecretProtector`; atomic flushed saves, last-known-good `.bak`, `.broken` recovery, `LoadWithReport`, `SuppressSaves`, `ResetToDefaults`), `SettingsMigrator` (no steps yet), `SettingsSanitizer`, `SettingsRedaction` (masks marked values for diagnostics) |
| `Diagnostics/` | `LogHub`/`ILog` with `RollingFileSink` (`logs/envoy.log`, 5 × 1 MiB) and `MemoryLogSink` (500-entry ring); `LogRedactor`, the one redactor; `Task.Forget`; `UnhandledErrorPolicy` (the global handlers' bodies), `CrashLoopGuard`, `CrashMarker`; `SafeModeDecision`, `StartupSentinel`, `StartupPlan`; `DiagnosticsReport`/`DiagnosticsBundle`; `DataInventory`; `ProblemText`, `SafeModeText`, `DiagnosticsText`, `DataText` |
| `Notifications/` | `NotificationThrottle` (the single throttle every notification surface asks), `ProblemReport`; spec 07a's advisories: `Notice`, `NoticeFactory`, `NoticeText`, `ProviderNoticeRules` and `NoticeCenter` (Show / Defer / Suppress, the War Room list, the in-game deferral queue, "Don't remind me") |
| `Updates/` | Spec 06. `ReleaseVersion` (SemVer 2.0 precedence; `FromInformational`), `UpdateChecker` (GitHub Releases; Stable = `releases/latest`, Beta = the highest SemVer of `releases?per_page=10`; typed `UpdateCheckException` kinds; only this repository's `https://github.com/…/releases/…` URLs are accepted), `IUpdateBackend` + `AvailableUpdate`, `GitHubReleaseBackend` (portable: notify only), `UpdatePreferences` (the immutable settings snapshot), `UpdateCoordinator` (the single update state and policy, contract §2.5: automatic vs manual checks, game deferral, periodic checks, skip, restart to update), `UpdateViewBuilder`/`UpdateView` (banner and status line), `UpdateText`, `ReleaseNotesText`, `UserAgents`, `UpdateEndpoints`, `PendingUpdatePolicy`, `UpdateDiagnostics` |
| `Security/` | `OutgoingText` (sanitises everything Envoy types or pastes), `SendTargetGuard` + `SecurityText` (which window a reply may be typed into), `LaunchTarget` (validation behind `ShellLauncher`), `ReleaseUrlPolicy`, `PhraseLimits` |

Everything here is covered by `tests/Envoy.Core.Tests` and runs on any OS.

### Message lifecycle

1. The watcher hands `ChatPipeline.ProcessAsync` the merged lines of one capture, with their geometry
   (`OcrMerger.MergeLines`). The pipeline reads its options snapshot once for the whole capture.
2. `ChatCaptureAnalyzer` cleans each line (`ChatTextCleaner`), marks lines that match a notice pattern as **system**,
   parses the rest with the active `ChatFormatProfile`, and joins sender-less lines to the message above them when the
   geometry says they are its wrapped continuation (gap at most one median line height, similar height, at most 4
   continuations). Sender-less lines with nothing above them become **fragments**. If analysis throws, that capture
   falls back to one message per line.
3. Senders are folded to a known player's name when they differ only by OCR confusions (`rn`/`m`, `q`/`g`, …).
4. `ChatDeduplicator.AcceptNew` decides the whole capture under one lock. A message's identity is (sender, text), so
   `A: gg` and `B: gg` are two messages while OCR jitter like `rushing`/`rushlng` is not new. A fragment contained in a
   remembered message (a tail whose head scrolled away) is dropped. Every entry is remembered, including the ones the
   filters drop next, so your own and muted players' tails stay hidden too.
5. Filters: hidden notices, noise, your own messages, muted players and filtered channels are dropped. Each is judged on the
   whole joined message.
6. `Classify`: a notice is *Untranslated* and never sent anywhere. A whole-message slang hit is answered from the
   glossary. A non-Latin script that matches your language is marked *Untranslated*. Everything else stays *Pending*.
7. `MessageReceived` fires so the overlay shows the line immediately ("translating…").
8. Pending messages are translated concurrently under the pipeline's own lifetime token, never the caller's. Every
   Pending message reaches exactly one terminal state and raises `MessageUpdated` exactly once (contract §2.6). New
   match, a region change and exit cancel that token; those messages end *Failed* with `WasCancelled`, and the overlay
   shows the original line without a spinner or error row.
9. `PlayerDirectory` records the sender's language. `SuggestedReplyLanguage` uses it to pick the reply language.

The golden corpus in `tests/Envoy.Core.Tests/Golden/Captures` replays recorded multi-frame captures through steps 1 to 7;
[chat formats](chat-formats.md) describes the format.

## Envoy.App

| Folder / file | What's there |
| --- | --- |
| `Program.cs` | The entry point: `VelopackApp.Build().SetAutoApplyOnStartup(false)…Run()` before any WPF object (install/uninstall hooks run and exit there; uninstall removes the Run value), then `App` |
| `AppHost.cs` | Composition root, built from `AppHostOptions`: owns settings, services and history; the instance data paths (`DataFolder`, `LogsFolder`, `UserPhrasesPath`, `CrashMarkerPath`); the log hub, the notification throttle and `ReportProblem`; marshals events to the UI thread; applies settings live; holds `HotkeyResults` (written only by `App.RegisterHotkeys`); owns the `TranslationRuntime` (`TranslationHealth`, the chat and compose-lane stacks, `ActiveProviderIds`, `TranslationCachePath`) and derives `ProviderStatus` from provider health; owns the `UpdateCoordinator` (`Updates`, `UpdateView`, `UpdatesToken`, `StartUpdates`, `SkipUpdate`, `JustUpdatedTo`), `AppVersion` and `UpdateBackendFactory`; wraps both translator stacks in the privacy gate (`PrivacyAcknowledged`, `SetPrivacyAcknowledged`); owns `Notices` (`NoticeCenter`, not in safe mode), raises `NoticeReady` and holds `PhrasesLoadError` |
| `App.xaml.cs` | Start-up and exit in the contract §4 order (see below), single instance, tray icon (incl. "Setup guide…" and "Hotkey cheat sheet…"), hotkey bindings, window management (the single setup guide, `ShowPage`), balloon actions; implements `IShell` |
| `IShell.cs` | The app-level actions view models may trigger (select region, open compose, open a URL, install an OCR pack, show an info box…). Each area adds its own block |
| `Security/` | `ShellLauncher`: the only code that starts processes (`TryOpen`, `InstallOcrPack`, `RestartSelf`, `RevealFile`), each validated by Core `LaunchTarget` |
| `Services/` | `Capture/` (`ICaptureBackend`, `GdiCaptureBackend`, `CaptureBackendFactory`), `ScreenCapturer` (one-shot facade for Scan & translate), `OcrService` (Windows.Media.Ocr; bounded single-flight overload for the watcher), `ChatWatcher` (the loop), `GameWindowTracker` (the only game-window probe), `HotkeyService` (RegisterHotKey), `KeyboardSender` (SendInput, guarded by `SendTargetGuard`), `GameDetector`, `ThemeManager`, `DpapiProtector`, `StartupRegistration`, `DialogService` (the only place that shows a `MessageBox`), `SettingsAutoApplier` (the 400 ms debounce), `LogBootstrap`, `GlobalExceptionHandlers` (wires the four exception events to `UnhandledErrorPolicy`), `ToastService` (problem toasts), `DiagnosticsCollector`, `VelopackUpdateBackend` (installed copies: the only `UpdateManager`; names no network type), `UpdateActions` (runs a banner, tray or balloon `UpdateAction`; opens only trusted release URLs), `TrayNotifier` (advisory tray balloons and their click actions) |
| `Windows/` | `MainWindow` (title bar, safe-mode banner, sidebar and page hosts), `OverlayWindow`, `ComposeWindow`, `LookupWindow`, `RegionSelectorWindow`, `ToastWindow` (non-activating problem toast), `SetupWizardWindow` (the setup guide) |
| `Pages/` | One `UserControl` per settings page (`WarRoomPage`, `TranslationPage`, `ReadingChatPage`, `OverlayPage`, `ReplyingPage`, `PhrasebookPage`, `HotkeysPage`, `AppearancePage`, `AboutPage`) and `PageIds` |
| `ViewModels/` | `MainViewModel` (one instance shared by every page, split into `.Translation`, `.ReadingChat`, `.Phrasebook`, `.Diagnostics`, `.Updates`, `.Hints` and `.Actions` partials), `SetupWizardViewModel` + `StepItem` (the setup guide), `CapturePreviewViewModel` (the live view), `ProviderHealthRow` (the Translation page's health card), `Option`, `OcrLanguageItem`, `ThemeOption` |
| `Controls/` | `HotkeyBox`, `SecretBox`, converters, `AutoApply.Ignore` |
| `Themes/` | `Palette.<theme>.xaml` (colours only) + `Controls.xaml` (all control templates, incl. the `Callout` style for page hints) |
| `SmokeTest.cs` | `Envoy.exe --smoke-test` builds every window and settings page under every palette and fails on binding errors; CI runs it on Windows |
| `SoakTest.cs` | `Envoy.exe --soak <minutes>`: runs the watcher on a synthetic chat window and fails if memory, handles, GDI/USER objects or pooled buffers grow by more than 20 % (pre-release, not CI) |

### Settings window

`MainWindow` creates one `MainViewModel` and sets it as its `DataContext`; the nine pages inherit it and never set
their own. Buttons and links bind to the view model's commands (`Envoy.Core.Mvvm`), and the view model reaches
app-level actions only through `IShell`. Any edit bubbles up to `MainWindow`, which restarts a 400 ms
`SettingsAutoApplier`; the apply runs `AppHost.ApplySettings()` and refreshes the status text. Controls that aren't
settings (the sidebar, the "Try it" box, the live view, the phrase preview language) set `c:AutoApply.Ignore="True"`.

### Start-up and exit

`Program.Main` runs Velopack first (auto-apply off). `App.OnStartup` then runs in a fixed order (contract §4): read
Shift → log hub and the four global exception handlers → the `--smoke-test` branch → the single-instance mutex → the
pending update apply (slot 5: a downloaded update is applied through Velopack's updater and Envoy exits, unless
`--safe-mode` or Shift; local only, never blocks start-up) → the safe-mode decision and `StartupSentinel.BeginStartup` →
`AppHost` (with the Velopack backend from slot 5) → toasts and, unless in safe mode, the `TrayNotifier` → `Host.Start()` and, unless in safe mode,
`Host.StartUpdates()` → overlay and hotkeys (not in safe mode) → tray (the notifier attaches and shows a held notice) and the window choice
(MainWindow in safe mode; tray only with `--minimized`; the setup guide when `OnboardingGate.ShouldAutoOpenAtStartup`;
else the first-run rule) → `StartupSentinel.MarkHealthy` after
30 s or the first render. `ExitApp` hides and unregisters, saves unless saving is suppressed, disposes the host
(cancelling update work), ends the session, closes toasts and tray, closes the log, optionally deletes the data
folder's inventory files, releases the mutex and optionally restarts: through `UpdateCoordinator.RestartToUpdate`
(Velopack's updater waits for the exit, applies and relaunches; `ShellLauncher.RestartSelf` is the fallback) for
**Restart to update**, else through `ShellLauncher.RestartSelf`. Later areas fill the slots marked `[NN …]` in the
code.

### Updates

`UpdateBackendFactory` (in `AppHost.cs`) picks the backend once: `VelopackUpdateBackend` for an installed copy, else
`GitHubReleaseBackend` (portable exe, `dotnet run`); the smoke test passes a scripted one through `AppHostOptions`.
`UpdateCoordinator` runs on the thread pool and raises `Changed` there; `AppHost` rebuilds `UpdateView` on the UI
thread, which the War Room banner, the About status line and the tray item bind to. Preferences reach it only as the
immutable `UpdatePreferences` (`ApplySettings` position 7); the game-running flag comes from `Game`/`GameWindow`, and a
download that waited for the game starts when it closes. `LastCheckUtc` is written back to settings through
`Dispatcher.BeginInvoke`. Nothing automatic runs in safe mode; About's **Check for updates** always works. The update
files live in the install folder (`%LocalAppData%\Envoy`), never in the data folder. The updater process (`Update.exe`) is
started by Velopack (pinned version, hash-verified packages), never by Envoy code, so `ShellLauncher` stays the only
launcher in Envoy's own code.

### Setup guide, privacy gate and notices (spec 07a)

- **Setup guide.** `IShell.OpenSetupWizard(stepId)` opens the single `SetupWizardWindow` (or moves the open one).
  Its view model drives Core's `OnboardingFlow`, writes the same `AppSettings` properties as the pages through its own
  `SettingsAutoApplier`, and uses its own `CapturePreviewViewModel`, active only on the Chat area step. Finish records
  `Onboarding.Completed`; Skip, Esc or the close button records `Skipped` and the step (`OnboardingOutcome`).
- **Privacy gate.** `PrivacyGatedTranslator` is the outermost wrapper of the chat and compose stacks. It reads a
  volatile flag that `ApplySettings` publishes at position 2 and fails every call with `Configuration` and
  `PrivacyText.TranslationOff` until `Onboarding.PrivacyAcknowledgedVersion >= PrivacyText.Version`, so nothing reaches
  03's runtime, health or cache. Every smoke and soak host acknowledges right after construction.
- **Advisories.** `AppHost.Notices` (`NoticeCenter`, UI thread, not created in safe mode) gets the OCR-readiness check
  (`ApplySettings` position 8 and `Start`), provider-health edges and the update state (end of `RefreshUpdateView`). It
  asks the one `NotificationThrottle` before every balloon, defers every advisory while the game is in front or the
  guide is open, and re-offers them on `GameWindowChanged`, game exit, guide close and a 5 s timer. A Show raises
  `NoticeReady`, which `TrayNotifier` turns into a balloon. The War Room lists active notices (never updates).
- **War Room order** (contract §8.6): safe-mode banner (MainWindow) → update banner → notice list (3, then "Show all
  (n)") → hint slot (privacy, then "Finish setting up Envoy", then the capture hint from `PageHints.Capture`) → the
  page's content. Other pages show one `Callout` from `PageHints`.

### Errors, logging and notifications

- Everything goes through `ILog` from `AppHost.Log` (`LogHub`). Every entry is redacted by `LogRedactor` before any
  sink sees it: registered `[Secret]` values, key patterns, URL queries and user-info, the user-profile path. Chat
  text and player names are logged only as `LogField.Sensitive`, which writes their length unless the user opted in.
- Unhandled exceptions (WPF dispatcher, WinForms tray callbacks, unobserved tasks, the AppDomain) go through
  `UnhandledErrorPolicy`: logged, then a problem toast (UI) or a redacted `last-crash.json` (AppDomain). During the
  smoke test UI exceptions stay unhandled so CI fails.
- Problems are shown as `ToastWindow`s through `ToastService`, gated by the single `NotificationThrottle` on
  `AppHost.Notifications`. No error opens a modal box; `DialogService` only confirms actions the user starts in
  `MainWindow`, plus the one crash-loop message.

### Capture loop

`ChatWatcher` runs one loop generation at a time on the thread pool. Each tick follows contract §2.7:

1. heartbeat for the `LoopWatchdog`;
2. read the `CaptureOptions` snapshot (the only settings read; `AppHost` rebuilds it in `ApplySettings` and
   `SetRegion` and publishes it with `Volatile.Write`);
3. probe the game window with `GameWindowTracker.Observe` and publish it (`AppHost.GameWindow`, contract §2.4; a 1 s
   UI timer also publishes it while capture is stopped);
4. resolve the region (`WindowRegions[game]` against the client area, else `Regions[game]`, else `Regions["any"]`) and
   ask `CaptureGate` whether to pause (no region, off-screen, minimised, no game, not foreground, covered);
5. capture into a pooled `FrameBuffer` through the `ICaptureBackend`;
6. fingerprint the raw pixels and skip unchanged frames (unless a Live view is open) or while OCR is still busy;
7. preprocess in place; 8. OCR through `SingleFlight` with `OcrTimeoutMs`; 9. `OcrMerger.MergeLines`;
10. `ChatPipeline.ProcessAsync(lines, CancellationToken.None)`, so stopping or restarting capture never strands a
    translation; 11. an exact-length copy to `FrameCaptured` while previewing;
12. return the buffer, record `CaptureMetrics` and wait `AdaptivePollPolicy`'s delay (fast after a change, slower
    while quiet, doubling after failures, 1 s while paused).

A 2 s watchdog timer restarts a loop that missed heartbeats for `max(15 s, 2 × OcrTimeoutMs + MaxIntervalMs)` on a
new backend, and gives up with `WatcherState.Error` after more than 3 restarts in 5 minutes. Failures never escape the
loop: they are logged under `Capture` and shown as a `CaptureText` sentence. All coordinates are physical pixels.

### Live view and preview leases

`ChatWatcher.BeginPreview()` returns a lease; while at least one is held (`IsPreviewing`), every capture is published
to `FrameCaptured` even when nothing changed. `CapturePreviewViewModel` holds a lease exactly while its `IsActive` is
true; `ReadingChatPage` sets that from its visibility. Two live views never switch each other off.

### Design rules

- **Never touch the game process.** Read pixels and send input only. This is a product promise; see the [FAQ](faq.md).
- **Logic goes in Core.** If something can be tested without Windows, it belongs in `Envoy.Core` with a test.
- **Everything themeable is a `DynamicResource`.** Colours never appear in window XAML.
- **Settings apply live.** `MainWindow` debounces any edit, calls `AppHost.ApplySettings()` and auto-saves.
- **Pages bind; view models command.** Page XAML has no event handlers and page code-behind no logic; view models use
  `IShell`, never `App.Current`, `Process.Start` or `MessageBox`. `tools/typecheck/check_ui_rules.py` enforces this.
- **Text built in code lives in Core.** User-facing strings assembled in code are `*Text` classes in `Envoy.Core`
  with named placeholders, filled by `TextTemplate.Format`.
- **Guard the edges.** Text typed into another window goes through `OutgoingText.Sanitize`; typing stops when the
  reply target closes or loses focus (`SendTargetGuard`); only `Security/ShellLauncher.cs` starts processes; native
  calls, network code and URL hosts are allowlisted in `tools/ci/policy.json`. See "Quality gates" below.
- **Fail soft: log, then toast.** Translation errors show on the message; a broken provider falls through to the
  next; OCR or capture errors show in the status line and never crash the loop. Every failure is logged with a
  category (every `catch` in `AppHost.cs` logs), and a problem the user must know about is a non-activating toast via
  `AppHost.ReportProblem`, never a modal box. Fire-and-forget tasks use `.Forget(log, what)`.
- **Nothing is sent before the user confirms.** Every translator stack is wrapped in `PrivacyGatedTranslator`;
  advisories go only through `NoticeCenter` and the one throttle, with text from `NoticeText`.
- **Data stays in one folder.** Every file lives under the instance `AppHost.DataFolder` and is listed in
  `DataInventory` and `docs/privacy.md`; only `SettingsStore.DefaultPath` names `%APPDATA%`.

## Threading

- The capture/OCR loop runs on the thread pool (`Task.Run`). Each tick does capture → OCR → hand-off to the pipeline
  without waiting for translation. It reads settings only through the immutable `CaptureOptions` snapshot and the
  detected game through a `volatile` field; `GameWindow` is published to the UI thread with `BeginInvoke`, and the
  watcher's `StateChanged`, `FrameCaptured` and `RegionChanged` fire on the loop thread.
- `ChatPipeline` is thread-safe for concurrent `ProcessAsync` calls: `AcceptNew` decides a whole capture under one lock,
  and the player directory locks internally. `ApplySettings` (and a game change) publishes a new immutable
  `ChatPipelineOptions` snapshot through `Pipeline.Options` (a volatile write); each capture reads it once.
  `Analyze`/`Describe` have no side effects and run on the UI thread for the Live view.
- All UI updates go through `Dispatcher.BeginInvoke` in `AppHost`.
- Translation state is thread-safe: one lock per breaker and bucket, a `SemaphoreSlim` per lane gate, lock-protected
  caches; no lock is held across an `await`. `TranslationRuntime.ProviderHealthChanged` and `Diagnostic` fire on the
  thread that finished the attempt; `AppHost` re-raises health changes on the UI thread. Background code reads only
  the immutable `TranslationRuntimeOptions` snapshot that `ApplySettings` publishes, never `AppSettings`.

## Quality gates

| Gate | Where | What it enforces |
| --- | --- | --- |
| Shared build settings | `Directory.Build.props` | Nullable, latest C#, deterministic builds, `AnalysisLevel=8.0-recommended`, code style; warnings are errors under `CI=true` (nullable always, in Core and its tests) |
| Analyzer rules | `.editorconfig` (+ `tests/`, `src/Envoy.App/`) | Culture-explicit formatting and comparison; security rules as errors |
| Banned APIs | `BannedSymbols.txt` (`RS0030`) | No `Process.Start`/`ProcessStartInfo` outside `ShellLauncher`, no TLS bypass, `WebClient`, `BinaryFormatter`, `XamlReader`, `Process.MainModule` |
| Policy check | `tools/ci/check_policy.py` + `policy.json` | P/Invoke allowlist and denied list, network code only in `Core/Translation/`, `Core/Updates/` and `AppHost.cs`, known URL hosts, `asInvoker` manifest, one `#pragma`, justified suppressions, build settings intact |
| Secret coverage | `SettingsSecretTests` | Credential-named settings are `[Secret]` and encrypted on save |
| Release tooling | `tools/release/` (run in the `check` job with self-tests) | One Velopack version in `Envoy.App.csproj`, `TypeCheck.csproj` and `.config/dotnet-tools.json` (`check_velopack_pin.sh`); signing secrets only in v* tag steps under the `release` environment (`check_signing_scope.py`); a release stays a draft until its exact asset list and `SHA256SUMS.txt` are asserted (`check_release_assets.py`) |
