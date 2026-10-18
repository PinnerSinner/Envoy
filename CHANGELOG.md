# Changelog

All notable changes to Envoy. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning: [SemVer](https://semver.org/).

## [Unreleased]

## [0.2.0] - 2026-10-18

### Added
- **The Herald's Writ**: a new look in every window, after an illuminated manuscript on a war table. The settings
  window is an oak wall with a banner rail (each tab shows a playful name and its plain one: Interpreters,
  Translation) and the page on a vellum charter with a knotwork border, a drop cap and an ink drawing. Overlay messages
  are dispatch scrolls sealed with the sender's language (中, 한, 日, Р); the reply box is a scribe's desk with a wax
  "Seal and send"; the setup guide is a chain of seals; provider health shows lit, guttering or snuffed candles.
- **Liveries**: the new default, Herald (gules and or), and the five game themes recast in the same oak and vellum:
  Imperial (Age of Empires IV, gules and or), Parchment (Age of Empires II, azure and or), Frontier (Age of Empires
  III, vert and argent), Olympian (Age of Mythology, purpure and or) and Classic (Age of Empires, sable and or). Saved
  theme choices keep working; "Follow the game" still switches with the running game.
- New fonts, embedded under the SIL Open Font License: UnifrakturMaguntia, IM FELL English and EB Garamond. Chinese,
  Japanese and Korean text still uses the Windows fonts.
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
- **Give up after** (Translation → Behaviour, 5 to 30 s, default 15): the time limit for a chat line, across retries and
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
- **Understanding chat** (Reading Chat): a long message that wraps onto the next line is now one message, translated
  once, instead of two half-sentences; your own and muted players' wrapped messages are hidden whole. When chat scrolls
  and only the end of an old message is left, it isn't shown again.
- **Game notices**: list lines such as `*has resigned*` or `{player} advanced to the * Age` under **Also treat these
  lines as notices**. Notices are never translated (they cost nothing) and are hidden unless you turn off **Hide game
  notices**. Optionally, lines that start with a player's name and have no `:` count as notices too. A pattern Envoy
  can't use is named in red under the box. See [chat formats](docs/chat-formats.md).
- **Chat formats**: a `chat-formats.json` in the Envoy folder can teach Envoy other channel words, separators and
  timestamps, per game (**Chat format**, default "Match the game"). Envoy only reads this file.
- The Live view shows **Understood as**: how Envoy split what it read into messages, notices and leftover lines, and
  **Copy as test capture** copies what it read (with positions) for a bug report. It contains chat and names, so
  anonymise it before sharing.
- **Installer with automatic updates.** Releases now include `Envoy-win-Setup.exe`, which installs Envoy for your
  Windows account (no admin), adds it to the Start menu and *Apps & features*, downloads new versions in the
  background (never during a game) and offers **Restart to update**; a downloaded update is otherwise applied the next
  time Envoy starts (hold Shift to skip that). The portable `Envoy.exe` is still published; it tells you about new
  versions and offers **Download** and **Get the installer**, but never changes itself. See
  [Installing & updates](docs/installing.md). With automatic checks off, or in safe mode, a check you start never
  downloads by itself: it offers **Update now**. A failed check never hides **Restart to update** for an update that
  is already downloaded, and if an update can't be applied Envoy starts on the old version instead of retrying in a
  loop.
- The War Room's update banner shows the new version and its release notes (**What's new**), **Skip** for a version
  you don't want, and after an update says once "Envoy was updated to …".
- **Update channel** (Appearance › Startup & updates): Stable, or Beta for early builds (GitHub prereleases).
  Switching back to Stable never installs an older version.
- About › Version & updates: the version (with its beta suffix), install type, channel, the update status and a
  **Check for updates** button that always works, also with automatic checks off and in safe mode.
- Releases list `SHA256SUMS.txt` for both downloads, use the `CHANGELOG.md` section as their notes, and are published
  only once every file is uploaded and checked. Signed releases are possible once a certificate is configured.

- **Setup guide**: new users get a short guide instead of the settings window: Language & privacy, Game & display
  (windowed / borderless / likely exclusive fullscreen), Chat area (with a live view), Text recognition (which packs
  are missing, with **Install**), Test translation and Hotkeys. Reopen it from the tray (**Setup guide…**, **Hotkey
  cheat sheet…**), About (**Run setup guide again**) or the War Room's "Finish setting up Envoy".
- **What leaves your PC**: the guide, the War Room and About › Privacy say where chat and replies are sent (by
  provider name; the default is Google's unofficial keyless endpoint), that update checks go to GitHub and that OCR
  and speech stay on your PC. See [Privacy](docs/privacy.md#what-leaves-your-pc).
- **Tips in the notification area** for a missing text recognition pack, a translation provider that keeps failing
  and a new version. They wait while the game is in front, never take focus and open the right page when clicked.
  The War Room lists the same notices with **Don't remind me**; Appearance › Notifications turns tips (or each kind)
  off.
- Empty and problem states on every page: the War Room explains an empty players or messages list and why chat
  reading paused (offering "Select chat area again" when the area is off-screen), the Phrasebook says why your
  `phrases.json` was not loaded (invalid JSON with its line, or larger than 1 MB), and the other pages point out a
  missing provider key, a hidden overlay or a missing or taken hotkey.
- FAQ: "Is Envoy allowed in ranked/anti-cheat games?".
- **Accessibility**: a visible focus ring on every control you can reach with Tab; screen readers announce every
  button, box, slider, phrase chip, theme card and text recognition row by name; the setup guide and reply box keep
  Tab inside the window (Enter = Next/Send, Esc = Skip/close, Alt+I = reply language); Ctrl+Tab and Ctrl+Shift+Tab
  switch settings pages.
- **High contrast**: with a Windows contrast theme on, Envoy uses your contrast theme's colours (settings, overlay and
  reply box) and follows changes live. Appearance › Accessibility › High contrast: Follow Windows, Always or Never.
- **Keep text readable** (Overlay page, on by default): when the overlay background is too transparent for the text to
  stay readable over bright terrain, Envoy raises it and says so ("Raised to 63% so text stays readable").
- **Reply history**: in the reply box, Up recalls replies you sent or copied this session and Down goes back to what
  you were typing. The history stays in memory and is never saved or logged.
- **Message length** under the reply box counts exactly what will be sent (an emoji or a Chinese character counts as
  one). Replying › Reply box: turn it off, or set "Warn above" so it turns red past a game's limit; it also says when
  text is long enough to be cut. It never stops you sending.
- **Quick phrases follow the language you reply in**: phrases translated into it come first, the ones you send to it
  most at the front (Replying › Quick phrase order; "Phrasebook order" keeps the old order). Envoy keeps only phrase
  ids and counts in `settings.json`, never what you typed.
- **Compact overlay** (Overlay page): one line per message with sender and translation only; switches live.
- **Copy a message from the overlay**: in edit mode (`Ctrl+Shift+E`) each message has a ⧉ button that copies the
  sender, translation and original.
- **Every release lists what is inside it and where it came from**: `Envoy.cdx.json` (a CycloneDX software bill of
  materials) and a GitHub build-provenance attestation (`gh attestation verify Envoy.exe -R PinnerSinner/Envoy`).
  [SECURITY.md](SECURITY.md#verifying-a-download) explains how to check a download.
### Changed
- Writing: no em or en dashes and no filler words in the app, the docs or this changelog; a check (typecheck R15)
  keeps it that way.
- The setup guide: its step list no longer skips past "What leaves your PC" before you confirm it; closing or skipping
  the guide that opened by itself at start-up now opens the War Room, so Envoy never seems to vanish; tray tips wait
  while that guide is opening.
- **Translation is off until you confirm "What leaves your PC"** (setup guide, or **Turn on translation** on the War
  Room). Until then no chat or reply text is sent anywhere and the overlay shows the original lines. This applies to
  existing settings files too.
- The War Room has a fixed order: update banner, notices, one hint, then its cards. About is ordered Version &
  updates, Setup guide, Troubleshooting, Privacy, licences; Appearance has a Notifications section before Startup &
  updates.
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
- **No line stays on "translating…".** A translation interrupted by **New match**, selecting the chat area again or
  quitting now ends showing the original line, without a spinner or error.
- **Better identity for messages.** Two players writing the same `gg` are two messages, and the same player's `gg`
  twice on screen shows twice. A name OCR misreads (`Zhanq Wei` for `Zhang Wei`) is shown as the player's name.
- Stray symbols OCR reads from rank, civilisation and platform icons (`■`, `©`, `|`), invisible characters and
  full-width letters are cleaned from chat lines, so names match your own name and the mute list (can be turned off).
- Less CPU and memory while gaming: unchanged frames skip all image processing, pixel buffers are reused, and the
  screen-copy surfaces are kept between frames.
- Selecting the chat area and placing the overlay are exact on every monitor, including mixed scaling (for example a
  150% laptop next to a 100% monitor).
- **Update checks** ask GitHub at start-up at most once an hour and every 12 hours while Envoy runs (it used to check
  at every start only). "Tell me when a new version is released" is now **Check for updates automatically**: off
  means no automatic update request at all; safe mode never checks by itself. A failed background check stays
  silent; a check you start says what went wrong and offers **Retry**. Prereleases are compared correctly
  (`1.2.0-beta.2` is older than `1.2.0`).
- Uninstalling the installed Envoy removes the "Start Envoy when I sign in to Windows" entry; with that option on,
  each start points it at the exe that is running.

- Caption and error text is brighter (darker in Parchment) so it reaches WCAG AA contrast (4.5:1) in every theme.

### Fixed
- A channel tag whose closing bracket OCR misreads (`[AllJ Henry: …`) no longer hides the sender's name; the channel
  is still recognised.
- A hotkey with a digit, such as `Ctrl+1`, now means the number key; it was read as an unrelated key before.

### Security
- Signed builds come only from protected `v*` tags: signing and publishing run in a separate release job that needs a
  reviewer's approval, builds no code and is the only place the signing certificate can be read. Every other CI job
  has read-only access.
- Links, files and folders are opened only through one launcher that accepts web links, Windows Settings pages and
  non-executable files in Envoy's data folder. The update check only trusts `https://github.com/…/releases/…` URLs of
  Envoy's own repository, so a spoofed update response can't open a local program or a network share.

### Internal
- CI and release (spec 08b): `build.yml` split into least-privilege `check`, `app-tests`, `windows`, `screenshots` and
  the tag-only `release` job (`environment: release`), actions pinned to commit SHAs, timeouts, concurrency and a
  NuGet cache; an Envoy.Core coverage gate (70 % lines, 55 % branches, `tools/ci/coverage_summary.py`), a
  vulnerable-package gate (`check_vulnerable.py`), the SBOM check (`check_sbom.py`) and per-job time budgets
  (`job_budget.py`); CodeQL (C# and workflows) and dependency review workflows; policy rules `workflow-permissions`,
  `workflow-pins`, `workflow-timeouts`, `required-docs` and `threat-model-refs`. `tests/Envoy.App.Tests` runs the OCR
  end-to-end harness (`src/Envoy.App/OcrEndToEnd.cs`: rendered chat through the real Windows OCR, line merging and
  chat understanding) and the `Hotkey` tests on Windows; `Envoy.exe --ocr-e2e <report.md>` runs the same cases. New
  `docs/security/threat-model.md`, `docs/security/repository-settings.md` and `docs/compatibility.md`; one ordered
  pre-release pass in `docs/releasing.md`; CODEOWNERS and security notes in the issue and PR templates.
- Reply box and overlay polish (spec 07c): `Envoy.Core.Compose` (`ReplyHistory`, `ReplyLength`, `ReplyText`,
  `PhraseOrdering`), `Envoy.Core.Pipeline.OverlayCopyText`, `OutgoingText.ExceedsHardMax`; settings `Overlay.Compact`,
  `Compose.ShowCharacterCount`, `MaxMessageLength`, `PhraseOrder`, `PhraseUsage`; `Services/ClipboardHelper.cs`;
  `OverlayWindow` item templates `FullItem`/`CompactItem`; smoke sections `overlay-compact({palette})` and
  `compose-history`; typecheck rule UIF-R13 (reply history never reaches settings or logs).
- Accessibility (spec 07b): `Envoy.Core.Ux.ContrastMath`/`Rgb`, `ThemeResolver`, `AccessibilityText`; settings
  `Accessibility.HighContrast` and `Overlay.EnsureReadableText`; palette keys `Focus.Ring`, `Overlay.Bg`,
  `Overlay.HitTest`, `Overlay.Dim` and `Palette.highcontrast.xaml` (from `SystemColors`); `PaletteContrastTests` hold every
  game palette to 4.5:1 text and 3:1 focus-ring contrast; `tools/typecheck/check_a11y.py` (focus visuals, accessible
  names, no literal colours in windows and pages, Tab cycling); smoke sections for the high-contrast palette, focus
  visuals, keyboard, readable overlay and a runtime name and Tab audit (`AccessibilityAudit`).
- Onboarding and notices (spec 07a): `Envoy.Core.Onboarding` (`OnboardingFlow`, `OnboardingGate`,
  `OnboardingOutcome`, `DisplayModeAdvisor`, `OnboardingText`), `Envoy.Core.Privacy.PrivacyText`,
  `PrivacyGatedTranslator` (outermost on both translator stacks), `OcrReadiness`, `HotkeyCheatSheet`, the
  `Notice`/`NoticeFactory`/`NoticeCenter` advisories on 02's throttle, `PageHints`/`WarRoomSlots`,
  `PhraseBook.LoadWithUserFile(path, out error)`; `SetupWizardWindow`, `TrayNotifier`, `IShell.OpenSetupWizard`/
  `ShowPage`; settings `Onboarding`, `Notifications` and `Updates.LastAnnouncedVersion`; smoke sections for the gate,
  the guide, notices, the War Room order and the tray notifier; typecheck rule R12.
- Distribution and updates (spec 06): `Envoy.Core.Updates` (`ReleaseVersion`, `IUpdateBackend`, `GitHubReleaseBackend`,
  the channel-aware `UpdateChecker`, `UpdatePreferences`, `UpdateCoordinator`, `UpdateViewBuilder`, `UpdateText`,
  `ReleaseNotesText`, `UserAgents`, `PendingUpdatePolicy`); `Program.Main` runs Velopack 1.2.161 (`vpk` pinned in
  `.config/dotnet-tools.json`); `VelopackUpdateBackend`, `UpdateActions`, `IShell.RestartToUpdate`, start-up slot 5
  (pending apply) and slot 9 (`StartUpdates`); typecheck rules UPD1/UPD2; smoke sections `updates(<palette>)` and
  `updates-portable` with `{palette}-update.png`; the release workflow (folder publish, `vpk pack`/`upload`, draft →
  asset assertion → publish, tag-only signing under the `release` environment, winget manifests) and its
  `tools/release/` checks; the diagnostics report has an "Updates" line. The User-Agent's Envoy token now carries the
  real version. `InstallLayout` keeps the portable exe, Debug builds and `--smoke-test` away from Velopack (whose
  locator writes a log file outside the data folder).
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
  handler wiring, diagnostics and the log; typecheck rules R8 to R10.
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

## [0.1.0]

First release.

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
