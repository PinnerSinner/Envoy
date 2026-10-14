# Reliability & Observability

Status: Proposed (revised against `00-integration.md`)

Area: `02-reliability-observability`. Step 3 of 11. **Depends on:** `01-ui-foundation` (pages, `IShell`,
`DialogService`, `HotkeyResults`, `Envoy.Core.Mvvm`, smoke loop over `PageIds.All`) and `08a-quality-gates`
(`BannedSymbols.txt`, `ShellLauncher`, `tools/ci/policy.json`, analyzers). It may assume nothing from later steps.

This area covers logging, global error handling, problem toasts and the shared notification throttle, the diagnostics
report, settings durability, safe mode, the data inventory with "Delete all Envoy data", instance data paths, and the
`App.OnStartup` / `ExitApp` skeleton that later areas fill in (contract §4). It does **not** cover `ChatWatcher`
internals (05), translation retry, circuit breaking or provider health (03), advisory notices and the tray notifier
(07a), installers or auto-update (06), or the CI workflow layout (08b).

When this file and `00-integration.md` disagree, the contract wins.

---

## Context

### What exists today (after 01 and 08a have landed)

| Concern | Today | Where |
| --- | --- | --- |
| Unhandled UI exceptions | `App.OnUnhandledException` appends to `%APPDATA%\Envoy\error.log`, then shows a **modal `MessageBox`** and sets `Handled = true`. | `src/Envoy.App/App.xaml.cs` |
| Background exceptions | No handler for `TaskScheduler.UnobservedTaskException` or `AppDomain.UnhandledException`. Tray items are WinForms callbacks, so an exception there shows WinForms' own exception dialog. | `App.xaml.cs` (`CreateTray`) |
| Swallowed failures | `AppHost.SaveNow` catches `IOException` and does nothing. Transcript writes, `ConfigureOcr`, `Say` and the update check use empty catches. `FallbackTranslator.ProviderFailed` only updates the `ProviderStatus` string. Nothing is recorded anywhere that lasts. | `src/Envoy.App/AppHost.cs` |
| Fire-and-forget | `_ = CheckForUpdatesAsync()` in `AppHost.Start`. 01's `AsyncRelayCommand` requires an `onError`, which `MainViewModel` routes to one handler that today only writes `Debug.WriteLine`. | `AppHost.cs`, `ViewModels/MainViewModel*.cs` |
| Modal boxes during play | `ComposeWindow` shows a `MessageBox` when `SendInput` fails (08a turned the failure into `SendBlockedException` with a curated message, but the box remains). It steals focus from the game. | `Windows/ComposeWindow.xaml.cs` |
| Settings file | `SettingsStore` writes atomically (`.tmp` then `File.Move`) and DPAPI-encrypts `[Secret]` properties. A corrupt file is copied to `settings.json.broken` and **defaults** are returned; the next autosave overwrites the user's file, losing every setting and key. `Version = 1` is never read. | `src/Envoy.Core/Settings/SettingsStore.cs`, `AppSettings.cs` |
| Data paths | `AppHost.DataFolder`, `LogsFolder` and `UserPhrasesPath` are **static** and always point at `%APPDATA%\Envoy`. Smoke-test hosts use a temp settings path but still read and write the real user folder. | `AppHost.cs`, `SmokeTest.cs` |
| Logs folder | `{DataFolder}\logs` already holds chat transcripts (`chat-yyyy-MM-dd_HHmmss.txt`). The About page's `OpenLogsCommand` opens it. | `AppHost.cs`, `Chat/TranscriptWriter.cs`, `Pages/AboutPage.xaml` |
| Process launches | 08a's `ShellLauncher` is the only file allowed to start processes (`TryOpen`, `InstallOcrPack`, `RestartSelf`, `RevealFile`). Rejected targets are not logged yet because no logger exists. | `Security/ShellLauncher.cs` |
| Bug reports | `docs/troubleshooting.md` asks for `error.log`, which holds only UI-thread exceptions with no version, OS, DPI or settings context. | `docs/troubleshooting.md` |
| Recovery | No way to start without the overlay, hotkeys and capture. A bad setting can only be fixed by deleting `settings.json`, which also deletes the API keys. No way to remove all of Envoy's data. | — |

### What is weak or missing for production

1. **No record of what happened.** "It stopped translating" comes with no log, version or OS.
2. **Modal dialogs during play.** A `MessageBox` steals focus from a borderless game mid-match.
3. **Silent background failures.** Unobserved task exceptions vanish; a non-UI crash leaves no trace.
4. **Fragile settings.** One corrupt write resets everything, including encrypted keys.
5. **No self-service recovery** and no way to delete everything Envoy stores.
6. **Smoke tests touch real user data** because the data paths are static.
7. **Privacy risk once logging exists.** A naive logger writes API keys (Google Free puts text in the query string),
   chat lines, player names, capture positions and the Windows user name. Logs get attached to public issues.

---

## Goals / Non-goals

### Goals

- A dependency-free logging abstraction in `Envoy.Core` with a size-capped rolling file and an in-memory ring. Redaction
  is central, so secrets, chat text and player names never reach disk unless the user opts in to chat text.
- Global exception handling whose decision logic is Core code with unit tests. Problems appear as **non-modal,
  non-activating toasts**, all gated by **one Core `NotificationThrottle`** that 07a later reuses.
- **Copy diagnostics**: a redacted, local-only text report for a GitHub issue.
- Durable settings: last-known-good backup, corruption recovery, an always-written schema version, save suppression for
  reset and delete.
- **Safe mode** (`--safe-mode` or Shift held) that skips the overlay, hotkeys and automatic capture.
- **Instance data paths** and a **data inventory** with "Delete all Envoy data".
- The ordered `OnStartup` / `ExitApp` skeleton from contract §4, with marked slots for later areas.

### Non-goals

- Telemetry, crash upload or any network reporting. Diagnostics leave the PC only when the user pastes or attaches them.
  This area adds **no network code**.
- Event Log, ETW, OpenTelemetry, a minidump writer, a full in-app log viewer, performance dashboards.
- Provider health tracking (03 owns `ProviderHealthRegistry`; it adds its rows to the report when it lands).
- Advisory notices, tray balloons and the War Room notice list (07a, built on this area's throttle).
- Changes inside `ChatWatcher` (05). This area observes `StateChanged` / `LastError` only.

---

## Requirements

Prefix `REL`. **MUST** = needed for a trustworthy 1.0 for strangers, **SHOULD** = expected, **COULD** = if time allows.
The MUST set is exactly the one in contract §9: REL-1, 2, 3, 4, 5, 11, 12, 13, 16, 18, 19, 23, 27, 29, 33, 34, 39, 40,
43, 44 (**20 MUST**). Folded ids are kept so references stay valid.

### Logging

- **REQ-REL-1 (MUST)** `Envoy.Core.Diagnostics` provides `ILog` and `LogHub` with levels Debug, Info, Warning, Error and
  Critical. They are thread-safe and add no NuGet package. 08a's `ShellLauncher` logs rejected targets through it.
- **REQ-REL-2 (MUST)** A rolling file sink writes `{DataFolder}\logs\envoy.log`. At 1 MiB it rolls to `envoy.1.log` …
  `envoy.4.log` (at most 5 files). It never creates, renames or deletes any other file in that folder.
- **REQ-REL-3 (MUST)** Writing a log entry never throws to the caller, even when the folder is unwritable or the disk is
  full. Failed writes are counted in `LogHub.DroppedWrites`.
- **REQ-REL-4 (MUST)** Before an entry reaches any sink, its message, fields and exception text are redacted with the mask
  `[redacted]`: every non-empty `[Secret]` value; known key patterns (`sk-ant-…`, `AKIA…`, DeepL `…:fx`,
  `x-api-key`/`authorization`/`auth_key` assignments); URL query strings and URL user-info. `LogRedactor` is the only
  redaction implementation (03's `ProviderHealthText.Sanitize` will delegate to it).
- **REQ-REL-5 (MUST)** Chat text and player names are logged only through `LogField.Sensitive(...)`, which renders
  `<N chars>` unless `Diagnostics.LogChatText` is `true`.
- **REQ-REL-6 (SHOULD)** The user-profile path is replaced with `%USERPROFILE%` in every entry.
- **REQ-REL-7 (SHOULD)** Identical entries (level, category, message) within 60 s collapse into one
  "(repeated N times)" entry.
- **REQ-REL-8 (SHOULD)** Minimum level is Info, or Debug when `Diagnostics.VerboseLogging` is on; changes apply without a
  restart.
- **REQ-REL-9 (SHOULD)** Each session starts with one Info header: version (`AppHost.CurrentVersion`), OS, runtime,
  architecture, safe-mode state, settings-load status. No user or machine name.
- **REQ-REL-10** *Folded into REQ-REL-23* (the 500-entry ring that feeds the report).

### Error handling

- **REQ-REL-11 (MUST)** Dispatcher **and** WinForms `ThreadException` (tray callbacks) exceptions go through one Core
  policy (`UnhandledErrorPolicy.OnUiException`) that logs at Error and returns the action: during the smoke test, leave
  unhandled (CI must fail); otherwise mark handled and raise a problem toast. The WinForms exception dialog never
  appears. *(Absorbs REQ-REL-14.)*
- **REQ-REL-12 (MUST)** `TaskScheduler.UnobservedTaskException` is logged at Warning and observed. The body is a Core
  method with a unit test; the smoke test checks wiring only.
- **REQ-REL-13 (MUST)** `AppDomain.UnhandledException` logs at Critical, flushes the log and writes a redacted
  `{DataFolder}\last-crash.json`. No UI.
- **REQ-REL-14** *Folded into REQ-REL-11.*
- **REQ-REL-15 (SHOULD)** If 5 or more UI exceptions occur within 30 s, Envoy logs Critical, writes `last-crash.json`,
  shows exactly one `DialogService` message and exits with code 1. This is the only modal error box allowed.
- **REQ-REL-16 (MUST)** Fire-and-forget tasks use Core `Task.Forget(ILog, string what)`, which logs faults at Error and
  cancellation at Debug. `_ = CheckForUpdatesAsync()` is converted. No `_ = …Async(` discard remains in `src/Envoy.App`.
  `MainViewModel.ReportCommandError` (01's single command-error handler) logs at Error and raises a problem toast.
- **REQ-REL-17 (SHOULD)** Previously silent failures are logged at Warning with a category and no chat text: settings
  save, transcript write, OCR configuration, speech, provider failure (today's `onProviderFailed` callback), capture
  watcher entering `Error`, a message ending `Failed`, a hotkey that is `Taken` or `Invalid`. Every `catch` in
  `AppHost.cs` logs; this rule goes into `CLAUDE.md` so later areas comply.

### Problem toasts and the notification throttle

- **REQ-REL-18 (MUST)** Problems appear in a `ToastWindow` that is topmost, `ShowActivated=false`,
  `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, themed only through palette keys, auto-closes after 10 s (paused while hovered)
  and has **Details** and **Close** buttons with `AutomationProperties.Name`. Every toast goes through the single
  `Envoy.Core.Notifications.NotificationThrottle` on `AppHost.Notifications`, with the contract §2.2 rules: the same key
  at most once per 2 min; at most 3 toasts visible (the oldest closes first); advisory balloons at least 60 s apart and at
  most 5 per session (enforced here, first used by 07a). *(Absorbs REQ-REL-20.)*
- **REQ-REL-19 (MUST)** No error opens a modal box. `MessageBox.Show` appears only in `Services/DialogService.cs` (01),
  and `DialogService` is used only for confirmations the user starts in `MainWindow` and the crash-loop message
  (REQ-REL-15). `ComposeWindow`'s send failure becomes a problem toast whose text is `SendBlockedException.Message` (08a)
  when it is that type, otherwise `ProblemText.ReplySendFailed`. Toast text never contains raw exception text.
- **REQ-REL-20** *Folded into REQ-REL-18.*
- **REQ-REL-21 (SHOULD)** `Diagnostics.ShowErrorToasts=false` turns problem toasts off. Problems are still logged.
- **REQ-REL-22 (SHOULD)** At startup, one toast reports a previous crash (`last-crash.json` present, or the session
  marker shows the previous session never ended) or settings that were recovered or reset.

### Diagnostics

- **REQ-REL-23 (MUST)** **Copy diagnostics** puts a plain-text report on the clipboard via `IShell.CopyText`. It contains
  Envoy and .NET versions, OS, architecture, culture, monitors (bounds, primary, DPI scale), the detected game profile,
  capture state and region **size only**, installed and selected OCR languages, the provider chain with "key set / not
  set" and the current provider status line, every non-`Registered` entry of `AppHost.HotkeyResults`, settings version
  and load status, safe-mode state, the last crash summary, dropped log writes, the last 50 log lines (from a 500-entry
  in-memory ring) and the redacted settings JSON. In that JSON `[Secret]` and `[Personal]` values become `"[set]"`/`""`
  or `"[N entries]"`, and `[Location]` values become `W×H`; a reflection test covers every marked property. The report
  is at most 60,000 characters. *(Absorbs REQ-REL-10 and REQ-REL-25.)*
- **REQ-REL-24 (SHOULD)** **Export diagnostics…** writes a `.zip` with `report.txt`, `settings.redacted.json`,
  `logs/envoy*.log` and, if present, `last-crash.json`, to a path from `IShell.ChooseExportPath`, then
  `IShell.RevealFile`. Never transcripts, raw `settings.json` or any `.bak`. Failure → "export failed" problem toast.
- **REQ-REL-25** *Folded into REQ-REL-23.*
- **REQ-REL-26** *Removed.* Provider health is owned by 03 (`ProviderHealthRegistry`); 03 adds its snapshot to the report.
- **REQ-REL-27 (MUST)** `Pages/AboutPage.xaml` gets a **Troubleshooting** section (contract §8.6 order: after Version &
  updates and Setup guide, before Privacy and licences) bound to commands in `ViewModels/MainViewModel.Diagnostics.cs`:
  Copy diagnostics; Open log folder; Restart in safe mode / Restart normally; Delete all Envoy data… (REQ-REL-44); and
  three checkboxes (show error pop-ups, detailed logging, include chat text in logs). Export and Reset buttons appear
  when REQ-REL-24 / 38 are built.
- **REQ-REL-28 (COULD)** Troubleshooting lists the 10 most recent Warning-or-higher entries and the last crash with a
  **Dismiss** button that deletes `last-crash.json`.

### Settings durability

- **REQ-REL-29 (MUST)** `AppSettings.CurrentVersion` is a constant (stays `1`), and `SettingsStore.Save` always writes
  `version = AppSettings.CurrentVersion`.
- **REQ-REL-30 (SHOULD)** A `SettingsMigrator` runs ordered `ISettingsMigration` steps (N→N+1) on the raw `JsonObject`.
  A missing `version` counts as 1. A gap or duplicate throws at construction. It ships with **no steps**.
- **REQ-REL-31 (SHOULD)** Before migrating, the original file is copied byte-for-byte to `settings.json.v{N}.bak`.
- **REQ-REL-32 (SHOULD)** A newer-version file loads best-effort, is copied to `settings.json.v{N}.bak` once, and is
  logged at Warning.
- **REQ-REL-33 (MUST)** A file that loads successfully is copied to `settings.json.bak` (last known good). If
  `settings.json` cannot be read, parsed or deserialized, it is copied to `settings.json.broken` and `.bak` is loaded
  instead. Only if that also fails are defaults used.
- **REQ-REL-34 (MUST)** `SettingsStore.LoadWithReport()` returns the settings plus `SettingsLoadStatus`, file version,
  backup path and a redacted error, for the startup toast and the report. `Load()` keeps working for existing callers.
- **REQ-REL-35 (SHOULD)** `null` settings sub-objects, lists, dictionaries and strings are replaced with defaults after
  deserialization.
- **REQ-REL-36 (SHOULD)** `Save` calls `Flush(true)` before the move. Save failures are logged; the third in a row raises
  a problem toast.
- **REQ-REL-37 (SHOULD)** An undecryptable `[Secret]` logs a Warning naming the property, never the value.
- **REQ-REL-38 (SHOULD)** **Reset settings…** confirms, suppresses saving (REQ-REL-44), copies `settings.json` to
  `settings.json.reset-{yyyyMMdd-HHmmss}.bak`, deletes `settings.json` and restarts. Nothing written later in the session
  (exit save, `MainWindow.OnClosed`, `Host.Dispose`) undoes the reset.

### Safe mode

- **REQ-REL-39 (MUST)** Envoy starts in safe mode when the first instance is launched with `--safe-mode` or while either
  Shift key is held (`GetAsyncKeyState`, read at the very start of `OnStartup`). The decision runs after the mutex and
  after 06's pending-update slot (contract §4 steps 4–6).
- **REQ-REL-40 (MUST)** In safe mode, following a Core `StartupPlan`: no overlay window; no global hotkeys (every
  `HotkeyResults` entry is `SkippedSafeMode`, a value this step appends to 01's `HotkeyStatus`); no automatic capture (manual start still works); `MainWindow` is always
  shown, even with `--minimized`; settings can still be edited and saved. `MainWindow` shows a safe-mode banner above
  every page with **Restart normally**, and the tray tooltip reads "Envoy (safe mode)". *(Absorbs REQ-REL-41.)*
- **REQ-REL-41** *Folded into REQ-REL-40.*
- **REQ-REL-42 (COULD)** After 2 consecutive launches that never became healthy (`StartupSentinel`), the next launch is
  in safe mode automatically and the banner says why.

### Verification and data

- **REQ-REL-43 (MUST)** The smoke test follows contract §6: it runs on a temp `DataFolder` (every host it creates gets its
  own under `%TEMP%\envoy-smoke-{guid}`; the root is deleted at teardown), and adds sections `safe-host`, `diagnostics`,
  `handlers` (setup), `safe-banner({id})` and `toast({id})` (per palette), and `log-tail` (teardown), each logging
  `[smoke] <section> ok elapsed_ms=<n>`.
- **REQ-REL-44 (MUST)** All Envoy data lives under the **instance** `AppHost.DataFolder`, from
  `AppHostOptions.DataFolder` (default: the settings file's folder). The static `DataFolder`, `LogsFolder` and
  `UserPhrasesPath` are deleted, and no code outside `SettingsStore.DefaultPath` names `ApplicationData`/`%APPDATA%`.
  Core `DataInventory.Enumerate(dataFolder)` lists each Envoy file and its purpose. About › Troubleshooting has
  **Delete all Envoy data…**: it confirms, suppresses saving, removes the Run value, deletes only inventory entries and
  restarts with `ShellLauncher.RestartSelf(RestartFlags.None)`. Every inventory name appears in `docs/privacy.md`.

**MUST count: 20.** SHOULD: 6, 7, 8, 9, 15, 17, 21, 22, 24, 30, 31, 32, 35, 36, 37, 38. COULD: 28, 42.

---

## Design

### Overview

```
Envoy.Core/Diagnostics                         Envoy.App
──────────────────────                         ─────────
LogHub ─► sinks: RollingFileSink, MemoryLogSink App.OnStartup (contract §4 skeleton, below)
  ├─ LogRedactor (the one redactor)               ├─ LogBootstrap.Create(dataFolder?)
  └─ RepeatSuppressor (SHOULD)                    ├─ GlobalExceptionHandlers → UnhandledErrorPolicy (Core)
TaskExtensions.Forget                             ├─ SafeModeDecision + StartupSentinel
UnhandledErrorPolicy, CrashLoopGuard, CrashMarker ├─ new AppHost(path, Dispatcher, AppHostOptions)
SafeModeDecision, StartupSentinel, StartupPlan    ├─ ToastService (ToastWindow ×≤3) ◄─ AppHost.Notifications
DiagnosticsReport (+ DiagnosticsBundle SHOULD)    └─ overlay/hotkeys per StartupPlan
DataInventory                                   DiagnosticsCollector (App: OS, monitors, DPI, OCR, game)
ProblemText, SafeModeText, DiagnosticsText      IShell 02 block → DialogService / ShellLauncher
Envoy.Core/Notifications
NotificationThrottle, NotificationClass, ProblemReport
Envoy.Core/Settings
SettingsStore.LoadWithReport ─► parse ─► (SettingsMigrator, SHOULD) ─► deserialize ─► (Sanitizer, SHOULD)
          │ fail → .broken + try .bak                       │ ok → refresh .bak
SettingsStore.SuppressSaves / ResetToDefaults
SettingsRedaction ([Secret] / [Personal] / [Location])
```

### Core: logging (`src/Envoy.Core/Diagnostics/`)

**`Logging.cs`**

```csharp
namespace Envoy.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error, Critical }

public readonly record struct LogField(string Name, object? Value, bool IsSensitive = false)
{
    public static LogField Of(string name, object? value) => new(name, value);
    public static LogField Sensitive(string name, string? value) => new(name, value, true);
}

/// <summary>A rendered, already-redacted entry. Sinks never see raw values.</summary>
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message,
                              string Fields, string? Exception, int RepeatCount = 0);

public interface ILogSink { void Write(LogEntry entry); void Flush(); }

public interface ILog
{
    string Category { get; }
    bool IsEnabled(LogLevel level);
    void Write(LogLevel level, string message, Exception? exception = null, params LogField[] fields);
}

public static class LogExtensions   // Debug/Info/Warn/Error/Critical helpers
{
    public static void Debug(this ILog log, string message, params LogField[] fields);
    public static void Info(this ILog log, string message, params LogField[] fields);
    public static void Warn(this ILog log, string message, Exception? ex = null, params LogField[] fields);
    public static void Error(this ILog log, string message, Exception? ex = null, params LogField[] fields);
    public static void Critical(this ILog log, string message, Exception? ex = null, params LogField[] fields);
}

public sealed class NullLog : ILog { public static readonly NullLog Instance = new(); /* no-ops */ }

public sealed class LogHub : IDisposable
{
    public LogHub(TimeProvider? clock = null);
    public LogLevel MinimumLevel { get; set; }       // volatile; default Info
    public bool IncludeSensitive { get; set; }       // volatile; = Diagnostics.LogChatText
    public LogRedactor Redactor { get; }
    public long DroppedWrites { get; }               // Interlocked
    public void AddSink(ILogSink sink);
    public void RemoveSink(ILogSink sink);
    public ILog For(string category);
    public void Flush();                             // never throws
    public void Dispose();                           // flushes repeat summaries, then sinks; never throws
}
```

`LogHub.Write`:

1. Check the level.
2. Render fields as space-separated `name=value`. Sensitive fields become `name=<N chars>` (N = `string.Length`) unless
   `IncludeSensitive`; then the value is quoted with `\r`, `\n` and `"` escaped.
3. Render the exception with `ToString()`.
4. Redact message, fields and exception with `Redactor.Redact`.
5. Pass through `RepeatSuppressor` (SHOULD; without it, write directly).
6. Fan out to each sink in a `try`/`catch`; a sink exception increments `DroppedWrites` and is swallowed.

One lock covers steps 5–6. Entry rates are single digits per second, so a lock is fine.

**`LogRedactor.cs`**

```csharp
public sealed class LogRedactor
{
    public const string Mask = "[redacted]";
    public void SetSecrets(IEnumerable<string> values);   // copies to an immutable array, Volatile.Write; ignores < 6 chars
    public void AddPathAlias(string path, string alias);  // SHOULD: user profile → "%USERPROFILE%"
    public string Redact(string? text);
}
```

Order: (1) registered secrets, ordinal, longest first; (2) patterns `sk-ant-[A-Za-z0-9_\-]{8,}`, `AKIA[0-9A-Z]{16}`,
`[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}:fx`,
`(?i)(x-api-key|authorization|auth_key)\s*[:=]\s*\S+`; (3) URLs `https?://[^\s"']+` lose user-info
(`https://[redacted]@host`) and query (`?[redacted]`); (4) path aliases, case-insensitive. Patterns are compiled once.
Plain GUIDs are kept. `SetSecrets` stores a copy, so later settings edits never change it without another
`ApplyDiagnosticsSettings` (contract §7 snapshot rule).

**`RepeatSuppressor.cs`** (SHOULD, internal): key `(Level, Category, Message)`; first occurrence written; repeats within
60 s (injected `TimeProvider`) only count; the next occurrence after the window, or `Flush`/`Dispose`, writes one entry
with `RepeatCount = n` (`… (repeated n times in 60 s)`).

**`RollingFileSink.cs`**

```csharp
public sealed class RollingFileSink : ILogSink, IDisposable
{
    public RollingFileSink(string folder, string baseName = "envoy", long maxBytes = 1_048_576, int maxFiles = 5);
    public string CurrentPath { get; }
    public IReadOnlyList<string> ExistingFiles();   // envoy.log, envoy.1.log … newest first
}
```

- Opens lazily with `FileMode.Append`, `FileShare.ReadWrite | FileShare.Delete`, UTF-8 without BOM, `AutoFlush = true`.
- Before a write, if `size + line > maxBytes`: close; delete `envoy.{maxFiles-1}.log`; rename `envoy.{i}.log` →
  `envoy.{i+1}.log` downwards; rename `envoy.log` → `envoy.1.log`; reopen.
- Touches only names matching `^envoy(\.\d+)?\.log$`.
- On `IOException`/`UnauthorizedAccessException` it throws to the hub (which counts it) and does not reopen for 30 s.
- Line format (`LogFormatter.Format`): `2026-10-14T18:03:12.481Z WRN [Translation] Provider failed provider=deepl`
  then exception lines indented two spaces. UTC, milliseconds, levels `DBG/INF/WRN/ERR/CRT`.

**`MemoryLogSink.cs`** — `MemoryLogSink(int capacity = 500)`: `Snapshot(LogLevel minimum = Debug, int max = int.MaxValue)`
(newest last). No events; the UI polls.

**`TaskExtensions.cs`** — `public static void Forget(this Task task, ILog log, string what)`: faults → Error with the
exception; cancellation → Debug; never throws.

### Core: error policy (`src/Envoy.Core/Diagnostics/`)

```csharp
public enum UnhandledSource { Dispatcher, WinForms, TaskScheduler, AppDomain }
public enum UiErrorAction { LeaveUnhandled, ShowProblem, ExitCrashLoop }
public sealed record UiErrorOutcome(UiErrorAction Action, ProblemReport? Problem);

/// <summary>The bodies of the four global handlers. App only wires events and applies the outcome.</summary>
public sealed class UnhandledErrorPolicy(LogHub hub, Func<bool> isSmokeTest, Func<string?> crashMarkerPath,
                                         string version, CrashLoopGuard? crashLoop = null)
{
    /// Logs Error under "Unhandled" with source. Smoke → LeaveUnhandled. Crash loop tripped (SHOULD) → logs Critical,
    /// writes the marker, flushes → ExitCrashLoop. Otherwise → ShowProblem(ProblemText.Unexpected(key)).
    /// Key = exception type + TargetSite name. Re-entrant calls (exception while handling) log Critical → LeaveUnhandled.
    public UiErrorOutcome OnUiException(Exception ex, UnhandledSource source);
    /// Logs Warning "Unobserved task exception" with the exception. Caller calls SetObserved().
    public void OnUnobserved(AggregateException ex);
    /// Logs Critical with isTerminating, writes CrashMarker to crashMarkerPath() if non-null, flushes. Never throws.
    public void OnDomainException(object? exceptionObject, bool isTerminating);
}

public sealed class CrashLoopGuard(int threshold = 5, TimeSpan? window = null /*30 s*/, TimeProvider? clock = null)
{
    public bool RecordAndCheckTripped();   // SHOULD (REQ-REL-15)
}

public sealed record CrashMarker(DateTimeOffset Time, string Version, string Source, string ExceptionType,
                                 string Message, string Stack)          // Message, Stack redacted; Stack ≤ 4,000 chars
{
    public static CrashMarker From(Exception ex, string source, string version, LogRedactor redactor, TimeProvider? clock = null);
    public void Write(string path);              // temp file + move; never throws
    public static CrashMarker? TryRead(string path);   // null if missing or corrupt
}
```

### Core: notifications (`src/Envoy.Core/Notifications/`)

```csharp
namespace Envoy.Core.Notifications;

public enum NotificationClass { Problem, Advisory }

/// <summary>A problem toast. Title/Detail come from ProblemText; never raw exception text.</summary>
public sealed record ProblemReport(string Key, string Title, string Detail, LogLevel Severity = LogLevel.Error);

/// <summary>The one throttle (contract §2.2). Single instance on AppHost.Notifications. Thread-safe (lock).</summary>
public sealed class NotificationThrottle(TimeProvider? clock = null)
{
    public static readonly TimeSpan SameKeyWindow = TimeSpan.FromMinutes(2);
    public const int MaxVisibleToasts = 3;                                  // enforced by ToastService
    public static readonly TimeSpan AdvisoryBalloonSpacing = TimeSpan.FromSeconds(60);
    public const int MaxAdvisoryBalloonsPerSession = 5;

    /// <summary>Problem toast: false if the same key was shown within SameKeyWindow; records the show when true.</summary>
    public bool TryShowToast(string key);
    /// <summary>Advisory balloon (07a): same-key window, plus ≥ 60 s since the last balloon, plus ≤ 5 per session.</summary>
    public bool TryShowBalloon(string key);
}
```

`TryShowBalloon` is specified behaviour of the throttle (contract §2.2), unit-tested here; 07a's `TrayNotifier` is its
first caller and keeps no counters of its own.

### Core: UI text (`src/Envoy.Core/Diagnostics/*Text.cs`)

Per contract §8.7: whole sentences, named placeholders, second person, no "Error:" prefixes, no raw exception text.

| Member | Title | Detail |
| --- | --- | --- |
| `ProblemText.Unexpected` | Something went wrong. | Envoy kept running. Select Details to see what happened. |
| `ProblemText.CommandFailed` | That action didn't finish. | Envoy kept running. Select Details to see what happened. |
| `ProblemText.CaptureError` | Envoy stopped reading chat. | Check the chat area, then start reading again. |
| `ProblemText.SettingsSaveFailed` | Envoy couldn't save your settings. | Your changes apply until you close Envoy. Check that the settings folder isn't read-only. |
| `ProblemText.SettingsRecovered` | Your settings file was damaged. | Envoy restored your last working settings. The damaged copy is saved as settings.json.broken. |
| `ProblemText.SettingsReset` | Your settings file was damaged. | Envoy started with default settings. The damaged copy is saved as settings.json.broken. |
| `ProblemText.PreviousCrash` | Envoy closed unexpectedly last time. | If it happens again, copy diagnostics from About › Troubleshooting into a bug report. |
| `ProblemText.ReplySendFailed` | Envoy couldn't type into the game. | Your reply is on the clipboard. Paste it into chat. |
| `ProblemText.ExportFailed` | Envoy couldn't save the diagnostics file. | Choose another folder and try again. |
| `ProblemText.ClipboardBusy` | Envoy couldn't use the clipboard. | Close any clipboard tool and try again. |

`SafeModeText` (banner, reasons, tray tooltip, "Hotkeys are off in safe mode."), `DiagnosticsText` (status lines,
privacy caption) and `DataText` (Delete-all confirmation built from the inventory) follow the same rule.

### Core: safe mode and start-up (`src/Envoy.Core/Diagnostics/SafeMode.cs`)

```csharp
public enum SafeModeReason { CommandLine, ShiftHeld, RepeatedStartupFailures }

public static class SafeModeDecision
{
    public const string Flag = "--safe-mode";
    /// flag > shift > (autoThreshold > 0 && unfinishedStarts >= autoThreshold) > null. autoThreshold = 0 unless REQ-REL-42 is built.
    public static SafeModeReason? Decide(IReadOnlyList<string> args, bool shiftHeld, int unfinishedStarts, int autoThreshold = 0);
}

public sealed record StartupHistory(int UnfinishedStarts, bool PreviousSessionEndedUncleanly);

/// <summary>{DataFolder}\session.json. Feeds REQ-REL-22 (unclean end) and REQ-REL-42 (COULD).</summary>
public sealed class StartupSentinel(string path, TimeProvider? clock = null)
{
    public StartupHistory BeginStartup();  // reads the previous record, then records "starting"; never throws
    public void MarkHealthy();             // records "running" and resets the unfinished count; never throws
    public void EndSession();              // deletes the file on a clean exit; never throws
}

public sealed record StartupPlan(bool CreateOverlay, bool RegisterHotkeys, bool AutoStartCapture,
                                 bool ShowMainWindow, bool SafeMode)
{
    /// Safe mode: no overlay, no hotkeys, no auto capture, always show MainWindow.
    /// Otherwise: overlay/hotkeys on; AutoStartCapture = startCapturingAutomatically;
    /// ShowMainWindow = !minimized || !firstRunCompleted (today's rule).
    public static StartupPlan For(SafeModeReason? safeMode, bool minimized, bool firstRunCompleted,
                                  bool startCapturingAutomatically);
}
```

Later areas add their own fields to `StartupPlan` in their own step (06: automatic update checks; 07a: notices and the
guide gate). No placeholder fields are added here.

### Core: diagnostics report (`src/Envoy.Core/Diagnostics/DiagnosticsReport.cs`)

```csharp
public sealed record MonitorInfo(string Name, bool Primary, int X, int Y, int Width, int Height, double Scale);
public sealed record ProviderLine(string Id, bool KeySet);
public sealed record HotkeyLine(string ActionName, string Combo, string Status);

public sealed record DiagnosticsSnapshot
{
    public required string AppVersion { get; init; }        // AppHost.CurrentVersion
    public string? InformationalVersion { get; init; }
    public required string Runtime { get; init; }
    public required string OsDescription { get; init; }
    public required string Architecture { get; init; }
    public string? Culture { get; init; }
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = [];
    public string? Game { get; init; }                      // profile name only, never window titles
    public string? CaptureState { get; init; }
    public string? RegionSize { get; init; }                // "600×200" only
    public IReadOnlyList<string> OcrInstalled { get; init; } = [];
    public IReadOnlyList<string> OcrSelected { get; init; } = [];
    public IReadOnlyList<ProviderLine> Providers { get; init; } = [];
    public string? ProviderStatus { get; init; }            // today's AppHost.ProviderStatus, redacted
    public IReadOnlyList<HotkeyLine> Hotkeys { get; init; } = [];   // non-Registered HotkeyResults only
    public SafeModeReason? SafeMode { get; init; }
    public SettingsLoadResult? SettingsLoad { get; init; }  // Settings property not rendered
    public CrashMarker? LastCrash { get; init; }
    public TimeSpan Uptime { get; init; }
    public long DroppedLogWrites { get; init; }
}

public static class DiagnosticsReport
{
    public const int MaxClipboardChars = 60_000;
    /// Header "## Envoy diagnostics", sections Envoy / Windows / Monitors / Game & capture / OCR / Translation /
    /// Hotkeys / Settings / Last crash / Recent log, redacted settings JSON in a code fence.
    public static string Render(DiagnosticsSnapshot snapshot, AppSettings settings, IReadOnlyList<LogEntry> recentLog,
                                int maxLogLines = 50, int maxChars = MaxClipboardChars);
}

public static class DiagnosticsBundle   // SHOULD (REQ-REL-24)
{
    public static void Write(Stream destination, string reportText, AppSettings settings,
                             IEnumerable<string> logFiles, string? crashMarkerPath);
}
```

- Truncation drops the oldest log lines first and adds `… (N earlier lines omitted)`; headers are never dropped.
- 03 adds a health section from `TranslationRuntime.Health.Snapshot()` when it lands; 06 adds update state. Neither
  field exists in this step.
- `DiagnosticsBundle` opens log files with `FileShare.ReadWrite | FileShare.Delete` and uses BCL `ZipArchive`.

### Core: data inventory (`src/Envoy.Core/Diagnostics/DataInventory.cs`)

```csharp
public sealed record DataItem(string Pattern, string Purpose);   // Pattern relative to the data folder
public sealed record DataFile(string Path, string Pattern, long Bytes);
public sealed record DeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failed);

public static class DataInventory
{
    public static IReadOnlyList<DataItem> Items { get; }
    public static IReadOnlyList<DataFile> Enumerate(string dataFolder);
    /// Deletes only files matching Items. Never recurses beyond logs/, never deletes reparse-point directories,
    /// removes logs/ and the data folder only if they end up empty. Collects failures; never throws.
    public static DeleteResult DeleteAll(string dataFolder);
}
```

| Pattern | Purpose |
| --- | --- |
| `settings.json` | Your settings; API keys are encrypted for your Windows account. |
| `settings.json.bak` | Last settings that loaded correctly. |
| `settings.json.v*.bak` | Copy kept before a settings upgrade. |
| `settings.json.reset-*.bak` | Copy kept when you reset settings. |
| `settings.json.broken` | A damaged settings file kept for troubleshooting. |
| `settings.json.tmp` | Left over if Envoy closed while saving. |
| `phrases.json` | Your own quick phrases. |
| `logs/envoy*.log` | Diagnostic log. |
| `logs/chat-*.txt` | Chat transcripts, if you turned them on. |
| `last-crash.json` | Details of the last crash. |
| `session.json` | Start-up marker used to detect crashes. |
| `error.log` | Error log from Envoy 0.1. |

03 adds `translation-cache.json` and 04 adds `chat-formats.json` in their own steps, each with its `docs/privacy.md` line.
This step creates `docs/privacy.md` with a **"What Envoy stores on your PC"** section listing these items; 07a later adds
the data-flow sections and the FAQ link.

### Core: settings (`src/Envoy.Core/Settings/`)

**`AppSettings.cs` (shared):**

```csharp
[AttributeUsage(AttributeTargets.Property)] public sealed class PersonalAttribute : Attribute;  // names, chat text
[AttributeUsage(AttributeTargets.Property)] public sealed class LocationAttribute : Attribute;  // screen positions

public sealed class AppSettings
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    [Personal] public string MyPlayerName { get; set; } = "";
    [Personal] public List<string> MutedPlayers { get; set; } = [];
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    // … unchanged
}
public sealed class CaptureSettings { [Location] public Dictionary<string, ScreenRegion> Regions { get; set; } = new(); /* … */ }

public sealed class DiagnosticsSettings
{
    public bool ShowErrorToasts { get; set; } = true;
    public bool VerboseLogging { get; set; } = false;
    public bool LogChatText { get; set; } = false;
}
```

All additive; `CurrentVersion` stays 1 (contract §7). 04 adds `[Personal]` to `Chat.SystemLinePatterns` and 05 adds
`[Location]` to `Capture.WindowRegions` in their own steps.

**`SettingsStore.cs` (changed):**

```csharp
public enum SettingsLoadStatus { Defaults, Loaded, RecoveredFromBackup, ResetCorrupt,
                                 Migrated, NewerVersion, MigrationFailed }   // last three only with REQ-REL-30/32

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, int FileVersion,
                                        string? BackupPath, string? Error);

public sealed class SettingsStore
{
    public SettingsStore(string path, ISecretProtector protector);                 // kept
    public SettingsStore(string path, ISecretProtector protector, ILog? log, TimeProvider? clock = null,
                         IReadOnlyList<ISettingsMigration>? migrations = null);
    public string LastKnownGoodPath => Path + ".bak";
    public string BrokenPath => Path + ".broken";
    public AppSettings Load() => LoadWithReport().Settings;
    public SettingsLoadResult LoadWithReport();
    public void Save(AppSettings settings);        // no-op (Info log) once suppressed
    public bool SavesSuppressed { get; }
    public void SuppressSaves();                    // irreversible for this instance
    public string ResetToDefaults();                // SHOULD: SuppressSaves(), copy to .reset-{ts}.bak, delete; returns backup path
}
```

`LoadWithReport`:

1. No file → `Defaults`.
2. Read text; `JsonNode.Parse`; root must be a `JsonObject`.
3. (SHOULD, REQ-REL-30–32) `ReadVersion`; older → `.v{v}.bak` then migrate; newer → `.v{v}.bak` once, Warning.
4. Deserialize; (SHOULD) `SettingsSanitizer.RepairNulls`; unprotect secrets, logging failures by property name.
5. On success copy the original text to `.bak` (temp + move; failures logged, not thrown). Return `Loaded`.
6. Read, parse or deserialize failure (`JsonException`, `InvalidOperationException`, `NotSupportedException`,
   `IOException`, `UnauthorizedAccessException`): copy to `.broken`, run steps 2–4 on `.bak`; success →
   `RecoveredFromBackup`, otherwise `ResetCorrupt` with defaults. The redacted message goes into `Error`.

`Save`: return if suppressed; deep-copy; set `copy.Version = CurrentVersion`; encrypt secrets; write `path.tmp` with
`Flush(true)` (SHOULD); `File.Move(..., overwrite: true)`. `IOException`/`UnauthorizedAccessException` still propagate
to `AppHost.SaveNow`.

Suppression lives in the store, not in `App`, so every later writer of `settings.json` (exit save,
`MainWindow.OnClosed`, `Host.Dispose`) is covered and the behaviour is a Core unit test.

**`SettingsMigration.cs` (SHOULD)**: `ISettingsMigration { int FromVersion; string Description; void Apply(JsonObject) }`,
`SettingsMigrator(migrations, currentVersion)` (throws on gap or duplicate), `ReadVersion` (missing → 1),
`SettingsMigrations.All = []`.

**`SettingsRedaction.cs`:**

```csharp
public static class SettingsRedaction
{
    public static IReadOnlyList<string> SecretValues(AppSettings settings);
    public static string ToRedactedJson(AppSettings settings);
    public static bool HasSecret(AppSettings settings, string propertyName);
}
```

`ToRedactedJson` walks a `JsonNode` copy. `[Secret]`/`[Personal]` string → `"[set]"` or `""`; list →
`"[N entries]"`. `[Location]` dictionary of `ScreenRegion` → each value becomes `"W×H"`; single `ScreenRegion` →
`"W×H"`.

### App: instance paths and `AppHostOptions` (shared `src/Envoy.App/AppHost.cs`)

```csharp
public sealed record AppHostOptions                 // 02 creates; 06 adds UpdateBackend in its own step
{
    public LogHub? Log { get; init; }               // null → new LogHub() with no sinks
    public MemoryLogSink? RecentLog { get; init; }
    public SafeModeReason? SafeMode { get; init; }
    public string? DataFolder { get; init; }        // null → Path.GetDirectoryName(settingsPath)
    public TimeProvider? TimeProvider { get; init; }// null → TimeProvider.System; passed to every timed Core type
}

public AppHost(string settingsPath, Dispatcher ui, AppHostOptions? options = null);

public string DataFolder { get; }                   // instance; the statics are deleted
public string LogsFolder => Path.Combine(DataFolder, "logs");
public string UserPhrasesPath => Path.Combine(DataFolder, "phrases.json");
public string CrashMarkerPath => Path.Combine(DataFolder, "last-crash.json");
public LogHub Log { get; }
public MemoryLogSink? RecentLog { get; }
public TimeProvider Time { get; }
public SafeModeReason? SafeMode { get; }
public bool IsSafeMode => SafeMode is not null;
public SettingsLoadResult SettingsLoad { get; }
public NotificationThrottle Notifications { get; }  // the single instance
public bool SaveSuppressed => _store.SavesSuppressed;
public void SuppressSave() => _store.SuppressSaves();
public event Action<ProblemReport>? ProblemReported; // raised on the UI thread
public void ReportProblem(ProblemReport report);     // any thread; marshals; logs at report.Severity
public string BuildDiagnosticsText(DiagnosticsSnapshot snapshot);
public void ResetSettings();                         // SHOULD: _store.ResetToDefaults()
```

Callers of the deleted statics move to the instance: `MainViewModel.DataFolder`, `OpenLogsCommand`, `EditPhrasesCommand`
and `IShell.OpenUrl` (which passes `Host.DataFolder` to `ShellLauncher.TryOpen`). `AppHost.CurrentVersion` stays
`Version`.

Constructor (contract §3 positions 1–2; later areas insert at 3–9):

1. `_store = new SettingsStore(path, new DpapiProtector(), Log.For("Settings"), Time)`;
   `SettingsLoad = _store.LoadWithReport(); Settings = SettingsLoad.Settings;`
2. `ApplyDiagnosticsSettings()`: `Log.MinimumLevel`, `Log.IncludeSensitive`,
   `Log.Redactor.SetSecrets(SettingsRedaction.SecretValues(Settings))`.

Then, in today's existing positions: phrases from `UserPhrasesPath`, transcripts in `LogsFolder`, and subscriptions —
`Watcher.StateChanged` → on `Error`, Warning under `Capture` (`error=` redacted `LastError`) and
`ReportProblem(ProblemText.CaptureError, key "capture:error")`; `onProviderFailed` → Warning
`Provider failed provider={id}` (03 replaces this with its `Diagnostic` subscription); `Pipeline.MessageUpdated` →
`Failed` logs Warning with `LogField.Sensitive("text", …)`.

`ApplySettings` position 1 is `ApplyDiagnosticsSettings()`. `RefreshGame` position 1 logs Info "Game detected" /
"Game closed" with the profile name only. `Start()`: capture starts only when `!IsSafeMode` and the setting is on;
`CheckForUpdatesAsync().Forget(_log, "update check")` (06 later replaces the body). `SaveNow()`: catches
`IOException`/`UnauthorizedAccessException`, logs Warning, and on the third consecutive failure reports
`SettingsSaveFailed` (SHOULD). Every `catch` logs. `Dispose()`: `Log.Flush()`; `App` owns and disposes the hub.

**Rule for Dispose-time writers (added to `CLAUDE.md`).** Anything written during `Host.Dispose` (03's cache, 04's
user formats) checks `Host.SaveSuppressed` first.

### App: `IShell` additions (02 block, contract §3)

```csharp
// 02
bool Confirm(string title, string message);       // → DialogService.Confirm (owner = MainWindow)
void Restart(bool safeMode);                      // → ExitApp(safeMode ? RestartFlags.SafeMode : RestartFlags.None)
void ResetSettingsAndRestart();                   // SHOULD (REQ-REL-38); omitted and noted under Deviations if not built
void DeleteAllDataAndRestart();                   // REQ-REL-44
string? ChooseExportPath(string defaultFileName); // SHOULD (REQ-REL-24) → DialogService.ChooseSavePath (Desktop)
void CopyText(string text);                       // Clipboard.SetText, 3 tries 100 ms apart; then ClipboardBusy problem
void RevealFile(string path);                     // SHOULD (REQ-REL-24) → ShellLauncher.RevealFile
```

`DialogService` (01: `ShowInfo`, `ShowWarning`) gains `bool Confirm(Window? owner, string title, string message)`
(OK/Cancel, Warning icon) and `string? ChooseSavePath(Window? owner, string defaultFileName)` (SHOULD, with REQ-REL-24;
`Microsoft.Win32.SaveFileDialog` in the Desktop folder). It remains the only file that calls `MessageBox.Show` (01's
`check_ui_rules.py` R5). `ShowWarning` is kept for the crash-loop message (REQ-REL-15); if REQ-REL-15 is not built, 02
deletes it, because its other caller (the old unhandled-exception box) is gone.

### App: `App.OnStartup` and `ExitApp` skeleton (contract §4)

Later steps fill the bracketed slots; this step leaves a one-line comment at each slot and no code.

```text
OnStartup(e):
 0. shiftHeld = Native.IsShiftDown()                        // read first, so a quick release still counts
 1. [06 Program.Main runs Velopack before WPF]
 2. dataFolder = IsSmokeArgs(e.Args) ? null : Path.GetDirectoryName(SettingsStore.DefaultPath)
    (_hub, _recent, _file) = LogBootstrap.Create(dataFolder)   // smoke: memory sink only; file sink added by SmokeTest
    ShellLauncher.Log = _hub.For("Shell")
    _handlers = GlobalExceptionHandlers.Install(this, policy)   // Dispatcher, WinForms, TaskScheduler, AppDomain
 3. smoke branch: --smoke-test → SmokeTest.Run(this, e.Args, _hub, _recent); return
    [08b --ocr-e2e] [05 --soak]
 4. single-instance mutex (unchanged); second instance signals and exits
 5. [06 pending update apply — skipped when --safe-mode or shiftHeld; runs before the sentinel]
 6. history = sentinel.BeginStartup()
    safe = SafeModeDecision.Decide(e.Args, shiftHeld, history.UnfinishedStarts, autoThreshold: 0 /* 2 with REQ-REL-42 */)
 7. Host = new AppHost(SettingsStore.DefaultPath, Dispatcher, new AppHostOptions { Log = _hub, RecentLog = _recent, SafeMode = safe })
    LogBootstrap.WriteHeader(_hub, safe, Host.SettingsLoad)
    plan = StartupPlan.For(safe, e.Args.Contains("--minimized"), Host.Settings.FirstRunCompleted, Host.Settings.StartCapturingAutomatically)
 8. _toasts = new ToastService(Dispatcher, Host.Notifications, () => Host.Settings.Diagnostics.ShowErrorToasts,
                               () => Host.Game?.MainWindow ?? 0, OpenTroubleshooting, _hub.For("Toast"))
    _handlers.ShowProblem = _toasts.Show; Host.ProblemReported += _toasts.Show
    [07a NoticeCenter + TrayNotifier unless safe] [07b high-contrast listeners]
 9. Host.Start()                                            // capture gated by plan.AutoStartCapture
    [06 Updates.StartAsync unless safe]
10. plan.CreateOverlay → overlay; plan.RegisterHotkeys → RegisterHotkeys() + SettingsApplied subscriptions
    else Host.HotkeyResults = every configured action with HotkeyStatus.SkippedSafeMode (a value 02 appends)
11. CreateTray()                                            // safe: tooltip SafeModeText.Tray, item "Restart normally"
    window choice: safe → ShowMain(); [07a guide gate]; else plan.ShowMainWindow → ShowMain()
    startup problem toast (SHOULD, REQ-REL-22) from Host.SettingsLoad, CrashMarker.TryRead, history.PreviousSessionEndedUncleanly
12. sentinel.MarkHealthy() after 30 s (one-shot DispatcherTimer) or the first MainWindow.ContentRendered, whichever first

ExitApp(RestartFlags? restart = null, bool wipeData = false):     // IShell.ExitApp() → ExitApp()
 1. hide overlay, unregister hotkeys
 2. if !Host.SaveSuppressed: apply pending edits, SaveNow()   // MainWindow.OnClosed checks the same flag
 3. Host.Dispose()                                          // [06 cancels update CTS] [03 saves cache unless suppressed]
 4. sentinel.EndSession()
 5. dispose [07a notifier], toasts (CloseAll), tray
 6. _hub.Flush(); _hub.Dispose()                            // last thing that writes envoy.log
 6a. if wipeData: result = DataInventory.DeleteAll(dataFolder); if result.Failed.Any → DialogService.ShowInfo(DataText.SomeFilesKept(...))
 7. release and dispose the mutex
 8. restart: ShellLauncher.RestartSelf(restart.Value)   [06: or UpdateCoordinator.RestartToUpdate]
 9. Shutdown()
```

Step 6a runs after the log is closed so `envoy.log` can be deleted. A user-started, `MainWindow`-originated message is
the only UI it can show (contract §2.2 allows `DialogService` confirmations of this kind).

**IShell implementations:**

- `Restart(bool safeMode)` → `ExitApp(safeMode ? RestartFlags.SafeMode : RestartFlags.None)`.
- `ResetSettingsAndRestart()` (SHOULD) → `Confirm(...)`; `Host.ResetSettings()` (suppresses first); `ExitApp(None)`.
- `DeleteAllDataAndRestart()` → `Confirm(DataText.DeleteAllTitle, DataText.DeleteAllMessage(DataInventory.Enumerate(Host.DataFolder)))`;
  `Host.SuppressSave()`; `StartupRegistration.Apply(false)` (removes the Run value); log Info;
  `ExitApp(RestartFlags.None, wipeData: true)`.
- `RegisterHotkeys()` (01 writes `HotkeyResults`): adds one Warning per `Taken`/`Invalid` entry.

**`OpenTroubleshooting`** = `ShowMain()`; `_main.Navigate(PageIds.About)`;
`((AboutPage)_main.PageFor(PageIds.About)).TroubleshootingFrame.BringIntoView()`.

**`Services/GlobalExceptionHandlers.cs`** wires the four events to `UnhandledErrorPolicy` and applies the outcome:
`e.Handled = true` and `ShowProblem(problem)` for `ShowProblem`; `DialogService.ShowWarning` then `Shutdown(1)` for
`ExitCrashLoop`; `SetObserved()` after `OnUnobserved`. It calls
`Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)` and exposes
`IReadOnlyList<UnhandledSource> InstalledSources` for the smoke test. The old `OnUnhandledException` and `error.log`
writing are deleted. `#if DEBUG` triggers (About: Ctrl+Alt+Shift+F12; tray "Throw test error"; "Crash (background
thread)") exist only for the manual steps.

**`Services/LogBootstrap.cs`**: `Create(string? dataFolder, TimeProvider? clock = null)` returns the hub, the memory
sink and the file sink (null when `dataFolder` is null), registers the user-profile alias (SHOULD).
`WriteHeader(hub, safeMode, load)`.

### App: toasts

**`Windows/ToastWindow.xaml(.cs)`**: `WindowStyle=None`, `AllowsTransparency=True`, `Topmost=True`,
`ShowInTaskbar=False`, `ShowActivated=False`, `ResizeMode=NoResize`, `SizeToContent=Height`, `Width=360`. A `Border`
with `Bg.Panel`, `BorderBrush` `Danger` (Error) or `Accent` (Warning), `Text.Primary`/`Text.Secondary` text, an icon in
`Font.Icons`. Buttons: `Button.Ghost` "Details" and `Button.Chrome` "×" with `AutomationProperties.Name="Close"`. Only
existing palette keys. On `SourceInitialized`: `Native.MakeNoActivate(hwnd)` (sets `WS_EX_NOACTIVATE |
WS_EX_TOOLWINDOW` via the existing `GetWindowLongPtrW`/`SetWindowLongPtrW`, not `WS_EX_TRANSPARENT`) then
`Native.KeepOnTop`. API: `ToastWindow(ProblemReport report)`, `event Action? DetailsRequested`,
`Place(Rect workAreaDip, int stackIndex)`. Timer: 10 s (Warning: 6 s); `MouseEnter` stops it, `MouseLeave` restarts.

**`Services/ToastService.cs`**:

```csharp
internal sealed class ToastService(Dispatcher ui, NotificationThrottle throttle, Func<bool> enabled,
                                   Func<IntPtr> gameWindow, Action openTroubleshooting, ILog log)
{
    public void Show(ProblemReport report);   // any thread → BeginInvoke; enabled() && throttle.TryShowToast(report.Key)
    public void CloseAll();
}
```

Placement: `MonitorFromWindow(gameWindow(), MONITOR_DEFAULTTOPRIMARY)` → `GetMonitorInfoW` work area →
`GetDpiForMonitor` → DIPs; bottom-right, stacking upward with 8 DIP gaps; a 4th toast closes the oldest. In-game policy
(contract §2.2): problems toast immediately whether or not the game is in front; the toast never takes focus.
Exclusive fullscreen hides it, as it hides the overlay; the problem is still logged.

| Source | Severity | Key |
| --- | --- | --- |
| Unhandled UI or WinForms exception | Error | exception type + target site |
| Command error (`MainViewModel.ReportCommandError`) | Error | `command:` + exception type |
| `ComposeWindow` send failure | Warning | `compose.send` |
| Settings save failed 3× (SHOULD) | Error | `settings.save` |
| Capture watcher entered `Error` | Warning | `capture:error` |
| Previous crash / settings recovered or reset (SHOULD) | Warning | `startup.crash` / `startup.settings` |
| Export failed (SHOULD) / clipboard busy | Warning | `export` / `clipboard` |

Translation failures are not toasted; the overlay shows them.

### App: diagnostics collector (`Services/DiagnosticsCollector.cs`)

`Collect(AppHost host)` → `DiagnosticsSnapshot`:

| Data | Source |
| --- | --- |
| Version | `AppHost.CurrentVersion`, `AssemblyInformationalVersionAttribute` |
| Runtime, OS, arch, culture | `RuntimeInformation`, `CultureInfo` |
| Monitors | `Forms.Screen.AllScreens`; scale = `GetDpiForMonitor(MonitorFromPoint(center))/96` |
| Game / capture | `host.Game?.Profile.Name`, `host.CaptureStatus`, `Watcher.CurrentRegion` → `W×H` |
| OCR | `OcrService.InstalledLanguages()` (try; empty on failure), `Settings.Capture.OcrLanguages` |
| Providers | `Primary` + `Fallbacks`, `SettingsRedaction.HasSecret` via `ProviderCatalog`, `host.ProviderStatus` |
| Hotkeys | `host.HotkeyResults.Where(r => r.Status != Registered)` |
| Other | `host.SafeMode`, `host.SettingsLoad`, `CrashMarker.TryRead(host.CrashMarkerPath)`, uptime, `host.Log.DroppedWrites` |

`ExportBundle(host, zipPath)` (SHOULD) uses the file sink's `ExistingFiles()`.

### UI (shared files after 01)

- **`Windows/MainWindow.xaml`**: `x:Name="SafeModeBanner"` `Border` above the page hosts (War Room slot 1, contract
  §8.6), bound to `IsSafeMode`; `Bg.PanelAlt`, `Danger` border; `SafeModeText.Banner` plus `SafeModeReasonText`;
  button "Restart normally" → `RestartNormallyCommand`.
- **`Pages/AboutPage.xaml`**: `OrnateFrame x:Name="TroubleshootingFrame"` with the three checkboxes (bound in the page,
  auto-applied by 01's `SettingsAutoApplier`); the "include chat text" caption ("Off by default. Only turn this on if a
  developer asks; the log will then contain your chat."); buttons Copy diagnostics, Export diagnostics… (SHOULD), Open
  log folder, Restart in safe mode / Restart normally, Reset settings… (SHOULD), Delete all Envoy data…; the privacy
  caption from `DiagnosticsText`; a status `TextBlock` bound to `DiagnosticsStatus`; (COULD) Recent problems. Every
  control has `AutomationProperties.Name`. No `Click=` handlers.
- **`ViewModels/MainViewModel.Diagnostics.cs`** (new partial): `IsSafeMode`, `SafeModeReasonText`,
  `DiagnosticsStatus`; `CopyDiagnosticsCommand` (`_shell.CopyText(Host.BuildDiagnosticsText(DiagnosticsCollector.Collect(Host)))`,
  status "Copied. Paste it into your bug report."), `ExportDiagnosticsCommand` (SHOULD), `RestartSafeModeCommand`,
  `RestartNormallyCommand`, `ResetSettingsCommand` (SHOULD), `DeleteAllDataCommand`; (COULD) `RecentProblems`,
  `LastCrashText`, `DismissCrashCommand`. `ReportCommandError` (01's shared handler, today `Trace.TraceError`) now logs
  Error under `UI` and calls `Host.ReportProblem(ProblemText.CommandFailed)`. No `App.Current` use.
- **Hotkeys page**: `SkippedSafeMode` entries render as `SafeModeText.HotkeysOff`.
- **`Windows/ComposeWindow.xaml.cs`**: the `catch` around `SendToWindowAsync` replaces 01's interim
  `DialogService.ShowInfo(null, …)` with a Warning log and `Host.ReportProblem(...)` (text per REQ-REL-19). The
  clipboard copy stays.

### Native and network allowlists

Added to `Interop/Native.cs` **and** `tools/ci/policy.json` → `native.allowed` in the same change (contract §5):

| Entry | Use |
| --- | --- |
| `user32!GetAsyncKeyState` | `IsShiftDown()` (`VK_SHIFT = 0x10`, high bit) |
| `user32!MonitorFromPoint` | monitor scale in diagnostics |
| `user32!MonitorFromWindow` | toast placement |
| `user32!GetMonitorInfoW` | toast work area |
| `shcore!GetDpiForMonitor` | toast placement and diagnostics |

Structs `POINT` and `MONITORINFO` (reused by 05). `MakeNoActivate` reuses `GetWindowLongPtrW`/`SetWindowLongPtrW`.
**Network:** none. No new `HttpClient` use, no new host literal.

### Threading

| Component | Thread(s) | Synchronisation |
| --- | --- | --- |
| `LogHub.Write` | any | one lock; `MinimumLevel`/`IncludeSensitive` volatile; secrets array `Volatile.Write` |
| `NotificationThrottle` | any | lock |
| `ToastService.Show`, `AppHost.ReportProblem` | any | `Dispatcher.BeginInvoke` |
| AppDomain handler | dying thread | synchronous log + marker; never touches the Dispatcher |
| `SettingsStore`, `ApplyDiagnosticsSettings` | UI thread only | — |

### Failure behaviour

| Situation | Behaviour |
| --- | --- |
| Log folder unwritable or disk full | Envoy keeps running; `DroppedWrites` grows and is in the report; retry after 30 s. |
| Exception inside a handler or toast | Logged at Critical; no recursion, no UI. |
| Clipboard locked | 3 tries, then `ClipboardBusy` toast. |
| `settings.json` and `.bak` both corrupt | Defaults; `.broken` kept; startup toast (SHOULD). |
| A file is locked during Delete all | Other files are deleted; one message names the kept files; restart continues. |
| Shift held while Envoy already runs | Ignored; the running instance is shown. |

---

## Acceptance criteria

Every MUST has at least one criterion of a contract §9 kind (Core unit test, typecheck, named smoke section, CI check, or
a numbered manual step for `docs/releasing.md`).

| AC | REQ | Criterion | Verified by |
| --- | --- | --- | --- |
| AC-REL-1 | 1 | `LogHub` delivers entries at or above `MinimumLevel` to every sink, drops lower ones, and loses none under 8 concurrent writers. | unit `LoggingTests.Hub_filters_below_minimum_level`, `.Concurrent_writes_all_delivered` |
| AC-REL-2 | 2 | With `maxBytes=1024, maxFiles=3`, 3× the cap leaves exactly `envoy.log`, `envoy.1.log`, `envoy.2.log`, each ≤ cap + one line, newest in `envoy.log`; a `chat-*.txt` beside them is untouched. | unit `RollingFileSinkTests.Rolls_over_and_keeps_max_files_without_touching_other_files` |
| AC-REL-3 | 3 | A directory squatting on `envoy.log` → `Write` does not throw, `DroppedWrites > 0`; a throwing sink does not affect others. | unit `RollingFileSinkTests.Unwritable_target_never_throws`, `LoggingTests.Throwing_sink_is_isolated` |
| AC-REL-4 | 4 | A registered secret, `sk-ant-…`, `AKIA…`, `…:fx`, URL query and user-info in message, field or exception all appear as `[redacted]`; a plain GUID survives; changing settings after `SetSecrets` does not change the registered set. | unit theory `LoggingTests.Redacts_secrets_everywhere`, `.Secrets_are_a_snapshot` |
| AC-REL-5 | 5 | `LogField.Sensitive("text","你好世界")` renders `text=<4 chars>`, and the quoted, escaped value with `IncludeSensitive`. | unit `LoggingTests.Sensitive_fields_masked_unless_opted_in` |
| AC-REL-6 | 11 | `OnUiException` logs Error and returns `ShowProblem` with `ProblemText.Unexpected` (no exception message in Title/Detail); returns `LeaveUnhandled` when `isSmokeTest()`; a re-entrant call logs Critical and returns `LeaveUnhandled`. Same result for `Dispatcher` and `WinForms` sources. | unit `UnhandledErrorPolicyTests.Ui_error_logged_and_reported`, `.Smoke_leaves_ui_error_unhandled`, `.Reentrant_call_does_not_recurse` |
| AC-REL-7 | 11, 12, 13 | Smoke `handlers` section: `InstalledSources` contains all four sources. | smoke `[smoke] handlers ok` |
| AC-REL-8 | 12 | `OnUnobserved` writes a Warning containing `Unobserved task exception` and the inner exception. | unit `UnhandledErrorPolicyTests.Unobserved_is_logged` |
| AC-REL-9 | 13 | `OnDomainException` logs Critical, writes a marker that `TryRead` returns with a redacted message, and flushes the sinks; `TryRead` returns null for missing or corrupt files. | unit `UnhandledErrorPolicyTests.Domain_crash_writes_marker_and_flushes`, `DiagnosticsTests.Crash_marker_round_trips_and_redacts` |
| AC-REL-10 | 16 | `Task.FromException(...).Forget(log,"x")` logs Error with the exception; a cancelled task logs Debug; neither throws. No `_ = …Async(` discard in `src/Envoy.App`. | unit `LoggingTests.Forget_logs_faults_and_cancellation`; typecheck `check_ui_rules.py` R9 |
| AC-REL-11 | 18 | Throttle: same key refused within 2 min, allowed after; balloons refused < 60 s apart and after 5 per session (FakeTimeProvider). | unit `NotificationThrottleTests.Toast_same_key_window`, `.Balloon_spacing_and_session_cap` |
| AC-REL-12 | 18 | Per palette, a `ToastWindow` is shown while `MainWindow` is active; its extended style has `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW`; the foreground window is still `MainWindow`; Details navigates `MainWindow` to About; four `ToastService.Show` calls leave 3 visible. Every palette key in `ToastWindow.xaml` exists in every palette. | smoke `[smoke] toast({id}) ok`; typecheck `check_resources.py` |
| AC-REL-13 | 19 | `MessageBox.Show` occurs in `src/Envoy.App` only in `Services/DialogService.cs`; `ComposeWindow.xaml.cs` calls no `DialogService` member; `App.xaml.cs` calls `DialogService.ShowWarning` at most once (the crash-loop path) and `ShowInfo` only from user-started actions (01's `IShell.ShowInfo`, the Delete-all result in `ExitApp` step 6a). | typecheck: 01's `check_ui_rules.py` R5, plus R10 (below) |
| AC-REL-14 | 19 | Every `ProblemText` member has a non-empty title and detail, no `{0}`-style or concatenated fragments, and none starts with "Error". | unit `ProblemTextTests.Texts_follow_voice_rules` |
| AC-REL-15 | 23 | `DiagnosticsReport.Render` includes every section header, `key: set` for a configured provider, region size without position, and no secret, player name, muted name or region X/Y; given 5,000 log lines it stays ≤ `maxChars` with all headers. | unit `DiagnosticsTests.Report_has_all_sections_and_no_private_values`, `.Report_is_capped_by_dropping_old_log_lines` |
| AC-REL-16 | 23 | Reflection fills every `[Secret]`/`[Personal]`/`[Location]` property with distinctive values; none appears in `ToRedactedJson`; `[Location]` regions render as `W×H`. | unit `SettingsRedactionTests.Every_marked_property_is_masked`, `.Location_becomes_size` |
| AC-REL-17 | 23 | Smoke `diagnostics` section builds the report from the smoke host and finds `## Envoy diagnostics`. | smoke `[smoke] diagnostics ok` |
| AC-REL-18 | 27 | Per palette, `PageFor(PageIds.About)` contains a visible `TroubleshootingFrame` whose buttons and checkboxes all have `AutomationProperties.Name`; `TroubleshootingFrame.BringIntoView()` from `OpenTroubleshooting` compiles against `AboutPage`. | smoke (01 page loop + `toast` section's Details check); typecheck |
| AC-REL-19 | 29 | `Save` writes `"version": 1` even when the in-memory `Version` was changed. | unit `SettingsStoreDurabilityTests.Save_always_writes_current_version` |
| AC-REL-20 | 33, 34 | After one good load, corrupting `settings.json` returns `RecoveredFromBackup` with earlier values and a `.broken` copy; without `.bak` it returns `ResetCorrupt`; a successful load refreshes `.bak` with the loaded text; an unreadable file is treated as corrupt. Existing `SettingsStoreTests` pass unchanged. | unit `SettingsStoreDurabilityTests.Corrupt_file_recovers_from_last_known_good`, `.Corrupt_without_backup_resets`, `.Successful_load_refreshes_last_known_good`, `.Load_status_is_reported`; existing tests |
| AC-REL-21 | 39 | `Decide` follows the policy (flag > Shift > threshold > null; threshold 0 disables). | unit theory `SafeModeTests.Safe_mode_decision` |
| AC-REL-22 | 40 | `StartupPlan.For` in safe mode: no overlay, no hotkeys, no auto capture, `ShowMainWindow` even when minimized; otherwise today's rule. | unit theory `SafeModeTests.Startup_plan` |
| AC-REL-23 | 40 | Smoke `safe-host` creates a safe host on its own temp `DataFolder`, calls `Start()` and asserts the watcher is not running; per palette, `safe-banner` opens a second `MainWindow` on it and asserts `SafeModeBanner` is visible, then closes it. | smoke `[smoke] safe-host ok`, `[smoke] safe-banner({id}) ok` |
| AC-REL-24 | 39, 40 | `Envoy.exe --safe-mode`: banner on every page, no overlay, hotkeys inert, tray tooltip "Envoy (safe mode)"; Restart normally brings them back. Holding Shift while launching does the same. | manual M1, M2 |
| AC-REL-25 | 43 | The CI smoke log contains this area's `[smoke] … ok` lines (three setup, two per palette, `log-tail`) and a `--- envoy.log tail ---` block whose first line is the session header without `Environment.UserName`/`MachineName`; every host's `DataFolder` starts with the smoke temp root, and the root no longer exists at exit. | Windows CI smoke step |
| AC-REL-26 | 44 | `Enumerate` lists exactly the inventory files in a temp folder seeded with every item plus `notes.txt` and `logs/mine.txt`; `DeleteAll` removes only inventory files, keeps the other two, reports a locked file under `Failed`; every `DataInventory.Items` pattern appears in `docs/privacy.md`. | unit `DataInventoryTests.Enumerate_lists_only_inventory`, `.DeleteAll_deletes_only_inventory`, `.Locked_file_is_reported`, `.Every_item_is_documented_in_privacy_md` |
| AC-REL-27 | 44 | After `SuppressSaves()` (and after `ResetToDefaults()`), `Save` leaves the file unchanged, so a disposing host cannot undo a reset or re-create a deleted file. | unit `SettingsStoreDurabilityTests.Suppressed_store_never_writes`, `.Reset_then_save_leaves_defaults` |
| AC-REL-28 | 44 | `SpecialFolder.ApplicationData` and `APPDATA` occur in `src/` only in `SettingsStore.cs`; `AppHost` has no static `DataFolder`/`LogsFolder`/`UserPhrasesPath`. | typecheck `check_ui_rules.py` R8; typecheck build |
| AC-REL-29 | 44 | Delete all Envoy data on a real install: confirmation lists the files; after restart Envoy is at first run, `%APPDATA%\Envoy` holds only the new session's files, and the Run value is gone. | manual M3 |
| AC-REL-30 | all | `dotnet test tests/Envoy.Core.Tests`, `tools/typecheck/typecheck.sh` and `python3 tools/ci/check_policy.py` pass (the five native entries are allowlisted). | CI `check` job |

SHOULD/COULD criteria (not gating): repeat collapsing (`LoggingTests.Repeats_are_collapsed`), profile alias
(`.User_profile_path_is_aliased`), migrator (`SettingsMigrationTests.*`), null repair, undecryptable secret by name,
crash loop guard (`DiagnosticsTests.Crash_loop_guard_trips_only_inside_window`), sentinel
(`SafeModeTests.Startup_sentinel_counts_unfinished_starts`), bundle entries
(`DiagnosticsTests.Bundle_contains_only_expected_entries`), and manual M4–M9.

---

## Test plan

### Unit tests (`tests/Envoy.Core.Tests`, xUnit, `FakeTimeProvider` from the referenced `Microsoft.Extensions.TimeProvider.Testing`)

| File | Tests |
| --- | --- |
| `LoggingTests.cs` | `Hub_filters_below_minimum_level`, `Concurrent_writes_all_delivered`, `Throwing_sink_is_isolated`, `Redacts_secrets_everywhere` (theory: secret, `sk-ant-`, `AKIA`, `uuid:fx`, `https://u:p@h/x?q=hello&key=abc`; message, field, exception; GUID kept), `Short_values_are_not_registered_as_secrets`, `Secrets_are_a_snapshot`, `Sensitive_fields_masked_unless_opted_in`, `Formatter_writes_one_line_per_entry_plus_indented_exception`, `Forget_logs_faults_and_cancellation`, `Memory_sink_is_a_ring`; SHOULD: `User_profile_path_is_aliased`, `Repeats_are_collapsed` |
| `RollingFileSinkTests.cs` | `Rolls_over_and_keeps_max_files_without_touching_other_files`, `Appends_across_instances`, `Oversized_existing_file_rolls_on_first_write`, `Unwritable_target_never_throws`, `ExistingFiles_lists_newest_first` |
| `UnhandledErrorPolicyTests.cs` | `Ui_error_logged_and_reported`, `Smoke_leaves_ui_error_unhandled`, `Reentrant_call_does_not_recurse`, `Unobserved_is_logged`, `Domain_crash_writes_marker_and_flushes`; SHOULD: `Crash_loop_exits_after_threshold` |
| `NotificationThrottleTests.cs` | `Toast_same_key_window`, `Different_keys_are_independent`, `Balloon_spacing_and_session_cap` |
| `DiagnosticsTests.cs` | `Crash_marker_round_trips_and_redacts` (incl. corrupt file), `Report_has_all_sections_and_no_private_values` (Claude key `sk-ant-TESTSECRET123`, DeepL key, player `ZZPLAYERZZ`, muted `MUTEDZZ`, region at X=1234,Y=987), `Report_is_capped_by_dropping_old_log_lines`, `ProblemTextTests.Texts_follow_voice_rules`; SHOULD: `Crash_loop_guard_trips_only_inside_window`, `Bundle_contains_only_expected_entries` |
| `SafeModeTests.cs` | `Safe_mode_decision` (theory), `Startup_plan` (theory); SHOULD: `Startup_sentinel_counts_unfinished_starts`, `Sentinel_reports_unclean_end` |
| `DataInventoryTests.cs` | `Enumerate_lists_only_inventory`, `DeleteAll_deletes_only_inventory`, `Locked_file_is_reported` (file opened without share), `Every_item_is_documented_in_privacy_md` (finds the repo root by walking up to `Envoy.sln`) |
| `SettingsStoreDurabilityTests.cs` | `Save_always_writes_current_version`, `Corrupt_file_recovers_from_last_known_good`, `Corrupt_without_backup_resets`, `Successful_load_refreshes_last_known_good`, `Unreadable_file_is_treated_as_corrupt`, `Load_status_is_reported`, `Suppressed_store_never_writes`; SHOULD: `Reset_then_save_leaves_defaults`, `Null_sections_are_repaired`, `Undecryptable_secret_is_logged_by_name` |
| `SettingsMigrationTests.cs` (SHOULD) | `Missing_version_means_v1`, `Runs_steps_in_order`, `Invalid_chain_throws`, `Shipped_registry_is_consistent`, `Backs_up_before_migrating`, `Newer_version_loads_best_effort_with_backup`, `Migration_failure_returns_MigrationFailed_and_keeps_backup` |
| `SettingsRedactionTests.cs` | `Every_marked_property_is_masked`, `Location_becomes_size`, `SecretValues_returns_only_non_empty_secrets`, `HasSecret_reports_set_and_unset` |

Existing `SettingsStoreTests.Round_trips_settings_and_encrypts_secrets_on_disk` and `Corrupt_file_falls_back_to_defaults`
pass unchanged.

### Smoke test (`src/Envoy.App/SmokeTest.cs`, placed per contract §6)

`Run(App app, string[] args, LogHub log, MemoryLogSink recent)`.

- **Setup (once):** 01 creates `%TEMP%\envoy-smoke-{guid}`; the main host moves to `{root}\data\settings.json` with
  `AppHostOptions { DataFolder = {root}\data, Log = log, RecentLog = recent }`. Before the host, add a `RollingFileSink`
  on `{root}\data\logs` to `log`; after it, write the header. 01's `[smoke] WARNING` lines are also logged at Warning
  under category `Smoke`.
  - `safe-host`: a second host with `SafeMode = CommandLine` and its own `{root}\safe-data`; `Start()`; assert the
    watcher is not running.
  - `diagnostics`: `Host.BuildDiagnosticsText(DiagnosticsCollector.Collect(host))` contains `## Envoy diagnostics`;
    with REQ-REL-24 built, `ExportBundle` to `{root}\diag.zip` and assert `report.txt` is in it.
  - `handlers`: `InstalledSources` has `Dispatcher`, `WinForms`, `TaskScheduler`, `AppDomain`.
- **Per palette, `MainWindow`-dependent checks (step 3, after 01's own checks):**
  - `safe-banner({id})`: `new MainWindow(safeHost)`, show, `SafeModeBanner` visible, close.
  - `toast({id})`: with `main` active, show a `ToastWindow` for `ProblemReport("smoke", "Smoke test", "Sample problem for the
    screenshot.")`; assert the extended style and that `GetForegroundWindow()` is still `main`; with `--screenshots`
    save `{palette}-toast.png` (with `Backdrop()`); raise `DetailsRequested` and assert `main` shows About; then four
    `ToastService.Show` calls with distinct keys leave 3 open; `CloseAll()`.
- **Teardown:** dispose both hosts; remove and dispose the file sink, capturing `envoy.log`'s text; delete the temp
  root; `log-tail`: print `--- envoy.log tail ---` and the last 50 captured lines, and assert line 1 is the header
  without `Environment.UserName`/`MachineName`.

Expected cost: under 3 s total plus about 0.3 s per palette.

### Static checks (rules added to 01's `tools/typecheck/check_ui_rules.py`, with cases in its `test_*.py`)

| Rule | Check | Backs |
| --- | --- | --- |
| R8 | `SpecialFolder.ApplicationData` or `APPDATA` in `src/**/*.cs` only in `src/Envoy.Core/Settings/SettingsStore.cs` | REQ-REL-44 |
| R9 | no `_ = <expr>Async(` discard in `src/Envoy.App/**/*.cs` (use `.Forget(log, what)`) | REQ-REL-16 |
| R10 | `src/Envoy.App/Windows/ComposeWindow.xaml.cs` contains no `DialogService.`; `App.xaml.cs` contains `DialogService.ShowWarning` at most once (crash loop) | REQ-REL-19 |

01's R5 already limits `MessageBox.Show` to `DialogService.cs`. `check_resources.py` covers `ToastWindow.xaml` palette
keys. 08a's `check_policy.py` covers the native additions.

### Manual steps (for the `docs/releasing.md` pass, 08b)

- **M1** `Envoy.exe --safe-mode`: banner on every page, no overlay, `Ctrl+Shift+T` inert, tooltip "Envoy (safe mode)".
  Restart normally brings back overlay and hotkeys.
- **M2** Hold Shift while launching from the Start menu (keep holding until the window appears): safe mode.
- **M3** Delete all Envoy data (AC-REL-29).
- **M4** (DEBUG) In a borderless match, Ctrl+Alt+Shift+F12 on About, then return to the game: a toast appears on the
  game's monitor, the game keeps focus (WASD and hotkeys work), it closes after 10 s; Details opens Troubleshooting.
- **M5** (DEBUG) Tray "Throw test error": toast, no WinForms dialog.
- **M6** Configure a DeepL key and a player name; Copy diagnostics into Notepad: sections present, `DeepL: key set`,
  no key, no name, region as size only.
- **M7** Corrupt `settings.json` (delete a brace): previous settings and decrypted key restored from `.bak`;
  `settings.json.broken` exists.
- **M8** (DEBUG) "Crash (background thread)": `last-crash.json` exists; the next start shows the crash toast (SHOULD).
- **M9** Set `logs\` read-only for a minute of use: no errors; the report shows `Dropped log writes: N`.

---

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Diagnostics/Logging.cs`, `LogRedactor.cs`, `LogFormatter.cs`, `RollingFileSink.cs`, `MemoryLogSink.cs`, `TaskExtensions.cs`, `RepeatSuppressor.cs` (SHOULD) | new | |
| `src/Envoy.Core/Diagnostics/UnhandledErrorPolicy.cs` (+ `CrashLoopGuard`, `CrashMarker`) | new | |
| `src/Envoy.Core/Diagnostics/SafeMode.cs` (`SafeModeReason`, `SafeModeDecision`, `StartupSentinel`, `StartupPlan`) | new | |
| `src/Envoy.Core/Diagnostics/DiagnosticsReport.cs` (+ `DiagnosticsBundle` SHOULD), `DataInventory.cs` | new | |
| `src/Envoy.Core/Diagnostics/ProblemText.cs`, `SafeModeText.cs`, `DiagnosticsText.cs`, `DataText.cs` | new | |
| `src/Envoy.Core/Notifications/NotificationThrottle.cs` (+ `NotificationClass`, `ProblemReport`) | new | |
| `src/Envoy.Core/Settings/AppSettings.cs` | `CurrentVersion`, `[Personal]`, `[Location]`, `Diagnostics` | **SHARED** |
| `src/Envoy.Core/Hotkeys/HotkeyStatus.cs`, `HotkeyText.cs` | append `SkippedSafeMode`; its display text is `SafeModeText.HotkeysOff` | shared with 01 |
| `src/Envoy.Core/Settings/SettingsStore.cs` | `LoadWithReport`, `.bak`/`.broken` recovery, `SuppressSaves`, `ResetToDefaults` (SHOULD), logging | |
| `src/Envoy.Core/Settings/SettingsRedaction.cs`; `SettingsMigration.cs`, `SettingsSanitizer.cs` (SHOULD) | new | |
| `src/Envoy.App/AppHost.cs` | `AppHostOptions`, instance paths (statics deleted), `Log`/`Notifications`/`SafeMode`/`SettingsLoad`/`SaveSuppressed`, `ReportProblem`, ctor and `ApplySettings`/`RefreshGame` position 1, logging in every catch, `Forget` | **SHARED** |
| `src/Envoy.App/App.xaml.cs` | §4 skeleton with slots, 02 `IShell` block, `ExitApp(restart, wipeData)`, tray safe-mode text, hotkey Warnings; delete `OnUnhandledException`/`error.log` | **SHARED** |
| `src/Envoy.App/IShell.cs` | 02 block | **SHARED** |
| `src/Envoy.App/Services/DialogService.cs` | `Confirm`, `ChooseSavePath` if missing | shared with 01 |
| `src/Envoy.App/Security/ShellLauncher.cs` | `public static ILog Log { get; set; } = NullLog.Instance;` and a Warning per rejected target | 08a file (CODEOWNERS) |
| `src/Envoy.App/Services/LogBootstrap.cs`, `GlobalExceptionHandlers.cs`, `ToastService.cs`, `DiagnosticsCollector.cs` | new | |
| `src/Envoy.App/Windows/ToastWindow.xaml(.cs)` | new | |
| `src/Envoy.App/Windows/MainWindow.xaml` | `SafeModeBanner` above page hosts | **SHARED** |
| `src/Envoy.App/Pages/AboutPage.xaml`, `HotkeysPage.xaml` | Troubleshooting frame; safe-mode hotkey text | **SHARED** |
| `src/Envoy.App/ViewModels/MainViewModel.Diagnostics.cs` | new partial | |
| `src/Envoy.App/ViewModels/MainViewModel*.cs` | instance `DataFolder`/`LogsFolder`/`UserPhrasesPath`; command-error handler logs + toasts | **SHARED** |
| `src/Envoy.App/Windows/ComposeWindow.xaml.cs` | `MessageBox` → log + `ReportProblem` | shared with 07c |
| `src/Envoy.App/Interop/Native.cs` | 5 imports, `POINT`, `MONITORINFO`, `IsShiftDown`, `MakeNoActivate` | **SHARED** |
| `tools/ci/policy.json` | 5 `native.allowed` entries | 08a file |
| `src/Envoy.App/SmokeTest.cs` | `Run` signature, temp `DataFolder`, six sections | **SHARED** |
| `tools/typecheck/check_ui_rules.py` + its `test_*.py` | rules R8–R10 | shared with 01 |
| `tests/Envoy.Core.Tests/*` | the new test files above | |
| `docs/privacy.md` | new: "What Envoy stores on your PC" (07a adds the rest) | shared with 07a |
| `docs/troubleshooting.md` | Copy diagnostics replaces `error.log`; Safe mode; settings recovered; Delete all data | |
| `docs/architecture.md` | `Diagnostics/` and `Notifications/` rows; "Fail soft" rule (log + toast); `ToastWindow`; start-up sequence | |
| `CLAUDE.md` | use `ILog`; chat/names only via `LogField.Sensitive`; every `catch` in `AppHost.cs` logs; new name/chat/position settings carry `[Personal]`/`[Location]`; Dispose-time writers check `SaveSuppressed`; new data files join `DataInventory` and `docs/privacy.md` | |
| `CHANGELOG.md` | Unreleased → Added: diagnostics, safe mode, settings recovery, Delete all data. Changed: errors no longer open dialogs | |

`.github/workflows/build.yml` is not changed; its `error.log` print becomes dead and 08b removes it.

**Estimated size:** Core ≈ 900 lines, App ≈ 550 (including XAML), tests ≈ 700. MUST-only subset ≈ 1,600.

---

## Risks & open questions

1. **Toasts over the game.** `WS_EX_NOACTIVATE` toasts do not take focus in borderless mode; exclusive fullscreen hides
   them, as it hides the overlay. A brief frame-pacing hitch on some machines is possible; the opt-out exists.
2. **Redaction completeness.** Pattern matching cannot catch every secret; registering real values is the primary
   defence. 03 must keep request text out of exception messages (its REQ-TRR-17).
3. **Logs folder shared with transcripts.** The strict `envoy(.N).log` name rule keeps the sink away from transcripts.
4. **Shift detection.** Users may release Shift before `OnStartup` runs (single-file start can take 300–800 ms). Shift is
   read first thing and the docs say "hold Shift until the window appears"; `--safe-mode` is the dependable path.
5. **Delete all while files are open.** A log open in another program can survive; the user is told which files were
   kept. Settings are never re-created because the store is suppressed.
6. **`.bak` contains DPAPI-encrypted secrets**, as `settings.json` does. It is never bundled; Delete all removes it.
7. **Open:** should the report include region position or the game window rectangle for DPI bugs? This spec says no
   (size and monitor layout only).

---

## Revision notes

Changes from the pre-review draft, and why:

- **Rewritten against 01** (critique blocker). Troubleshooting moved to `Pages/AboutPage.xaml` with commands in
  `MainViewModel.Diagnostics.cs`; no `Click=` handlers or `App.Current`; the banner stays in `MainWindow.xaml` above the
  pages; `ScrollToTroubleshooting` goes through `PageFor(PageIds.About)`. Depends on 01 and 08a.
- **`IShell` 02 block** added exactly as contract §3: `Confirm`, `Restart`, `ResetSettingsAndRestart`,
  `DeleteAllDataAndRestart`, `ChooseExportPath`, `CopyText`, `RevealFile`.
- **Process launches** go through 08a's `ShellLauncher.RestartSelf` and `RevealFile` (critique blocker); no
  `Process.Start`. `ShellLauncher` now logs rejected targets.
- **`AppHostOptions` and instance paths** (critique major). The record has `Log`, `RecentLog`, `SafeMode`, `DataFolder`,
  `TimeProvider`; `UpdateBackend` is left to 06 (arrival rule). The static `DataFolder`/`LogsFolder`/`UserPhrasesPath`
  are deleted; smoke hosts get temp folders that are deleted afterwards. Rule R8 in `check_ui_rules.py` limits `ApplicationData` to
  `SettingsStore.cs`.
- **Start-up and exit order** (critique major): the contract §4 skeleton is written here with slots for 05, 06, 07a,
  07b and 08b. Shift is read first, the sentinel starts after 06's update slot, and `MarkHealthy` waits 30 s or the first
  render. Step 6a (data wipe after the log closes) is a clarification inside that skeleton.
- **Reset undone by exit save** (critique major): suppression now lives in `SettingsStore.SuppressSaves()`, so the
  behaviour has Core tests; `ExitApp` and `MainWindow.OnClosed` check `SaveSuppressed`, and a `CLAUDE.md` rule covers
  Dispose-time writers in later areas.
- **Provider health** (critique major): REQ-REL-26, `ProviderHealthTracker` and the `ProviderHealth` record/property are
  removed. The report shows the chain, key flags and today's status line; 03 adds its registry snapshot.
- **Notifications** (critique major): `NotificationThrottle` moved to `Envoy.Core.Notifications`, is a single instance on
  `AppHost.Notifications`, and implements every contract §2.2 rule, including the advisory-balloon spacing and cap that
  07a will use. REQ-REL-20 was folded into REQ-REL-18 so that 07a builds on a MUST. The MUST count does not change.
- **Modal dialogs** (critique major on 01's `ShowInfo`): `MessageBox.Show` is allowed only in `DialogService`, which
  01's R5 checks; this step adds R8–R10 to 01's `check_ui_rules.py` instead of separate greps. 02 also appends
  `HotkeyStatus.SkippedSafeMode`, as revised 01 expects. The crash-loop box goes through `DialogService`. The compose failure uses 08a's
  `SendBlockedException` message. 01's command-error handler now logs and shows a toast.
- **Hotkeys**: `Host.HotkeyFailures` was removed. Diagnostics and logging read 01's `AppHost.HotkeyResults`, and safe
  mode fills it with `SkippedSafeMode`.
- **Privacy** (critique minor): `[Location]` was added for `Capture.Regions`, which is redacted to `W×H`. The
  reflection test covers all three markers. 04 and 05 mark their own properties.
- **Verifiability** (critique minor): the handler bodies are now the Core `UnhandledErrorPolicy`, which has unit tests.
  Smoke S4 checks wiring only and never reports SKIPPED. Safe-mode behaviour is a pure `StartupPlan` with tests. The
  DEBUG triggers back only manual steps for non-MUST checks. The `AppHost` catch-log rule went into `CLAUDE.md`.
- **New concern (contract §8.2):** REQ-REL-44 covers `DataInventory`, Delete all Envoy data, and the instance-path rule.
  This step creates `docs/privacy.md` with the stored-data section so the inventory test can run now. 07a adds the
  data-flow sections. The contract's §1 table and §8.1 were updated in the final consistency pass to match, so this is
  no longer a deviation.
- **UI strings (contract §8.7):** `ProblemText`, `SafeModeText`, `DiagnosticsText` and `DataText` were added, and toasts
  no longer show raw exception text.
- **Smoke structure (contract §6):** the smoke sections were renamed and placed in setup, per-palette or teardown. The
  per-palette checks run while `MainWindow` is open. Criteria refer to palettes, not "5 themes".
- **Allowlists:** the exact native entries are `user32!GetAsyncKeyState`, `MonitorFromPoint`, `MonitorFromWindow`,
  `GetMonitorInfoW` and `shcore!GetDpiForMonitor`, plus the `POINT` and `MONITORINFO` structs. Each is added to
  `policy.json` in the same change. There are no network additions.
- **Scope trimmed to 20 MUSTs (was 30).** Folded: 10 and 25 into 23, 14 into 11, 41 into 40, 20 into 18. Demoted to
  SHOULD: 8, 15, 17, 21, 24, 30, 31 (and 38, already SHOULD). REQ-REL-42 stays COULD with `autoThreshold` 0 by default.
  Removed: 26. Added: 44 (MUST, assigned by the contract). The migrator ships with no steps, as contract §7 requires.
