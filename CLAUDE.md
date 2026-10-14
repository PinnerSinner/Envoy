# Envoy — notes for AI coding sessions

Live chat translator for the Age of Empires / Age of Mythology games. Windows WPF app + cross-platform core.

## Commands
- `dotnet test tests/Envoy.Core.Tests` — unit tests (any OS). Must stay green.
- `tools/typecheck/typecheck.sh` — type-checks `src/Envoy.App` on Linux (no WindowsDesktop SDK there) and validates
  XAML resource keys. Run it after every app change; it mirrors the real project's compiler settings
  (keep `tools/typecheck/TypeCheck.csproj` in sync with `src/Envoy.App/Envoy.App.csproj`).
- The real WPF build + `Envoy.exe --smoke-test` only run on Windows CI (`.github/workflows/build.yml`).

## Layout
- `src/Envoy.Core` — all logic: chat parsing, OCR merging, pipeline, providers, settings. Put new logic here with tests.
- `src/Envoy.App` — WPF shell: `AppHost` (composition root), `Services/` (capture, OCR, hotkeys, SendInput),
  `Windows/`, `Themes/` (palettes + `Controls.xaml`).
- `docs/` — user and developer docs. Update them with user-facing changes, plus `CHANGELOG.md`.

## Rules
- Never read/write game memory, inject or hook. Screen pixels in, keystrokes out — that's the anti-cheat promise.
- UI colours only via `DynamicResource` palette keys; every `Palette.*.xaml` defines the same keys.
- New windows must be added to `SmokeTest.cs` so CI exercises them under every theme.
- Settings changes: add the property to `AppSettings`, bind it in `MainWindow.xaml` (edits auto-apply), and pass it
  through `AppHost.ApplySettings()` if a service needs it.
