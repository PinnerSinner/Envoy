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
- `tools/release/` (release tooling, run with `--self-test` in the `check` job): `check_velopack_pin.sh` (Velopack in
  `Envoy.App.csproj`, `TypeCheck.csproj` and `vpk` in `.config/dotnet-tools.json` are one version),
  `check_signing_scope.py` (signing secrets only in v* tag steps), `check_release_assets.py`, `changelog_notes.py`,
  `render_winget.py`. Run them after touching the workflow or the pins.

## Layout
- `src/Envoy.Core` — all logic: chat parsing, OCR merging, pipeline, providers, settings, diagnostics (logging,
  safe mode, data inventory), notifications and notices, onboarding (the setup guide's flow), privacy text and page
  hints (`Ux/`). Put new logic here with tests.
- `src/Envoy.App` — WPF shell: `AppHost` (composition root), `IShell` (app actions for view models),
  `Security/ShellLauncher.cs` (the only process launcher), `Services/` (`Capture/` backends, `ChatWatcher`,
  `GameWindowTracker`, OCR, hotkeys, SendInput, `DialogService`, `TrayNotifier`), `Windows/` (incl. the setup guide),
  `Pages/` (one `UserControl` per settings page, ids in `PageIds`), `ViewModels/` (`MainViewModel` partials,
  `SetupWizardViewModel`, `CapturePreviewViewModel`), `Themes/` (palettes + `Controls.xaml`).
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
- Translation providers throw only `TranslationException` with a `TranslationErrorKind` (or caller-requested
  `OperationCanceledException`), with messages from `TranslationErrorText`, never a response body or raw exception
  text. A new provider sends through `Providers/ProviderHttp` (status classification, Retry-After, `Parse` →
  `BadResponse`), turns SDK retries off, gets `ResilienceOptions.For` defaults and contract tests with fixtures under
  `tests/Envoy.Core.Tests/Fixtures/Providers/`. Provider health is read only from `AppHost.TranslationHealth`
  (`ProviderHealthRegistry`); nothing else is named `ProviderHealth`.
- Background capture code reads the `CaptureOptions` snapshot (`AppHost.CaptureOptions`, rebuilt in `ApplySettings`
  and `SetRegion`), never `AppSettings`; `ChatWatcher` passes `CancellationToken.None` to `ProcessAsync` (typecheck
  R11). Game-window probing goes only through `GameWindowTracker` → `AppHost.GameWindow`; capture coordinates are
  physical pixels. Capture/OCR failures become a `CaptureText` sentence via `CaptureErrors.Describe`.
- Chat understanding lives in Core: `ChatCaptureAnalyzer` (pure) → `ChatDeduplicator.AcceptNew` (one lock per capture)
  → filters in `ChatPipeline`, which reads only its immutable `ChatPipelineOptions` snapshot (built by
  `ChatPipelineOptions.From` in `AppHost.ApplySettings`/`RefreshGame`). Every Pending message gets exactly one
  `MessageUpdated` (contract §2.6): translations run under the pipeline's lifetime token, never the caller's. A change
  to parsing, cleaning or stitching adds or updates a golden fixture in `tests/Envoy.Core.Tests/Golden/Captures`
  (format in `docs/chat-formats.md`). Notices are never translated; ship no unverified game notice strings.
- Log through `ILog` (`AppHost.Log.For("Category")`), never `Debug`/`Trace`/`Console`. Chat text and player names go
  only through `LogField.Sensitive(...)`. Every `catch` in `AppHost.cs` logs. Fire-and-forget is
  `task.Forget(log, "what")`, never `_ = SomethingAsync()` (typecheck R9).
- Errors never open a modal box: report them with `AppHost.ReportProblem(ProblemText.…)` (a non-activating toast).
  `DialogService` is only for confirmations the user starts in `MainWindow`.
- All Envoy files live under the instance `AppHost.DataFolder`; only `SettingsStore.DefaultPath` names `%APPDATA%`
  (typecheck R8). A new data file joins `DataInventory.Items` and `docs/privacy.md` in the same change.
- Updates (spec 06): all update state and policy is `AppHost.Updates` (`UpdateCoordinator`); UI binds
  `Host.UpdateView` and runs actions through `Services/UpdateActions.cs`. Update UI text comes from `UpdateText`.
  Only `Services/VelopackUpdateBackend.cs` creates an `UpdateManager`, only `Program.cs` runs `VelopackApp` (auto-apply
  stays off; typecheck UPD1/UPD2). Nothing automatic reaches the network in safe mode or with `CheckForUpdates` off.
  Never write user data under the install folder (`%LocalAppData%\Envoy`); all user data lives under
  `Host.DataFolder`. Signing secrets appear only in v* tag steps (`check_signing_scope.py`).
- Privacy gate (spec 07a, contract §8.1): every translator stack AppHost builds is wrapped in `PrivacyGatedTranslator`
  (outermost); nothing translates around it. Translation is off until `Onboarding.PrivacyAcknowledgedVersion >=
  PrivacyText.Version`; bump `PrivacyText.Version` when the data flows change. A new smoke or test host calls
  `SetPrivacyAcknowledged(true)` right after construction (the main smoke host checks the closed gate first).
- Advisories (OCR pack missing, provider failing, update available) go only through `AppHost.Notices` (`NoticeCenter`
  on the one `NotificationThrottle`, UI thread, null in safe mode), with text from `NoticeText` or
  `UpdateView.Headline`; `TrayNotifier` only displays what it decided. Never show a balloon directly.
- War Room order is fixed (contract §8.6): safe-mode banner, update banner, notice list, hint slot (`WarRoomSlots`),
  content. A page's empty or error state is one `Callout` whose hint comes from Core `PageHints`. The setup guide's view
  model edits only `Host.Settings` through its applier (typecheck R12) and its steps come from `OnboardingFlow`.
- Anything written during `Host.Dispose` or exit checks `Host.SaveSuppressed` first, so a reset or "Delete all Envoy
  data" is never undone. Handlers of background events (pipeline, watcher) are unsubscribed in `AppHost.Dispose` and
  do nothing once the host is disposed or saving is suppressed. Start-up and exit code goes in its slot of `App.OnStartup`/`ExitApp` (contract §4).
