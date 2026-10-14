# Envoy — notes for AI coding sessions

Live chat translator for the Age of Empires / Age of Mythology games. Windows WPF app + cross-platform core.

## Commands
- `dotnet test tests/Envoy.Core.Tests` — unit tests (any OS). Must stay green.
- `tools/typecheck/typecheck.sh` — type-checks `src/Envoy.App` on Linux (no WindowsDesktop SDK there): XAML base
  classes and event-handler names, resource keys, and the UI rules in `check_ui_rules.py` (after running its own
  Python tests). Run it after every app change; it mirrors the real project's compiler settings
  (keep `tools/typecheck/TypeCheck.csproj` in sync with `src/Envoy.App/Envoy.App.csproj`).
- `python3 tools/ci/check_policy.py` (+ `python3 -m unittest discover -s tools/ci -p "test_*.py"`) — the policy
  check CI runs first: P/Invoke allowlist and denied names, network code and URL hosts, manifest, process launches,
  `#pragma`s, suppressions and build settings (`tools/ci/policy.json`).
- CI sets `CI=true`, which turns every warning into an error. Run `CI=true dotnet test tests/Envoy.Core.Tests -c Release`
  and `CI=true tools/typecheck/typecheck.sh` locally before pushing.
- The real WPF build + `Envoy.exe --smoke-test` only run on Windows CI (`.github/workflows/build.yml`).

## Layout
- `src/Envoy.Core` — all logic: chat parsing, OCR merging, pipeline, providers, settings, diagnostics (logging,
  safe mode, data inventory) and notifications. Put new logic here with tests.
- `src/Envoy.App` — WPF shell: `AppHost` (composition root), `IShell` (app actions for view models),
  `Security/ShellLauncher.cs` (the only process launcher), `Services/`
  (capture, OCR, hotkeys, SendInput, `DialogService`), `Windows/`, `Pages/` (one `UserControl` per settings page,
  ids in `PageIds`), `ViewModels/` (`MainViewModel` partials, `CapturePreviewViewModel`), `Themes/` (palettes +
  `Controls.xaml`).
- `tools/ci/` — `check_policy.py` + `policy.json` (allowlists); `tools/typecheck/` — the Linux type-check.
- `docs/` — user and developer docs. Update them with user-facing changes, plus `CHANGELOG.md`.

## Rules
- Never read/write game memory, inject or hook. Screen pixels in, keystrokes out — that's the anti-cheat promise.
- UI colours only via `DynamicResource` palette keys; every `Palette.*.xaml` defines the same keys.
- New windows must be added to `SmokeTest.cs` in the per-palette phase so CI exercises them under every palette; new
  settings pages are added to `PageIds.All` (and get a `MainWindow` host and `PageFor` case).
- Settings changes: add the property to `AppSettings`, bind it on the relevant page in `src/Envoy.App/Pages/` (edits
  auto-apply; a non-setting input sets `c:AutoApply.Ignore="True"`), and pass it through `AppHost.ApplySettings()` if
  a service needs it.
- Pages never set `DataContext` and have no event handlers: buttons and links bind to `MainViewModel` commands
  (`Envoy.Core.Mvvm`; `AsyncRelayCommand` always gets `ReportCommandError`). View models reach the app only through
  `IShell`; every `MessageBox` goes through `Services/DialogService.cs`. `check_ui_rules.py` enforces this.
- Every new interactive control gets `AutomationProperties.Name`.
- User-facing text built in code lives in a Core `public static class *Text` with whole-sentence templates and named
  placeholders (`{count}`, never `{0}`), filled only by `TextTemplate.Format`. XAML labels stay inline.
- Only `src/Envoy.App/Security/ShellLauncher.cs` starts processes (`TryOpen`, `InstallOcrPack`, `RestartSelf`,
  `RevealFile`, each validated by Core `LaunchTarget`); `Process.Start`/`ProcessStartInfo` are banned elsewhere (RS0030).
  Text typed or pasted into another window goes through `OutgoingText.Sanitize`.
- Allowlists change in the same PR as the code: a new P/Invoke goes into `Interop/Native.cs` **and**
  `tools/ci/policy.json` → `native.allowed`; a new literal URL host into `hosts.allowed`. Network types stay in
  `Core/Translation/`, `Core/Updates/` and `AppHost.cs`. Never name denied native calls, even in comments.
- Fix analyzer warnings; don't silence them. No `#pragma warning disable` (the launcher's one RS0030 line is the
  only exception). A `[SuppressMessage]` always has a `Justification = "..."`.
- .NET CLI tools are pinned in `.config/dotnet-tools.json` and run after `dotnet tool restore`, never
  `dotnet tool install -g`.
- New credential settings are `string` properties marked `[Secret]` on a settings class reachable from `AppSettings`
  (not inside a collection); `SettingsSecretTests` enforces it. A new setting holding player names or chat text is
  marked `[Personal]`, one holding screen positions `[Location]` (`SettingsRedactionTests` checks every marker).
- Log through `ILog` (`AppHost.Log.For("Category")`), never `Debug`/`Trace`/`Console`. Chat text and player names go
  only through `LogField.Sensitive(...)`. Every `catch` in `AppHost.cs` logs. Fire-and-forget is
  `task.Forget(log, "what")`, never `_ = SomethingAsync()` (typecheck R9).
- Errors never open a modal box: report them with `AppHost.ReportProblem(ProblemText.…)` (a non-activating toast).
  `DialogService` is only for confirmations the user starts in `MainWindow`.
- All Envoy files live under the instance `AppHost.DataFolder`; only `SettingsStore.DefaultPath` names `%APPDATA%`
  (typecheck R8). A new data file joins `DataInventory.Items` and `docs/privacy.md` in the same change.
- Anything written during `Host.Dispose` or exit checks `Host.SaveSuppressed` first, so a reset or "Delete all Envoy
  data" is never undone. Start-up and exit code goes in its slot of `App.OnStartup`/`ExitApp` (contract §4).
