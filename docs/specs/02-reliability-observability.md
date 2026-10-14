# Reliability & Observability

Status: Proposed

Area: `02-reliability-observability`. This area covers logging, error handling, crash and diagnostics reporting,
settings durability and migration, and safe mode. It does **not** cover `ChatWatcher` internals (capture-engine),
translation retry or circuit breaking (translation-resilience), installers or auto-update (distribution-updates), or
the CI workflow itself (quality-security-ci).

---

## Context

### What exists today

| Concern | Today | Where |
| --- | --- | --- |
| Unhandled UI exceptions | `App.OnUnhandledException` appends `[time] exception` to `%APPDATA%\Envoy\error.log`, then shows a **modal `MessageBox`** and sets `Handled = true`. | `src/Envoy.App/App.xaml.cs` (`OnUnhandledException`) |
| Background exceptions | No handler for `TaskScheduler.UnobservedTaskException` or `AppDomain.CurrentDomain.UnhandledException`. Tray menu items are WinForms `ContextMenuStrip` callbacks, so an exception in one reaches WinForms' `Application.ThreadException` and shows WinForms' own "Unhandled exception" dialog. | `App.xaml.cs` (`CreateTray`) |
| Swallowed failures | `AppHost.SaveNow` catches `IOException` and does nothing. Transcript writes, `ConfigureOcr`, `Say` (speech) and the update check all use empty catches. `ChatWatcher` keeps only `LastError` (a string). `FallbackTranslator.ProviderFailed` only updates the `ProviderStatus` string. `ChatPipeline` stores a failed translation's message on the message. None of this is recorded anywhere that lasts. | `src/Envoy.App/AppHost.cs`, `src/Envoy.App/Services/ChatWatcher.cs`, `src/Envoy.Core/Pipeline/ChatPipeline.cs` |
| Fire-and-forget | `_ = CheckForUpdatesAsync()` in `AppHost.Start`. `async void` handlers in `App` (`SelectRegion`, `ScanAndTranslate`) and `MainWindow` (`OnTestTranslate`). | `AppHost.cs`, `App.xaml.cs`, `Windows/MainWindow.xaml.cs` |
| Modal boxes during play | `ComposeWindow` shows a `MessageBox` when `SendInput` fails. The game has just been given focus at that moment, so the modal steals it back. | `Windows/ComposeWindow.xaml.cs` (`catch` around `KeyboardSender.SendToWindowAsync`) |
| Settings file | `SettingsStore` writes JSON atomically (`.tmp` then `File.Move`) and encrypts `[Secret]` properties with DPAPI. If the file is corrupt it copies it to `settings.json.broken` and returns **defaults**. The next autosave then overwrites the user's file with those defaults, which loses every setting and key. `AppSettings.Version = 1` exists but is never read. There is no migration path. | `src/Envoy.Core/Settings/SettingsStore.cs`, `AppSettings.cs`; test `SettingsStoreTests.Corrupt_file_falls_back_to_defaults` |
| Logs folder | `AppHost.LogsFolder` = `%APPDATA%\Envoy\logs` already holds **chat transcripts** (`chat-yyyy-MM-dd_HHmmss.txt`, `TranscriptWriter`). The About/Appearance page has an "Open logs" button. | `AppHost.cs`, `src/Envoy.Core/Chat/TranscriptWriter.cs`, `Windows/MainWindow.xaml` |
| Bug reports | `docs/troubleshooting.md` asks users to attach `error.log`. That file holds only UI-thread exceptions, with no version, OS, DPI or settings context. | `docs/troubleshooting.md` |
| Recovery | There is no way to start Envoy without the overlay, hotkeys and capture. A bad setting (for example an off-screen overlay, a hotkey that clashes, or a capture region that crashes OCR) can only be fixed by deleting `settings.json`, and that also deletes the API keys. | — |
| CI | The smoke test (`SmokeTest.cs`) builds every window under every theme. CI prints `%APPDATA%\Envoy\error.log` if it exists. | `.github/workflows/build.yml`, `src/Envoy.App/SmokeTest.cs` |

### What is weak or missing for production

1. **No record of what happened.** When a stranger reports that "it stopped translating", we have no log of provider
   failures, capture errors or state changes, and no version or OS information.
2. **Modal dialogs during play.** A `MessageBox` steals focus from a full-screen or borderless game in the middle of a
   match. Because the WinForms tray dialog is not routed, a tray-menu bug shows a scary .NET dialog.
3. **Silent background failures.** Unobserved task exceptions disappear. A crash on a non-UI thread kills the process
   and leaves no trace.
4. **Settings are fragile.** One corrupt write (power loss, antivirus lock, a hand edit) resets everything, including
   DPAPI-encrypted keys. There is no schema versioning, so future areas cannot rename or restructure settings safely.
5. **No self-service recovery.** There is no safe mode and no "reset settings but keep a backup".
6. **Privacy risk once logging exists.** A naive logger would write API keys (in exception text and URLs, because Google
   Free puts the text in the query string), chat lines, player names and the Windows user name (from paths). Envoy
   promises no telemetry and that chat goes only to the chosen provider. Local logs must not become a leak when users
   attach them to public GitHub issues.

---

## Goals / Non-goals

### Goals

- A small, dependency-free logging abstraction in `Envoy.Core`, with a size-capped rolling file sink and an
  in-memory ring buffer. Redaction is applied centrally, so secrets, chat text, player names and the user-profile path
  never reach disk unless the user opts in to chat text.
- Global exception handling for the dispatcher, the task scheduler, the AppDomain and WinForms. Errors appear as
  **non-modal, non-activating, themed toasts**. A crash-loop guard stops runaway error storms.
- A one-click **Copy diagnostics** (text for a GitHub issue) and **Export diagnostics bundle** (zip). Both are local
  only and redacted.
- Durable settings: a last-known-good backup, recovery from corruption, a schema version, and an ordered, tested
  migration framework that other areas can extend.
- **Safe mode** (`--safe-mode` or holding Shift at launch). It skips the overlay, hotkeys and automatic capture so the
  user can fix bad settings.

### Non-goals

- Any telemetry, crash upload, Sentry, Application Insights or other automatic network reporting. Diagnostics leave
  the PC only when the user pastes or attaches them.
- Windows Event Log, ETW or OpenTelemetry sinks.
- A full in-app log viewer. A short "Recent problems" list and an "Open log folder" button are enough.
- Changes inside `ChatWatcher`. This area only observes its existing `StateChanged` / `LastError`.
- Translation retry, back-off and circuit breakers (translation-resilience). This area only records provider
  success and failure for the diagnostics report.
- Performance metrics and dashboards.
- A minidump writer.

---

## Requirements

Prefix `REL`. Priorities: **MUST** (ship-blocking for 1.0), **SHOULD** (expected), **COULD** (if time allows).

### Logging

- **REQ-REL-1 (MUST)** `Envoy.Core` provides `ILog` and `LogHub` with levels Debug, Info, Warning, Error and Critical.
  They are thread-safe and add no new NuGet package.
- **REQ-REL-2 (MUST)** A rolling file sink writes `%APPDATA%\Envoy\logs\envoy.log`. When the file reaches 1 MiB it
  rolls over to `envoy.1.log` … `envoy.4.log`, so at most 5 files exist. It never creates, renames or deletes any
  other file in that folder (transcripts live there).
- **REQ-REL-3 (MUST)** Writing a log entry never throws to the caller, even when the folder is unwritable or the
  disk is full. Failed writes are counted in `LogHub.DroppedWrites`.
- **REQ-REL-4 (MUST)** Before an entry reaches any sink, its message, fields and exception text are redacted:
  - every non-empty `[Secret]` settings value is replaced with `[redacted]`;
  - text matching known key patterns (`sk-ant-…`, `AKIA…`, `…:fx`) is replaced;
  - URL query strings and URL user-info are replaced.
- **REQ-REL-5 (MUST)** Chat text and player names are logged only through `LogField.Sensitive(...)`. They render as
  `<N chars>` unless `Diagnostics.LogChatText` is `true`.
- **REQ-REL-6 (SHOULD)** The current user-profile path (`C:\Users\<name>`) is replaced with `%USERPROFILE%` in every
  entry.
- **REQ-REL-7 (SHOULD)** Entries with the same level, category and message template that repeat within 60 s are
  collapsed into one "(repeated N times)" entry.
- **REQ-REL-8 (MUST)** The minimum level is Info by default and Debug when `Diagnostics.VerboseLogging` is on.
  Changing the setting takes effect without a restart.
- **REQ-REL-9 (SHOULD)** Each session starts with one Info header line. It contains the Envoy version, OS version,
  .NET runtime version, process architecture, safe-mode state and settings-load status. It contains no user or
  machine name.
- **REQ-REL-10 (MUST)** An in-memory ring buffer keeps the last 500 log entries for the diagnostics report and the
  "Recent problems" list.

### Error handling

- **REQ-REL-11 (MUST)** `Application.DispatcherUnhandledException` does the following:
  - logs the exception at Error;
  - sets `Handled = true`;
  - shows a non-modal error toast instead of a `MessageBox`;
  - while the smoke test is running, it leaves the exception unhandled, as today.
- **REQ-REL-12 (MUST)** `TaskScheduler.UnobservedTaskException` logs the exception at Warning and calls
  `SetObserved()`.
- **REQ-REL-13 (MUST)** `AppDomain.CurrentDomain.UnhandledException` logs the exception at Critical, flushes the log
  and writes a redacted `%APPDATA%\Envoy\last-crash.json`.
- **REQ-REL-14 (MUST)** WinForms `Application.ThreadException` (from tray-menu callbacks) goes through the same
  handler as dispatcher exceptions, so the WinForms exception dialog never appears.
- **REQ-REL-15 (MUST)** If 5 or more dispatcher exceptions occur within 30 s, Envoy:
  - logs Critical;
  - writes `last-crash.json`;
  - shows exactly one modal explanation;
  - exits with code 1.
- **REQ-REL-16 (MUST)** Fire-and-forget tasks use a Core `Forget(ILog, string what)` extension that logs faults. The
  existing `_ = CheckForUpdatesAsync()` is converted to it.
- **REQ-REL-17 (MUST)** These previously silent failures are logged at Warning with a category and without chat text:
  - settings save;
  - transcript write;
  - OCR configuration;
  - speech;
  - a provider failure;
  - the capture watcher entering the `Error` state;
  - a message ending as `Failed`;
  - hotkey registration failure.

### Toasts

- **REQ-REL-18 (MUST)** Errors during normal use appear in a `ToastWindow`. The window:
  - is topmost;
  - opens with `ShowActivated=false` and `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, so it does not take focus from the
    game;
  - is themed only through `DynamicResource` palette keys;
  - closes itself after 10 s, with the timer paused while the mouse is over it;
  - has **Details** and **×** buttons.
- **REQ-REL-19 (MUST)** No error that happens on a background thread or while the user is playing opens a modal box.
  This covers `ComposeWindow`'s `SendInput` failure. The only exceptions are the crash-loop modal (REQ-REL-15) and
  confirmations the user starts in `MainWindow`.
- **REQ-REL-20 (SHOULD)** A toast with the same dedupe key is shown at most once every 2 minutes, and at most 3 toasts
  are visible at once (the oldest closes first).
- **REQ-REL-21 (MUST)** When `Diagnostics.ShowErrorToasts` is `false`, no toasts are shown. Problems are still logged
  and listed under Recent problems.
- **REQ-REL-22 (SHOULD)** At startup, Envoy shows one toast if the previous session crashed (`last-crash.json` is
  present) or if the settings were recovered, reset or could not be migrated.

### Diagnostics

- **REQ-REL-23 (MUST)** **Copy diagnostics** puts a plain-text report on the clipboard. The report:
  - contains Envoy and .NET versions, OS, architecture, culture, the monitors (bounds, primary, DPI scale), the
    detected game, capture state and region size, installed and selected OCR languages, the provider chain with
    "key set / not set" and health, hotkey failures, settings schema and load status, safe-mode state, the last crash,
    and the last 50 log lines;
  - is at most 60,000 characters.
- **REQ-REL-24 (MUST)** **Export diagnostics bundle** writes a `.zip` that contains `report.txt`,
  `settings.redacted.json`, `logs/envoy*.log` and, if present, `last-crash.json`. It never contains transcripts, the
  raw `settings.json` or any `.bak` file.
- **REQ-REL-25 (MUST)** In the redacted settings JSON:
  - every `[Secret]` property becomes `"[set]"` or `""`;
  - every `[Personal]` property (`MyPlayerName`, `MutedPlayers`) becomes `"[set]"`/`""` or `"[N entries]"`.

  A reflection-driven test enforces this, so any key added later is covered automatically.
- **REQ-REL-26 (SHOULD)** A `ProviderHealthTracker` records success and failure counts, last success and failure
  times, and the last redacted error for each provider. The report includes it.
- **REQ-REL-27 (MUST)** The About page gets a **Troubleshooting** section with:
  - Copy diagnostics;
  - Export diagnostics bundle…;
  - Open log folder;
  - Restart in safe mode / Restart normally;
  - three settings checkboxes: show error pop-ups, detailed logging, include chat text in logs.
- **REQ-REL-28 (COULD)** The Troubleshooting section lists the 10 most recent Warning-or-higher entries ("Recent
  problems"), and shows the last crash with a **Dismiss** button that deletes `last-crash.json`.

### Settings durability and migration

- **REQ-REL-29 (MUST)** `AppSettings.CurrentVersion` is a constant, and `SettingsStore.Save` always writes
  `version = AppSettings.CurrentVersion`.
- **REQ-REL-30 (MUST)** A `SettingsMigrator` runs ordered `ISettingsMigration` steps (N→N+1) on the raw `JsonObject`
  before deserialization. A missing `version` field counts as version 1. A gap or duplicate in the chain throws at
  construction.
- **REQ-REL-31 (MUST)** Before migrating a file whose version is lower than current, the store copies it byte-for-byte
  to `settings.json.v{N}.bak`.
- **REQ-REL-32 (SHOULD)** A file whose version is newer than current is loaded best-effort (unknown fields are
  ignored), copied to `settings.json.v{N}.bak` before it is overwritten, and logged at Warning.
- **REQ-REL-33 (MUST)** A file that loads successfully is copied to `settings.json.bak` (last known good). If
  `settings.json` cannot be parsed or deserialized, the store copies it to `settings.json.broken` and loads
  `settings.json.bak` instead. Only if the backup also fails does it fall back to defaults.
- **REQ-REL-34 (MUST)** `SettingsStore.LoadWithReport()` returns the settings plus a `SettingsLoadStatus`, the file
  version and the relevant backup path, for the startup toast and the diagnostics report.
- **REQ-REL-35 (SHOULD)** After deserialization, any `null` settings sub-object, list or dictionary (for example
  `"capture": null` in a hand-edited file) is replaced with its default instance.
- **REQ-REL-36 (SHOULD)** `Save` flushes the temporary file to disk (`Flush(true)`) before the atomic move. Save
  failures (`IOException` or `UnauthorizedAccessException`) are logged, and the third failure in a row raises a toast.
- **REQ-REL-37 (SHOULD)** When a `[Secret]` value cannot be decrypted (for example after moving to a new PC), a
  Warning is logged that names the property but never the value.
- **REQ-REL-38 (SHOULD)** **Reset settings…** in Troubleshooting asks for confirmation, then copies `settings.json` to
  `settings.json.reset-{yyyyMMdd-HHmmss}.bak` and restores the defaults.

### Safe mode

- **REQ-REL-39 (MUST)** Envoy starts in safe mode when the first instance is launched with `--safe-mode`, or while
  either Shift key is held (read with `GetAsyncKeyState`).
- **REQ-REL-40 (MUST)** In safe mode Envoy:
  - creates no overlay window;
  - registers no global hotkeys;
  - does not auto-start capture (the user can still start it manually);
  - always shows `MainWindow`, even with `--minimized`;
  - still lets the user edit and save settings.
- **REQ-REL-41 (MUST)** In safe mode, every `MainWindow` page shows a banner explaining safe mode with a
  **Restart normally** button, and the tray tooltip reads "Envoy (safe mode)".
- **REQ-REL-42 (COULD)** Envoy starts in safe mode automatically, and says why in the banner, after 2 consecutive
  launches that never reached 20 s of healthy running (`StartupSentinel`).

### Verification hooks

- **REQ-REL-43 (MUST)** The smoke test covers:
  - `ToastWindow` under every theme;
  - `MainWindow` with an `AppHost` in safe mode;
  - building the diagnostics report and bundle;
  - the unobserved-task handler being wired up.

  It also appends the tail of the diagnostics log to the smoke log.

MUST count: 30.

---

## Design

### Overview

```
Envoy.Core/Diagnostics                    Envoy.App
──────────────────────                    ─────────
LogHub ──► sinks: RollingFileSink         App.OnStartup
  │              MemoryLogSink               ├─ LogBootstrap.Create(dataFolder)    (first thing)
  ├─ LogRedactor (secrets, patterns,         ├─ GlobalExceptionHandlers.Install(...)
  │               urls, %USERPROFILE%)       ├─ SafeModeDecision.Decide(args, shift, sentinel)
  └─ RepeatSuppressor                        ├─ new AppHost(path, Dispatcher, options{Log, SafeMode})
ILog = hub.For("Category")                   ├─ ToastService (ToastWindow ×≤3)
TaskExtensions.Forget                        └─ overlay/hotkeys only when !SafeMode
CrashLoopGuard, NotificationThrottle
CrashMarker, StartupSentinel, SafeModeDecision
ProviderHealthTracker
DiagnosticsReport + DiagnosticsBundle  ◄── DiagnosticsCollector (App: OS, monitors, DPI, OCR, game)

Envoy.Core/Settings
SettingsStore.LoadWithReport ─► parse JsonObject ─► SettingsMigrator ─► deserialize ─► SettingsSanitizer
                                   │ fail → .broken + try .bak                 │ ok → refresh .bak
SettingsRedaction (reflection over [Secret]/[Personal])
```

### Core: logging (`src/Envoy.Core/Diagnostics/`)

**`Logging.cs`**

```csharp
namespace Envoy.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error, Critical }

/// <summary>One structured field. Sensitive fields (chat text, player names) are masked unless the user opts in.</summary>
public readonly record struct LogField(string Name, object? Value, bool Sensitive = false)
{
    public static LogField Of(string name, object? value) => new(name, value);
    public static LogField Sensitive(string name, string? value) => new(name, value, true);
}

/// <summary>A fully rendered, already-redacted entry. Sinks never see raw values.</summary>
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message,
                              string Fields, string? Exception, int RepeatCount = 0);

public interface ILogSink { void Write(LogEntry entry); void Flush(); }

public interface ILog
{
    string Category { get; }
    bool IsEnabled(LogLevel level);
    void Write(LogLevel level, string message, Exception? exception = null, params LogField[] fields);
}

public static class LogExtensions
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
    public LogLevel MinimumLevel { get; set; }            // default Info
    public bool IncludeSensitive { get; set; }            // = Settings.Diagnostics.LogChatText
    public LogRedactor Redactor { get; }
    public long DroppedWrites { get; }                    // sink exceptions caught
    public void AddSink(ILogSink sink);
    public ILog For(string category);
    public void Flush();                                  // flushes all sinks; never throws
    public void Dispose();                                // flushes repeat summaries, then sinks
}
```

How `LogHub.Write` works:

1. Check the level.
2. Render the fields as `name=value` separated by spaces. Sensitive fields become `name=<N chars>` unless
   `IncludeSensitive` is on; then the value is quoted, with `\r`, `\n` and `"` escaped.
3. Render the exception with `ToString()`.
4. Run the message, the fields and the exception through `Redactor.Redact`.
5. Pass the result through `RepeatSuppressor`.
6. Fan out to each sink inside a `try`/`catch`. Any exception from a sink increments `DroppedWrites` and is swallowed.

The hub uses one lock around steps 5 and 6. Entries come at a rate of single digits per second at most, so a lock is
fine.

**`LogRedactor.cs`**

```csharp
public sealed class LogRedactor
{
    public const string Mask = "[redacted]";
    public void SetSecrets(IEnumerable<string> values);    // replaces the set; ignores values shorter than 6 chars
    public void AddPathAlias(string path, string alias);   // e.g. user profile → "%USERPROFILE%"
    public string Redact(string? text);
}
```

The redactor applies these steps in order:

1. Replace each registered secret with an ordinal, case-sensitive replacement. Longer secrets are replaced first.
2. Apply built-in patterns:
   - `sk-ant-[A-Za-z0-9_\-]{8,}`;
   - `AKIA[0-9A-Z]{16}`;
   - `[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}:fx` (DeepL Free);
   - `(?i)(x-api-key|authorization|auth_key)\s*[:=]\s*\S+`.
3. Rewrite URLs matching `https?://[^\s"']+`: drop the user-info (`https://[redacted]@host`) and replace the query
   string (`?[redacted]`).
4. Apply path aliases, case-insensitively.

Patterns are compiled once with `RegexOptions.Compiled`. Plain GUIDs are **not** redacted, because message ids are
useful in logs.

**`RepeatSuppressor.cs`** (internal, used by `LogHub`): the key is `(Level, Category, Message)`, taken before the
fields are rendered. The first occurrence is written. Further occurrences within 60 s (from the injectable
`TimeProvider`) only increment a counter. The next occurrence after the window, or `Flush`/`Dispose`, writes one entry
with `RepeatCount = n`, which renders as `… (repeated n times in 60 s)`.

**`RollingFileSink.cs`**

```csharp
public sealed class RollingFileSink : ILogSink, IDisposable
{
    public RollingFileSink(string folder, string baseName = "envoy", long maxBytes = 1_048_576, int maxFiles = 5);
    public string CurrentPath { get; }                     // {folder}/envoy.log
    public IReadOnlyList<string> ExistingFiles();          // envoy.log, envoy.1.log … that exist, newest first
}
```

- The sink opens the file lazily with `FileMode.Append`, `FileShare.ReadWrite | FileShare.Delete` and UTF-8 without a
  BOM, using a `StreamWriter` with `AutoFlush = true`, so a crash loses nothing.
- Before each write, if `size + line > maxBytes`, it rolls over:
  1. close the writer;
  2. delete `envoy.{maxFiles-1}.log`;
  3. rename `envoy.{i}.log` → `envoy.{i+1}.log`, going downwards;
  4. rename `envoy.log` → `envoy.1.log`;
  5. reopen `envoy.log`.
- It only ever touches files that match `^envoy(\.\d+)?\.log$`.
- When an `IOException` or `UnauthorizedAccessException` occurs, the sink throws to the hub (which counts it) and
  backs off: it does not try to reopen the file for 30 s.
- Line format, from `LogFormatter.Format(LogEntry)`:
  `2026-10-14T18:03:12.481Z WRN [Translation] Provider failed provider=deepl status=429` followed by the exception
  lines indented by two spaces. Times are UTC ISO-8601 with milliseconds.

**`MemoryLogSink.cs`**

```csharp
public sealed class MemoryLogSink(int capacity = 500) : ILogSink
{
    public IReadOnlyList<LogEntry> Snapshot(LogLevel minimum = LogLevel.Debug, int max = int.MaxValue); // newest last
    public event Action? Changed;  // raised on the writing thread; App marshals to the UI thread
}
```

**`TaskExtensions.cs`**

```csharp
public static class TaskExtensions
{
    /// <summary>Observes a fire-and-forget task. Faults are logged at Error; cancellation is logged at Debug.</summary>
    public static void Forget(this Task task, ILog log, string what);
}
```

### Core: error policy helpers (`src/Envoy.Core/Diagnostics/`)

```csharp
public sealed class CrashLoopGuard(int threshold = 5, TimeSpan? window = null /*30 s*/, TimeProvider? clock = null)
{
    /// <summary>Records one unhandled error; true when threshold errors happened within the window.</summary>
    public bool RecordAndCheckTripped();
}

public sealed record ProblemReport(LogLevel Severity, string Title, string Detail, string DedupeKey, bool HasDetails = true);

public sealed class NotificationThrottle(TimeSpan? dedupeWindow = null /*2 min*/, TimeProvider? clock = null)
{
    public bool ShouldShow(ProblemReport report);   // false when the same DedupeKey was shown within the window
}

public sealed record CrashMarker(DateTimeOffset Time, string Version, string ExceptionType, string Message)
{
    public static CrashMarker From(Exception ex, string version, LogRedactor redactor, TimeProvider? clock = null);
    public void Write(string path);                       // never throws
    public static CrashMarker? TryRead(string path);      // null if missing or corrupt
}

public enum SafeModeReason { CommandLine, ShiftHeld, RepeatedStartupFailures }

public static class SafeModeDecision
{
    public const string Flag = "--safe-mode";
    public static SafeModeReason? Decide(IReadOnlyList<string> args, bool shiftHeld, int unfinishedStarts, int autoThreshold = 2);
}

/// <summary>COULD (REQ-REL-42): detects launches that never reached a healthy state.</summary>
public sealed class StartupSentinel(string path)
{
    public int BeginStartup();   // returns previous consecutive unfinished starts, then records this one
    public void MarkHealthy();   // deletes the file; never throws
}

public sealed record ProviderHealth(string ProviderId, int Successes, int Failures,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, string? LastError);

public sealed class ProviderHealthTracker(LogRedactor redactor, TimeProvider? clock = null)
{
    public void RecordSuccess(string providerId);
    public void RecordFailure(string providerId, Exception error);   // stores the redacted ex.Message
    public IReadOnlyList<ProviderHealth> Snapshot();
}
```

Policy for `Decide`:

| Condition | Result |
| --- | --- |
| `args` contains `--safe-mode` | `CommandLine` |
| else `shiftHeld` | `ShiftHeld` |
| else `unfinishedStarts >= autoThreshold` | `RepeatedStartupFailures` |
| otherwise | `null` |

### Core: diagnostics report (`src/Envoy.Core/Diagnostics/DiagnosticsReport.cs`)

The App gathers the data. Core holds the formatting and packaging, so both are unit-tested.

```csharp
public sealed record MonitorInfo(string Name, bool Primary, int X, int Y, int Width, int Height, double Scale);

public sealed record DiagnosticsSnapshot
{
    public required string AppVersion { get; init; }          // assembly version
    public string? InformationalVersion { get; init; }
    public required string Runtime { get; init; }             // RuntimeInformation.FrameworkDescription
    public required string OsDescription { get; init; }       // RuntimeInformation.OSDescription
    public required string Architecture { get; init; }        // ProcessArchitecture
    public string? Culture { get; init; }                     // CurrentCulture / CurrentUICulture names
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = [];
    public string? Game { get; init; }                        // profile name only, never window titles
    public string? CaptureState { get; init; }
    public string? RegionSize { get; init; }                  // "600×200" only, no position
    public IReadOnlyList<string> OcrInstalled { get; init; } = [];
    public IReadOnlyList<string> OcrSelected { get; init; } = [];
    public IReadOnlyList<ProviderHealth> Providers { get; init; } = [];
    public IReadOnlyList<string> HotkeyFailures { get; init; } = [];
    public SafeModeReason? SafeMode { get; init; }
    public SettingsLoadResult? SettingsLoad { get; init; }
    public CrashMarker? LastCrash { get; init; }
    public TimeSpan Uptime { get; init; }
    public long DroppedLogWrites { get; init; }
}

public static class DiagnosticsReport
{
    public const int MaxClipboardChars = 60_000;
    /// <summary>Markdown-friendly text: header, sections, redacted settings JSON (in a code fence), last log lines.</summary>
    public static string Render(DiagnosticsSnapshot snapshot, AppSettings settings,
                                IReadOnlyList<LogEntry> recentLog, int maxLogLines = 50, int maxChars = MaxClipboardChars);
}

public static class DiagnosticsBundle
{
    /// <summary>Writes report.txt, settings.redacted.json, logs/{name} for each log file, last-crash.json (optional).</summary>
    public static void Write(Stream destination, string reportText, AppSettings settings,
                             IEnumerable<string> logFiles, string? crashMarkerPath);
}
```

- Truncation in `Render` drops the oldest log lines first and adds a `… (N earlier lines omitted)` line. It never
  drops the header sections.
- The provider section lists each id in `Primary` + `Fallbacks`, a `key: set / not set` flag (from a non-empty
  `[Secret]` property relevant to that provider, through `ProviderCatalog`), and the health row.
- `DiagnosticsBundle` opens the log files with `FileShare.ReadWrite`, because the active log is open, and uses
  `System.IO.Compression.ZipArchive`, which is in the BCL.

### Core: settings (`src/Envoy.Core/Settings/`)

**`AppSettings.cs` (shared file):**

```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class PersonalAttribute : Attribute;      // player-identifying; redacted in diagnostics

public sealed class AppSettings
{
    public const int CurrentVersion = 1;                 // bump together with a new ISettingsMigration
    public int Version { get; set; } = CurrentVersion;   // was: = 1
    [Personal] public string MyPlayerName { get; set; } = "";       // attribute added
    [Personal] public List<string> MutedPlayers { get; set; } = []; // attribute added
    public DiagnosticsSettings Diagnostics { get; set; } = new();   // new
    // … everything else unchanged
}

public sealed class DiagnosticsSettings
{
    /// <summary>Show a small pop-up in the corner when something goes wrong.</summary>
    public bool ShowErrorToasts { get; set; } = true;
    /// <summary>Write Debug-level entries to the log (for bug reports).</summary>
    public bool VerboseLogging { get; set; } = false;
    /// <summary>Include chat text and player names in logs. Off by default; only for diagnosing chat-reading bugs.</summary>
    public bool LogChatText { get; set; } = false;
}
```

`CurrentVersion` stays **1**. Adding `Diagnostics` is additive and does not need a migration. The registry ships
empty. Any later area that renames or restructures a property adds an `ISettingsMigration` and bumps
`CurrentVersion`. That rule goes into `CLAUDE.md` under "Settings changes" (integrator note).

**`SettingsMigration.cs` (new):**

```csharp
public interface ISettingsMigration
{
    int FromVersion { get; }            // upgrades FromVersion → FromVersion + 1
    string Description { get; }
    void Apply(JsonObject root);        // camelCase property names, as on disk
}

public sealed class SettingsMigrator
{
    /// <summary>Throws ArgumentException unless migrations cover 1..currentVersion-1 exactly once each.</summary>
    public SettingsMigrator(IReadOnlyList<ISettingsMigration> migrations, int currentVersion = AppSettings.CurrentVersion);
    public static int ReadVersion(JsonObject root);    // missing/invalid → 1
    /// <summary>Applies the needed steps in order and sets root["version"]. Returns the version read from the file.</summary>
    public int Migrate(JsonObject root);
}

public static class SettingsMigrations
{
    public static IReadOnlyList<ISettingsMigration> All { get; } = [];
}
```

**`SettingsStore.cs` (changed):**

```csharp
public enum SettingsLoadStatus { Defaults, Loaded, Migrated, NewerVersion, RecoveredFromBackup, ResetCorrupt, MigrationFailed }

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, int FileVersion,
                                        string? BackupPath, string? Error);

public sealed class SettingsStore
{
    public SettingsStore(string path, ISecretProtector protector);   // existing signature kept
    public SettingsStore(string path, ISecretProtector protector, IReadOnlyList<ISettingsMigration> migrations,
                         ILog? log = null, TimeProvider? clock = null, int currentVersion = AppSettings.CurrentVersion);

    public string Path { get; }
    public string LastKnownGoodPath => Path + ".bak";
    public string BrokenPath => Path + ".broken";                     // name kept: existing test relies on it
    public string VersionBackupPath(int version) => $"{Path}.v{version}.bak";

    public AppSettings Load() => LoadWithReport().Settings;            // existing callers unchanged
    public SettingsLoadResult LoadWithReport();
    public void Save(AppSettings settings);                            // writes Version = currentVersion on the copy
    public string ResetToDefaults();                                   // REQ-REL-38: backs up, deletes, returns backup path
}
```

`LoadWithReport` algorithm:

1. If there is no file, return `Defaults`.
2. Read the text. Parse it with `JsonNode.Parse`, and require the root to be a `JsonObject`.
3. Read `v = ReadVersion(root)`. Then branch:

   | Version | Action |
   | --- | --- |
   | `v < current` | Copy the original file to `VersionBackupPath(v)`, then `Migrate(root)`. If a migration throws, log Error and return `MigrationFailed` with defaults. The `.v{v}.bak` copy is kept. |
   | `v > current` | Copy the file to `VersionBackupPath(v)` (once, without overwriting) and log Warning. The status is `NewerVersion`. |
   | `v == current` | No change. |

4. Deserialize `root` into `AppSettings`. Run `SettingsSanitizer.RepairNulls(settings)` (REQ-REL-35), then unprotect
   the secrets, logging each failure by property name (REQ-REL-37).
5. On success, copy the **original text** to `LastKnownGoodPath`. Write it to a temporary file and move it into place.
   Failures here are logged, not thrown. Return `Loaded`, `Migrated` or `NewerVersion`.
6. If step 2 or step 4 fails (`JsonException`, `InvalidOperationException`, `NotSupportedException`):
   1. copy the file to `BrokenPath`;
   2. run steps 2–4 on `LastKnownGoodPath`;
   3. if that succeeds, return `RecoveredFromBackup` (the backup path is in the result);
   4. otherwise return `ResetCorrupt` with defaults.
7. An `IOException` or `UnauthorizedAccessException` while reading `settings.json` is handled the same way as step 6.

`Save`:

1. Deep-copy the settings (as today) and set `copy.Version = currentVersion`.
2. Encrypt the secrets.
3. Write `path.tmp` through a `FileStream` and call `Flush(flushToDisk: true)`.
4. `File.Move(tmp, path, overwrite: true)`.

`Save` still throws `IOException` and `UnauthorizedAccessException`, and `AppHost` handles them (below).

**`SettingsSanitizer.cs` (new, internal plus tests):** walks the public read/write properties of `AppSettings` and its
nested settings classes, using the same rule as `SettingsStore.TransformSecrets` (namespace check). Any `null` value
whose type is a settings class, `List<>` or `Dictionary<,>` is replaced with `Activator.CreateInstance(type)`. Any
`null` string is replaced with `""`.

**`SettingsRedaction.cs` (new):**

```csharp
public static class SettingsRedaction
{
    public static IReadOnlyList<string> SecretValues(AppSettings settings);   // non-empty [Secret] values, for LogRedactor
    public static string ToRedactedJson(AppSettings settings);               // SettingsStore.JsonOptions, secrets/personal masked
    public static bool HasSecret(AppSettings settings, string propertyName);  // for the "key set / not set" flag
}
```

`ToRedactedJson` works on a `JsonNode` copy. For each `[Secret]` or `[Personal]` property, found by reflection and
mapped to its camelCase name:

| Property type | Redacted value |
| --- | --- |
| string | `"[set]"` if non-empty, `""` if empty |
| list | `"[N entries]"` |

### App: bootstrap and global handlers

**`src/Envoy.App/Services/LogBootstrap.cs` (new):**

```csharp
internal static class LogBootstrap
{
    /// <summary>Hub with RollingFileSink({dataFolder}/logs) + MemoryLogSink, user-profile alias registered.</summary>
    public static (LogHub Hub, MemoryLogSink Memory, RollingFileSink File) Create(string dataFolder);
    public static void WriteHeader(LogHub hub, SafeModeReason? safeMode, SettingsLoadResult? load);
}
```

**`src/Envoy.App/Services/GlobalExceptionHandlers.cs` (new):**

```csharp
internal sealed class GlobalExceptionHandlers
{
    public static GlobalExceptionHandlers Install(App app, LogHub hub, string crashMarkerPath, Func<bool> isSmokeTest);
    /// <summary>Set once ToastService exists; until then UI errors are logged only.</summary>
    public Action<ProblemReport>? ShowProblem { get; set; }
    public void HandleUiException(Exception ex, string source);   // shared by Dispatcher + WinForms paths
}
```

`Install` wires up five things:

- **`app.DispatcherUnhandledException`**:
  1. log Error under category `Unhandled`;
  2. if `isSmokeTest()`, return without setting `Handled` (CI must still fail);
  3. otherwise call `CrashLoopGuard.RecordAndCheckTripped()`. If it trips:
     1. log Critical;
     2. `CrashMarker.Write`;
     3. `hub.Flush()`;
     4. show **one** `MessageBox`: "Envoy hit repeated errors and has to close. A log was saved — use About →
        Troubleshooting → Export diagnostics next time.";
     5. `app.Shutdown(1)`.
  4. Otherwise set `e.Handled = true` and call `ShowProblem(new ProblemReport(Error, "Something went wrong",
     "Envoy kept running. " + ex.Message (redacted), DedupeKey: ex.GetType().FullName + ex.TargetSite?.Name))`.
- **`System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)`**, plus
  `Forms.Application.ThreadException` → `HandleUiException(e.Exception, "WinForms")`.
- **`TaskScheduler.UnobservedTaskException`**: log Warning `Unobserved task exception` with the exception, then
  `e.SetObserved()`. No toast.
- **`AppDomain.CurrentDomain.UnhandledException`**: log Critical with `IsTerminating`, call
  `CrashMarker.From(...).Write(crashMarkerPath)`, then `hub.Flush()`. No UI, because the process is dying.
- **Re-entrancy**: if handling an exception throws, the handler logs it at Critical and does nothing else (no
  recursion).

`ex.Message` in toasts goes through `hub.Redactor.Redact` first.

### App: toasts

**`src/Envoy.App/Windows/ToastWindow.xaml(.cs)` (new):**

- **Window properties:** `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`, `Topmost=True`,
  `ShowInTaskbar=False`, `ShowActivated=False`, `ResizeMode=NoResize`, `SizeToContent=Height`, `Width=360`.
- **Visual:** a `Border` with `Background={DynamicResource Bg.Panel}`, `BorderBrush={DynamicResource Danger}` for
  errors or `{DynamicResource Accent}` for info, and `Text.Primary` / `Text.Secondary` text. An icon glyph in
  `Font.Icons`. Buttons use `Button.Ghost` ("Details") and `Button.Chrome` ("×"). No hard-coded colours, and only
  existing palette keys: `Bg.Panel`, `Danger`, `Accent`, `Text.Primary`, `Text.Secondary`, `Border.Subtle`.
- **No activation:** on `SourceInitialized`, call a new `Native.MakeNoActivate(hwnd)`, which sets
  `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` but not `WS_EX_TRANSPARENT`, so the toast stays clickable. Then call
  `Native.KeepOnTop`.
- **API:** `public ToastWindow(ProblemReport report)`; `public event Action? DetailsRequested`;
  `public void Place(Rect workAreaDip, int stackIndex)`.
- **Timer:** a `DispatcherTimer` closes the toast after 10 s (`Info`: 6 s). `MouseEnter` stops the timer and
  `MouseLeave` restarts it.

**`src/Envoy.App/Services/ToastService.cs` (new):**

```csharp
internal sealed class ToastService
{
    public ToastService(Dispatcher ui, Func<bool> enabled, Func<IntPtr> gameWindow, Action openTroubleshooting,
                        NotificationThrottle? throttle = null);
    public void Show(ProblemReport report);   // any thread; marshals; applies enabled + throttle; max 3 visible
    public void CloseAll();
}
```

- **Placement:** the toast goes in the bottom-right of the work area of the monitor that holds `gameWindow()`
  (`Forms.Screen.FromHandle`), falling back to the primary monitor. Physical pixels are converted to DIPs with the
  monitor's DPI (`GetDpiForMonitor`, added to `Native.cs`). Toasts stack upward with a 8 DIP gap.
- **Limit:** when a 4th toast arrives, the oldest closes first.
- **`openTroubleshooting`** = `App.ShowMain()` + `MainWindow.Navigate("About")` + `MainWindow.ScrollToTroubleshooting()`.
- **Exclusive full-screen:** like the overlay, a toast cannot show over exclusive full-screen. This is documented. The
  problem is still listed under Recent problems.

**Who raises toasts** (all through `AppHost.ProblemReported` or the global handlers):

| Source | Severity | Dedupe key |
| --- | --- | --- |
| Unhandled UI or WinForms exception | Error | exception type + target site |
| `ComposeWindow` `SendInput` failure (replaces the `MessageBox`): "Couldn't type into the game — the translation is on your clipboard." | Warning | `compose.send` |
| Settings save failed 3 times in a row | Error | `settings.save` |
| Capture watcher entered `Error` with a new `LastError` | Warning | `capture:` + LastError |
| Startup: previous crash, or settings `RecoveredFromBackup` / `ResetCorrupt` / `MigrationFailed` | Warning | `startup.*` |

Translation failures are **not** toasted. They already show on the overlay message.

### App: AppHost changes (shared file `src/Envoy.App/AppHost.cs`)

```csharp
public sealed record AppHostOptions
{
    public LogHub? Log { get; init; }                 // null → new LogHub() with no sinks (tests/smoke)
    public MemoryLogSink? RecentLog { get; init; }
    public SafeModeReason? SafeMode { get; init; }
    public string? DataFolder { get; init; }          // diagnostics/crash paths; default Path.GetDirectoryName(settingsPath)
}

public AppHost(string settingsPath, Dispatcher ui, AppHostOptions? options = null);  // old 2-arg calls still compile

public LogHub LogHub { get; }
public MemoryLogSink? RecentLog { get; }
public SafeModeReason? SafeMode { get; }
public bool IsSafeMode => SafeMode is not null;
public SettingsLoadResult SettingsLoad { get; }
public ProviderHealthTracker ProviderHealth { get; }
public string CrashMarkerPath { get; }                // {DataFolder}/last-crash.json
public event Action<ProblemReport>? ProblemReported; // raised on the UI thread
public IReadOnlyList<string> HotkeyFailures { get; set; } = []; // set by App.RegisterHotkeys (for diagnostics)
```

Changes in the body:

- **Constructor:**
  - `new SettingsStore(path, new DpapiProtector(), SettingsMigrations.All, LogHub.For("Settings"))`, then
    `SettingsLoad = _store.LoadWithReport(); Settings = SettingsLoad.Settings;`.
  - `ApplyDiagnosticsSettings()`, which sets `LogHub.MinimumLevel`, `IncludeSensitive` and
    `Redactor.SetSecrets(SettingsRedaction.SecretValues(Settings))`.
  - `Watcher.StateChanged`: when the state becomes `Error`, log Warning under `Capture` with `error=LastError` (no
    chat) and raise a `ProblemReport` (deduped by `NotificationThrottle` in `ToastService`). No changes to
    `ChatWatcher.cs`.
  - The `BuildTranslator` `onProviderFailed` callback also calls `ProviderHealth.RecordFailure(id, ex)` and logs Warning
    `Provider failed provider={id}`.
  - `Pipeline.MessageUpdated`:
    - `Translated` or `Untranslated` with a provider id → `ProviderHealth.RecordSuccess(ProviderId)`;
    - `Failed` → log Warning `Translation failed provider=… error=…` with `LogField.Sensitive("text", m.Chat.Text)`.
- **`Start()`:**
  - `if (Settings.StartCapturingAutomatically && !IsSafeMode) Watcher.Start();`
  - `CheckForUpdatesAsync().Forget(log, "update check")`. Inside the existing `catch`, add a Debug log.
- **`ApplySettings()`:** also calls `ApplyDiagnosticsSettings()`.
- **`SaveNow()`:** catches `IOException` and `UnauthorizedAccessException`, logs Warning and increments
  `_saveFailures`. On the 3rd consecutive failure it raises `ProblemReport("Couldn't save settings", ex.Message)`. A
  successful save resets the count.
- **Empty catches** in `OnMessage` (transcript), `Say`, `ConfigureOcr` and `RefreshGame`: add Warning or Debug log calls.
  `RefreshGame` logs Info "Game detected" / "Game closed" with the profile name only.
- **New methods:**
  - `public string BuildDiagnosticsText(DiagnosticsSnapshot snapshot)` → `DiagnosticsReport.Render(...)`;
  - `public void ResetSettings()` → `_store.ResetToDefaults()`, then App restarts the process;
  - `public void ReportProblem(ProblemReport report)` → raises `ProblemReported` on the UI thread.
- **`Dispose()`:** `LogHub.Flush()`. App disposes the hub; AppHost does not own it.

### App: App.xaml.cs changes (shared file)

`OnStartup` runs in this order:

1. `var smoke = e.Args.Contains("--smoke-test");`
   `var dataFolder = smoke ? SmokeTest.DataFolder : AppHost.DataFolder;` (`SmokeTest.DataFolder` is a new static: a
   temp folder created once).
2. `(_log, _recent, _file) = LogBootstrap.Create(dataFolder);`
   `_handlers = GlobalExceptionHandlers.Install(this, _log, Path.Combine(dataFolder, "last-crash.json"), () => SmokeTest.IsRunning);`
   This replaces `DispatcherUnhandledException += OnUnhandledException`. The old `OnUnhandledException` method and its
   `error.log` writing are **deleted**.
3. Smoke branch: `SmokeTest.Run(this, e.Args, _log, _recent)` (signature extended).
4. Single-instance mutex, unchanged. Safe mode is decided **after** the mutex, so a second launch with Shift held just
   shows the running instance.
5. `var sentinel = new StartupSentinel(Path.Combine(dataFolder, "startup.pending"));` (COULD; otherwise pass 0)
   `var safe = SafeModeDecision.Decide(e.Args, Native.IsShiftDown(), sentinel.BeginStartup());`
6. `Host = new AppHost(SettingsStore.DefaultPath, Dispatcher, new AppHostOptions { Log = _log, RecentLog = _recent, SafeMode = safe });`
   `LogBootstrap.WriteHeader(_log, safe, Host.SettingsLoad);`
7. `_toasts = new ToastService(Dispatcher, () => Host.Settings.Diagnostics.ShowErrorToasts, () => Host.Game?.MainWindow ?? 0, OpenTroubleshooting);`
   `_handlers.ShowProblem = _toasts.Show; Host.ProblemReported += _toasts.Show;`
8. `Host.Start();`
9. If not safe mode: create the overlay, `RegisterHotkeys()` and the `SettingsApplied` subscriptions, as today. If safe
   mode: skip all of these.
10. `CreateTray()`. In safe mode the tooltip is `"Envoy (safe mode)"` and the menu gains "Restart normally".
11. Show main: `if (safe is not null || !e.Args.Contains("--minimized") || !Host.Settings.FirstRunCompleted) ShowMain();`
12. Startup toasts (REQ-REL-22): if `CrashMarker.TryRead(...)` returns a marker, or `Host.SettingsLoad.Status` is
    `RecoveredFromBackup`, `ResetCorrupt` or `MigrationFailed`, show one toast.
13. A one-shot 20 s `DispatcherTimer` calls `sentinel.MarkHealthy()`.

New and changed members:

- `public void Restart(bool safeMode)`:
  1. `_instanceMutex?.ReleaseMutex(); _instanceMutex?.Dispose(); _instanceMutex = null;`
  2. `Process.Start(Environment.ProcessPath!, safeMode ? "--safe-mode" : "")`;
  3. `ExitApp()`.

  Releasing the mutex before starting the new process stops the new process from treating itself as a second
  instance.
- `RegisterHotkeys()`: after binding, `Host.HotkeyFailures = failed;` and log Warning for each failure.
- `ExitApp()`: `sentinel?.MarkHealthy(); _toasts?.CloseAll(); … Host.Dispose(); _log.Dispose();`.
- `public static bool IsSafeMode => Host?.IsSafeMode == true;`

**`Interop/Native.cs`** additions:

- `GetAsyncKeyState` and `IsShiftDown()` (`VK_SHIFT = 0x10`, high bit);
- `MakeNoActivate(IntPtr hwnd)`;
- `GetDpiForMonitor` (shcore) and `MonitorFromWindow`, for toast placement and diagnostics.

### App: diagnostics collector

**`src/Envoy.App/Services/DiagnosticsCollector.cs` (new):**

```csharp
internal static class DiagnosticsCollector
{
    public static DiagnosticsSnapshot Collect(AppHost host);
    public static string CopyToClipboard(AppHost host);                 // returns the text; retries Clipboard.SetText 3× on COMException
    public static string ExportBundle(AppHost host, string zipPath, IEnumerable<string> logFiles); // returns zipPath
}
```

`Collect` reads its data from these sources:

| Data | Source |
| --- | --- |
| Envoy version | `AppHost.CurrentVersion` and `AssemblyInformationalVersionAttribute` |
| Runtime, OS, architecture | `RuntimeInformation` |
| Monitors | `Forms.Screen.AllScreens` (device name, bounds, primary). Scale comes from `GetDpiForMonitor(MonitorFromPoint(center))/96`. |
| Game | `host.Game?.Profile.Name` |
| Capture | `host.CaptureStatus`, plus the width × height of the current region |
| OCR | `OcrService.InstalledLanguages()` (inside a try; empty on failure) and `Settings.Capture.OcrLanguages` |
| Providers | `host.ProviderHealth.Snapshot()`, merged with the configured chain |
| Other | `host.HotkeyFailures`, `host.SafeMode`, `host.SettingsLoad`, `CrashMarker.TryRead(host.CrashMarkerPath)`, process uptime, `host.LogHub.DroppedWrites` |

### UI: MainWindow (shared files `Windows/MainWindow.xaml`, `MainWindow.xaml.cs`, `ViewModels/MainViewModel.cs`)

- **Safe-mode banner.** A `Border` at the top of the page-content `Grid` row, above all page `ScrollViewer`s.
  - Visibility is bound to `IsSafeMode` (`BoolToVisibility`).
  - `Background={DynamicResource Bg.PanelAlt}`, `BorderBrush={DynamicResource Danger}`.
  - Text: "Safe mode — the overlay, hotkeys and automatic chat reading are off so you can fix your settings." plus
    `SafeModeReasonText` (for example "Started with Shift held").
  - Button "Restart normally" → `App.Current.Restart(false)`.
- **About page → new `OrnateFrame` "Troubleshooting"**, placed above the "Open settings folder" buttons:
  - Checkboxes bound to the settings, which auto-apply through the existing `OnEdited` handler:
    - "Show a pop-up when something goes wrong" (`Settings.Diagnostics.ShowErrorToasts`);
    - "Detailed logging (for bug reports)" (`Settings.Diagnostics.VerboseLogging`);
    - "Include chat text and player names in logs" (`Settings.Diagnostics.LogChatText`), with the caption: "Off by
      default. Only turn on if a developer asks — the logs will then contain your chat."
  - A `WrapPanel` of buttons:
    - "Copy diagnostics" (`OnCopyDiagnostics`). It shows the status text "Copied — paste it into your bug report.";
    - "Export diagnostics…" (`OnExportDiagnostics`). It opens a `Microsoft.Win32.SaveFileDialog` in the Desktop folder
      with the default name `envoy-diagnostics-{yyyyMMdd-HHmmss}.zip`, then runs `explorer.exe /select,"…"`;
    - "Open log folder" (reuses the existing `OnOpenLogs`);
    - "Restart in safe mode" or "Restart normally", depending on `IsSafeMode`;
    - "Reset settings…" (SHOULD). It shows a confirmation `MessageBox` owned by `MainWindow`. This is allowed because
      the user started it and is not in play.
  - Caption: "Diagnostics never include API keys, transcripts or your player name, and are never sent anywhere —
    you choose where to paste or attach them."
  - (COULD) "Recent problems": an `ItemsControl` bound to `RecentProblems`, showing time, category and message in
    `Text.Caption`. When there are none, it shows "No problems recorded this session." Last crash line plus a
    "Dismiss" button.
- **`MainViewModel` additions:**
  - `IsSafeMode` and `SafeModeReasonText`;
  - `ObservableCollection<ProblemRow> RecentProblems` (`record ProblemRow(string Time, string Category, string Message)`),
    refreshed inside the existing 1 s `RefreshStatus` from `host.RecentLog?.Snapshot(LogLevel.Warning, 10)`;
  - `LastCrashText` and `DiagnosticsStatus`.
- **`MainWindow.xaml.cs`:** add the handlers above plus `public void ScrollToTroubleshooting()`, which calls
  `TroubleshootingFrame.BringIntoView()`. `Hotkeys` page: when `IsSafeMode`, the hotkey error list shows "Hotkeys are
  off in safe mode."

### UI: ComposeWindow

In `ComposeWindow.xaml.cs` `catch (Exception ex)` around `SendToWindowAsync`, replace the `MessageBox.Show(...)` with
`_host.ReportProblem(new ProblemReport(LogLevel.Warning, "Couldn't type into the game", ex.Message + " — the translation is on your clipboard.", "compose.send"))`
and a Warning log. This is a one-line ownership overlap with onboarding-ux-accessibility, if that area also edits
ComposeWindow.

### Threading summary

| Component | Thread(s) | Synchronisation |
| --- | --- | --- |
| `LogHub.Write` | any | one `lock`. Sinks are called inside it, and file I/O is a small append with AutoFlush. |
| `MemoryLogSink.Changed` | writer's thread | VM refreshes by polling every 1 s, so no cross-thread binding. |
| `ToastService.Show` | any | `Dispatcher.BeginInvoke` |
| `AppHost.ProblemReported` | raised on UI thread | `_ui.BeginInvoke` |
| `ProviderHealthTracker` | pipeline tasks + UI | `lock` |
| AppDomain handler | dying thread | synchronous log + marker write. Never touches the Dispatcher. |
| `SettingsStore` | UI thread (as today) | — |

### Failure behaviour

| Situation | Behaviour |
| --- | --- |
| Log folder unwritable or disk full | Envoy keeps running. `DroppedWrites` grows and is shown in diagnostics. The sink retries after 30 s. |
| Exception inside a toast or handler | Logged at Critical. No recursion, no UI. |
| Clipboard locked | 3 retries 100 ms apart. If they all fail, the status text reads "Couldn't access the clipboard — use Export instead." |
| Export path unwritable | The status text shows the error, and Envoy logs a Warning. |
| `settings.json` and `.bak` both corrupt | Defaults are loaded, `.broken` is kept, and the startup toast explains. |
| Migration throws | Defaults are loaded, and `.v{N}.bak` keeps the original for manual recovery and the bug report. |
| Launch with Shift while Envoy is already running | Shift is ignored and the running instance is shown, as today's single-instance behaviour does. |

---

## Acceptance criteria

| AC | REQ | Verified by |
| --- | --- | --- |
| **AC-REL-1** `LogHub` writes entries at or above `MinimumLevel` to every sink and drops lower ones. | 1, 8 | unit `LoggingTests.Hub_filters_below_minimum_level` |
| **AC-REL-2** Writing 3× the size cap with `maxBytes=1024, maxFiles=3` leaves exactly `envoy.log`, `envoy.1.log`, `envoy.2.log`, each ≤ 1024 + one line, with the newest entry in `envoy.log`. A `chat-*.txt` file in the same folder is untouched. | 2 | unit `RollingFileSinkTests.Rolls_over_and_keeps_max_files_without_touching_other_files` |
| **AC-REL-3** With a directory squatting on `envoy.log`, `Write` does not throw and `DroppedWrites > 0`. A sink that throws does not affect other sinks. | 3 | unit `RollingFileSinkTests.Unwritable_target_never_throws`, `LoggingTests.Throwing_sink_is_isolated` |
| **AC-REL-4** A registered secret, an `sk-ant-…` key, an `AKIA…` id, a `…:fx` DeepL key, a URL query string and URL user-info in the message, a field or the exception text all appear as `[redacted]` in sink output. | 4 | unit theory `LoggingTests.Redacts_secrets_everywhere` |
| **AC-REL-5** `LogField.Sensitive("text","你好世界")` renders `text=<4 chars>` by default and the quoted value when `IncludeSensitive=true`. | 5 | unit `LoggingTests.Sensitive_fields_masked_unless_opted_in` |
| **AC-REL-6** The user-profile alias replaces the path case-insensitively. | 6 | unit `LoggingTests.User_profile_path_is_aliased` |
| **AC-REL-7** 10 identical warnings within 60 s produce 1 entry plus 1 "repeated 9 times" entry after the window elapses (FakeTimeProvider). | 7 | unit `LoggingTests.Repeats_are_collapsed` |
| **AC-REL-8** Toggling "Detailed logging" in About → Troubleshooting makes Debug entries appear in `envoy.log` without a restart. | 8 | manual step M3 |
| **AC-REL-9** The first line of each session contains `Envoy`, the version and `safeMode=`, and does not contain `Environment.UserName` or `Environment.MachineName`. | 9 | smoke test assertion (S5) |
| **AC-REL-10** `MemoryLogSink(capacity:3)` holds only the last 3 entries, and `Snapshot(Warning, 2)` filters and limits. | 10 | unit `LoggingTests.Memory_sink_is_a_ring` |
| **AC-REL-11** A thrown exception in a dispatcher callback shows a toast, not a MessageBox, and Envoy keeps running. | 11, 18, 19 | manual step M1 (debug-only hidden trigger: Ctrl+Alt+Shift+F12 in About, compiled under `#if DEBUG`) |
| **AC-REL-12** An unobserved faulted task is logged at Warning. | 12 | smoke test S4 (memory sink contains `Unobserved task exception`) |
| **AC-REL-13** `CrashMarker` round-trips through `Write`/`TryRead`, has a redacted message, and `TryRead` returns null for missing or corrupt files. | 13 | unit `DiagnosticsTests.Crash_marker_round_trips_and_redacts` |
| **AC-REL-14** A tray-menu exception produces a toast, not the WinForms dialog. | 14 | manual step M2 (DEBUG tray item "Throw test error") |
| **AC-REL-15** `CrashLoopGuard(5, 30 s)` trips on the 5th error within 30 s and not when the errors span more than 30 s. | 15 | unit `DiagnosticsTests.Crash_loop_guard_trips_only_inside_window` |
| **AC-REL-16** `Task.FromException(...).Forget(log,"x")` logs Error with the exception. A cancelled task logs Debug. | 16 | unit `LoggingTests.Forget_logs_faults_and_cancellation` |
| **AC-REL-17** No `catch` in `AppHost.cs` is left without a log call. | 17 | code review checklist plus typecheck. Grep in the test plan: `grep -nA2 "catch" src/Envoy.App/AppHost.cs` shows a `_log.` call in each. |
| **AC-REL-18** `ToastWindow` builds and shows under every theme. Its colours resolve from palette keys. | 18 | Windows smoke test S1, `check_resources.py` (typecheck) |
| **AC-REL-19** `ComposeWindow.xaml.cs` and `App.xaml.cs` contain no `MessageBox.Show` except the crash-loop path in `GlobalExceptionHandlers`. | 19 | typecheck-time grep (test plan T1) |
| **AC-REL-20** `NotificationThrottle` suppresses a repeat key within 2 min and allows it after. | 20 | unit `DiagnosticsTests.Throttle_dedupes_within_window` |
| **AC-REL-21** With "Show a pop-up…" off, the M1 trigger produces no toast, but the entry appears in Recent problems. | 21, 28 | manual step M1b |
| **AC-REL-22** After a forced crash (M4), the next start shows a "closed unexpectedly" toast. | 22, 13 | manual step M4 |
| **AC-REL-23** `DiagnosticsReport.Render` includes every section header, includes `key: set` for a configured provider, contains no secret, player name or muted-player value, and stays ≤ `maxChars` while keeping the headers when given 5,000 log lines. | 23, 25 | unit `DiagnosticsTests.Report_has_all_sections_and_no_secrets`, `DiagnosticsTests.Report_is_capped_by_dropping_old_log_lines` |
| **AC-REL-24** `DiagnosticsBundle.Write` produces a zip with exactly `report.txt`, `settings.redacted.json`, `logs/envoy.log` (plus rolled files) and optionally `last-crash.json`, and no other entries. The smoke test exports a bundle successfully. | 24 | unit `DiagnosticsTests.Bundle_contains_only_expected_entries`, smoke S3 |
| **AC-REL-25** For every `[Secret]` and `[Personal]` property found by reflection, a settings object with distinctive values serialises through `ToRedactedJson` without those values. | 25 | unit `SettingsRedactionTests.Every_secret_and_personal_property_is_masked` |
| **AC-REL-26** `ProviderHealthTracker` counts successes and failures per provider, stores the redacted last error and timestamps. | 26 | unit `DiagnosticsTests.Provider_health_tracks_counts_and_redacts` |
| **AC-REL-27** The About page shows the Troubleshooting section with all buttons under every theme. Copy and Export work. | 27 | smoke S1 (navigates About) plus manual M5 |
| **AC-REL-28** `Save` writes `"version": CurrentVersion` even when the loaded file was newer. | 29, 32 | unit `SettingsMigrationTests.Save_always_writes_current_version` |
| **AC-REL-29** The migrator runs injected steps 1→2→3 in order on a v1 file. A file without `version` is treated as v1. A gapped or duplicate chain throws `ArgumentException`. The shipped `SettingsMigrations.All` validates against `AppSettings.CurrentVersion`. | 30 | unit `SettingsMigrationTests.Runs_steps_in_order`, `.Missing_version_means_v1`, `.Invalid_chain_throws`, `.Shipped_registry_is_consistent` |
| **AC-REL-30** Migrating a v1 file to v3 leaves a byte-identical `settings.json.v1.bak`. | 31 | unit `SettingsMigrationTests.Backs_up_before_migrating` |
| **AC-REL-31** Loading a v99 file returns `NewerVersion`, keeps known values and writes `settings.json.v99.bak`. | 32 | unit `SettingsMigrationTests.Newer_version_loads_best_effort_with_backup` |
| **AC-REL-32** After one good load, corrupting `settings.json` and loading again returns `RecoveredFromBackup` with the earlier values, plus a `.broken` copy. With no `.bak`, it returns `ResetCorrupt` with defaults. The existing `Corrupt_file_falls_back_to_defaults` still passes. | 33, 34 | unit `SettingsMigrationTests.Corrupt_file_recovers_from_last_known_good`, `.Corrupt_without_backup_resets`, existing test |
| **AC-REL-33** `"capture": null` and `"mutedPlayers": null` load as default instances. | 35 | unit `SettingsMigrationTests.Null_sections_are_repaired` |
| **AC-REL-34** A protector that throws for one secret logs a Warning naming that property, without the ciphertext. | 37 | unit `SettingsMigrationTests.Undecryptable_secret_is_logged_by_name` |
| **AC-REL-35** `ResetToDefaults` creates a `settings.json.reset-*.bak`, and the next load returns `Defaults`. | 38 | unit `SettingsMigrationTests.Reset_keeps_a_backup` |
| **AC-REL-36** `SafeModeDecision.Decide` follows the policy table (flag > shift > unfinished starts ≥ 2 > null). | 39, 42 | unit theory `DiagnosticsTests.Safe_mode_decision` |
| **AC-REL-37** In safe mode, `MainWindow` builds under every page with the banner visible, and no `OverlayWindow` is created. | 40, 41 | smoke S2 |
| **AC-REL-38** Launching `Envoy.exe --safe-mode` shows the banner, no overlay, and hotkeys do nothing. "Restart normally" relaunches with the overlay and hotkeys. Holding Shift while launching does the same. | 39, 40, 41 | manual steps M6, M7 |
| **AC-REL-39** `StartupSentinel` returns 0, then 1, then 2 across three `BeginStartup` calls without `MarkHealthy`, and 0 after `MarkHealthy`. | 42 | unit `DiagnosticsTests.Startup_sentinel_counts_unfinished_starts` |
| **AC-REL-40** CI smoke log contains `toast: ok`, `safe mode: ok`, `diagnostics bundle: ok`, `unobserved handler: ok` and a `--- envoy.log tail ---` block. | 43 | Windows CI smoke step |
| **AC-REL-41** `dotnet test tests/Envoy.Core.Tests` and `tools/typecheck/typecheck.sh` pass. | all | CI `check` job |

---

## Test plan

### Unit tests (`tests/Envoy.Core.Tests`, xUnit, `FakeTimeProvider` from the already-referenced `Microsoft.Extensions.TimeProvider.Testing`)

**`LoggingTests.cs` (new)**

| Test | Asserts |
| --- | --- |
| `Hub_filters_below_minimum_level` | Debug is dropped at Info and kept at Debug, via a `MemoryLogSink`. |
| `Throwing_sink_is_isolated` | Sink A throws and sink B still receives the entry. `DroppedWrites == 1`. No exception. |
| `Redacts_secrets_everywhere` (Theory, 6 cases) | Registered secret, `sk-ant-…`, `AKIA…`, `uuid:fx`, `https://u:p@h/x?q=hello&key=abc`. For each, the message, a field and the exception message all contain no original value and contain `[redacted]`. A plain GUID is kept. |
| `Short_values_are_not_registered_as_secrets` | `SetSecrets(["abc"])` does not redact "abc" in "abcdef". This avoids gutting logs. |
| `Sensitive_fields_masked_unless_opted_in` | `<4 chars>` versus `"你好世界"`. Newlines in the value are escaped when opted in. |
| `User_profile_path_is_aliased` | `C:\Users\Bob\AppData\x` → `%USERPROFILE%\AppData\x`, case-insensitive. |
| `Repeats_are_collapsed` | Covered above. Also, a different category is not collapsed. |
| `Memory_sink_is_a_ring` | Covered above. |
| `Formatter_writes_one_line_per_entry_plus_indented_exception` | Format shape, UTC `Z` timestamp, level abbreviations `DBG/INF/WRN/ERR/CRT`. |
| `Forget_logs_faults_and_cancellation` | Covered above. Awaits completion through a `TaskCompletionSource`-backed sink signal. |

**`RollingFileSinkTests.cs` (new)**

| Test | Asserts |
| --- | --- |
| `Rolls_over_and_keeps_max_files_without_touching_other_files` | AC-REL-2. |
| `Appends_across_instances` | Two sinks in sequence on one folder; both lines are present. |
| `Oversized_existing_file_rolls_on_first_write` | An existing file larger than the cap rolls on the first write. |
| `Unwritable_target_never_throws` | AC-REL-3. |
| `ExistingFiles_lists_newest_first` | Files are listed newest first. |

**`DiagnosticsTests.cs` (new)**

| Test | Asserts |
| --- | --- |
| `Crash_loop_guard_trips_only_inside_window` | AC-REL-15. |
| `Throttle_dedupes_within_window` | AC-REL-20. |
| `Crash_marker_round_trips_and_redacts` | AC-REL-13. Includes a corrupt-file case. |
| `Safe_mode_decision` (Theory) | Flag, Shift, sentinel threshold, precedence, null. |
| `Startup_sentinel_counts_unfinished_starts` | AC-REL-39. Uses a temp file path. |
| `Provider_health_tracks_counts_and_redacts` | AC-REL-26. |
| `Report_has_all_sections_and_no_secrets` | Settings with Claude key `sk-ant-TESTSECRET123`, DeepL key, player name `ZZPLAYERZZ` and muted `MUTEDZZ`. Asserts that the section headers (`Envoy`, `Windows`, `Monitors`, `Capture`, `OCR`, `Translation`, `Hotkeys`, `Settings`, `Recent log`) are present and that none of those values appears. |
| `Report_is_capped_by_dropping_old_log_lines` | Covered above. |
| `Bundle_contains_only_expected_entries` | Writes to a `MemoryStream`, reopens it as a `ZipArchive` and asserts the entry names. `settings.redacted.json` is parsed and contains no secrets. |

**`SettingsMigrationTests.cs` (new)**

| Test | Asserts |
| --- | --- |
| `Missing_version_means_v1` | Covered above. |
| `Runs_steps_in_order` | Test migrations append to a `trace` array in the JSON. |
| `Invalid_chain_throws` | Gap and duplicate cases. |
| `Shipped_registry_is_consistent` | `new SettingsMigrator(SettingsMigrations.All)` does not throw. |
| `Backs_up_before_migrating` | Covered above. |
| `Newer_version_loads_best_effort_with_backup` | Covered above. |
| `Save_always_writes_current_version` | Covered above. |
| `Corrupt_file_recovers_from_last_known_good` | Covered above. |
| `Corrupt_without_backup_resets` | Covered above. |
| `Successful_load_refreshes_last_known_good` | `.bak` content equals the loaded file. |
| `Migration_failure_returns_MigrationFailed_and_keeps_backup` | A migration that throws returns `MigrationFailed` and the backup is kept. |
| `Null_sections_are_repaired` | Covered above. |
| `Undecryptable_secret_is_logged_by_name` | Covered above. |
| `Reset_keeps_a_backup` | Covered above. |

**`SettingsRedactionTests.cs` (new)**

| Test | Asserts |
| --- | --- |
| `Every_secret_and_personal_property_is_masked` | Reflection fills every `[Secret]`/`[Personal]` string with `"VAL_" + name` and every list with `["VAL_"+name]`, then asserts none of them appears in `ToRedactedJson`. |
| `SecretValues_returns_only_non_empty_secrets` | Only non-empty secrets are returned. |
| `HasSecret_reports_set_and_unset` | Set and unset keys are reported correctly. |

**Existing tests:** `SettingsStoreTests.Round_trips_settings_and_encrypts_secrets_on_disk` and
`Corrupt_file_falls_back_to_defaults` must pass unchanged.

### Smoke-test additions (`src/Envoy.App/SmokeTest.cs`, shared file)

- **S1** In the theme loop, after `LookupWindow`:
  `var toast = new ToastWindow(new ProblemReport(LogLevel.Error, "Smoke test", "Sample error for the screenshot.", "smoke")); toast.Show(); await Pump();`
  With `--screenshots`, save `{theme}-toast.png` (with `Backdrop()`). Then `toast.Close();`. After the loop, log
  `toast: ok`. The existing `main.PageNames` loop already navigates About, which builds the Troubleshooting section.
- **S2** After the theme loop:
  1. `var safeHost = new AppHost(otherTempSettingsPath, app.Dispatcher, new AppHostOptions { SafeMode = SafeModeReason.CommandLine, Log = log });`
  2. `new MainWindow(safeHost)`: show it, navigate all pages, assert `FindName("SafeModeBanner")` is `Visible`, then
     close it;
  3. `safeHost.Dispose();`
  4. log `safe mode: ok`.
- **S3**
  1. `var text = DiagnosticsCollector.Collect(host)` → `host.BuildDiagnosticsText(...)`;
  2. assert it contains `"## Envoy diagnostics"`;
  3. `DiagnosticsCollector.ExportBundle(host, Path.Combine(DataFolder,"diag.zip"), file.ExistingFiles())`;
  4. open the zip and assert it has `report.txt`;
  5. log `diagnostics bundle: ok ({text.Length} chars)`.
- **S4** `_ = Task.Run(() => throw new InvalidOperationException("smoke unobserved"));`, wait 200 ms,
  `GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();`, then assert that the memory sink has a Warning with
  `Unobserved task exception`. Log `unobserved handler: ok`. Because GC timing can vary, retry 3×. If it is still
  missing, log `unobserved handler: SKIPPED (GC did not finalize)` without failing, and report that in review.
- **S5** At the end, append `--- envoy.log tail ---` and the last 50 lines of `envoy.log` from `SmokeTest.DataFolder`.
  Assert that line 1 matches the header rule (AC-REL-9).
- `Run` gains parameters `(App app, string[] args, LogHub log, MemoryLogSink recent)`, and `SmokeTest.DataFolder`
  becomes a static temp path that `App.OnStartup` uses.

### Static checks (run locally and in the CI `check` job by the integrator or quality-security-ci)

- **T1:** `! grep -n "MessageBox.Show" src/Envoy.App/App.xaml.cs src/Envoy.App/Windows/ComposeWindow.xaml.cs`
  (AC-REL-19). This can be a 3-line step in `typecheck.sh` or the workflow. If neither owner takes it, it is a
  manual review item.
- **T2:** `tools/typecheck/typecheck.sh`, which includes `check_resources.py`. Every `DynamicResource` key in
  `ToastWindow.xaml` must exist in every palette.

### Manual checks (Windows, real game in borderless mode)

- **M1** (DEBUG build) About → press Ctrl+Alt+Shift+F12 while a match runs in borderless mode. A toast appears
  bottom-right on the game's monitor. The game keeps keyboard focus (WASD and hotkeys keep working). The toast
  disappears after 10 s. "Details" opens About → Troubleshooting.
  - **M1b:** turn "Show a pop-up…" off, repeat, and confirm there is no toast and the entry appears in Recent problems.
- **M2** (DEBUG) Tray → "Throw test error" shows a toast and no WinForms dialog.
- **M3** Turn on Detailed logging, open `envoy.log` in Notepad while Envoy runs (the file is shared for reading), and
  see `DBG` lines. Turn it off and the `DBG` lines stop.
- **M4** Kill the process with an unhandled background exception (DEBUG item "Crash (background thread)"). Relaunch:
  a "closed unexpectedly" toast appears and `last-crash.json` exists. Dismiss deletes it.
- **M5** Configure a DeepL key and player name, then Copy diagnostics and paste into Notepad: sections present, `DeepL:
  key set`, the key and name absent. Export creates a zip on the Desktop and Explorer selects it. Contents match
  AC-REL-24.
- **M6** `Envoy.exe --safe-mode` shows the banner on every page, no overlay, and `Ctrl+Shift+T` does nothing. Tray
  tooltip reads "Envoy (safe mode)". "Restart normally" brings back the overlay and hotkeys.
- **M7** Hold Shift while launching from the Start menu: Envoy starts in safe mode.
- **M8** Corrupt `settings.json` by hand (delete a brace) and launch. The previous settings, including the decrypted
  API key, are restored from `.bak`, a toast reports the recovery, and `settings.json.broken` exists.
- **M9** Set the logs folder read-only and use Envoy for a minute. There are no errors or crashes, and diagnostics
  show `Dropped log writes: N`.

The DEBUG-only triggers (`#if DEBUG`) live in `GlobalExceptionHandlers.DebugTriggers` and are not compiled into
release builds.

---

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Diagnostics/Logging.cs` | new: `LogLevel`, `LogField`, `LogEntry`, `ILog`, `ILogSink`, `LogExtensions`, `NullLog`, `LogHub` | |
| `src/Envoy.Core/Diagnostics/LogRedactor.cs` | new | |
| `src/Envoy.Core/Diagnostics/RepeatSuppressor.cs` | new (internal) | |
| `src/Envoy.Core/Diagnostics/LogFormatter.cs` | new | |
| `src/Envoy.Core/Diagnostics/RollingFileSink.cs` | new | |
| `src/Envoy.Core/Diagnostics/MemoryLogSink.cs` | new | |
| `src/Envoy.Core/Diagnostics/TaskExtensions.cs` | new: `Forget` | |
| `src/Envoy.Core/Diagnostics/ErrorPolicy.cs` | new: `CrashLoopGuard`, `ProblemReport`, `NotificationThrottle`, `CrashMarker` | |
| `src/Envoy.Core/Diagnostics/SafeMode.cs` | new: `SafeModeReason`, `SafeModeDecision`, `StartupSentinel` | |
| `src/Envoy.Core/Diagnostics/ProviderHealthTracker.cs` | new | |
| `src/Envoy.Core/Diagnostics/DiagnosticsReport.cs` | new: `MonitorInfo`, `DiagnosticsSnapshot`, `DiagnosticsReport`, `DiagnosticsBundle` | |
| `src/Envoy.Core/Settings/AppSettings.cs` | add `CurrentVersion`, `PersonalAttribute`, `[Personal]` on `MyPlayerName`/`MutedPlayers`, `Diagnostics` property, `DiagnosticsSettings` class | **SHARED: AppSettings.cs** |
| `src/Envoy.Core/Settings/SettingsStore.cs` | `LoadWithReport`, migration, `.bak` / `.v{N}.bak` / `.broken`, recovery, `Flush(true)`, `ResetToDefaults`, logging; old ctor and `Load()` kept | |
| `src/Envoy.Core/Settings/SettingsMigration.cs` | new: `ISettingsMigration`, `SettingsMigrator`, `SettingsMigrations` | |
| `src/Envoy.Core/Settings/SettingsSanitizer.cs` | new | |
| `src/Envoy.Core/Settings/SettingsRedaction.cs` | new | |
| `src/Envoy.App/Services/LogBootstrap.cs` | new | |
| `src/Envoy.App/Services/GlobalExceptionHandlers.cs` | new (plus `#if DEBUG` triggers) | |
| `src/Envoy.App/Services/ToastService.cs` | new | |
| `src/Envoy.App/Services/DiagnosticsCollector.cs` | new | |
| `src/Envoy.App/Windows/ToastWindow.xaml` + `.xaml.cs` | new window | |
| `src/Envoy.App/Interop/Native.cs` | add `GetAsyncKeyState`/`IsShiftDown`, `MakeNoActivate`, `GetDpiForMonitor`, `MonitorFromPoint` | possibly shared with capture-engine |
| `src/Envoy.App/App.xaml.cs` | bootstrap order, handlers (delete `OnUnhandledException` and `error.log`), safe mode, toasts, `Restart`, sentinel, tray safe-mode text, `HotkeyFailures` | **SHARED: App.xaml.cs** |
| `src/Envoy.App/AppHost.cs` | `AppHostOptions` ctor overload; `LogHub`/`RecentLog`/`SafeMode`/`SettingsLoad`/`ProviderHealth`/`CrashMarkerPath`/`HotkeyFailures`; `ProblemReported`; logging in catches; `Forget`; safe-mode capture gate; `ResetSettings`; `BuildDiagnosticsText` | **SHARED: AppHost.cs** |
| `src/Envoy.App/SmokeTest.cs` | `DataFolder`, extended `Run` signature, S1–S5 | **SHARED: SmokeTest.cs** |
| `src/Envoy.App/Windows/MainWindow.xaml` | safe-mode banner (`x:Name="SafeModeBanner"`), About → Troubleshooting frame (`x:Name="TroubleshootingFrame"`) | **SHARED: MainWindow*** |
| `src/Envoy.App/Windows/MainWindow.xaml.cs` | `OnCopyDiagnostics`, `OnExportDiagnostics`, `OnRestartSafeMode`/`OnRestartNormal`, `OnResetSettings`, `OnDismissCrash`, `ScrollToTroubleshooting` | **SHARED: MainWindow*** |
| `src/Envoy.App/ViewModels/MainViewModel.cs` | `IsSafeMode`, `SafeModeReasonText`, `RecentProblems`, `LastCrashText`, `DiagnosticsStatus`; refresh in `RefreshStatus` | shared with ui-foundation / onboarding |
| `src/Envoy.App/Windows/ComposeWindow.xaml.cs` | `MessageBox` → `ReportProblem` + log | possibly shared with onboarding-ux-accessibility |
| `tests/Envoy.Core.Tests/LoggingTests.cs` | new | |
| `tests/Envoy.Core.Tests/RollingFileSinkTests.cs` | new | |
| `tests/Envoy.Core.Tests/DiagnosticsTests.cs` | new | |
| `tests/Envoy.Core.Tests/SettingsMigrationTests.cs` | new | |
| `tests/Envoy.Core.Tests/SettingsRedactionTests.cs` | new | |
| `docs/troubleshooting.md` | replace the `error.log` advice with "About → Troubleshooting → Copy diagnostics / Export"; add a Safe mode section; add a "Settings were reset/recovered" section | |
| `docs/architecture.md` | add a `Diagnostics/` row to the Core table; update the "Fail soft" rule (errors are logged and toasted); add `ToastWindow` to the Windows row | |
| `CHANGELOG.md` | Unreleased → Added: diagnostics, safe mode, settings recovery | |
| `CLAUDE.md` | "Settings changes" rule: renames or restructures need an `ISettingsMigration` + `CurrentVersion` bump; use `ILog`, never log chat text without `LogField.Sensitive` | |
| `.github/workflows/build.yml` | *(optional, quality-security-ci owns it)* the smoke step's `error.log` print becomes dead; the smoke log now includes the log tail | flag for quality-security-ci |

**Estimated size:** Core about 650 lines, App about 450 lines (including XAML), tests about 550 lines. The MUST-only
subset (no `StartupSentinel`, Recent problems list, repeat suppression, reset button or sanitizer) is about 1,300
lines.

---

## Risks & open questions

1. **Toast overlap with ui-foundation or onboarding.** These areas may also want a notification surface. This spec
   defines `ToastWindow`/`ToastService` with a neutral `ProblemReport` input, so it can become the shared mechanism.
   The integrator should pick one owner. If ui-foundation adds a generic toast, `ToastService` keeps only the
   throttling and no-activate logic.
2. **Toasts over the game.** `WS_EX_NOACTIVATE` toasts do not steal focus in borderless mode. In exclusive
   full-screen they are invisible, as the overlay already is, and Recent problems is the fallback. We do not know
   whether a topmost window over a borderless game causes a brief frame-pacing hitch on every machine. The toast is
   small and short-lived, and the "Show a pop-up" opt-out exists.
3. **Redaction completeness.** Pattern-based redaction cannot catch every possible secret. Registering the actual
   secret values is the primary defence. Exception messages from providers might include echoed chat text (for
   example, a provider's 400 body). The translation-resilience area should not put source text into
   `TranslationException.Message`. This spec relies on that and lists it as a cross-area expectation.
4. **Logs folder shared with transcripts.** This spec keeps the requested `%APPDATA%\Envoy\logs` and uses a strict
   `envoy(.N).log` file-name rule. The "Open logs" button therefore shows both. Alternative: `logs\diagnostics\`. This
   needs the integrator's or user's preference. The change is one constant in `LogBootstrap`.
5. **`AppSettings.CurrentVersion` stays 1.** The framework ships with an empty registry, tested with injected
   migrations. If another area (for example capture-engine moving `Regions` to per-monitor) needs a breaking change,
   that area adds migration 1→2. The integrator must make sure only one area claims each version number.
6. **Shift detection.** `GetAsyncKeyState` at `OnStartup` is reliable for a held key. Users launching from the Start
   menu may release Shift before the check runs, after about 300–800 ms of .NET single-file start-up. This is
   documented: "hold Shift until the Envoy window appears". `--safe-mode` is the dependable path, and a "Envoy (safe
   mode)" Start-menu shortcut could be added by distribution-updates.
7. **`UnobservedTaskException` in the smoke test is GC-timing dependent**, so S4 degrades to SKIPPED rather than
   failing CI. Alternative: a unit test of the handler method itself. The handler lives in App, so it cannot be tested
   in Core.
8. **Restart and the single-instance mutex.** Releasing the mutex before `Process.Start` briefly allows a third
   instance to start during the restart window. This is acceptable.
9. **Settings `.bak` contains DPAPI-encrypted secrets**, the same as `settings.json`. It is never included in bundles.
   "Reset settings" keeps a backup that still holds the encrypted keys. The confirmation text should say this.
10. **Open:** should the diagnostics report include the capture region **position** and the game window rectangle? They
    help with DPI and multi-monitor bugs but slightly fingerprint a setup. This spec includes only size plus monitor
    layout.
11. **Open:** do we want an opt-in "Open a pre-filled GitHub issue" button (a URL with the report in the body)? It is
    left out to keep the no-network promise simple. The user pastes the report themselves.
