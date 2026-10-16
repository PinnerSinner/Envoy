# Changelog

All notable changes to Envoy. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning: [SemVer](https://semver.org/).

## [Unreleased]

### Added
- **Scan & translate** (`Ctrl+Shift+Q`): drag over any text on screen (lobby names, map descriptions, menus) and get
  an instant translation popup.
- **Herald**: optionally reads translated messages aloud with a Windows voice, with voice, volume and speed settings.
- CI renders screenshots of every window in every theme.
- **Copy diagnostics** (About › Troubleshooting): a report for bug reports with versions, Windows and monitor
  details, the detected game, OCR languages, which providers have a key set, failed hotkeys and the recent log.
  It never contains API keys, chat, player names or where the chat area is. **Export diagnostics…** saves it with the
  diagnostic log as a `.zip`. Nothing is ever uploaded.
- A diagnostic log, `logs\envoy.log` (five files of 1 MiB at most). Keys, web-address queries and your Windows user
  folder are masked; chat text and names are written only as their length unless you turn on "Include chat text in
  logs".
- **Safe mode**: hold Shift while Envoy starts, run `Envoy.exe --safe-mode`, or use **Restart in safe mode**. The
  overlay, hotkeys and automatic reading are off and a banner says why; Envoy also starts in safe mode by itself
  after two starts in a row that never finished.
- **Settings recovery**: Envoy keeps the last settings that loaded correctly (`settings.json.bak`) and uses them, with
  your keys, if `settings.json` is damaged; the damaged file is kept as `settings.json.broken`. Saves are flushed to
  disk before they replace the old file.
- **Reset settings…** and **Delete all Envoy data…** under About › Troubleshooting. Delete all removes exactly the files
  listed in the new [privacy page](docs/privacy.md) and the "Start with Windows" entry, then restarts.
- Start-up tells you once if Envoy closed unexpectedly last time or the settings file had to be recovered.
- **Provider health** (Translation page): one row per provider in the order Envoy tries them, with a coloured state,
  speed, last translation and a **Retry now** button. The War Room line says which provider is translating right now.
- **Give up after** (Translation → Behaviour, 5–30 s, default 15): the time limit for a chat line, across retries and
  the fallback provider.
- **Remember translations between sessions** (off by default): keeps the translation cache in
  `translation-cache.json` (2,000 entries, 30 days, original lines stored only as a one-way fingerprint), with
  **Clear saved translations**. See [Remembering translations](docs/translation-providers.md#remembering-translations).
- **The chat area follows the game window.** Select it while the game runs and Envoy keeps reading the right place
  when you move the window, resize it or move it to another monitor (Reading Chat says "follows the … window", and
  asks you to select it again if the game's size changed). An area chosen without a game stays a fixed screen
  position, and existing settings keep working unchanged.
- **Automatic pauses.** Reading pauses while the game is minimised, while another window covers the chat area (can be
  turned off), and optionally while the game isn't the active window; the status says why. Nothing on your desktop or
  in a browser is read and sent for translation in the meantime.
- **Adaptive reading speed** (Reading Chat › Fine tuning, on by default): checks every 0.3 s while the chat is moving
  and slows to 1.5 s while it's quiet ("Fastest check" and "Slowest check"); turn it off for the old fixed interval.
- The Live view shows how reading is going and its capture and OCR times, skip rate, check interval and capture method.
- A hint when the chat area is blank, which usually means the game runs in exclusive fullscreen.
- `Envoy.exe --soak <minutes>`: a pre-release endurance run on a synthetic chat window that fails if Envoy's memory,
  handles or drawing objects keep growing.

### Changed
- **Translation never hangs.** A slow provider used to leave a line on "translating…" forever and the fallback was
  never tried. Each attempt now has its own time limit and each line an overall one; a timeout moves on to the next
  provider and a line that still fails says so.
- **Translation recovers by itself.** Brief server errors and "too many requests" are retried (honouring short
  Retry-After waits); a provider that keeps failing, rejects your key or is out of quota is skipped for a while
  instead of slowing every line; requests to the free providers are paced so a burst of chat doesn't get your IP
  blocked; the reply box uses its own, slower allowance. Identical lines in flight share one request.
- **Clear error messages.** Translation errors are short sentences such as "Google Translate (free) sent a reply Envoy
  could not read." instead of raw server or parser text.
- The translation cache now survives settings changes, and switching provider no longer serves the old provider's
  cached translations (a fallback's translation is not kept as your main provider's).
- Characters a paid provider never received (a skipped provider, a connection that failed before sending) no longer
  count against the daily cap. Claude and Amazon Translate no longer retry on their own behind Envoy's back.
- **Errors no longer open dialogs.** A problem shows as a small pop-up in the bottom-right corner that never takes
  focus from the game and closes by itself; **Details** opens Troubleshooting, and the pop-ups can be turned off.
  This includes a reply that couldn't be typed into the game (it is still copied to the clipboard). `error.log` is no
  longer written.
- **Replies only go where you meant them to.** The reply box now types into the detected game window, or the window
  that was in front when you opened it, and never into the taskbar or Envoy itself. Typing stops if that window
  closes or another window comes to the front. A reply opened from the tray or the settings window while no game is
  detected now goes to the clipboard instead of being typed into whatever happens to be in front.
- Text Envoy types is cleaned first: line breaks and tabs become spaces (so a stray newline can't end the chat line
  early and turn the rest into game hotkeys), control characters and invisible text-direction overrides are removed,
  and a reply is capped at 1000 characters.
- `phrases.json` limits: files over 1 MiB are ignored, at most 500 user phrases load, phrases without an `id` are
  skipped, and long ids, categories, icons and texts are shortened. See [the phrasebook docs](docs/phrasebook.md#limits).
- **Chat reading heals itself.** Text recognition that takes longer than 4 s is abandoned; if Windows text
  recognition hangs for good, Envoy recreates it (up to three times each time reading is turned on, then it says "Chat
  reading stopped responding" until you turn reading off and on, which recreates it again). Failures slow the checks
  down (up to every 5 s) and the first good frame recovers; a stuck loop is restarted, and only after more than three
  restarts in five minutes does Envoy stop and say "Chat reading stopped responding". Status and error lines are plain sentences, never raw error text, and "Paused" now reads "Chat
  reading is off".
- Stopping or restarting chat reading no longer leaves lines stuck on "translating…".
- Less CPU and memory while gaming: unchanged frames skip all image processing, pixel buffers are reused, and the
  screen-copy surfaces are kept between frames.
- Selecting the chat area and placing the overlay are exact on every monitor, including mixed scaling (for example a
  150% laptop next to a 100% monitor).

### Security
- Links, files and folders are opened only through one launcher that accepts web links, Windows Settings pages and
  non-executable files in Envoy's data folder. The update check only trusts `https://github.com/…/releases/…` URLs of
  Envoy's own repository, so a spoofed update response can't open a local program or a network share.

### Internal
- Capture engine (spec 05): `Envoy.Core.Capture` (`CaptureOptions` snapshot, `RegionMath`, `CaptureGate`,
  `AdaptivePollPolicy`, `FramePool`, `FrameProcessing`, `SingleFlight`, `LoopWatchdog`, `CaptureMetrics`,
  `CaptureText`/`CaptureErrors`, `SoakBudget`), `OcrMerger.MergeLines` with row geometry and the `OcrLine`
  `ProcessAsync` overload; `ChatWatcher` rewritten around the contract §2.7 tick with `ICaptureBackend`/
  `GdiCaptureBackend`, `GameWindowTracker` and `AppHost.GameWindow`/`GameWindowChanged`; ten user32 window queries
  (plus `GetGuiResources` for the soak run) allowlisted; typecheck rule R11; smoke section `capture-engine`.
- Translation resilience (spec 03): `TranslationErrorKind`/`TranslationErrorText`, `ProviderHttp`, the
  `Translation/Resilience/` building blocks (`RetryPolicy`, `CircuitBreaker`, `TokenBucket`, `ResilientTranslator`,
  `ProviderHealthRegistry`), `TranslationRuntime` (long-lived state, owned by `AppHost`, configured from an immutable
  `TranslationRuntimeOptions` snapshot), `TranslationLogging` (`Translation` log category), provider health in the
  diagnostics report, contract tests for all six providers over recorded fixtures, and the smoke sections
  `translation-health-seed` and `translation-health-rows`.
- Reliability and observability (spec 02): `Envoy.Core.Diagnostics` (`LogHub`, `LogRedactor`, `UnhandledErrorPolicy`,
  safe mode, `DiagnosticsReport`, `DataInventory`) and `Envoy.Core.Notifications.NotificationThrottle`;
  `AppHostOptions` and per-instance data paths; `App.OnStartup`/`ExitApp` in the contract §4 order; settings markers
  `[Personal]`/`[Location]`; the smoke test runs on temporary data folders and checks the safe-mode banner, toasts,
  handler wiring, diagnostics and the log; typecheck rules R8–R10.
- Settings pages are now one `UserControl` each under `Pages/`, driven by commands on a shared `MainViewModel`
  (new `Envoy.Core.Mvvm` toolkit). Page logic and text moved into tested Core classes, app actions go through
  `IShell`, message boxes through `DialogService`, and the live view through preview leases
  (`ChatWatcher.BeginPreview()`). Hotkey registration results are typed (`AppHost.HotkeyResults`). No visible change.
- The Linux type-check now catches XAML root/base-class mismatches, missing event handlers and UI-structure rule
  breaks; the Windows smoke test covers every page under every palette, fails on binding errors and logs per-section
  timings.
- Quality gates: a shared `Directory.Build.props` with pinned analyzers (warnings are errors in CI, nullable warnings
  always), `BannedSymbols.txt` (no `Process.Start` outside `Security/ShellLauncher.cs`, no TLS bypass, no
  `BinaryFormatter`/`XamlReader`/`WebClient`), and `tools/ci/check_policy.py`, which checks P/Invokes, network code,
  URL hosts, the `asInvoker` manifest, `#pragma`s and suppressions in the CI `check` job. The Windows job reports the
  app's warning count. `.config/dotnet-tools.json` is the one place to pin .NET tools. Culture-sensitive formatting
  fixed (transcript timestamps, hotkey names).

## [0.1.0] — first release

### Reading chat
- Screen OCR with the built-in Windows engine. It runs several languages at once and keeps the best reading per line.
- Clean-up of CJK spacing; fuzzy de-duplication so each message is translated once despite OCR jitter.
- Parsing of channel and player names across AoE chat formats (`[All] Name: msg`, `Name (Team): msg`, timestamps,
  full-width colons).
- A chat area remembered per game; auto-detection of AoE I/II/III/IV (DE and older) and AoM: Retold.
- Live view of the captured image and the OCR text for tuning.
- Skips OCR when the chat area hasn't changed; can idle when no game is running.

### Translating
- Free providers with no account: Google Translate, with Microsoft Translator as the fallback.
- Bring-your-own-key providers: DeepL, Claude, Amazon Translate, LibreTranslate.
- A fallback chain, a translation cache, and a daily character cap for paid providers.
- An offline slang glossary (666, ㅋㅋ, ㅈㅈ, изи, kkkk, www and more).
- Per-player language memory, so replies default to the language of whoever spoke last.
- A mute list and a channel filter (all / team only / everything except team).
- Optional chat transcripts, saved locally.

### Replying
- A hotkey reply box with a live translation preview and back-translation.
- 16 hand-translated quick phrases in 13 languages; user phrasebook overrides.
- Sends via Unicode keystrokes or paste, or copies to the clipboard.

### App
- Five themes inspired by each game, with optional automatic switching.
- A click-through overlay with anchoring, drag-to-place, fade-out and adjustable look.
- Tray icon, global hotkeys, start with Windows, update notifications.
- API keys encrypted with Windows DPAPI.
- A single self-contained `Envoy.exe`; CI builds it, smoke-tests it on Windows and attaches it to releases.
