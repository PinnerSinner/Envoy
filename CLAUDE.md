# Envoy — notes for AI coding sessions

Live chat translator for the Age of Empires / Age of Mythology games. Windows WPF app + cross-platform core.

## Commands
- `dotnet test tests/Envoy.Core.Tests` — unit tests (any OS). Must stay green.
- `tools/typecheck/typecheck.sh` — type-checks `src/Envoy.App` on Linux (no WindowsDesktop SDK there): XAML base
  classes and event-handler names, resource keys, and the UI rules in `check_ui_rules.py` (after running its own
  Python tests). Run it after every app change; it mirrors the real project's compiler settings
  (keep `tools/typecheck/TypeCheck.csproj` in sync with `src/Envoy.App/Envoy.App.csproj`).
- The real WPF build + `Envoy.exe --smoke-test` only run on Windows CI (`.github/workflows/build.yml`).

## Layout
- `src/Envoy.Core` — all logic: chat parsing, OCR merging, pipeline, providers, settings. Put new logic here with tests.
- `src/Envoy.App` — WPF shell: `AppHost` (composition root), `IShell` (app actions for view models), `Services/`
  (capture, OCR, hotkeys, SendInput, `DialogService`), `Windows/`, `Pages/` (one `UserControl` per settings page,
  ids in `PageIds`), `ViewModels/` (`MainViewModel` partials, `CapturePreviewViewModel`), `Themes/` (palettes +
  `Controls.xaml`).
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
