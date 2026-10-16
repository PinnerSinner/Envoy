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
| `Languages/` | `LanguageCatalog` (codes, names, OCR tags, normalising provider codes), `ScriptDetector` (offline language guess from Unicode blocks) |
| `Translation/` | `ITranslator` and providers (`Providers/`, all HTTP through `ProviderHttp`), `TranslationException` with a `TranslationErrorKind` and its `TranslationErrorText`; `FallbackTranslator` (overall deadline), `CoalescingTranslator`, `CachingTranslator` + `TranslationCache` (+ the opt-in `TranslationCacheStore`), `BudgetedTranslator` + `UsageTracker`, `TranslatorFactory`, `SlangGlossary`; `TranslationRuntime` (long-lived: per-provider guards, `ProviderHealthRegistry`, the cache; survives rebuilds) and `TranslationLogging` |
| `Translation/Resilience/` | `ResilienceOptions` (per-provider defaults, chat and compose lanes), `RetryPolicy`, `CircuitBreaker`, `TokenBucket`, `ResilientTranslator`, `ProviderHealthRegistry` (the single provider-health source, contract §2.1) and `ProviderHealthText` |
| `Pipeline/` | `ChatPipeline` (OCR lines → `IncomingMessage` events; owns the pending-message lifecycle) with its immutable `ChatPipelineOptions`, `PipelineText`, `OutgoingTranslator` (your text → their language) |
| `Phrases/` | `PhraseBook` + embedded `phrases.json`; `PhrasePreview` (the Phrasebook page's rows and starter file) |
| `Hotkeys/` | `HotkeyStatus`, `HotkeyRegistrationResult` (one per action, published as `AppHost.HotkeyResults`), `HotkeyText` |
| `Mvvm/` | `ObservableObject`, `RelayCommand`, `RelayCommand<T>`, `AsyncRelayCommand` (required `onError`; never rethrows) |
| `Text/` | `TextTemplate.Format`, the one formatter for the `*Text` classes (named placeholders such as `{count}`) |
| `Threading/` | `LeaseCounter` (backs the watcher's preview leases) |
| `Settings/` | `AppSettings` (the whole settings model; `[Secret]`, `[Personal]` and `[Location]` markers) and `SettingsStore` (JSON, `[Secret]` properties encrypted via `ISecretProtector`; atomic flushed saves, last-known-good `.bak`, `.broken` recovery, `LoadWithReport`, `SuppressSaves`, `ResetToDefaults`), `SettingsMigrator` (no steps yet), `SettingsSanitizer`, `SettingsRedaction` (masks marked values for diagnostics) |
| `Diagnostics/` | `LogHub`/`ILog` with `RollingFileSink` (`logs/envoy.log`, 5 × 1 MiB) and `MemoryLogSink` (500-entry ring); `LogRedactor`, the one redactor; `Task.Forget`; `UnhandledErrorPolicy` (the global handlers' bodies), `CrashLoopGuard`, `CrashMarker`; `SafeModeDecision`, `StartupSentinel`, `StartupPlan`; `DiagnosticsReport`/`DiagnosticsBundle`; `DataInventory`; `ProblemText`, `SafeModeText`, `DiagnosticsText`, `DataText` |
| `Notifications/` | `NotificationThrottle` (the single throttle every notification surface asks), `ProblemReport` |
| `Updates/` | `UpdateChecker` (GitHub Releases; only this repository's `https://github.com/…/releases/…` URLs are accepted) |
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
5. Filters: hidden notices, noise, your own messages, muted players and filtered channels are dropped — judged on the
   whole joined message.
6. `Classify`: a notice is *Untranslated* and never sent anywhere. A whole-message slang hit is answered from the
   glossary. A non-Latin script that matches your language is marked *Untranslated*. Everything else stays *Pending*.
7. `MessageReceived` fires so the overlay shows the line immediately ("translating…").
8. Pending messages are translated concurrently under the pipeline's own lifetime token, never the caller's. Every
   Pending message reaches exactly one terminal state and raises `MessageUpdated` exactly once (contract §2.6). New
   match, a region change and exit cancel that token; those messages end *Failed* with `WasCancelled`, and the overlay
   shows the original line without a spinner or error row.
9. `PlayerDirectory` records the sender's language. `SuggestedReplyLanguage` uses it to pick the reply language.

The golden corpus in `tests/Envoy.Core.Tests/Golden/Captures` replays recorded multi-frame captures through steps 1–7;
[chat formats](chat-formats.md) describes the format.

## Envoy.App

| Folder / file | What's there |
| --- | --- |
| `AppHost.cs` | Composition root, built from `AppHostOptions`: owns settings, services and history; the instance data paths (`DataFolder`, `LogsFolder`, `UserPhrasesPath`, `CrashMarkerPath`); the log hub, the notification throttle and `ReportProblem`; marshals events to the UI thread; applies settings live; holds `HotkeyResults` (written only by `App.RegisterHotkeys`); owns the `TranslationRuntime` (`TranslationHealth`, the chat and compose-lane stacks, `ActiveProviderIds`, `TranslationCachePath`) and derives `ProviderStatus` from provider health |
| `App.xaml.cs` | Start-up and exit in the contract §4 order (see below), single instance, tray icon, hotkey bindings, window management; implements `IShell` |
| `IShell.cs` | The app-level actions view models may trigger (select region, open compose, open a URL, install an OCR pack, show an info box…). Each area adds its own block |
| `Security/` | `ShellLauncher`: the only code that starts processes (`TryOpen`, `InstallOcrPack`, `RestartSelf`, `RevealFile`), each validated by Core `LaunchTarget` |
| `Services/` | `Capture/` (`ICaptureBackend`, `GdiCaptureBackend`, `CaptureBackendFactory`), `ScreenCapturer` (one-shot facade for Scan & translate), `OcrService` (Windows.Media.Ocr; bounded single-flight overload for the watcher), `ChatWatcher` (the loop), `GameWindowTracker` (the only game-window probe), `HotkeyService` (RegisterHotKey), `KeyboardSender` (SendInput, guarded by `SendTargetGuard`), `GameDetector`, `ThemeManager`, `DpapiProtector`, `StartupRegistration`, `DialogService` (the only place that shows a `MessageBox`), `SettingsAutoApplier` (the 400 ms debounce), `LogBootstrap`, `GlobalExceptionHandlers` (wires the four exception events to `UnhandledErrorPolicy`), `ToastService` (problem toasts), `DiagnosticsCollector` |
| `Windows/` | `MainWindow` (title bar, safe-mode banner, sidebar and page hosts), `OverlayWindow`, `ComposeWindow`, `LookupWindow`, `RegionSelectorWindow`, `ToastWindow` (non-activating problem toast) |
| `Pages/` | One `UserControl` per settings page (`WarRoomPage`, `TranslationPage`, `ReadingChatPage`, `OverlayPage`, `ReplyingPage`, `PhrasebookPage`, `HotkeysPage`, `AppearancePage`, `AboutPage`) and `PageIds` |
| `ViewModels/` | `MainViewModel` (one instance shared by every page, split into `.Translation`, `.ReadingChat`, `.Phrasebook`, `.Diagnostics` and `.Actions` partials), `CapturePreviewViewModel` (the live view), `ProviderHealthRow` (the Translation page's health card), `Option`, `OcrLanguageItem`, `ThemeOption` |
| `Controls/` | `HotkeyBox`, `SecretBox`, converters, `AutoApply.Ignore` |
| `Themes/` | `Palette.<theme>.xaml` (colours only) + `Controls.xaml` (all control templates) |
| `SmokeTest.cs` | `Envoy.exe --smoke-test` builds every window and settings page under every palette and fails on binding errors; CI runs it on Windows |
| `SoakTest.cs` | `Envoy.exe --soak <minutes>`: runs the watcher on a synthetic chat window and fails if memory, handles, GDI/USER objects or pooled buffers grow by more than 20 % (pre-release, not CI) |

### Settings window

`MainWindow` creates one `MainViewModel` and sets it as its `DataContext`; the nine pages inherit it and never set
their own. Buttons and links bind to the view model's commands (`Envoy.Core.Mvvm`), and the view model reaches
app-level actions only through `IShell`. Any edit bubbles up to `MainWindow`, which restarts a 400 ms
`SettingsAutoApplier`; the apply runs `AppHost.ApplySettings()` and refreshes the status text. Controls that aren't
settings (the sidebar, the "Try it" box, the live view, the phrase preview language) set `c:AutoApply.Ignore="True"`.

### Start-up and exit

`App.OnStartup` runs in a fixed order (contract §4): read Shift → log hub and the four global exception handlers →
the `--smoke-test` branch → the single-instance mutex → the safe-mode decision and `StartupSentinel.BeginStartup` →
`AppHost` → toasts → `Host.Start()` → overlay and hotkeys (not in safe mode) → tray and window →
`StartupSentinel.MarkHealthy` after 30 s or the first render. `ExitApp` hides and unregisters, saves unless saving is
suppressed, disposes the host, ends the session, closes toasts and tray, closes the log, optionally deletes the data
folder's inventory files, releases the mutex and optionally restarts through `ShellLauncher.RestartSelf`. Later areas
fill the slots marked `[NN …]` in the code.

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
10. `ChatPipeline.ProcessAsync(lines, CancellationToken.None)` — stopping or restarting capture never strands a
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
