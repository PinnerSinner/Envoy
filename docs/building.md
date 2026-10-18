# Building from source

## On Windows

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows 10 2004 (19041) or later.
Visual Studio 2022 or Rider is optional.

```powershell
git clone <this repo>
cd envoy
dotnet test tests/Envoy.Core.Tests            # unit tests
dotnet test tests/Envoy.App.Tests -c Debug    # OCR end-to-end and hotkey tests (Windows only)
dotnet run --project src/Envoy.App            # run the app (Debug)
dotnet publish src/Envoy.App -c Release -o publish
# → publish\Envoy.exe  (self-contained single file, ~75 MB, runs on any Windows 10/11 x64)
```

For a small exe that needs the .NET 8 Desktop Runtime installed:

```powershell
dotnet publish src/Envoy.App -c Release -o publish-fd -p:SelfContained=false
```

To build the installer the way CI does (Velopack's `vpk`, pinned in `.config/dotnet-tools.json`):

```powershell
dotnet tool restore
dotnet publish src/Envoy.App -c Release -o publish-velo -p:PublishSingleFile=false -p:InformationalVersion=0.1.0-local.1
dotnet vpk pack --packId Envoy --packVersion 0.1.0-local.1 --packDir publish-velo --mainExe Envoy.exe --packTitle Envoy `
  --channel win --runtime win-x64 --noPortable --shortcuts StartMenuRoot -o Releases
# → Releases\Envoy-win-Setup.exe plus the update packages
```

To try an update without a release, pack two versions into one folder, install the first, and start it with
`ENVOY_UPDATE_FEED` set to that folder (a local folder only; anything else is ignored).

To run the UI smoke test locally:

```powershell
publish\Envoy.exe --smoke-test smoke.log; Get-Content smoke.log
```

It opens every settings page and window under every palette, checks commands, auto-apply and the live-view lease, and
fails on any WPF binding error. Each section logs `[smoke] <section> ok elapsed_ms=<n>`. Add
`--screenshots <folder>` to save a PNG of every page and window.

## On Linux or macOS

The core library and its tests are cross-platform:

```bash
dotnet test tests/Envoy.Core.Tests
```

The WPF app can't be *built* off Windows, because the XAML compiler ships only with the Windows SDK. You can still
**type-check** it:

```bash
tools/typecheck/typecheck.sh
```

This first runs the tool's own Python tests, then generates stand-ins for the XAML-generated code and compiles the
app's C# against the Windows reference assemblies with warnings as errors. The stand-ins carry each XAML root's base
class and a `nameof` for every event handler the XAML names, so a `<UserControl>` paired with `: Window`, or a
`Click="OnFoo"` with no `OnFoo`, fails here rather than on Windows. It also checks that every
`StaticResource`/`DynamicResource` key exists, that all palettes define the same keys, and the UI-structure rules in
`check_ui_rules.py` (no handlers or `DataContext` in `Pages/`, no `MessageBox` outside `DialogService`, and so on), and
the accessibility rules in `check_a11y.py` (a focus visual on every focusable style, an accessible name on every
interactive control, Tab cycling in the setup guide and reply box, no literal colours in windows, pages and controls).
CI runs the real Windows build and smoke test on every push.

## Analyzers and the policy check

The root `Directory.Build.props` applies to every project (including the type-check): `Nullable`, `ImplicitUsings`,
`LangVersion`, `Deterministic`, `AnalysisLevel=8.0-recommended` and code-style enforcement, plus
`Microsoft.CodeAnalysis.BannedApiAnalyzers` with the root `BannedSymbols.txt`. The analyzer rules live in
`.editorconfig` (`tests/.editorconfig` and `src/Envoy.App/.editorconfig` relax a few for tests and WPF).

- **Warnings:** nullable warnings are always errors in `Envoy.Core` and its tests. GitHub Actions sets `CI=true`,
  which makes every warning an error (except in the real WPF build, where the Linux type-check is the gate). Reproduce
  CI locally with:

  ```bash
  CI=true dotnet test tests/Envoy.Core.Tests -c Release
  CI=true tools/typecheck/typecheck.sh
  ```

- **Banned APIs** (`RS0030`, always an error): `Process.Start`/`ProcessStartInfo` (only
  `src/Envoy.App/Security/ShellLauncher.cs` may start processes), TLS-validation bypasses, `WebClient`/`WebRequest`,
  `BinaryFormatter`, `XamlReader`, `Process.MainModule`/`Modules`.
- **Policy check:** `python3 tools/ci/check_policy.py` (and its tests,
  `python3 -m unittest discover -s tools/ci -p "test_*.py"`) checks P/Invokes against `tools/ci/policy.json`, denied
  native calls, where network code and URL hosts may appear, the `asInvoker` manifest, process launches, `#pragma`s
  and `[SuppressMessage]` justifications, and the build settings. It prints `path:line: rule: message` per problem.
  A new P/Invoke or host goes into `policy.json` in the same change.
- **.NET tools** are pinned in `.config/dotnet-tools.json` and run after `dotnet tool restore`; never
  `dotnet tool install -g`.

## Coverage

The `check` job measures `Envoy.Core`'s coverage with `coverlet.collector` (settings in `tests/coverage.runsettings`:
Cobertura, `[Envoy.Core]*` only, `[GeneratedCode]`/`[ExcludeFromCodeCoverage]` excluded) and fails below **70 % of
lines or 55 % of branches** (contract §9). To see what CI sees:

```bash
dotnet test tests/Envoy.Core.Tests -c Release --collect "XPlat Code Coverage" \
  --settings tests/coverage.runsettings --results-directory TestResults
python3 tools/ci/coverage_summary.py --package Envoy.Core --min-line 70 --min-branch 55 TestResults/*/coverage.cobertura.xml
```

The summary prints a total and one row per top-level Core folder (CI adds it to the job summary and keeps the XML as
the `coverage` artifact). The thresholds never go down. Ratchet: a pull request that lifts coverage 3 or more points
above a threshold raises that threshold in `build.yml` to just below the new value. The 1.0 target is 80 % / 65 %.
`[ExcludeFromCodeCoverage]` is allowed only on thin SDK adapters, with a comment saying why.

## App tests (Windows)

`tests/Envoy.App.Tests` holds only tests that need WPF or Windows types: the OCR end-to-end harness and the `Hotkey`
parser. It runs in Debug only (in Release `Envoy.App` is self-contained, which a test project cannot reference):

```powershell
dotnet test tests/Envoy.App.Tests -c Debug
```

The OCR end-to-end cases (`src/Envoy.App/OcrEndToEnd.cs`) render known chat lines at 14 to 18 px with an outline or a
shadow, run them through the capture path's enlargement and enhancement, the real Windows OCR engine, line merging and
chat understanding, and require the right sender and at least 0.85 text similarity. The six en-US cases are required;
the ja-JP, zh-CN, ko-KR and ru-RU cases report `Skipped (no recognizer)` when that OCR language is not installed. The
same cases run inside the published exe with `Envoy.exe --ocr-e2e report.md`. On Linux, `tools/typecheck/typecheck.sh`
compiles the test project's sources against the type-checked app.

## CI

`.github/workflows/build.yml` runs on every push and pull request:

| Job | Runner | What it does | Budget |
| --- | --- | --- | --- |
| `check` | Ubuntu | Policy check and `tools/ci` tests, release-tooling self-tests, Core tests with the coverage gate, the type-check, the vulnerable-package gate (`tools/ci/check_vulnerable.py`: High or Critical advisories fail) | 8 min |
| `app-tests` | Windows | `tests/Envoy.App.Tests` (OCR end-to-end, hotkeys); installs the ja/zh/ko/ru OCR capabilities first when it can | none |
| `windows` | Windows | Publish, smoke test (`smoke-log` artifact), screenshots, the CycloneDX SBOM `Envoy.cdx.json` (`tools/ci/check_sbom.py`), the unsigned dev build, and on `main` an unsigned installer | 20 min |
| `screenshots` | Ubuntu | Pushes the renders to the `ci-screenshots` branch (pushes to `main`) | none |
| `release` | Windows | `v*` tags only, in the `release` environment: signing, packing, attestation and publishing ([releasing](releasing.md)) | none |

Every job starts read-only; only `screenshots` and `release` get `contents: write`, and they build and run no project
code. Each budgeted job ends with `tools/ci/job_budget.py`, which fails the job when it ran over and lists the five
slowest smoke-test sections. Third-party actions are pinned to commit SHAs (with the tag in a comment) and Dependabot
keeps them current. `codeql.yml` (C# and the workflows, `security-extended`) and `dependency-review.yml` (pull
requests) run separately. The policy check enforces the workflow rules (`workflow-permissions`, `workflow-pins`,
`workflow-timeouts`) and the release documents (`required-docs`, `threat-model-refs`).

## Project settings worth knowing

- `Envoy.App.csproj` turns on `PublishSingleFile`, `SelfContained` and compression for Release builds, and embeds
  debug symbols.
- `app.manifest` declares Per-Monitor-V2 DPI awareness (capture regions are in physical pixels) and `asInvoker`
  (never asks for admin rights).
- `AllowUnsafeBlocks` is required by the `LibraryImport` P/Invoke source generator in `Interop/Native.cs`.
- WPF projects drop `System.IO` and `System.Net.Http` from implicit usings; `GlobalUsings.cs` adds them back.
