# Quality, security & CI: analyzers, coverage, code scanning, SBOM, a threat model and the code fixes it calls for

Status: Proposed

Area id: `08-quality-security-ci` · Requirement prefix: `QSC` · Depends on nothing hard. It touches the workflow file
that `06-distribution-updates` rewrites, so the two must be merged with care (see Risks). Measurements below were
taken on commit `f540fc4` with the .NET 8.0.131 SDK on Linux.

---

## Context

### What exists today

| Concern | Today | Where |
| --- | --- | --- |
| CI | One workflow. `check` (ubuntu): `dotnet test tests/Envoy.Core.Tests -c Release` and `tools/typecheck/typecheck.sh`. `windows` (needs `check`): publish, `Envoy.exe --smoke-test`, screenshots artifact, push of an orphan `ci-screenshots` branch on `main`, SHA-256, artifact, and on `v*` tags a GitHub Release (`softprops/action-gh-release@v2`). | `.github/workflows/build.yml` |
| Token scope | Workflow-wide `permissions: contents: write`. Every job, including the one that builds and runs code from any pushed branch, holds a write token. | `build.yml` lines 9–10 |
| Action pinning | Actions are pinned by major tag (`@v4`, `@v2`). A moved or compromised tag would run new code with a write token. | `build.yml` |
| Timeouts / concurrency | No `timeout-minutes` (the GitHub default is 360 min). No `concurrency` group, so superseded pushes keep running. No NuGet cache. | `build.yml` |
| Shared build settings | None. Each `.csproj` repeats `Nullable`, `ImplicitUsings` and `LangVersion`. No `Directory.Build.props`. | `src/Envoy.Core/Envoy.Core.csproj`, `src/Envoy.App/Envoy.App.csproj`, `tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj` |
| Analyzers | SDK defaults only (`AnalysisLevel` default, few CA rules on). Warnings are not errors for Core. The typecheck already uses `TreatWarningsAsErrors=true` for the app's C#. | `tools/typecheck/TypeCheck.csproj` |
| Style | `.editorconfig` has formatting rules and `csharp_style_namespace_declarations = file_scoped:warning`, which is not enforced at build time (no `EnforceCodeStyleInBuild`). | `.editorconfig` |
| Tests | 113 xUnit tests in `tests/Envoy.Core.Tests`, all passing. There is no test project for app-level code. | `tests/Envoy.Core.Tests/*.cs` |
| Coverage | Not collected. Measured with `coverlet.collector`: Envoy.Core **74.1 % lines (1311/1770), 60.7 % branches (608/1002)**, including source-generated regex code. The lowest are the HTTP providers (`DeepLTranslator` 29 %, `AwsTranslator` 24 %, `LibreTranslateTranslator` 0 %) and `UpdateChecker.CheckAsync` (0 %). | measurement |
| Dependency hygiene | `dependabot.yml`: monthly NuGet (one group) and GitHub Actions. It does not cover `tools/typecheck/TypeCheck.csproj`, which is outside `Envoy.sln` and pins `System.Security.Cryptography.ProtectedData` separately. No vulnerability gate, no dependency review, no SBOM. | `.github/dependabot.yml` |
| Code scanning | None (no CodeQL). | — |
| Security docs | `SECURITY.md` covers private reporting and what Envoy does with data. There is no threat model, no "supported versions", no guidance on verifying downloads beyond SHA-256 (`docs/releasing.md`), and no guidance on secrets in tests or issues. | `SECURITY.md`, `docs/releasing.md` |
| Release process | `docs/releasing.md`: update CHANGELOG, tag, push. There is no checklist, and no manual in-game verification step before or after a release. | `docs/releasing.md` |
| Review gates | PR template checklist. No CODEOWNERS. Branch protection state is unknown and not documented. | `.github/pull_request_template.md` |

### Security gaps found while reading the code

These are real today and are fixed by this area, because the threat model would otherwise list them as unmitigated.

1. **Keystrokes can reach the wrong window.** `KeyboardSender.SendToWindowAsync` focuses `gameWindow` only
   `if (gameWindow != IntPtr.Zero && Native.IsWindow(gameWindow))`. Otherwise it types into **whatever window is in
   the foreground**, and with `SendMode.TypeAndSend` it then presses Enter. `App.OpenCompose` passes `IntPtr.Zero`
   when the settings window was in front and no game is detected. If the game closed, or the user alt-tabbed while a
   slow `KeystrokeDelayMs` was typing, the translation (and Enter) goes into a browser, chat client or terminal.
   (`src/Envoy.App/Services/KeyboardSender.cs`, `src/Envoy.App/App.xaml.cs` `OpenCompose`.)
2. **Typed text is not sanitised.** Translation output and user `phrases.json` text are typed character by character
   with `KEYEVENTF_UNICODE`, or pasted with Ctrl+V. A `\n` or `\r` from a provider response or a shared phrase
   file can end the chat line early. The rest of the text then arrives with the chat box closed, where letters act as
   game hotkeys. Bidi override characters (U+202E) can make the message display differently to other players.
   (`KeyboardSender.cs`, `src/Envoy.App/Windows/ComposeWindow.xaml.cs` `SendAsync`, `src/Envoy.Core/Phrases/PhraseBook.cs`.)
3. **`App.OpenUrl` will launch anything.** `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` is
   called with `UpdateInfo.PageUrl`, which comes straight from the GitHub API's `html_url`
   (`MainWindow.xaml.cs` line 162). A spoofed or tampered response with `file:///…/x.exe` or `\\host\share\x.exe` would
   be executed. `UpdateChecker.Parse` accepts any `html_url` and any `browser_download_url`.
   (`src/Envoy.App/App.xaml.cs` line 186, `src/Envoy.Core/Updates/UpdateChecker.cs`.)
4. **`phrases.json` is unbounded.** `PhraseBook.LoadWithUserFile` reads a file of any size with any number of
   phrases and any text length.
5. **Culture bugs.** `TranscriptWriter.Format` uses `ToString("HH:mm:ss")` under the current culture. In custom
   formats `:` is the culture's time separator, so some locales write a different separator. `Hotkey.ToString()` uses
   culture-sensitive `int.ToString()`. Envoy is a multilingual app, so this class of bug matters.

### Analyzer baseline (measured)

With `AnalysisLevel=8.0-recommended`, `EnforceCodeStyleInBuild=true` and the rule set in Design §1, the only
diagnostics are:

| Location | Rule | Planned resolution |
| --- | --- | --- |
| `src/Envoy.Core/Chat/TranscriptWriter.cs:23` | CA1305 | Fix: `ToString("HH:mm:ss", CultureInfo.InvariantCulture)` |
| `src/Envoy.Core/Languages/LanguageCatalog.cs:65` | CA1307 | Fix: `Contains('-', StringComparison.Ordinal)` (same behaviour) |
| `src/Envoy.Core/Languages/ScriptDetector.cs:73,78` | CA1307 | Fix: `Contains(c, StringComparison.Ordinal)` (same behaviour) |
| `src/Envoy.Core/Translation/Providers/ClaudeTranslator.cs:77` | CA1307 | Fix: `IndexOf('\|', StringComparison.Ordinal)` (same behaviour) |
| `src/Envoy.Core/Phrases/PhraseBook.cs:65` | CA1869 | Fix: one `static readonly JsonSerializerOptions` |
| `ClaudeTranslator.cs:11`, `EdgeFreeTranslator.cs:12` | CA1001 | Suppress with `[SuppressMessage]` and a justification: translators live as long as the translator stack, and disposal ownership belongs to `03-translation-resilience` |
| `tests/Envoy.Core.Tests/TranslationTests.cs:300` | CA1310 | Fix: `StartsWith("enc:", StringComparison.Ordinal)` |
| `tests/**`: CA1707 (61 test names with `_`), CA1307 (8), CA2000 (1) | — | Rules off for tests (`tests/.editorconfig`) |
| `src/Envoy.App/Services/HotkeyService.cs:43` | CA1305 | Fix: `ToString(CultureInfo.InvariantCulture)` |
| `src/Envoy.App/Services/ScreenCapturer.cs:29,53` | CA1806 | Fix: `_ = Native.SetStretchBltMode(…)`, `_ = Native.ReleaseDC(…)` |
| `src/Envoy.App/ViewModels/MainViewModel.cs:202,206,208` | CA1822 | Rule off for `src/Envoy.App` (WPF binds to instance members) |
| `src/Envoy.App/ViewModels/MainViewModel.cs:14` | CA1716 | Rule off globally (C# only; the VB keyword clash is irrelevant) |
| `src/Envoy.App/App.xaml.cs:41` | CA2000 | Rule off globally (too noisy with `HttpClient` and WPF ownership patterns) |
| CA1859, CA1861 (3 sites) | — | Severity `suggestion` (micro-optimisations) |

No CS86xx nullable warnings exist today, so nullable-as-error does not need any fixes.

### What is weak or missing for production

Strangers will run a binary that sends keystrokes, holds their API keys and talks to the internet. Before 1.0 the
project needs the following, all cheap to get:

- a build that cannot silently regress: analyzers, nullable errors, a coverage floor;
- automated scanning: CodeQL, dependency review, vulnerable-package checks;
- a release whose provenance can be checked: SBOM, attestation, least-privilege tokens, pinned actions;
- a mechanical check of the anti-cheat and no-telemetry promises, so they don't depend on reviewers noticing;
- a written threat model whose mitigations point at real code and tests;
- the four code gaps above, closed.

## Goals / Non-goals

**Goals**

- One `Directory.Build.props` and a curated, documented rule set, introduced at zero warnings.
- Coverage collected, summarised in the job, with a floor for Envoy.Core.
- CodeQL (C# and Actions), dependency review on PRs, a vulnerable-package gate, and a CycloneDX SBOM on releases.
- Least-privilege, SHA-pinned workflows with timeouts. CI time stays about the same.
- A policy check that enforces the product promises: no process-memory, hook or injection APIs; network code only
  in known places; only known hosts; `asInvoker`.
- Threat model, release checklist, repository-settings guide, an updated `SECURITY.md`, and CODEOWNERS.
- Code mitigations: outgoing-text sanitiser, send-target guard, shell-launch policy, release-URL validation and
  `phrases.json` limits, each with unit tests.
- A home for app-level tests (`tests/Envoy.App.Tests`) for logic that cannot move to Core because it uses WPF types.

**Non-goals**

- Code signing, installers, auto-update and checksum files (`06-distribution-updates`). This area adds the SBOM and
  provenance attestation next to 06's release step.
- Log redaction and settings migration or sanitising (`02-reliability-observability`). The threat model refers to them.
- Provider contract tests that would raise HTTP-provider coverage (`03-translation-resilience`).
- Fuzzing, mutation testing, OpenSSF Scorecard and signed commits. These are listed as follow-ups only.
- Any change to settings, pages or windows beyond the reply-failure message text.

## Requirements

### Build hygiene and analyzers

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-1 | MUST | A root `Directory.Build.props` sets `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`, `Deterministic=true`, `AnalysisLevel=8.0-recommended` and `EnforceCodeStyleInBuild=true` for every project, including `tools/typecheck/TypeCheck.csproj`. |
| REQ-QSC-2 | MUST | Nullable warnings are compile errors in `Envoy.Core` and `Envoy.Core.Tests` in every build, local or CI. |
| REQ-QSC-3 | MUST | When the `CI` environment variable is `true`, all warnings are errors for every project except the real WPF `Envoy.App` build. NuGet audit warnings NU1901–NU1904 stay warnings. |
| REQ-QSC-4 | MUST | The rule set is introduced with every existing diagnostic fixed or suppressed with a written justification. After the change, `CI=true dotnet test tests/Envoy.Core.Tests -c Release` and `tools/typecheck/typecheck.sh` both pass. |
| REQ-QSC-5 | MUST | Culture-sensitive API rules CA1304, CA1305, CA1307, CA1309 and CA1310 are at `warning` severity for `src/**`. |
| REQ-QSC-6 | MUST | `Microsoft.CodeAnalysis.BannedApiAnalyzers` with a root `BannedSymbols.txt` makes it a build error (RS0030) to use: TLS-validation bypass members, `WebClient`/`WebRequest`, `BinaryFormatter`, `XamlReader`, `Process.MainModule`/`Process.Modules`, and `Process.Start` anywhere except `ShellLauncher`. |
| REQ-QSC-7 | SHOULD | The Windows publish of `Envoy.App` keeps warnings non-fatal (the Linux typecheck is the warning gate for app C#). The job summary prints its warning count. |

### Policy check (product promises)

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-8 | MUST | `tools/ci/check_policy.py` fails when a `DllImport`/`LibraryImport` in `src/**` is not in the allowlist in `tools/ci/policy.json`, or when any identifier on the hard-deny list (process memory, remote threads, hooks, debugging, dynamic loading) appears in `src/**/*.cs`. |
| REQ-QSC-9 | MUST | The policy check fails when a network API type (`HttpClient`, `HttpRequestMessage`, `HttpMessageHandler`, `SocketsHttpHandler`, `WebRequest`, `WebClient`, `TcpClient`, `UdpClient`, `Socket`, `ClientWebSocket`) is named in a `src/**/*.cs` file outside the allowed paths in `policy.json`. |
| REQ-QSC-10 | SHOULD | The policy check fails when an `http(s)://` literal in `src/**/*.cs` or `src/**/*.xaml` names a host not in `policy.json`'s host allowlist. XAML schema hosts are ignored. |
| REQ-QSC-11 | MUST | The policy check fails when `src/Envoy.App/app.manifest` does not request `level="asInvoker"` or sets `uiAccess="true"`. |
| REQ-QSC-12 | SHOULD | The policy check fails when a workflow has no top-level `permissions:` block, or that block grants `write`. It also fails when a non-local `uses:` is not pinned to a 40-hex commit SHA, or when a job has no `timeout-minutes`. |
| REQ-QSC-13 | SHOULD | `tools/ci/test_*.py` (standard-library `unittest`) cover the policy and coverage scripts and run in the `check` job. |

### Coverage

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-14 | MUST | The `check` job collects Cobertura coverage for `Envoy.Core` with `coverlet.collector`, excluding `[GeneratedCode]` and `[ExcludeFromCodeCoverage]` code. |
| REQ-QSC-15 | MUST | The `check` job writes a Markdown coverage summary (overall plus one row per top-level Core folder, line and branch) to `$GITHUB_STEP_SUMMARY`, and uploads the Cobertura XML as the `coverage` artifact. |
| REQ-QSC-16 | MUST | The `check` job fails if Envoy.Core line coverage is below 70 % or branch coverage is below 55 %. |

### Supply chain and scanning

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-17 | MUST | A `CodeQL` workflow analyses C# (`build-mode: none`, `security-extended` queries) on pushes to `main`, PRs to `main` and weekly. Results go to code scanning. |
| REQ-QSC-18 | SHOULD | The CodeQL workflow also analyses the GitHub Actions workflows (`actions` language). |
| REQ-QSC-19 | MUST | A `dependency-review` workflow on PRs fails when the PR adds a dependency with a known vulnerability of severity `high` or above, or with a license outside the allowlist. |
| REQ-QSC-20 | MUST | The `check` job fails when `dotnet list package --vulnerable --include-transitive` reports a `High` or `Critical` advisory for `tests/Envoy.Core.Tests` (covers Core) or `tools/typecheck/TypeCheck.csproj` (mirrors the app's packages). |
| REQ-QSC-21 | MUST | Every tagged release includes `Envoy.cdx.json`, a CycloneDX JSON SBOM of `Envoy.App` including transitive NuGet packages and `Envoy.Core`'s dependencies. CI fails if the SBOM lacks the `Anthropic`, `AWSSDK.Translate` or `System.Security.Cryptography.ProtectedData` components. |
| REQ-QSC-22 | SHOULD | Tagged releases have a GitHub build-provenance attestation for every released executable, which `gh attestation verify <file> -R PinnerSinner/Envoy` accepts. |
| REQ-QSC-23 | MUST | Every workflow declares top-level `permissions: contents: read`. Only jobs that publish (screenshots branch, release) are granted `contents: write`, and CodeQL gets `security-events: write`. |
| REQ-QSC-24 | SHOULD | Third-party actions are pinned to a full commit SHA with the version tag in a trailing comment. Dependabot's `github-actions` ecosystem keeps them current. |
| REQ-QSC-25 | SHOULD | `build.yml` cancels superseded non-tag runs (`concurrency`), every job has `timeout-minutes`, and NuGet packages are cached. |
| REQ-QSC-26 | MUST | The `check` job finishes in at most 8 minutes. The time from push to the Windows artifact grows by no more than 2 minutes over the pre-change baseline. CodeQL and dependency review run in parallel and do not gate the `windows` job. |
| REQ-QSC-27 | SHOULD | Dependabot also watches `/tools/typecheck` (NuGet), so the typecheck's package pins stay in step with the app's. |

### Security mitigations in code

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-28 | MUST | All text Envoy types or pastes into another window first goes through `OutgoingText.Sanitize`. It removes C0/C1 control characters, turns CR/LF/TAB runs into one space, removes bidi embedding, override and isolate controls (U+202A–U+202E, U+2066–U+2069), trims, and caps the text at 1000 UTF-16 code units without splitting a surrogate pair. |
| REQ-QSC-29 | MUST | `KeyboardSender` refuses to type and throws `SendBlockedException` (so the reply falls back to the clipboard) in three cases: there is no target window, the target window no longer exists, or the foreground window's process is not the target window's process. It checks after focusing, before every character, before Ctrl+V and before Enter. |
| REQ-QSC-30 | MUST | Every shell launch goes through `ShellLauncher.TryOpen`. It opens only absolute `http`/`https` URLs without user-info, `ms-settings:` URIs, and non-executable paths inside the Envoy data folder. It opens nothing for anything else. |
| REQ-QSC-31 | MUST | `UpdateChecker` accepts a release only when its `html_url` is `https://github.com/<owner>/<repo>/releases/…`. It keeps a download URL only when that URL passes the same check. `CheckAsync` also requires `<owner>/<repo>` to equal its configured repository (case-insensitive). |
| REQ-QSC-32 | MUST | A user `phrases.json` over 1 MiB is ignored (built-ins only). At most 500 user phrases are loaded. Phrase texts are sanitised and capped at 500 characters. `Id` and `Category` are capped at 64 characters and `Icon` at 16. Phrases with an empty `Id` are dropped. |
| REQ-QSC-33 | MUST | A unit test fails if a `string` property on any `Envoy.Core.Settings` type has a name ending in `Key`, `KeyId`, `Secret`, `Token` or `Password`, is not on an explicit allowlist of key-binding names, and lacks `[Secret]`. |
| REQ-QSC-34 | MUST | Unit tests fail if any `[Secret]` value appears in plain text in the file `SettingsStore.Save` writes, or if a `[Secret]` property sits on a type that `SettingsStore` cannot reach (only through a collection, for example). |
| REQ-QSC-35 | COULD | `EndpointPolicy.IsInsecureRemote(url)` is true for `http://` URLs whose host is not loopback or a private address. When it is true, the Translation page shows a warning next to the LibreTranslate URL. |

### Documentation and process

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-36 | MUST | `docs/security/threat-model.md` lists assets, trust boundaries, entry points and threats T1–T14. Each threat has its mitigations, the code file(s), the test(s) or check(s) that verify them, and the residual risk. |
| REQ-QSC-37 | SHOULD | The policy check fails when a back-ticked repository path or `ClassName.TestName` in the threat model doesn't exist. |
| REQ-QSC-38 | MUST | `docs/releasing.md` has a release checklist covering pre-tag checks, manual in-game checks and post-release verification. |
| REQ-QSC-39 | MUST | `docs/security/repository-settings.md` lists the recommended repository settings: rulesets for `main` and `v*` tags, required checks, secret scanning with push protection, Dependabot alerts and security updates, Actions token defaults, and 2FA. |
| REQ-QSC-40 | MUST | `SECURITY.md` states supported versions, explains how to verify a download (SHA-256, attestation, SBOM), and links the threat model. Its contributor guidance says to never commit real keys and to use fake key strings that don't match provider formats in tests. |
| REQ-QSC-41 | SHOULD | `.github/CODEOWNERS` assigns the security-sensitive paths to the maintainer. |
| REQ-QSC-42 | SHOULD | The bug-report template warns against pasting `settings.json` or API keys. The PR template gains a "security-sensitive change" checkbox. |
| REQ-QSC-43 | SHOULD | `tests/Envoy.App.Tests` (net8.0-windows, xUnit) exists, holds `Hotkey` parsing tests, and runs in a parallel Windows CI job. |

## Design

### 1. Build configuration

**`Directory.Build.props` (new, repository root):**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Deterministic>true</Deterministic>
    <!-- Pinned (not "latest") so an SDK update can't add new rules and break CI. Bump deliberately. -->
    <AnalysisLevel>8.0-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Authors>Envoy contributors</Authors>
    <RepositoryUrl>https://github.com/PinnerSinner/Envoy</RepositoryUrl>
    <IsPackable>false</IsPackable>
    <!-- A new advisory must not break unrelated builds; the explicit vulnerable-package step is the gate. -->
    <WarningsNotAsErrors>$(WarningsNotAsErrors);NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>
  </PropertyGroup>

  <!-- GitHub Actions sets CI=true. Locally warnings stay warnings; run `CI=true dotnet test` to reproduce CI. -->
  <PropertyGroup Condition="'$(CI)' == 'true'">
    <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="3.3.4">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt" />
  </ItemGroup>
</Project>
```

**Project file changes:**

- `src/Envoy.Core/Envoy.Core.csproj`: remove the now-shared `Nullable`, `ImplicitUsings` and `LangVersion`. Add
  `<WarningsAsErrors>$(WarningsAsErrors);nullable</WarningsAsErrors>`.
- `tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj`: remove the shared properties, add the same `WarningsAsErrors`,
  and add `<PackageReference Include="coverlet.collector" Version="6.0.4">` with
  `PrivateAssets=all` and the usual `IncludeAssets`.
- `src/Envoy.App/Envoy.App.csproj`: remove the shared properties. Add
  `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` with the comment "the Linux typecheck
  (TreatWarningsAsErrors=true) is the warning gate; Windows-only markup-compiler warnings must not block a
  release". The existing `NoWarn` stays.
- `tools/typecheck/TypeCheck.csproj`: unchanged apart from removing the duplicated shared properties. It keeps
  `TreatWarningsAsErrors=true`. `Directory.Build.props` applies to it automatically (MSBuild walks up from
  `tools/typecheck/`), so analyzers and banned APIs are checked for the app's C# on Linux.

**Rule set.** These go in `.editorconfig` files. They are not in a ruleset file because `.editorconfig` applies by
path, and the typecheck compiles `src/Envoy.App/**` from its real location.

Appended to the root `.editorconfig` under `[*.cs]`:

```ini
# --- Analyzer policy (see docs/specs/08-quality-security-ci.md). Raise severities, don't silence them. ---
# Culture: Envoy is multilingual; every culture-sensitive call must say what it means.
dotnet_diagnostic.CA1304.severity = warning
dotnet_diagnostic.CA1305.severity = warning
dotnet_diagnostic.CA1307.severity = warning
dotnet_diagnostic.CA1309.severity = warning
dotnet_diagnostic.CA1310.severity = warning
# Correctness
dotnet_diagnostic.CA2016.severity = warning   # forward CancellationToken
dotnet_diagnostic.CA1806.severity = warning   # ignored HRESULT/return values
# Security: errors
dotnet_diagnostic.RS0030.severity = error     # banned API (BannedSymbols.txt)
dotnet_diagnostic.CA2300.severity = error     # BinaryFormatter family
dotnet_diagnostic.CA2301.severity = error
dotnet_diagnostic.CA2302.severity = error
dotnet_diagnostic.CA3075.severity = error     # insecure DTD processing
dotnet_diagnostic.CA3077.severity = error
dotnet_diagnostic.CA5350.severity = error     # weak crypto
dotnet_diagnostic.CA5351.severity = error
dotnet_diagnostic.CA5359.severity = error     # disabled certificate validation
dotnet_diagnostic.CA5364.severity = error     # deprecated TLS
dotnet_diagnostic.CA5386.severity = error     # hard-coded TLS version
dotnet_diagnostic.CA5397.severity = error
# Off or quieter, with reasons
dotnet_diagnostic.CA1716.severity = none      # VB keyword clashes; C#-only codebase
dotnet_diagnostic.CA2000.severity = none      # dispose analysis is noisy for HttpClient/WPF ownership
dotnet_diagnostic.CA2007.severity = none      # ConfigureAwait: app code needs the UI context; Core already uses it where it matters
dotnet_diagnostic.CA5394.severity = none      # Random for jitter/backoff is not security-relevant
dotnet_diagnostic.CA1848.severity = none      # LoggerMessage delegates: not used
dotnet_diagnostic.CA1859.severity = suggestion
dotnet_diagnostic.CA1861.severity = suggestion
```

New `tests/.editorconfig` (not `root`):

```ini
[*.cs]
dotnet_diagnostic.CA1707.severity = none   # test names use underscores
dotnet_diagnostic.CA1307.severity = none   # Assert.Contains etc. on literals
dotnet_diagnostic.CA2000.severity = none
dotnet_diagnostic.CA1861.severity = none   # inline data arrays
```

New `src/Envoy.App/.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.CA1822.severity = none   # WPF binds to instance members; event handlers must be instance
```

**Fixes.** The fixes and suppressions are exactly those in the Context baseline table. Each `[SuppressMessage]`
has a `Justification` string. No `#pragma warning disable` is allowed except the single RS0030 one in
`ShellLauncher` (§6.3), which has a comment.

### 2. Banned APIs (`BannedSymbols.txt`, new, repository root)

```text
T:System.Net.WebClient;Use the shared HttpClient (threat model T13)
T:System.Net.WebRequest;Use the shared HttpClient (threat model T13)
T:System.Net.HttpWebRequest;Use the shared HttpClient (threat model T13)
P:System.Net.Http.HttpClientHandler.ServerCertificateCustomValidationCallback;Never bypass TLS validation (T9)
P:System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;Never bypass TLS validation (T9)
P:System.Net.Security.SslClientAuthenticationOptions.RemoteCertificateValidationCallback;Never bypass TLS validation (T9)
P:System.Net.ServicePointManager.ServerCertificateValidationCallback;Never bypass TLS validation (T9)
T:System.Runtime.Serialization.Formatters.Binary.BinaryFormatter;Insecure deserialization (T7)
T:System.Windows.Markup.XamlReader;Never load XAML from data (T7)
P:System.Diagnostics.Process.MainModule;Don't open the game process (T11)
P:System.Diagnostics.Process.Modules;Don't open the game process (T11)
M:System.Diagnostics.Process.Start(System.String);Use ShellLauncher.TryOpen (T8)
M:System.Diagnostics.Process.Start(System.String,System.String);Use ShellLauncher.TryOpen (T8)
M:System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo);Use ShellLauncher.TryOpen (T8)
```

Symbols that don't resolve in a project, such as `XamlReader` in Core, are ignored by the analyzer.
`Process.GetProcessesByName` (used by `GameDetector`) stays allowed: it lists processes without opening the game's
memory.

### 3. Policy check (`tools/ci/check_policy.py` + `tools/ci/policy.json`, new)

The script uses only the Python 3 standard library, like the typecheck scripts. It runs as
`python3 tools/ci/check_policy.py [--root <repo>]`, prints one line per violation (`path:line: rule: message`) and
exits 1 if there is any violation, 0 otherwise. It finishes in under a second.

`policy.json` (seeded from the current code):

```json
{
  "native": {
    "allowed": [
      "user32!SendInput", "user32!MapVirtualKeyW", "user32!GetForegroundWindow", "user32!SetForegroundWindow",
      "user32!IsWindow", "user32!IsIconic", "user32!GetWindowRect", "user32!GetWindowThreadProcessId",
      "user32!GetWindowLongPtrW", "user32!SetWindowLongPtrW", "user32!SetWindowPos", "user32!RegisterHotKey",
      "user32!UnregisterHotKey", "user32!GetDC", "user32!ReleaseDC",
      "gdi32!CreateCompatibleDC", "gdi32!CreateCompatibleBitmap", "gdi32!SelectObject", "gdi32!StretchBlt",
      "gdi32!SetStretchBltMode", "gdi32!GetDIBits", "gdi32!DeleteObject", "gdi32!DeleteDC"
    ],
    "denied": [
      "ReadProcessMemory", "WriteProcessMemory", "OpenProcess", "OpenThread", "VirtualAllocEx", "VirtualProtectEx",
      "VirtualQueryEx", "CreateRemoteThread", "CreateRemoteThreadEx", "NtReadVirtualMemory", "NtWriteVirtualMemory",
      "SetWindowsHookEx", "SetWindowsHookExA", "SetWindowsHookExW", "QueueUserAPC", "DebugActiveProcess",
      "SetThreadContext", "GetThreadContext", "LoadLibrary", "LoadLibraryA", "LoadLibraryW", "LoadLibraryExW",
      "GetProcAddress", "DuplicateHandle", "MiniDumpWriteDump"
    ]
  },
  "network": {
    "types": ["HttpClient", "HttpRequestMessage", "HttpMessageHandler", "SocketsHttpHandler", "WebRequest",
              "WebClient", "TcpClient", "UdpClient", "Socket", "ClientWebSocket"],
    "allowedPaths": ["src/Envoy.Core/Translation/", "src/Envoy.Core/Updates/", "src/Envoy.App/AppHost.cs"]
  },
  "hosts": {
    "allowed": ["localhost", "api.github.com", "github.com", "api.deepl.com", "api-free.deepl.com", "www.deepl.com",
                "translate.googleapis.com", "edge.microsoft.com", "api-edge.cognitive.microsofttranslator.com",
                "console.anthropic.com", "aws.amazon.com"],
    "ignored": ["schemas.microsoft.com", "schemas.openxmlformats.org"]
  },
  "manifest": "src/Envoy.App/app.manifest",
  "threatModel": "docs/security/threat-model.md"
}
```

The exact `allowed` native list is regenerated from `Interop/Native.cs` at implementation time. The entry point is
`EntryPoint = "…"` when present, otherwise the method name. The DLL name is lower-cased without `.dll`.

Rules:

| Rule id | What it checks |
| --- | --- |
| `native-unlisted` | Each `[DllImport("x")]`/`[LibraryImport("x", …)]` + `static extern\|partial … Name(` → `x!EntryPoint` must be in `native.allowed`. |
| `native-denied` | Any whole-word occurrence of a `native.denied` identifier in `src/**/*.cs`, including comments and strings, even if allowlisted. This keeps the anti-cheat promise mechanical: comments describing what Envoy doesn't do must not use these names. |
| `network-location` | Whole-word `network.types` in `src/**/*.cs` outside `network.allowedPaths`. `HttpRequestException` does not match (word boundary). |
| `host` | `https?://([A-Za-z0-9.-]+)` in `src/**/*.cs` and `src/**/*.xaml`. The host must be in `hosts.allowed` or `hosts.ignored`. |
| `manifest` | The manifest contains `level="asInvoker"` and doesn't contain `uiAccess="true"`. |
| `workflow` (SHOULD) | For each `.github/workflows/*.yml`: a top-level `permissions:` block exists before `jobs:` and contains no `write`. Every `uses:` not starting with `./` or `docker://` matches `@[0-9a-f]{40}`. The count of `timeout-minutes:` is at least the count of `runs-on:`. These are line-based regex checks; no YAML library. |
| `threat-model-refs` (SHOULD) | Back-ticked tokens in the threat model that start with `src/`, `tests/`, `tools/`, `docs/` or `.github/` must exist. Tokens matching `[A-Z]\w*Tests\.\w+` must have a method of that name in `tests/**/*.cs`. |

Adding an allowlist entry is a deliberate act in a CODEOWNERS-protected file (§9). That review is the human gate. The
script only makes sure nobody skips it by accident.

### 4. Coverage

**`tests/coverage.runsettings` (new):**

```xml
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat code coverage">
        <Configuration>
          <Format>cobertura</Format>
          <Include>[Envoy.Core]*</Include>
          <ExcludeByAttribute>GeneratedCodeAttribute,ExcludeFromCodeCoverageAttribute</ExcludeByAttribute>
          <SkipAutoProps>true</SkipAutoProps>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

**`tools/ci/coverage_summary.py` (new, standard library only):**

`coverage_summary.py --package Envoy.Core --min-line 70 --min-branch 55 <cobertura.xml>…`

- It reads the `<package name="Envoy.Core">` `line-rate`/`branch-rate` for the totals. For the per-folder rows it
  groups `<class filename>` by first path segment (`Chat`, `Ocr`, `Translation`, …), counts unique
  `(file, line)` pairs and hits, and sums branch `condition-coverage` "(a/b)".
- It writes a Markdown table to `$GITHUB_STEP_SUMMARY` when that variable is set, otherwise to stdout.
- Exit 0 when at or above both thresholds. Exit 1 when below, with
  `::error::Envoy.Core line coverage 68.2% is below 70%`. Exit 2 on missing or invalid input, or a missing package.

**Thresholds.** 70 % lines and 55 % branches sit 4–6 points under today's measurement, so other areas can land
without fighting the gate. Untested modules still trip it. Policy, written in `docs/building.md`: when a PR lifts
coverage 3 or more points above a threshold, raise the threshold in the same PR. The 1.0 target is 80 % / 65 %, and
it depends on 03's provider contract tests.

### 5. Workflows

All third-party `uses:` below are written `owner/action@<sha> # vN`. The implementer resolves each SHA with
`gh api repos/<owner>/<action>/commits/<tag> --jq .sha`.

**`build.yml` (changed; SHARED with 06):**

```yaml
permissions:
  contents: read

concurrency:
  group: build-${{ github.ref }}
  cancel-in-progress: ${{ !startsWith(github.ref, 'refs/tags/') }}

jobs:
  check:
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@<sha> # v4
      - uses: actions/setup-dotnet@<sha> # v4
        with: { dotnet-version: 8.0.x }
      - uses: actions/cache@<sha> # v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', 'Directory.Build.props', '.config/dotnet-tools.json') }}
          restore-keys: nuget-${{ runner.os }}-
      - name: Policy check
        run: |
          python3 tools/ci/check_policy.py
          python3 -m unittest discover -s tools/ci -p "test_*.py"
      - name: Unit tests with coverage
        run: >
          dotnet test tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj -c Release
          --collect "XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory TestResults
      - name: Coverage summary and threshold
        run: python3 tools/ci/coverage_summary.py --package Envoy.Core --min-line 70 --min-branch 55 TestResults/*/coverage.cobertura.xml
      - uses: actions/upload-artifact@<sha> # v4
        if: always()
        with: { name: coverage, path: TestResults/*/coverage.cobertura.xml, if-no-files-found: ignore }
      - name: Type-check Windows app
        run: tools/typecheck/typecheck.sh
      - name: Vulnerable packages (High/Critical fail)
        run: |
          for p in tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj tools/typecheck/TypeCheck.csproj; do
            dotnet list "$p" package --vulnerable --include-transitive | tee -a vuln.txt
          done
          if grep -E '\b(High|Critical)\b' vuln.txt; then echo "::error::Vulnerable packages found"; exit 1; fi

  app-tests:                # REQ-QSC-43 (SHOULD); parallel with `windows`
    runs-on: windows-latest
    needs: check
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@<sha> # v4
      - uses: actions/setup-dotnet@<sha> # v4
        with: { dotnet-version: 8.0.x }
      - name: App unit tests
        run: dotnet test tests/Envoy.App.Tests/Envoy.App.Tests.csproj -c Debug

  windows:
    runs-on: windows-latest
    needs: check
    timeout-minutes: 30
    outputs:
      version: ${{ steps.version.outputs.value }}
      prerelease: ${{ steps.version.outputs.prerelease }}
    steps:
      # … existing/06 steps: version, publish, (06: signing), smoke test, screenshots artifact, checksums …
      # REMOVED from here: "Publish screenshots branch" (moves to `screenshots` job) and the release step
      # (moves to `release` job).
      - name: SBOM
        shell: pwsh
        run: |
          dotnet tool restore
          dotnet tool run dotnet-CycloneDX src/Envoy.App/Envoy.App.csproj --output publish --filename Envoy.cdx.json `
            --output-format Json --set-name Envoy --set-version "${{ steps.version.outputs.value }}"
          $sbom = Get-Content publish/Envoy.cdx.json -Raw
          foreach ($c in "Anthropic", "AWSSDK.Translate", "System.Security.Cryptography.ProtectedData") {
            if ($sbom -notmatch [regex]::Escape("`"name`": `"$c`"")) { Write-Error "SBOM lacks $c"; exit 1 }
          }
      # The existing portable artifact upload gains publish/Envoy.cdx.json.

  screenshots:
    if: github.event_name == 'push' && github.ref == 'refs/heads/main'
    needs: windows
    runs-on: ubuntu-latest
    timeout-minutes: 5
    permissions: { contents: write }
    steps:
      - uses: actions/checkout@<sha> # v4
      - uses: actions/download-artifact@<sha> # v4
        continue-on-error: true
        with: { name: screenshots, path: shots }
      - name: Publish screenshots branch
        run: |
          ls shots/*.png >/dev/null 2>&1 || { echo "no screenshots"; exit 0; }
          # same orphan-branch push as today, in bash

  release:
    if: startsWith(github.ref, 'refs/tags/v')
    needs: [windows, app-tests]
    runs-on: windows-latest        # same OS as 06's vpk steps
    timeout-minutes: 20
    permissions: { contents: write, id-token: write, attestations: write }
    steps:
      - uses: actions/download-artifact@<sha> # v4      (portable + 06's setup/Releases artifacts)
      - name: Verify checksums between jobs
        # Recompute SHA-256 of each downloaded exe and compare with the .sha256 / SHA256SUMS.txt from the windows job.
      - uses: actions/attest-build-provenance@<sha> # v2
        with:
          subject-path: |
            dist/Envoy.exe
            dist/Envoy-win-Setup.exe        # when 06 has landed
      # 06's step 12 (vpk upload / gh release upload / gh release edit) runs here, unchanged, and adds
      # Envoy.cdx.json to its `gh release upload` file list. Before 06 lands: softprops/action-gh-release with
      # files Envoy.exe, Envoy.exe.sha256, Envoy.cdx.json.
```

Why split out `screenshots` and `release`: the `windows` job builds and runs code from any pushed branch. After
the split its token is read-only. Write access lives only in short jobs that run no project code. Those jobs add
about 30–60 s, and only on `main` pushes and tags. If 06's signing secrets are needed, they stay in `windows`
(signing happens before checksums). The release job only uploads already-signed files.

**`.config/dotnet-tools.json` (new):** a local tool manifest pinning `CycloneDX` (command `dotnet-CycloneDX`) to
an exact current version. CLI flag names differ between CycloneDX major versions. The implementer checks them
against `dotnet tool run dotnet-CycloneDX --help` for the pinned version (see Risks).

**`.github/workflows/codeql.yml` (new):**

```yaml
name: CodeQL
on:
  push: { branches: [main] }
  pull_request: { branches: [main] }
  schedule: [{ cron: "23 4 * * 1" }]
permissions:
  contents: read
jobs:
  analyze:
    runs-on: ubuntu-latest
    timeout-minutes: 20
    permissions: { contents: read, security-events: write, actions: read }
    strategy:
      fail-fast: false
      matrix: { language: [csharp, actions] }
    steps:
      - uses: actions/checkout@<sha> # v4
      - uses: github/codeql-action/init@<sha> # latest major
        with: { languages: "${{ matrix.language }}", build-mode: none, queries: security-extended }
      - uses: github/codeql-action/analyze@<sha> # latest major
        with: { category: "/language:${{ matrix.language }}" }
```

`build-mode: none` means CodeQL needs no Windows build. The app's XAML-generated members are absent, which costs
some precision in `Windows/*.xaml.cs` but no failures.

**`.github/workflows/dependency-review.yml` (new):**

```yaml
name: Dependency review
on: pull_request
permissions:
  contents: read
jobs:
  review:
    runs-on: ubuntu-latest
    timeout-minutes: 5
    permissions: { contents: read, pull-requests: write }
    steps:
      - uses: actions/dependency-review-action@<sha> # v4
        with:
          fail-on-severity: high
          allow-licenses: MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, 0BSD, MS-PL, Unlicense
          comment-summary-in-pr: on-failure
```

**`.github/dependabot.yml` (changed):** the NuGet entry uses `directories: ["/", "/tools/typecheck"]`. Everything
else is unchanged.

**Wall clock.** `check` gains the policy check (under 1 s), the coverage collector (a few seconds), the summary
(under 1 s) and the vulnerable-package queries (about 10–20 s, network), and saves restore time through the cache.
`windows` gains the SBOM (about 20–40 s, after publish). `app-tests` runs in parallel with `windows`. CodeQL (about
3–6 min per language) and dependency review (under 1 min) are separate workflows. On tags, `release` waits for
`app-tests`, but `app-tests` finishes before `windows`.

### 6. Security mitigations in code

All new Core types live in `src/Envoy.Core/Security/` (namespace `Envoy.Core.Security`). They are static, pure and
thread-safe.

#### 6.1 Outgoing text (`OutgoingText.cs`)

```csharp
namespace Envoy.Core.Security;

/// <summary>Makes text safe to type or paste into another program's text box (game chat).</summary>
public static class OutgoingText
{
    public const int HardMaxLength = 1000;

    /// <summary>
    /// Removes C0/C1 control characters (CR, LF and TAB become a space), removes bidi embedding/override/isolate
    /// controls (U+202A–U+202E, U+2066–U+2069), collapses whitespace runs to one space, trims, and truncates to
    /// <paramref name="maxLength"/> UTF-16 code units without splitting a surrogate pair. ZWJ/ZWNJ (needed by emoji
    /// and Indic scripts) are kept. Null → "". Idempotent.
    /// </summary>
    public static string Sanitize(string? text, int maxLength = HardMaxLength);

    /// <summary>True for characters Sanitize removes or replaces.</summary>
    public static bool IsUnsafe(char c);
}
```

The 1000 cap is a safety ceiling against runaway provider output. It is not a claim about any game's chat limit,
which is unknown, so no game-specific cap is added.

Callers:

- `ComposeWindow.SendAsync`: `text = OutgoingText.Sanitize(AppendOriginal(text));` before both the clipboard and
  the typing branches.
- `ComposeWindow`'s copy (Ctrl+Enter) path does the same.
- `KeyboardSender.SendToWindowAsync` calls it again on entry, as a defence in depth. It is idempotent.

#### 6.2 Send-target guard (`SendTargetGuard.cs` + App changes)

```csharp
namespace Envoy.Core.Security;

public enum SendTargetVerdict { Ok, NoTarget, TargetClosed, FocusLost }

public static class SendTargetGuard
{
    /// <param name="hasTarget">The reply box knows which window it was opened over.</param>
    /// <param name="targetExists">That window still exists (IsWindow).</param>
    /// <param name="targetProcessId">Process owning the target window; 0 = unknown → NoTarget.</param>
    /// <param name="foregroundProcessId">Process owning the current foreground window.</param>
    public static SendTargetVerdict Check(bool hasTarget, bool targetExists, uint targetProcessId, uint foregroundProcessId);

    /// <summary>User-facing reason; "" for Ok.</summary>
    public static string Describe(SendTargetVerdict verdict);
}
```

`Describe` texts:

- NoTarget: "Envoy didn't type anything because it doesn't know which window to type into. Open the reply box
  from the game with your hotkey."
- TargetClosed: "Envoy didn't type anything because the window you were replying to has closed."
- FocusLost: "Envoy stopped typing because another window came to the front."

The comparison is by **process**, not HWND, so a game that recreates its top-level window (for example on a
fullscreen toggle) still matches.

App, `src/Envoy.App/Services/KeyboardSender.cs`:

```csharp
public sealed class SendBlockedException(SendTargetVerdict verdict)
    : InvalidOperationException(SendTargetGuard.Describe(verdict))
{
    public SendTargetVerdict Verdict { get; } = verdict;
}

public static async Task SendToWindowAsync(IntPtr target, string text, ComposeSettings settings, CancellationToken ct = default)
{
    text = OutgoingText.Sanitize(text);
    if (text.Length == 0) return;
    var targetPid = ProcessOf(target);                 // 0 when target == Zero or GetWindowThreadProcessId fails
    Ensure(target, targetPid);                         // NoTarget / TargetClosed before focusing
    Native.SetForegroundWindow(target);
    await Task.Delay(settings.FocusDelayMs, ct);
    Ensure(target, targetPid);                         // FocusLost if focusing failed
    // open-chat key → Ensure → each character: Ensure + SendUnicode | Paste: Ensure + Ctrl+V → Ensure + Enter
}

private static void Ensure(IntPtr target, uint targetPid)
{
    var verdict = SendTargetGuard.Check(target != IntPtr.Zero, Native.IsWindow(target), targetPid,
                                        ProcessOf(Native.GetForegroundWindow()));
    if (verdict != SendTargetVerdict.Ok) throw new SendBlockedException(verdict);
}
```

`GetWindowThreadProcessId`, `GetForegroundWindow` and `IsWindow` already exist in `Interop/Native.cs`, so there is
no new P/Invoke. `ComposeWindow`'s existing `catch` already copies the text to the clipboard and shows
`ex.Message` plus "The translation was copied to your clipboard instead." With 02's notice replacing the
`MessageBox`, 02 must use `SendBlockedException.Message` as the notice text when the exception has that type.

**Behaviour change.** Opening the reply box when no game window is known (from the settings window, with no game
running) now always falls back to the clipboard. Today it types into whatever window happens to be in front. This
goes in the CHANGELOG.

**Threading.** `SendToWindowAsync` is called on the UI thread, as today. `Ensure` makes a few user32 calls that take
microseconds, once per character. That is negligible next to `KeystrokeDelayMs`.

#### 6.3 Shell launches (`LaunchTarget.cs` + `ShellLauncher.cs`)

```csharp
namespace Envoy.Core.Security;

public enum LaunchKind { Rejected, WebUrl, SettingsUri, DataPath }

public static class LaunchTarget
{
    /// <summary>
    /// WebUrl: absolute http/https URI with a host and no user-info. SettingsUri: "ms-settings:" scheme.
    /// DataPath: fully-qualified path that, after Path.GetFullPath, equals <paramref name="dataFolder"/> or lies
    /// under it (ordinal-ignore-case prefix + separator), and whose extension is not executable
    /// (.exe .com .bat .cmd .ps1 .psm1 .vbs .vbe .js .jse .wsf .wsh .msi .msp .scr .lnk .url .hta .cpl .reg .pif .appref-ms).
    /// Everything else, including UNC (\\ or //), file:, javascript:, shell:, search-ms:, ms-msdt: and relative paths, is Rejected.
    /// Fully-qualified paths are classified before URIs, because on Unix "/x" parses as a file: URI.
    /// </summary>
    public static LaunchKind Classify(string? target, string dataFolder);
}
```

App, `src/Envoy.App/Services/ShellLauncher.cs` (new):

```csharp
public static class ShellLauncher
{
    /// <summary>Opens a web page, an ms-settings page, or a file/folder inside AppHost.DataFolder.
    /// Returns false and opens nothing for anything else, or when the shell fails.</summary>
    public static bool TryOpen(string target);
}
```

It is the single place with `#pragma warning disable RS0030 // the one sanctioned Process.Start; target validated
by LaunchTarget`. `App.OpenUrl(string url)` keeps its signature and becomes `=> ShellLauncher.TryOpen(url);`. Its
callers (`MainWindow`, 01's `IShell.OpenUrl`, 06's `OpenReleasePage`/`GetInstaller`) need no change. Rejected
targets are logged through 02's `ILog` once it exists. Until then they are silently ignored, because a rejected
target means bad data and there is nothing useful to show the user.

#### 6.4 Release URLs (`src/Envoy.Core/Updates/UpdateChecker.cs`, changed; SHARED with 06)

```csharp
namespace Envoy.Core.Security;

public static class ReleaseUrlPolicy
{
    /// <summary>https, host exactly github.com, default port, no user-info, path "/{owner}/{repo}/releases/…".
    /// When <paramref name="repository"/> is given, {owner}/{repo} must equal it (OrdinalIgnoreCase).</summary>
    public static bool IsTrusted(string? url, string? repository = null);
}
```

- `UpdateChecker.Parse(string json)` (and 06's `Parse(json, includePrereleases)`) returns `null` when `html_url` is
  not trusted. Download URLs (`DownloadUrl`, 06's `InstallerUrl`) that are not trusted become `null`.
- `UpdateChecker.CheckAsync` (all overloads) then requires `IsTrusted(info.PageUrl, Repository)`. Otherwise it
  returns `null`.
- The existing test release (`https://github.com/o/r/releases/…`) still parses.

Release asset downloads redirect to other hosts, but the URL in the API response is on `github.com`, so the check
holds. 06's Velopack path downloads through Velopack's `GithubSource`, which verifies package hashes against
`releases.win.json`. The threat model refers to it.

#### 6.5 `phrases.json` limits (`src/Envoy.Core/Phrases/PhraseBook.cs`, changed)

```csharp
public static class PhraseLimits
{
    public const long MaxFileBytes = 1_048_576;
    public const int MaxPhrases = 500;
    public const int MaxTextLength = 500;
    public const int MaxIdLength = 64;
    public const int MaxCategoryLength = 64;
    public const int MaxIconLength = 16;
}
```

`LoadWithUserFile` does the following:

1. If `new FileInfo(userFile).Length > MaxFileBytes`, it returns the built-ins. This is the same path as a JSON
   error, so 07's `loadError` reporting covers it, with the reason "larger than 1 MB".
2. It deserialises with the cached options.
3. It runs `internal static List<Phrase> Clean(IEnumerable<Phrase>)`: drop phrases with an empty `Id`, take the first
   `MaxPhrases`, truncate `Id`/`Category`/`Icon`, and set each text value to
   `OutgoingText.Sanitize(value, MaxTextLength)`. Empty texts are removed.
4. It merges with the built-ins as today.

Built-in phrases are trusted and are not cleaned.

#### 6.6 Secret coverage tests (no production change)

The tests in the Test plan reflect over `Envoy.Core.Settings`. No production code changes. If a later area adds
credentials inside a list (for example per-provider configs), the "reachable" test fails until `SettingsStore`
learns to walk collections. That is intended.

#### 6.7 (COULD) `EndpointPolicy`

```csharp
public static class EndpointPolicy
{
    /// <summary>True for http:// URLs whose host isn't loopback (localhost, 127.0.0.0/8, ::1) or private
    /// (10/8, 172.16/12, 192.168/16, fc00::/7, *.local). False for https, unparsable input and safe hosts.</summary>
    public static bool IsInsecureRemote(string? url);
}
```

If built, the Translation page (01's `Pages/TranslationPage.xaml`, or `MainWindow.xaml` before 01) shows a `Callout`
Warning bound to `IsInsecureRemote(Settings.Translation.LibreTranslateUrl)`: "This server is reached without
encryption. Chat text and the API key can be read on the network."

### 7. App-level test project (`tests/Envoy.App.Tests`, SHOULD)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <WarningsAsErrors>$(WarningsAsErrors);nullable</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Envoy.App\Envoy.App.csproj" />
    <!-- Microsoft.NET.Test.Sdk, xunit, xunit.runner.visualstudio: same versions as Envoy.Core.Tests -->
  </ItemGroup>
</Project>
```

- It runs in **Debug** only. In Release, `Envoy.App` is self-contained, and a non-self-contained test host can't
  reference a self-contained exe (NETSDK1151).
- It is added to `Envoy.sln`. A root `dotnet test` on Linux already fails today because the solution contains the
  WPF app, so nothing regresses. Linux contributors keep using `dotnet test tests/Envoy.Core.Tests`.
- Rule: code goes here only when it needs WPF/Windows types, like `Hotkey`, which uses `System.Windows.Input.Key`.
  Everything else goes to Core.

### 8. Documents

**`docs/security/threat-model.md` (new).** Structure:

1. Scope and method (STRIDE-lite; reviewed each minor release, see the checklist).
2. **Assets:**
   - A1 provider API keys;
   - A2 chat text (incoming OCR text, outgoing replies, optional transcripts);
   - A3 the ability to send keystrokes (SendInput) and set the clipboard;
   - A4 the player's game-account standing (the anti-cheat promise);
   - A5 the integrity of `Envoy.exe` and the update channel;
   - A6 the user's PC (no code execution from data).
3. **Trust boundaries / entry points:**
   - TB1 screen pixels → OCR (any player can write chat text);
   - TB2 Envoy ↔ translation providers (network);
   - TB3 user-writable files in `%APPDATA%\Envoy` (`settings.json`, `phrases.json`, logs);
   - TB4 GitHub API/Releases → update notice and downloads;
   - TB5 Envoy → foreground window (keystrokes, clipboard);
   - TB6 repository, CI and dependencies → release binary;
   - TB7 named kernel objects (single-instance mutex and event).
4. **Egress table.** Every host Envoy contacts, which feature contacts it, and the setting that controls it.
   It includes SDK-internal endpoints (`api.anthropic.com`, `translate.<region>.amazonaws.com`) that are not literals
   in our code.
5. **Threats table** (columns: ID, threat, asset, mitigations, code, verified by, residual risk):

| ID | Threat | Mitigations → code | Verified by |
| --- | --- | --- | --- |
| T1 | Chat-borne manipulation: an opponent types text aimed at the translator (prompt injection against the LLM provider, or huge text) | Translation output is display-only (WPF `TextBlock`, no markup or links). Incoming chat is never typed automatically. Only the user's own reply, after they press Send, is typed. Overlay shows the original line. → `Pipeline/ChatPipeline.cs`, `Windows/OverlayWindow.xaml` | `ChatPipelineTests.*` (existing); review |
| T2 | Malicious `phrases.json` (shared "phrase packs"): newlines that close chat and turn the rest into game hotkeys; huge file | REQ-QSC-28/32 → `Core/Security/OutgoingText.cs`, `Core/Phrases/PhraseBook.cs` | `PhraseBookLimitsTests.*`, `OutgoingTextTests.*` |
| T3 | Malicious or MITM'd provider response injecting control characters into a typed reply; runaway length | HTTPS + sanitiser + 1000 cap; the reply box shows a preview → `OutgoingText.cs`, `ComposeWindow.xaml.cs` | `OutgoingTextTests.*` |
| T4 | Keystrokes delivered to the wrong window (game closed, alt-tab, focus steal), possibly with Enter | REQ-QSC-29 → `Core/Security/SendTargetGuard.cs`, `App/Services/KeyboardSender.cs` | `SendTargetGuardTests.*`; manual in-game steps M3/M4 |
| T5 | API key disclosure at rest (backup, sync, other users) | DPAPI CurrentUser + entropy; `[Secret]` round-trip; "credential-like ⇒ [Secret]" test → `App/Services/DpapiProtector.cs`, `Core/Settings/SettingsStore.cs` | `SettingsSecretTests.*`, `SettingsStoreTests.Round_trips_settings_and_encrypts_secrets_on_disk` |
| T6 | Key or chat disclosure through logs, diagnostics or issue reports | 02's redactor (when landed); transcripts off by default; bug template warning | 02's `Redacts_secrets_everywhere`; review |
| T7 | Settings tampering → code execution or exfiltration | No polymorphic or type-name deserialization (System.Text.Json, `BinaryFormatter`/`XamlReader` banned); launches gated by `ShellLauncher`; 02/05 clamp values. Residual: whoever can write `settings.json` can set `LibreTranslateUrl` to their server, but already runs as the user (out of scope) | `LaunchTargetTests.*`; RS0030 in the typecheck |
| T8 | Update hijack: spoofed API response or `html_url` → ShellExecute of a local or UNC binary; fake release | `ReleaseUrlPolicy` + `ShellLauncher`; release only from CI on protected tags; SHA-256 (06), attestation, SBOM, Authenticode (06), Velopack hash checks (06) | `UpdateUrlTests.*`, `LaunchTargetTests.*`; `gh attestation verify` |
| T9 | Provider MITM | `HttpClient` default OS certificate validation; bypass APIs banned; LibreTranslate default is loopback; COULD warning for remote `http` | RS0030; `EndpointPolicyTests.*` (if built) |
| T10 | Supply chain (NuGet, Actions) | Dependabot; dependency review; vulnerable-package gate; SHA-pinned actions; least-privilege tokens; CodeQL; SBOM | CI checks |
| T11 | Anti-cheat regression: contributor adds memory reads, hooks or injection | `check_policy.py` native allowlist + deny list; banned `Process.MainModule`; CODEOWNERS on `Interop/` | `test_check_policy.py`; CI |
| T12 | Elevation | `asInvoker` manifest; no `uiAccess` | policy `manifest` rule |
| T13 | Telemetry or unexpected egress | Network types only in allowed paths; host allowlist | policy `network-location`/`host` rules |
| T14 | Single-instance object squatting (same-user process creates `Envoy.SingleInstance.v1` first → Envoy won't start) | Accepted: same-user attacker; only a denial of service | — |

6. **Out of scope:** malware running as the same Windows user; a compromised OS or certificate store; game-side
   anti-cheat policy changes.
7. **Review log** (date, version, reviewer).

The doc uses back-ticked paths and `Class.Test` names that `check_policy.py` validates (REQ-QSC-37). The tests in
the table use the real class names in the Test plan below. In the doc, paths are written in full (`src/Envoy.Core/…`).

**`docs/security/repository-settings.md` (new).** These are maintainer-applied settings, with the `gh` commands
where they exist:

- **Ruleset `main`:**
  - require a pull request (0 approvals is acceptable while there is a single maintainer; 1 once there are two);
  - required status checks: `check`, `windows`, `app-tests`, `analyze (csharp)`, `review` (dependency review);
  - block force pushes and deletion; require linear history;
  - do not require branches to be up to date (saves CI time).
- **Ruleset tags `v*`:** only maintainers may create; no update or deletion.
- **Code security:**
  - private vulnerability reporting on;
  - Dependabot alerts and security updates on;
  - secret scanning and **push protection** on (free for public repos);
  - code scanning default setup **off**, because the CodeQL workflow is used instead.
- **Actions:**
  - default `GITHUB_TOKEN` permission "read repository contents";
  - "Allow GitHub Actions to create and approve pull requests" off;
  - require approval for workflows from first-time contributors' forks;
  - allowed actions limited to GitHub-owned, verified creators, and the explicit list used in our workflows.
- **Account:** 2FA (hardware key or TOTP) on every account with write access.
- **Secrets:** only 06's signing secrets. They are scoped to an `release` environment with required reviewers
  when the repository plan allows it.
- **Secret-scanning guidance for contributors** (repeated in `SECURITY.md`):
  - never paste real keys into issues, tests or screenshots;
  - test keys use obviously fake values such as `"FAKE-DEEPL-KEY"`;
  - when a test must exercise a provider-shaped key (as 02's redaction tests do), build it at runtime by
    concatenation so the literal never matches a provider pattern and push protection doesn't block the push;
  - if a real key leaks, revoke it at the provider first, then remove it from history.

**`docs/releasing.md` (changed): "Release checklist" section.**

- **Before tagging**
  - `main` is green: `check`, `windows`, `app-tests`, CodeQL, with no open high or critical code-scanning or
    Dependabot alerts.
  - CHANGELOG has the version section (06's notes script enforces this for stable tags).
  - `policy.json` diff since the last release has been reviewed (any new native API, network path or host?).
  - The threat model has been reviewed for changes since the last release: review-log entry added.
  - **Manual in-game pass** on at least one supported title, using the dev artifact of the commit to be tagged:
    - capture and translation of live chat;
    - reply in "Type and send" mode arrives as one line;
    - steps M3/M4 below (focus loss and closed game produce no stray keystrokes);
    - overlay click-through;
    - hotkeys.
- **Tag:** `git tag vX.Y.Z && git push origin vX.Y.Z`. Use a prerelease tag (`-rc.N`) first for a new minor
  version.
- **After the workflow finishes**
  - The release lists the exe(s), checksums and `Envoy.cdx.json`.
  - Download and verify on a clean Windows machine:
    - `Get-FileHash` matches;
    - `gh attestation verify Envoy.exe -R PinnerSinner/Envoy` passes;
    - (with 06) the Authenticode signature is valid.
  - A previous version shows the update notice, and the notice's link opens the GitHub release page.
  - Announce. If anything fails: delete the release (not the tag), fix, and ship the next patch version. Never
    re-tag.

**`SECURITY.md` (changed):**

- **Supported versions:** the latest release only.
- **Verifying a download:** SHA-256 file; `gh attestation verify`; the SBOM, and what it is for.
- A link to the threat model.
- **For contributors:** the secret guidance above; security-sensitive paths need maintainer review (CODEOWNERS).
- **Response target:** acknowledge reports within 7 days. This is worded as a target, not a guarantee.
- The existing data section is kept as is.

### 9. Review gates

**`.github/CODEOWNERS` (new):**

```text
/.github/                                   @PinnerSinner
/tools/ci/                                  @PinnerSinner
/Directory.Build.props                      @PinnerSinner
/BannedSymbols.txt                          @PinnerSinner
/src/Envoy.App/Interop/                     @PinnerSinner
/src/Envoy.App/app.manifest                 @PinnerSinner
/src/Envoy.App/Services/KeyboardSender.cs   @PinnerSinner
/src/Envoy.App/Services/ShellLauncher.cs    @PinnerSinner
/src/Envoy.Core/Security/                   @PinnerSinner
/src/Envoy.Core/Settings/SettingsStore.cs   @PinnerSinner
/docs/security/                             @PinnerSinner
```

**PR template:** add `- [ ] Security-sensitive (Interop, keystrokes, network, secrets, workflows)? Threat model /
policy.json updated`.

**Bug template:** add a `markdown` block at the top: "Please don't paste `settings.json` or API keys. Envoy never
needs them for a bug report."

### 10. Settings, UI, threading and failure behaviour (summary)

- **Settings added:** none. `AppSettings.cs` is unchanged.
- **UI:** no new windows or pages. The reply-failure message text comes from `SendTargetGuard.Describe`. COULD: one
  warning callout on the Translation page (§6.7).
- **Threading:** all new Core code is pure and static. The guard runs on the UI thread inside the existing async
  send.
- **Failure behaviour:**
  - send blocked → nothing typed, text on the clipboard, message;
  - launch rejected → nothing opens;
  - untrusted release URL → no update notice;
  - oversized `phrases.json` → built-ins only;
  - CI policy, coverage or vulnerability failures → red check with `::error::` annotations naming the file and
    line, or the number.

## Acceptance criteria

| ID | REQs | Verified by |
| --- | --- | --- |
| AC-QSC-1 | 1, 2, 3, 4, 5 | **CI check:** `check` job runs `dotnet test … -c Release` with `CI=true` set by Actions and passes. The build log shows `0 Warning(s)` for `Envoy.Core` and `Envoy.Core.Tests`. Locally: `CI=true dotnet test tests/Envoy.Core.Tests -c Release` passes. |
| AC-QSC-2 | 2 | **Manual (local):** add `string s = null;` to any Core method and run `dotnet build src/Envoy.Core` without `CI`. It fails with error CS8600. Revert. |
| AC-QSC-3 | 1, 4, 5, 6 | **Typecheck:** `tools/typecheck/typecheck.sh` passes with `Directory.Build.props` applied (the log shows BannedApiAnalyzers restored). |
| AC-QSC-4 | 6 | **Manual (local):** adding `System.Diagnostics.Process.Start("x");` to any `src/Envoy.App` file outside `ShellLauncher.cs` makes `tools/typecheck/typecheck.sh` fail with RS0030. Adding `new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }` to Core makes `dotnet build src/Envoy.Core` fail with RS0030. Revert both. |
| AC-QSC-5 | 8, 9, 10, 11, 12, 13 | **CI check:** "Policy check" step passes on the change. `tools/ci/test_check_policy.py` tests (Test plan) pass. |
| AC-QSC-6 | 14, 15 | **CI check:** the `check` job summary shows an "Envoy.Core coverage" table with a Total row and rows for `Chat`, `Ocr`, `Translation`, `Pipeline`, `Phrases`, `Settings`, `Languages` and `Updates`. The `coverage` artifact contains `coverage.cobertura.xml`. |
| AC-QSC-7 | 16 | `tools/ci/test_coverage_summary.py::test_fails_below_line_threshold` and `test_fails_below_branch_threshold` pass. **CI check:** the real run passes at 70/55. |
| AC-QSC-8 | 17, 18 | **CI check:** the `CodeQL` workflow is green on the PR. **Manual:** Security → Code scanning lists analyses for `csharp` and `actions`. |
| AC-QSC-9 | 19 | **CI check:** `Dependency review / review` runs on the PR and passes. **Manual (once, throwaway PR):** adding `<PackageReference Include="System.Text.Json" Version="8.0.0" />` to Core makes the review fail (high-severity advisory). Close the PR. |
| AC-QSC-10 | 20 | **Manual (once, same throwaway branch):** the "Vulnerable packages" step fails and names the package. |
| AC-QSC-11 | 21, 22 | **CI check:** the `windows` job's SBOM step passes (components present). **Manual:** after pushing a prerelease tag (for example `v0.2.0-rc.1`, or on a fork), the release contains `Envoy.cdx.json`, and `gh attestation verify Envoy.exe -R <owner>/<repo>` succeeds on the downloaded exe. |
| AC-QSC-12 | 23, 24, 25, 27 | **CI check:** the policy `workflow` rule passes for all three workflows. **Review:** `windows` and `check` have no `permissions` override granting write. `dependabot.yml` lists `/tools/typecheck`. |
| AC-QSC-13 | 26 | **CI check / manual:** in the PR description, the implementer records the durations of `check` and of push→`windows`-artifact for the PR run and for the previous `main` run. `check` ≤ 8 min and the delta ≤ 2 min. |
| AC-QSC-14 | 28 | `OutgoingTextTests` (all tests in the Test plan) pass. |
| AC-QSC-15 | 29 | `SendTargetGuardTests` pass. Typecheck passes. **Manual in-game** M3 and M4 pass. |
| AC-QSC-16 | 28, 29 | **Manual in-game** M1 and M2 pass. |
| AC-QSC-17 | 30 | `LaunchTargetTests` pass. Typecheck passes (RS0030 proves `ShellLauncher` is the only `Process.Start`). **Windows smoke test** passes unchanged. **Manual:** About page links open in the browser; Phrasebook "Edit" opens `phrases.json`; "Open data folder" and "Open logs" open Explorer; the OCR-language help opens Windows Settings. |
| AC-QSC-18 | 31 | `UpdateUrlTests` pass. The existing `UpdateCheckerTests` pass unchanged. |
| AC-QSC-19 | 32 | `PhraseBookLimitsTests` pass. The existing `PhraseBookTests` pass. |
| AC-QSC-20 | 33, 34 | `SettingsSecretTests` pass. |
| AC-QSC-21 | 35 | (COULD) `EndpointPolicyTests` pass. Typecheck passes. The Windows smoke test (Translation page in every theme) passes. |
| AC-QSC-22 | 36, 37 | `docs/security/threat-model.md` exists with T1–T14. The policy `threat-model-refs` rule passes. |
| AC-QSC-23 | 38, 39, 40, 41, 42 | **Review:** the docs and files exist with the sections listed in Design §8–9. **Manual (maintainer):** settings are applied per `repository-settings.md`. `gh api repos/PinnerSinner/Envoy/rulesets` lists the `main` and `v*` rulesets, and secret scanning push protection shows "Enabled". |
| AC-QSC-24 | 43 | **CI check:** the `app-tests` job is green and runs `HotkeyTests`. |

## Test plan

### Unit tests: `tests/Envoy.Core.Tests/Security/` (new folder; new files avoid conflicts with other areas)

| File | Test | Asserts |
| --- | --- | --- |
| `OutgoingTextTests.cs` | `Newlines_and_tabs_become_single_space` | `"gl\r\n\thf"` → `"gl hf"` |
| | `Control_characters_are_removed` (Theory: U+0000, U+0007, U+001B, U+007F, U+0085, U+009B) | `"a" + c + "b"` → `"ab"` |
| | `Bidi_controls_are_removed` (Theory: U+202A–U+202E, U+2066–U+2069) | removed; surrounding text unchanged |
| | `Keeps_cjk_emoji_and_joiners` | `"你好 👍 👨‍👩‍👧 क्‍ष"` unchanged (ZWJ/ZWNJ kept) |
| | `Collapses_whitespace_and_trims` | `"  a   b  "` → `"a b"` |
| | `Caps_at_max_length_without_splitting_surrogates` | 999 × `a` + `"😀"` with max 1000 → length 999, no lone surrogate |
| | `Default_cap_is_1000` | 5000 chars → 1000 |
| | `Null_and_whitespace_give_empty` | `null`, `""`, `" \n "` → `""` |
| | `Is_idempotent` (Theory over the inputs above) | `Sanitize(Sanitize(x)) == Sanitize(x)` |
| `SendTargetGuardTests.cs` | `No_target_is_NoTarget` | `Check(false, false, 0, 42)` → NoTarget |
| | `Unknown_target_process_is_NoTarget` | `Check(true, true, 0, 0)` → NoTarget |
| | `Closed_target_is_TargetClosed` | `Check(true, false, 10, 10)` → TargetClosed |
| | `Other_foreground_process_is_FocusLost` | `Check(true, true, 10, 11)` → FocusLost |
| | `Same_process_is_Ok` | `Check(true, true, 10, 10)` → Ok |
| | `Every_blocking_verdict_has_a_message` | `Describe` non-empty for all non-Ok values; `""` for Ok |
| `LaunchTargetTests.cs` (data folder = `Path.Combine(Path.GetTempPath(), "envoy-data-test")`) | `Web_urls_are_allowed` (Theory) | `https://github.com/PinnerSinner/Envoy`, `http://localhost:5000/` → WebUrl |
| | `Settings_uri_is_allowed` | `ms-settings:regionlanguage` → SettingsUri |
| | `Paths_inside_data_folder_are_allowed` | the folder itself, `<data>/logs`, `<data>/phrases.json` → DataPath |
| | `Executables_inside_data_folder_are_rejected` (Theory: `.exe .bat .cmd .ps1 .lnk .scr .js .vbs .msi .hta .com .url`) | Rejected |
| | `Paths_escaping_or_beside_data_folder_are_rejected` | `<data>/../x.json`, `<data>2/x.json` (sibling prefix), temp root → Rejected |
| | `Other_schemes_and_forms_are_rejected` (Theory) | `file:///etc/passwd`, `\\server\share\a.json`, `//server/share`, `javascript:alert(1)`, `shell:startup`, `search-ms:query=x`, `ms-msdt:/id`, `relative/a.json`, `""`, `null`, `https://user:pw@example.com` → Rejected |
| `UpdateUrlTests.cs` | `Genuine_release_urls_are_kept` | JSON with `https://github.com/PinnerSinner/Envoy/releases/tag/v1.0.0` and `…/releases/download/v1.0.0/Envoy.exe` → Parse keeps both; `ReleaseUrlPolicy.IsTrusted(url, "pinnersinner/envoy")` true |
| | `Untrusted_page_url_rejects_release` (Theory) | `file:///C:/Windows/System32/calc.exe`, `\\evil\share\x.exe`, `http://github.com/o/r/releases/tag/v1`, `https://github.com.evil.example/o/r/releases/x`, `https://user@github.com/o/r/releases/x`, `https://github.com:8443/o/r/releases/x`, `https://github.com/o/r/archive/x` → Parse returns null |
| | `Untrusted_download_url_is_dropped` | valid `html_url`, asset URL `https://evil.example/Envoy.exe` → `DownloadUrl == null` |
| | `Other_repository_is_not_trusted` | `IsTrusted("https://github.com/other/repo/releases/tag/v1", "PinnerSinner/Envoy")` false |
| | `CheckAsync_ignores_release_of_other_repository` | fake `HttpMessageHandler` returning a newer release whose `html_url` is in another repo → `CheckAsync` returns null |
| `PhraseBookLimitsTests.cs` | `Oversized_user_file_is_ignored` | file of 1 MiB + 1 byte (valid JSON padded with spaces) → `All.Count == LoadBuiltIn().All.Count` |
| | `User_text_is_sanitized` | text `"gl\r\nhf\u202E"` → `In("en") == "gl hf"` |
| | `User_phrase_count_is_capped` | 600 user phrases → 500 user ids present |
| | `Long_fields_are_truncated` | 100-char id/category, 40-char icon → 64/64/16 |
| | `Phrases_without_id_are_dropped` | `{"id":"", …}` not present |
| `SettingsSecretTests.cs` | `Credential_like_properties_are_marked_Secret` | every `string` property in `Envoy.Core.Settings` types matching `(Key\|KeyId\|Secret\|Token\|Password)$` is `[Secret]` or in the allowlist `{ "OpenChatKey" }` (key bindings) |
| | `Saved_file_contains_no_plaintext_secret` | every reachable `[Secret]` set to a unique `MARKER-n`; `Save` with a reversing test protector; the file contains no `MARKER-`; at least 5 secrets found |
| | `Secret_properties_are_reachable_by_store` | every type in the Core assembly with a `[Secret]` property is reachable from `AppSettings` through non-collection properties of types in the settings namespace (the rule `SettingsStore.TransformSecrets` uses) |
| `EndpointPolicyTests.cs` (COULD) | `Classifies_endpoints` (Theory) | `http://localhost:5000` false, `http://127.0.0.1` false, `http://192.168.1.5` false, `http://example.com` true, `https://example.com` false, `not a url` false |

The existing `UpdateCheckerTests`, `PhraseBookTests` and `SettingsStoreTests` must stay green unchanged.

### App tests: `tests/Envoy.App.Tests/HotkeyTests.cs` (SHOULD, Windows CI only)

| Test | Asserts |
| --- | --- |
| `Round_trips` (Theory: `Ctrl+Shift+T`, `Alt+1`, `Win+F9`, `Ctrl+Alt+Space`) | `TryParse` true and `ToString()` returns the input |
| `Digits_map_to_D_keys` | `"Ctrl+1"` → `Key.D1`; `ToString()` == `"Ctrl+1"` |
| `ToString_is_culture_invariant` | under `CultureInfo("ar-SA")` and `("fa-IR")`, `Ctrl+7` still prints `"Ctrl+7"` |
| `Rejects_unknown_tokens` | `"Ctrl+Banana"`, `""`, `null` → false |
| `Rejects_modifier_only` | `"Ctrl+Shift"` → false |

### Script tests: `tools/ci/` (standard-library `unittest`, run in `check`)

| File | Test | Asserts |
| --- | --- | --- |
| `test_check_policy.py` | `test_repository_passes` | `check_policy` on the real repo root returns no violations |
| | `test_denied_api_fails_even_if_allowlisted` | temp tree whose `Native.cs` declares `ReadProcessMemory` and allowlists it → `native-denied` |
| | `test_unlisted_pinvoke_fails` | `[LibraryImport("user32.dll")] … PostMessageW(` → `native-unlisted` |
| | `test_entrypoint_is_used_for_key` | `EntryPoint = "GetWindowLongPtrW"` matches the `user32!GetWindowLongPtrW` allowlist entry |
| | `test_network_type_outside_allowed_paths_fails` | `src/Envoy.Core/Chat/X.cs` naming `HttpClient` → `network-location`; `HttpRequestException` doesn't match |
| | `test_unknown_host_fails_and_schema_hosts_ignored` | `https://telemetry.example` → `host`; `http://schemas.microsoft.com/…` ignored |
| | `test_manifest_requires_asInvoker` | `requireAdministrator` → `manifest`; `uiAccess="true"` → `manifest` |
| | `test_workflow_rules` | write-permission top-level, `@v4` pin and a job without `timeout-minutes` → three `workflow` violations |
| | `test_threat_model_dangling_reference_fails` | doc referencing `src/Nope.cs` and `FooTests.Missing` → two violations |
| `test_coverage_summary.py` | `test_passes_above_threshold` | fixture XML 0.80/0.60 with 70/55 → exit 0, summary has Total and folder rows |
| | `test_fails_below_line_threshold` | 0.65 lines → exit 1 and `::error::` mentioning 70 |
| | `test_fails_below_branch_threshold` | 0.50 branches → exit 1 |
| | `test_writes_to_github_step_summary` | with `GITHUB_STEP_SUMMARY` set to a temp file → the file contains the Markdown table |
| | `test_missing_package_exits_2` | package `Nope` → exit 2 |

### Smoke test

No additions. No new windows. The existing `Envoy.exe --smoke-test` must still pass, which shows that `ShellLauncher`
and the `KeyboardSender` changes don't break start-up or window construction under every theme.

### Manual in-game checks (Windows, any supported AoE/AoM title, a skirmish against AI)

- **M1:** Reply mode "Type it into game chat and send". Open the reply box with the hotkey over the game, type
  "hello", press Enter. "hello" (translated) appears once in game chat, and nothing else happens in the game.
- **M2:** Add a user phrase whose `en` text is `"gl\nhf"` to `%APPDATA%\Envoy\phrases.json`, reload, and send it.
  Chat shows one message `gl hf`. No units are selected or commanded by stray letters.
- **M3:** Set "Delay between keystrokes" to 100 ms. Send a 60-character reply and alt-tab to Notepad after about 1 s.
  Typing stops; Notepad receives no characters; the message says another window came to the front; the text is on
  the clipboard.
- **M4:** Open the reply box over the game, close the game, then press Send. Nothing is typed anywhere. The message
  says the window closed, and the text is on the clipboard.
- **M5:** About page → GitHub link opens the browser. "Open data folder" opens Explorer at `%APPDATA%\Envoy`.

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `Directory.Build.props` | new: shared properties, analyzers, BannedApiAnalyzers, CI warnings-as-errors | |
| `BannedSymbols.txt` | new | |
| `.editorconfig` | append the analyzer policy block under `[*.cs]` | yes (all areas edit code it governs) |
| `tests/.editorconfig`, `src/Envoy.App/.editorconfig` | new, rule relaxations | |
| `.config/dotnet-tools.json` | new: CycloneDX pinned | possibly (06 installs `vpk` globally, not here) |
| `src/Envoy.Core/Envoy.Core.csproj` | remove shared props; nullable-as-errors | yes (other areas add packages) |
| `src/Envoy.App/Envoy.App.csproj` | remove shared props; `TreatWarningsAsErrors=false` | yes (06 adds Velopack) |
| `tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj` | remove shared props; nullable-as-errors; `coverlet.collector` | yes |
| `tools/typecheck/TypeCheck.csproj` | remove duplicated shared props | yes (06 adds Velopack pin) |
| `tests/coverage.runsettings` | new | |
| `tests/Envoy.App.Tests/Envoy.App.Tests.csproj`, `HotkeyTests.cs` | new (SHOULD) | |
| `Envoy.sln` | add `Envoy.App.Tests` | yes |
| `tests/Envoy.Core.Tests/Security/*.cs` | new test files (6, plus 1 COULD) | |
| `tests/Envoy.Core.Tests/TranslationTests.cs` | CA1310 fix (line 300) | yes (03 edits this file) |
| `src/Envoy.Core/Security/OutgoingText.cs`, `SendTargetGuard.cs`, `LaunchTarget.cs`, `ReleaseUrlPolicy.cs` (+ `EndpointPolicy.cs` COULD) | new | |
| `src/Envoy.Core/Updates/UpdateChecker.cs` | trusted-URL checks in `Parse` and `CheckAsync` | yes (06 rewrites this file) |
| `src/Envoy.Core/Phrases/PhraseBook.cs` | `PhraseLimits`, size check, `Clean`, static options (CA1869) | yes (07 adds load-error reporting) |
| `src/Envoy.Core/Chat/TranscriptWriter.cs` | CA1305 fix | |
| `src/Envoy.Core/Languages/LanguageCatalog.cs`, `ScriptDetector.cs` | CA1307 fixes | |
| `src/Envoy.Core/Translation/Providers/ClaudeTranslator.cs`, `EdgeFreeTranslator.cs` | CA1307 fix; CA1001 `[SuppressMessage]` | yes (03 edits providers) |
| `src/Envoy.App/Services/KeyboardSender.cs` | sanitise, guard, `SendBlockedException` | possibly (07 reply polish) |
| `src/Envoy.App/Services/ShellLauncher.cs` | new | |
| `src/Envoy.App/App.xaml.cs` | `OpenUrl` body → `ShellLauncher.TryOpen(url)` (one line; signature unchanged) | **SHARED: App.xaml.cs** |
| `src/Envoy.App/Windows/ComposeWindow.xaml.cs` | `OutgoingText.Sanitize` in send and copy paths (two lines) | yes (02 and 07 edit the catch and the reply box) |
| `src/Envoy.App/Services/HotkeyService.cs` | CA1305 fix | |
| `src/Envoy.App/Services/ScreenCapturer.cs` | CA1806 fixes (`_ =`) | yes (05 rewrites capture) |
| `src/Envoy.App/AppHost.cs` | **no change** | SHARED, untouched |
| `src/Envoy.Core/Settings/AppSettings.cs` | **no change** | SHARED, untouched |
| `src/Envoy.App/SmokeTest.cs` | **no change** | SHARED, untouched |
| `src/Envoy.App/Windows/MainWindow*` | **no change** (COULD §6.7 adds one callout to the Translation page) | SHARED (MainWindow*) only if the COULD is built |
| `.github/workflows/build.yml` | permissions, concurrency, cache, timeouts, SHA pins, policy, coverage, vulnerable packages, SBOM, `app-tests`, `screenshots` and `release` jobs | yes (06 rewrites the `windows` job and release steps) |
| `.github/workflows/codeql.yml`, `dependency-review.yml` | new | |
| `.github/dependabot.yml` | NuGet `directories` adds `/tools/typecheck` | |
| `.github/CODEOWNERS` | new | |
| `.github/pull_request_template.md`, `.github/ISSUE_TEMPLATE/bug_report.yml` | security checkbox; "don't paste keys" note | |
| `tools/ci/check_policy.py`, `policy.json`, `coverage_summary.py`, `test_check_policy.py`, `test_coverage_summary.py` | new | |
| `docs/security/threat-model.md`, `docs/security/repository-settings.md` | new | |
| `docs/releasing.md` | release checklist section | yes (06 edits it) |
| `SECURITY.md` | supported versions, verification, contributor guidance, threat-model link | |
| `docs/building.md`, `CLAUDE.md`, `CONTRIBUTING.md` | analyzers, coverage commands, policy check, `CI=true` tip | yes (CLAUDE.md is read by every session) |
| `CHANGELOG.md` | Unreleased: reply now refuses to type into the wrong window; phrases.json limits; SBOM and attestation on releases | yes |

Estimated size: about 350 lines of C# (production and tests), about 350 lines of Python, about 200 lines of
workflow/config and about 300 lines of docs.

## Risks & open questions

1. **Merge order with other areas.** Analyzers with `TreatWarningsAsErrors` under CI will flag culture and banned
   API issues in code other areas write. Recommendation: land this area's §1–§2 (props, rules, banned APIs, the
   baseline fixes) **right after 01-ui-foundation**, so every later area is written against the rules. Land §5's
   workflow restructure **after 06**, because 06 rewrites the same `windows` job. If that order isn't possible, the
   integrator can temporarily add `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` in a single area's csproj,
   but never in `Directory.Build.props`.
2. **06 workflow overlap.** 06 keeps signing, packing and `vpk upload` in the `windows` job. This spec moves the
   upload steps into a separate `release` job on `windows-latest`, so the build job keeps a read-only token. This
   assumes `vpk upload github` only needs the `Releases/` folder plus a token, which matches 06's step 12 inputs.
   If it turns out to need the build tree, fall back: keep release in `windows` with `contents: write`, and record
   it as an accepted exception in the threat model (T10).
3. **Windows-only warnings.** The real WPF build was not observed here. It might emit warnings the typecheck can't
   see (markup compiler, WinRT projections). That is why `Envoy.App` keeps warnings non-fatal on Windows
   (REQ-QSC-7). **Open:** once one Windows CI run shows zero warnings, flip it to strict.
4. **CycloneDX CLI flags.** Option names changed across CycloneDX .NET major versions (`--json` became
   `--output-format`). The implementer pins one version in `.config/dotnet-tools.json` and adjusts flag names to its
   `--help`. The component-presence check (REQ-QSC-21) catches a wrong invocation. It is also unverified whether
   CycloneDX includes project-reference (`Envoy.Core`) packages by default. If not, add its recursive or
   project-reference option; the presence check for `Anthropic` and `AWSSDK.Translate` enforces this.
5. **Dependency-review license data.** NuGet packages sometimes expose only a license URL, which dependency review
   reports as unknown. If that blocks legitimate PRs, set `license-check: false` and rely on
   `THIRD-PARTY-NOTICES.md` review. Severity checking is the MUST part.
6. **CodeQL action major.** `github/codeql-action` moved to a new major in 2025. Pin whatever is current by SHA.
   `build-mode: none` for C# and the `actions` language both need a reasonably recent CodeQL bundle, which the
   current action provides.
7. **Send guard false positives.** Some games or launchers might report a different foreground process for their
   render window (for example an overlay or anti-cheat window owned by another process). The failure mode is safe:
   nothing is typed and the text goes to the clipboard. **Open:** confirm with the manual M1 check on each supported
   title before 1.0. If one fails, add a calibration escape hatch (a list of extra process names to trust per game
   profile) rather than dropping the guard.
8. **`native-denied` whole-word scan** forbids those identifiers even in comments. That is deliberate, but a
   contributor writing "we never call OpenProcess" will trip it. The error message explains why.
9. **Coverage thresholds vs. other areas.** If several areas land large, network-heavy Core code (for example 06's
   Velopack backend) with thin tests, total coverage could dip under 70 %. The gate then does its job. The fix is
   tests, or `[ExcludeFromCodeCoverage]` on thin SDK adapters with a justification, never lowering the threshold
   without a CHANGELOG-visible reason.
10. **Banned `Process.Start` and other areas.** 02 (open log folder), 06 (`LaunchUpdaterAfterExit`, which uses
    Velopack's API, not `Process.Start`) and 07 (open URLs) must route through `ShellLauncher`. If an area really
    needs to start a process (for example 06 restarting Envoy itself), it adds a narrowly named method next to
    `ShellLauncher.TryOpen`, such as `ShellLauncher.RestartSelf(string[] args)` limited to
    `Environment.ProcessPath`, with its own `#pragma` and test. It does not get a blanket suppression.
11. **Out of scope follow-ups:** OpenSSF Scorecard workflow, NuGet lock files with `--locked-mode` (needs
    `RuntimeIdentifiers` set unconditionally in `Envoy.App.csproj` so Debug and Release restores agree),
    fuzzing `ChatLineParser` and `OutgoingText` with SharpFuzz, signed commits, and a `global.json` SDK pin
    (deliberately omitted: `AnalysisLevel` is pinned instead, so contributors with only a newer SDK aren't blocked).
