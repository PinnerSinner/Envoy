# CI and release: workflow restructure, coverage, scanning, SBOM, OCR end-to-end, release pass

Status: Implemented — revised against `00-integration.md` (binding). Lands as step 11 of 11; see Deviations.

Area id: `08b-ci-release` · Requirement prefix: `QSC` (ids keep the numbers from the old `08-quality-security-ci.md`)
· **Step 11 of 11 (last).** It depends on steps 1–10, in particular 08a (policy check, analyzers, the tool manifest),
02 (smoke three-phase structure, `error.log` removed), 05 (`FrameProcessing`, `MergeLines`, `--soak`), 04
(`ChatPipeline.Analyze`, golden captures), 06 (the Velopack release steps, tag-only signing, `tools/release/*`) and
07a (`docs/privacy.md`).

This area restructures `build.yml` into least-privilege jobs, moves signing and publishing into a tag-only `release`
job, adds the coverage gate, the vulnerable-package gate, CodeQL, the SBOM and a CI time budget, adds the OCR
end-to-end test on Windows, and writes the threat model, `SECURITY.md`, the consolidated pre-release pass and the
compatibility matrix. It adds no product feature, setting, window, P/Invoke or network code.

When this file and `00-integration.md` disagree, the contract wins.

---

## Context

### What exists after steps 1–10

| Concern | State | Where |
| --- | --- | --- |
| `check` job (Ubuntu) | Steps: unit tests; `typecheck.sh` (01 UI rules, 07b `check_a11y.py`); 08a `check_policy.py` + `tools/ci` tests; 06 `check_velopack_pin.sh`, `check_signing_scope.py`, `check_release_assets.py --self-test`. No coverage, no vulnerability gate. | `build.yml` |
| `windows` job | Covers everything else in this order: version, notes, publish, smoke test (`WaitForExit(600000)`, 01), screenshots, the orphan screenshots-branch push, then 06's tag-only signing, folder publish, `vpk pack`, checksums, `vpk upload`/`gh release` and winget. It runs under `environment: ${{ startsWith(github.ref,'refs/tags/v') && 'release' \|\| '' }}` with job-level `contents: write`. The smoke step still prints the `error.log` that 02 made dead. | `build.yml` |
| Workflow hygiene | Workflow-wide `permissions: contents: write`. Actions are pinned by tag. There are no `timeout-minutes`, no `concurrency` and no NuGet cache. | `build.yml` |
| Tool manifest | `.config/dotnet-tools.json` (08a) pins `vpk` (06). | — |
| Tests | `tests/Envoy.Core.Tests` (golden captures from 04, fixtures from 03). There is no app-level test project, and no test runs the real `Windows.Media.Ocr` on known text: 05's smoke OCRs the runner desktop only. | — |
| Coverage | Not collected. At `f540fc4` it measured 74.1 % lines and 60.7 % branches for Envoy.Core. 02–07 have since added Core code with tests, so it is re-measured at the start of this step. | measurement |
| Release docs | `docs/releasing.md` holds per-area manual steps appended by 01–07c, each with its own numbering (01 `M1–M3`, 02 `M1–M9`, 04 `M-CHAT-n`, 05 `M-CAP-n`, 06 `M-DIST-n`, 08a `M-QSC-1…5`, …). It has no order, no checklist and no compatibility matrix. `SECURITY.md` has no supported-versions or verification section. There is no threat model. | `docs/` |

### What is weak or missing for 1.0

- The job that builds and runs code from any branch holds a write token, and actions are tag-pinned.
- Signing secrets are reachable from a job that also runs the app (only tag runs, but still the same job).
- Nothing stops coverage from eroding or a vulnerable package from shipping. There is no code scanning and no SBOM.
- Real OCR on known chat text is never tested, so an OCR or merge regression is invisible until a player reports it.
- The manual checks from eight areas are scattered, and no one records which titles, display modes, DPI settings and
  Windows versions were verified.

## Goals / Non-goals

**Goals**

- Least-privilege jobs: build and test jobs are read-only, and write access and secrets sit only in short jobs that
  run no project code.
- CI gates: a coverage floor, vulnerable packages, CodeQL, SBOM presence and a time budget.
- An OCR end-to-end test on the Windows runner, with en-US on every build.
- One ordered pre-release pass and one compatibility matrix. A threat model whose rows point at real code and tests.

**Non-goals**

- Analyzers, banned APIs, the policy check and the security types (08a, already landed).
- Velopack packaging, signing mechanics and update logic (06). This step only **moves** 06's tag-only steps into the
  `release` job.
- Fuzzing, mutation testing, OpenSSF Scorecard, signed commits, NuGet lock files (follow-ups).

## Requirements

MUSTs for this area (contract §9): QSC-14, 17, 20, 21, 23, 26, 36, 38, 40, 44 (**10**).

### Policy check additions and script tests

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-12 | SHOULD | The policy check fails when a non-local `uses:` is not pinned to a 40-hex commit SHA, or when a job has no `timeout-minutes`. |
| REQ-QSC-13 | SHOULD | `tools/ci/test_*.py` (`unittest`) cover every script this area adds and every new policy rule, and run in `check`. |

### Coverage

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-14 | MUST | The `check` job collects Cobertura coverage for `Envoy.Core` with `coverlet.collector`, excluding `[GeneratedCode]` and `[ExcludeFromCodeCoverage]` code. It **fails when line coverage is below 70 % or branch coverage is below 55 %** (folded from REQ-QSC-16). |
| REQ-QSC-15 | SHOULD | The `check` job writes a Markdown coverage table (total plus one row per top-level Core folder) to `$GITHUB_STEP_SUMMARY`, and uploads the XML as the `coverage` artifact. |
| REQ-QSC-16 | — | Folded into REQ-QSC-14. |

### Supply chain, scanning and workflow hygiene

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-17 | MUST | A `CodeQL` workflow analyses C# (`build-mode: none`, `security-extended`) on pushes to `main`, PRs to `main` and weekly. Results go to code scanning. |
| REQ-QSC-18 | SHOULD | CodeQL also analyses the workflows (`actions` language). |
| REQ-QSC-19 | SHOULD | A `dependency-review` workflow fails a PR that adds a dependency with a known `high`+ vulnerability. |
| REQ-QSC-20 | MUST | The `check` job fails when `dotnet list package --vulnerable --include-transitive` reports a `High` or `Critical` advisory for `tests/Envoy.Core.Tests` (covers Core) or `tools/typecheck/TypeCheck.csproj` (mirrors the app's packages, including Velopack). |
| REQ-QSC-21 | MUST | Every `windows` run produces `Envoy.cdx.json`, a CycloneDX JSON SBOM of `Envoy.App` with transitive packages and `Envoy.Core`'s dependencies. It fails if the SBOM lacks `Anthropic`, `AWSSDK.Translate`, `System.Security.Cryptography.ProtectedData` or `Velopack`. Every tagged release has the file attached. |
| REQ-QSC-22 | SHOULD | Tagged releases have a build-provenance attestation for `Envoy.exe` and `Envoy-win-Setup.exe`. |
| REQ-QSC-23 | MUST | Every workflow declares top-level `permissions: contents: read`. Only the `screenshots` and `release` jobs get `contents: write`, and CodeQL's job gets `security-events: write`. The signing secrets are referenced only in the tag-only `release` job, which runs under `environment: release` (contract §8.8). |
| REQ-QSC-24 | SHOULD | Third-party actions are pinned to a full commit SHA with the tag in a trailing comment. Dependabot keeps them current. |
| REQ-QSC-25 | SHOULD | `build.yml` cancels superseded non-tag runs, every job has `timeout-minutes`, and NuGet packages are cached. |
| REQ-QSC-26 | MUST | The `check` job takes at most 8 minutes, and the `windows` job at most 20 minutes of wall time, excluding tag-only release steps (contract §6). A budget step at the end of each job fails it when the budget is exceeded. CodeQL and dependency review run as separate workflows and never gate `windows`. |
| REQ-QSC-27 | SHOULD | Dependabot also watches `/tools/typecheck` (NuGet). |

### Documentation and process

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-36 | MUST | `docs/security/threat-model.md` lists assets, trust boundaries, an egress table and threats T1–T16. Each threat has mitigations, code files, the tests or checks that verify it, and residual risk. The policy rule `required-docs` checks that every T-row exists and that its "Verified by" cell is not empty. |
| REQ-QSC-37 | SHOULD | The policy check fails when a back-ticked repository path or `ClassName.TestName` in the threat model does not exist. |
| REQ-QSC-38 | MUST | `docs/releasing.md` has a release checklist (before tagging, tag, after release) and **one ordered pre-release manual pass**, which gathers every area's manual steps under uniform ids (`M-<AREA>-n`). The pass includes 04's real-capture calibration and 05's 120-minute soak. `docs/compatibility.md` holds the matrix of supported titles × display mode × DPI × Windows version, with the version last verified in each cell (contract §8.3). `required-docs` checks the structure of both files. |
| REQ-QSC-39 | SHOULD | `docs/security/repository-settings.md` lists the recommended repository settings (rulesets, required checks, secret scanning with push protection, Dependabot, Actions token defaults, 2FA, the `release` environment). |
| REQ-QSC-40 | MUST | `SECURITY.md` states the supported versions and explains how to verify a download (`SHA256SUMS.txt`, Authenticode signature, attestation, SBOM). It links the threat model and `docs/privacy.md`, and tells contributors never to commit real keys. `required-docs` checks the headings and links. |
| REQ-QSC-41 | SHOULD | `.github/CODEOWNERS` assigns the security-sensitive paths (`tools/ci/`, `Interop/`, `Security/`, workflows, …) to the maintainer. |
| REQ-QSC-42 | SHOULD | The bug-report template warns against pasting `settings.json` or keys. The PR template gains a "security-sensitive change" checkbox. |

### App-level tests and OCR end-to-end

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-QSC-43 | SHOULD | `tests/Envoy.App.Tests` (the REQ-QSC-44 project) also holds `Hotkey` parsing and formatting tests. |
| REQ-QSC-44 | MUST | `tests/Envoy.App.Tests` (net8.0-windows, xUnit) runs an OCR end-to-end harness in the `app-tests` CI job on every build (contract §8.4). The harness renders known chat lines at 14–18 px, with outline or shadow, in system fonts. It runs them through 05's preprocessing, the real `Windows.Media.Ocr`, `OcrMerger.MergeLines` and `ChatPipeline.Analyze`, and asserts sender equality and `TextSimilarity.Similarity ≥ 0.85`. The en-US cases are required. A non-required language whose recognizer is missing reports `Skipped (no recognizer)` in the job summary instead of failing. |
| REQ-QSC-45 | SHOULD | The `app-tests` job installs the `Language.OCR` capabilities for ja-JP, zh-CN, ko-KR and ru-RU before the tests, so that those cases run. |
| REQ-QSC-46 | SHOULD | `Envoy.exe --ocr-e2e <report.md>` (the contract §4 slot 3 smoke branch) runs the same cases inside the published exe and writes the Markdown report. The release pass uses it on Windows 10. |

REQ-QSC-1 to 11 and 28 to 35 are in `08a-quality-gates.md`.

## Design

### 1. Job graph

```text
build.yml
  check (ubuntu, ≤8 min) ──┬──► windows (windows, ≤20 min, read-only) ──┬──► screenshots (ubuntu; main pushes; contents: write)
                           │                                              └──► release (windows; v* tags; environment: release;
                           └──► app-tests (windows, read-only) ──────────────┘    contents: write, id-token, attestations)
codeql.yml            analyze ×{csharp, actions} (security-events: write) — separate, never gates windows
dependency-review.yml review (PRs only) — separate
```

`app-tests` runs in parallel with `windows`, so it does not count toward the `windows` budget. `release` waits for
both jobs. `windows` builds and runs code from any pushed branch, so its token is read-only. The jobs with write
access (`screenshots`, `release`) build and run no project code. They only push files, sign them and upload them.

### 2. `build.yml` (restructured)

All third-party `uses:` are written `owner/action@<sha> # vN`. The implementer resolves each SHA with
`gh api repos/<owner>/<action>/commits/<tag> --jq .sha`.

```yaml
permissions:
  contents: read

concurrency:
  group: build-${{ github.ref }}
  cancel-in-progress: ${{ !startsWith(github.ref, 'refs/tags/') }}

jobs:
  check:
    runs-on: ubuntu-latest
    timeout-minutes: 12                       # hard stop; the 8-min budget is enforced by the last step
    steps:
      - run: echo "JOB_START=$(date +%s)" >> "$GITHUB_ENV"
      - uses: actions/checkout@<sha> # v4
      - uses: actions/setup-dotnet@<sha> # v4
        with: { dotnet-version: 8.0.x }
      - uses: actions/cache@<sha> # v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', 'Directory.Build.props', '.config/dotnet-tools.json') }}
          restore-keys: nuget-${{ runner.os }}-
      - name: Policy check                                  # 08a, plus this area's rules
        run: |
          python3 tools/ci/check_policy.py
          python3 -m unittest discover -s tools/ci -p "test_*.py"
      - name: Release tooling checks                        # 06's steps, unchanged apart from check_signing_scope's new shape
        run: |
          tools/release/check_velopack_pin.sh && tools/release/check_velopack_pin.sh --self-test
          python3 tools/release/check_signing_scope.py && python3 tools/release/check_signing_scope.py --self-test
          python3 tools/release/check_release_assets.py --self-test
      - name: Unit tests with coverage
        run: >
          dotnet test tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj -c Release
          --collect "XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory TestResults
      - name: Coverage gate
        run: python3 tools/ci/coverage_summary.py --package Envoy.Core --min-line 70 --min-branch 55 TestResults/*/coverage.cobertura.xml
      - uses: actions/upload-artifact@<sha> # v4
        if: always()
        with: { name: coverage, path: TestResults/*/coverage.cobertura.xml, if-no-files-found: ignore }
      - name: Type-check Windows app
        run: tools/typecheck/typecheck.sh
      - name: Vulnerable packages
        run: python3 tools/ci/check_vulnerable.py tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj tools/typecheck/TypeCheck.csproj
      - name: Time budget
        if: always()
        run: python3 tools/ci/job_budget.py --label check --max-minutes 8

  app-tests:
    runs-on: windows-latest
    needs: check
    timeout-minutes: 25
    env: { OCR_E2E_SUMMARY: "${{ runner.temp }}/ocr-e2e.md" }
    steps:
      - uses: actions/checkout@<sha> # v4
      - uses: actions/setup-dotnet@<sha> # v4
        with: { dotnet-version: 8.0.x }
      - uses: actions/cache@<sha> # v4      (same key scheme)
      - name: OCR language capabilities (SHOULD, REQ-QSC-45)
        continue-on-error: true
        timeout-minutes: 10
        shell: pwsh
        run: |
          foreach ($t in 'ja-JP','zh-CN','ko-KR','ru-RU') {
            Add-WindowsCapability -Online -Name "Language.OCR~~~$t~0.0.1.0" | Out-Null
          }
      - name: App tests (OCR end-to-end, hotkeys)
        shell: pwsh
        run: dotnet test tests/Envoy.App.Tests/Envoy.App.Tests.csproj -c Debug
      - name: OCR summary
        if: always()
        shell: pwsh
        run: if (Test-Path $env:OCR_E2E_SUMMARY) { Get-Content $env:OCR_E2E_SUMMARY >> $env:GITHUB_STEP_SUMMARY }

  windows:
    runs-on: windows-latest
    needs: check
    timeout-minutes: 30                       # hard stop; the 20-min budget is enforced by the budget step
    outputs: { version: …, pack: …, tagged: …, prerelease: … }   # 06's version step outputs
    steps:
      - shell: pwsh
        run: '"JOB_START=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())" >> $env:GITHUB_ENV'
      - checkout, setup-dotnet, cache, `dotnet tool restore`
      - 06 step 1 Version, 06 step 2 Release notes (writes notes.md)
      - 06 step 3 Publish single-file exe
      - Smoke test (01's WaitForExit(600000); the dead error.log print removed; smoke.log kept as an artifact)
      - Screenshots artifact (unchanged)
      - name: SBOM
        shell: pwsh
        run: |
          dotnet CycloneDX src/Envoy.App/Envoy.App.csproj --output publish --filename Envoy.cdx.json `
            --output-format Json --set-name Envoy --set-version "${{ steps.version.outputs.pack }}"
          python tools/ci/check_sbom.py publish/Envoy.cdx.json --require Anthropic AWSSDK.Translate `
            System.Security.Cryptography.ProtectedData Velopack
      - Warning count to the summary (08a REQ-QSC-7, SHOULD)
      - 06 steps 7 + 9 on main pushes only (SHOULD, REQ-DIST-4): folder publish and an unsigned vpk pack
      - Envoy.exe.sha256 (untagged runs; tag runs compute it after signing in `release`)
      - Artifacts: `Envoy-<v>-win-x64` (Envoy.exe, Envoy.cdx.json, notes.md), `smoke-log`
      - name: Time budget
        if: always()
        run: python tools/ci/job_budget.py --label windows --max-minutes 20 --smoke-log smoke.log
      # ---- tag-only, after the budget step (excluded from the budget, contract §6) ----
      - 06 step 7 on tags: folder publish → artifact `publish-velo`

  screenshots:
    if: github.event_name == 'push' && github.ref == 'refs/heads/main'
    needs: windows
    runs-on: ubuntu-latest
    timeout-minutes: 5
    permissions: { contents: write }
    steps:
      - checkout; download artifact `screenshots` (continue-on-error)
      - the same orphan-branch push as today, in bash; "no screenshots" → exit 0

  release:
    if: startsWith(github.ref, 'refs/tags/v')
    needs: [windows, app-tests]
    runs-on: windows-latest                    # vpk --signTemplate and signtool need Windows
    environment: release                       # required reviewers; holds ENVOY_SIGN_PFX_BASE64/_PASSWORD
    timeout-minutes: 30
    permissions: { contents: write, id-token: write, attestations: write }
    steps:
      - checkout (tools/release scripts, icon, CHANGELOG); setup-dotnet; `dotnet tool restore` (vpk)
      - download artifacts `Envoy-<v>-win-x64` → publish/, `publish-velo` → publish-velo/
      - 06 step 5 signing setup and 06 step 6 sign publish/Envoy.exe, then write publish/Envoy.exe.sha256
      - 06 step 8 previous release for deltas
      - 06 step 9 vpk pack (tag variant, with --signTemplate when signing == 'true')
      - 06 step 10 SHA256SUMS.txt
      - uses: actions/attest-build-provenance@<sha> # v2       (SHOULD, REQ-QSC-22)
        with: { subject-path: "publish/Envoy.exe\nReleases/Envoy-win-Setup.exe" }
      - 06 step 12 release: vpk upload … --merge; gh release upload … publish/Envoy.exe publish/Envoy.exe.sha256
          SHA256SUMS.txt publish/Envoy.cdx.json --clobber; check_release_assets.py; gh release edit --draft=false
      - 06 step 13 winget manifests (SHOULD)
```

The smoke-tested bytes and the released bytes differ only by the Authenticode signature. 06 already relies on this,
because it signs after the smoke test. 06's step bodies move verbatim, and only their job changes.

**Changes to 06's tools.**

- `tools/release/check_signing_scope.py` (06) now checks the new shape:
  - every `secrets.ENVOY_SIGN_` reference is inside the `release` job;
  - that job has `environment: release` and `if: startsWith(github.ref, 'refs/tags/v')`;
  - no other job declares an `environment:`.

  Its `--self-test` fixtures are updated to match.
- `tools/release/check_release_assets.py` (06) adds `Envoy.cdx.json` to the expected asset names.

### 3. Policy check additions (`tools/ci/check_policy.py`, `policy.json`)

| Rule id | Backs | What it checks (line-based regex, no YAML library) |
| --- | --- | --- |
| `workflow-permissions` | 23 (MUST) | Each `.github/workflows/*.yml` has a column-0 `permissions:` block before `jobs:` that grants no `write`. A job-level `permissions:` that grants `write` is allowed only for the jobs listed in `workflows.writeJobs`. |
| `workflow-pins` | 12 | Every `uses:` that does not start with `./` or `docker://` matches `@[0-9a-f]{40}`. |
| `workflow-timeouts` | 12, 25 | The number of `timeout-minutes:` lines at job indentation is at least the number of `runs-on:` lines. |
| `required-docs` | 36, 38, 40 (MUST) | Each file in `requiredDocs` exists and contains its listed headings and links. In the threat model, each `T1`…`T16` row exists with a non-empty last-but-one cell ("Verified by"). In `releasing.md`, the manual pass has at least one step for each prefix in `stepPrefixes`, and step ids are unique. In `compatibility.md`, every title × mode row exists. |
| `threat-model-refs` | 37 | Back-ticked tokens in the threat model that start with `src/`, `tests/`, `tools/`, `docs/` or `.github/` exist. Each `[A-Z]\w*Tests\.\w+` token matches a method in `tests/**/*.cs`. |

`policy.json` additions:

```json
"workflows": { "writeJobs": { "build.yml": ["screenshots", "release"], "codeql.yml": ["analyze"] } },
"requiredDocs": {
  "docs/security/threat-model.md": { "headings": ["Assets", "Trust boundaries", "Egress", "Threats", "Out of scope", "Review log"],
                                     "threats": 16 },
  "docs/releasing.md": { "headings": ["Before tagging", "Pre-release manual pass", "Tag", "After the release"],
                         "stepPrefixes": ["M-UIF", "M-REL", "M-TRR", "M-CAP", "M-CHAT", "M-DIST", "M-OUX", "M-QSC"] },
  "docs/compatibility.md": { "titles": ["Age of Empires: Definitive Edition", "Age of Empires II: Definitive Edition",
                                        "Age of Empires III: Definitive Edition", "Age of Empires IV", "Age of Mythology: Retold"],
                             "modes": ["Windowed", "Borderless", "Exclusive fullscreen"] },
  "SECURITY.md": { "headings": ["Supported versions", "Verifying a download", "For contributors"],
                   "links": ["docs/security/threat-model.md", "docs/privacy.md"] }
}
```

The title strings follow `GameCatalog`'s display names at implementation time.

### 4. Coverage

`tests/coverage.runsettings` (new) sets: Cobertura format, `Include [Envoy.Core]*`,
`ExcludeByAttribute GeneratedCodeAttribute,ExcludeFromCodeCoverageAttribute` and `SkipAutoProps true`.
`tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj` gains `coverlet.collector` (6.0.4, `PrivateAssets=all`).

`tools/ci/coverage_summary.py --package Envoy.Core --min-line 70 --min-branch 55 <xml>…` (standard library only):

- It reads the package `line-rate`/`branch-rate` for the totals.
- For the per-folder rows (SHOULD) it groups `<class filename>` by first path segment, counts unique `(file, line)`
  pairs and hits, and sums branch `condition-coverage` "(a/b)".
- It writes the Markdown table to `$GITHUB_STEP_SUMMARY` when that variable is set, otherwise to stdout.
- Exit codes: 0 at or above both thresholds; 1 below, with `::error::Envoy.Core line coverage 68.2% is below 70%`;
  2 for missing or invalid input or a missing package.

The thresholds are fixed by the contract (70 % lines, 55 % branches). If the re-measurement at the start of this step
is below them, this step adds Core tests until it passes. It never lowers a threshold. `[ExcludeFromCodeCoverage]` is
allowed only on thin SDK adapters, with a justification comment. The ratchet policy goes in `docs/building.md`: a PR
that lifts coverage 3 or more points above a threshold raises the threshold. The 1.0 target is 80 % / 65 %.

### 5. Vulnerable packages (`tools/ci/check_vulnerable.py`)

For each project argument it runs
`dotnet list <proj> package --vulnerable --include-transitive --format json`. Both projects are already restored by
the earlier steps. It fails, with `::error::` lines naming the package, version, severity and advisory URL, when any
advisory is `High` or `Critical`. `--from-json <file>` parses a saved output, which is how the unit tests run without
a network. Moderate and Low advisories are printed to the summary and do not fail.

### 6. SBOM

- `.config/dotnet-tools.json` gains CycloneDX (command `dotnet-CycloneDX`, invoked as `dotnet CycloneDX`) at one exact
  version. The flag names in §2 are checked against `--help` for that version.
- `tools/ci/check_sbom.py <file> --require <name>…` parses the JSON and fails when a required component name is
  absent. The windows job runs it on every build, so a broken SBOM shows up long before a tag.
- The release job uploads `Envoy.cdx.json` with the other assets.

### 7. Time budget (`tools/ci/job_budget.py`)

- It reads `JOB_START` (epoch seconds, written by the job's first step) and computes the elapsed minutes.
- It writes `| job | elapsed | budget |` to the summary.
- It exits 1 with `::error::windows took 21.4 min (budget 20)` when over budget.
- With `--smoke-log`, it adds the 5 slowest `[smoke] <section> ok elapsed_ms=<n>` lines and the total.
- It runs with `if: always()`, so the times appear even when an earlier step failed. Then the exit code changes
  nothing.
- `timeout-minutes` is set above the budget (12 and 30), so a slow run reaches the budget step and reports clearly
  instead of being killed.

Expected cost per run (contract §6):

- `check` gains the coverage collector (a few seconds), the summary (under 1 s) and the vulnerable-package queries
  (10–20 s), and the cache saves restore time.
- `windows` gains the SBOM step (20–40 s). It loses the tag-only steps, which move to `release`, and the screenshots
  push, which moves to `screenshots`.
- `app-tests` runs in parallel with `windows`. The OCR capability install is the slow part (SHOULD, capped at 10 min,
  `continue-on-error`).

### 8. CodeQL and dependency review

`.github/workflows/codeql.yml`:

- triggers: push to `main`, PR to `main`, and a weekly cron (`23 4 * * 1`);
- top-level `permissions: contents: read`;
- job `analyze` with `security-events: write`, `actions: read` and `timeout-minutes: 20`;
- matrix `language: [csharp, actions]` (the `actions` entry is SHOULD);
- `github/codeql-action/init` with `build-mode: none` and `queries: security-extended`, then `analyze`;
- all actions SHA-pinned.

`build-mode: none` needs no Windows build. Members generated from XAML are missing, which costs some precision in
`*.xaml.cs` but causes no failures.

`.github/workflows/dependency-review.yml` (SHOULD): it runs on `pull_request`, with `contents: read` and
`timeout-minutes: 5`. It uses `actions/dependency-review-action` with `fail-on-severity: high`. The license check is
off by default: NuGet packages often report a license URL only, which shows up as unknown. `THIRD-PARTY-NOTICES.md`
review covers licenses instead.

### 9. OCR end-to-end (`tests/Envoy.App.Tests`, `src/Envoy.App/OcrEndToEnd.cs`)

**Harness.** `src/Envoy.App/OcrEndToEnd.cs` sits next to `SmokeTest.cs`. The precedent is the same: test harness code
that must run against the real app types. Placing it in the app lets the REQ-QSC-46 `--ocr-e2e` slot reuse it.

```csharp
public enum TextEffect { Outline, Shadow }
public sealed record OcrE2ELine(string Rendered, string Sender, string Text);          // "[All] Bob: gg wp", "Bob", "gg wp"
public sealed record OcrE2ECase(string Id, string OcrLanguage, string Font, double SizePx, TextEffect Effect,
                                IReadOnlyList<OcrE2ELine> Lines, bool Required);
public enum OcrE2EOutcome { Passed, Failed, SkippedNoRecognizer }
public sealed record OcrE2EResult(string CaseId, OcrE2EOutcome Outcome, IReadOnlyList<string> Recognized, string Detail);

public static class OcrEndToEnd
{
    public static IReadOnlyList<OcrE2ECase> Cases { get; }
    /// UI (STA) thread. Draws the lines top to bottom as FormattedText geometry on a dark textured gradient
    /// (game-like), with a 1.5 px dark outline or a (+1,+1) shadow. Returns BGRA as CapturedImage (exact-length copy).
    public static CapturedImage Render(OcrE2ECase c);
    /// Applies the capture path's default scale and FrameProcessing.Preprocess (05, default CaptureOptions), then
    /// OcrService.RecognizeAsync for c.OcrLanguage, OcrMerger.MergeLines(passes, "en"), and ChatPipeline.Analyze(lines)
    /// with default ChatPipelineOptions and a translator that throws if called. For each expected line, there must be
    /// one ChatMessage whose Sender equals it (ordinal) and whose Text has TextSimilarity.Similarity ≥ 0.85.
    /// A missing recognizer gives SkippedNoRecognizer when !Required, and Failed when Required.
    public static Task<OcrE2EResult> RunAsync(OcrE2ECase c);
    public static string ToMarkdown(IEnumerable<OcrE2EResult> results);   // | case | outcome | detail |
}
```

**Cases.**

| Language | Font | Sizes × effects | Lines | Required |
| --- | --- | --- | --- | --- |
| en-US | Segoe UI | 14, 16, 18 px × Outline, Shadow (6 cases) | `[All] Henry: attack the gold at 3` · `[Team] Ada: need wood pls` · `Bob_99: gg wp` | **yes** |
| ja-JP | Yu Gothic UI | 16 px Outline | `Taro: こんにちは、よろしく` | no |
| zh-CN | Microsoft YaHei UI | 16 px Outline | `Wei: 我们去打金矿` | no |
| ko-KR | Malgun Gothic | 16 px Outline | `Minsu: 나무가 필요해` | no |
| ru-RU | Segoe UI | 16 px Outline | `Ivan: нужно больше дерева` | no |

**Test project** `tests/Envoy.App.Tests/Envoy.App.Tests.csproj`:

- `net8.0-windows10.0.19041.0`, `UseWPF`, `EnableWindowsTargeting`, and a `ProjectReference` to `Envoy.App`;
- the same xUnit and test-SDK versions as `Envoy.Core.Tests`;
- added to `Envoy.sln`, and covered by `Directory.Build.props`;
- runs in **Debug** only, because in Release `Envoy.App` is self-contained and a framework-dependent test host cannot
  reference it (NETSDK1151).

The rule for this project: code goes here only when it needs WPF or Windows types.

- `OcrEndToEndTests.Case_passes_or_is_skipped` (`[Theory]` over `Cases` ids) runs `RunAsync` on a dedicated STA
  thread with a `Dispatcher`. It writes the outcome to `ITestOutputHelper` and appends the Markdown row to
  `$OCR_E2E_SUMMARY` when that is set. It asserts `Passed`, or `SkippedNoRecognizer` for a non-required case.
- `OcrEndToEndTests.Required_cases_cover_english` asserts that at least 6 required en-US cases exist.
- `HotkeyTests` (SHOULD, REQ-QSC-43) are listed in the Test plan.

**`--ocr-e2e` (SHOULD, REQ-QSC-46).** In 02's `OnStartup` skeleton, slot 3, `--ocr-e2e <report.md>` runs every case
on the UI thread, writes `ToMarkdown` to the path and shuts down. The exit code is 0 unless a required case failed. It
runs before the mutex (as the smoke branch does), uses no settings, data folder or network, and never runs in a normal
start. CI does not use it. The release pass runs it on a Windows 10 machine to fill the OS column of the matrix.

### 10. Documents

**`docs/security/threat-model.md`** (new). It is written last, so it names the real classes and tests.

1. Scope and method (STRIDE-lite; reviewed every minor release as a checklist item).
2. **Assets:**
   - A1 provider keys;
   - A2 chat text (incoming OCR, outgoing replies, optional transcripts, the opt-in translation cache);
   - A3 the ability to send keystrokes and set the clipboard;
   - A4 the player's game-account standing;
   - A5 the integrity of `Envoy.exe` and the update channel;
   - A6 the user's PC (no code execution from data);
   - A7 personal data at rest (names, positions, logs).
3. **Trust boundaries:**
   - TB1 screen pixels → OCR;
   - TB2 Envoy ↔ translation providers;
   - TB3 user-writable files in the data folder;
   - TB4 GitHub API/Releases → updates (portable checker and Velopack);
   - TB5 Envoy → foreground window;
   - TB6 repository, CI and dependencies → release binary;
   - TB7 named kernel objects.
4. **Egress table.** Every host Envoy contacts, which feature uses it, and the setting that controls it. It includes
   SDK-internal endpoints (`api.anthropic.com`, `translate.<region>.amazonaws.com`) and Velopack's GitHub feed. It
   states that `docs/privacy.md` (07a) is the user-facing version, and that `policy.json` → `hosts` is the mechanical
   list.
5. **Threats** (columns: ID, threat, asset, mitigations → code, verified by, residual risk):

| ID | Threat | Mitigations → code | Verified by |
| --- | --- | --- | --- |
| T1 | Chat-borne manipulation (prompt injection against an LLM provider, huge text) | Output is display-only. Incoming chat is never typed. → `src/Envoy.Core/Pipeline/ChatPipeline.cs` | `ChatPipelineTests.*`; review |
| T2 | Malicious `phrases.json` (newlines become hotkeys, huge file) | `OutgoingText`, `PhraseLimits` (08a) | `PhraseBookLimitsTests.*`, `OutgoingTextTests.*`; M-QSC-2 |
| T3 | Provider response injects control characters or runaway length into a typed reply | HTTPS, `OutgoingText`, preview in the reply box | `OutgoingTextTests.*` |
| T4 | Keystrokes go to the wrong window (game closed, alt-tab, tray-opened reply) | `SendTargetGuard.ChooseTarget`/`Check`, `KeyboardSender` | `SendTargetGuardTests.*`; M-QSC-1, M-QSC-3, M-QSC-4 |
| T5 | API key disclosure at rest | DPAPI CurrentUser; `[Secret]`; `.bak` files stay encrypted (02) | `SettingsSecretTests.*`, `SettingsStoreTests.Round_trips_settings_and_encrypts_secrets_on_disk` |
| T6 | Key or chat disclosure through logs, diagnostics or bundles | 02 `LogRedactor`, `[Personal]`/`[Location]`; 03 `ProviderHealthText.Sanitize` delegates to it | `LoggingTests.Redacts_secrets_everywhere`, `SettingsRedactionTests.Every_marked_property_is_masked`, `DiagnosticsTests.Report_has_all_sections_and_no_private_values` |
| T7 | Settings tampering leads to code execution | No polymorphic deserialization; `BinaryFormatter`/`XamlReader` banned; launches only through `ShellLauncher` | `LaunchTargetTests.*`; RS0030 in the typecheck |
| T8 | Update hijack (spoofed `html_url`, fake release, tampered package) | `ReleaseUrlPolicy`, `ShellLauncher`; CI-only releases on protected tags; Authenticode (06); Velopack hash-verified packages, with the updater started by Velopack and never by Envoy code (06); SHA-256; SBOM; attestation | `UpdateUrlTests.*`, `UpdateCheckerTests.*`; `check_release_assets.py` |
| T9 | Provider MITM | OS certificate validation; bypass APIs banned; COULD warning for remote `http` | RS0030; `EndpointPolicyTests.*` (if built) |
| T10 | Supply chain (NuGet, Actions) | Dependabot; vulnerable-package gate; SHA pins; least privilege; CodeQL; SBOM; dependency review | CI checks (`check_vulnerable.py`, `workflow-*` rules) |
| T11 | Anti-cheat regression (memory reads, hooks, injection) | Native allowlist and deny list; `Process.MainModule` banned; CODEOWNERS on `Interop/` | `test_check_policy.py`; CI |
| T12 | Elevation | `asInvoker`, no `uiAccess`; the OCR-pack install is the only elevated launch, user-initiated through UAC with a validated tag | policy `manifest` rule; `LaunchTargetTests.Ocr_install_arguments_only_for_catalog_languages` |
| T13 | Telemetry or unexpected egress | Network types only in the three allowed paths; host allowlist | policy `network-location`/`host` rules |
| T14 | Single-instance object squatting | Accepted: needs a same-user attacker, and the result is only a denial of service | — |
| T15 | Chat text sent to a third-party provider without the user knowing | 07a privacy disclosure; no provider call before acknowledgement | `TranslationGateTests.*` |
| T16 | Personal data left behind | 02 `DataInventory` + "Delete all Envoy data"; every inventory file documented | `DataInventoryTests.Enumerate_lists_only_inventory`, `DataInventoryTests.Every_item_is_documented_in_privacy_md` |

6. **Out of scope:** same-user malware, a compromised OS or certificate store, and publisher policy changes. The FAQ
   (07a) covers the policy stance.
7. **Review log** (date, version, reviewer).

The test names above come from the revised specs. The SHOULD `threat-model-refs` rule catches any drift.

**`docs/releasing.md`** (restructured; the per-area sections are replaced):

1. **Before tagging**
   - `main` is green: `check`, `app-tests`, `windows`, CodeQL, with no open high or critical code-scanning or
     Dependabot alerts.
   - The CHANGELOG has the version section. 06's `changelog_notes.py` enforces this for stable tags.
   - The `policy.json` diff since the last release has been reviewed.
   - The threat model has been reviewed and has a review-log entry.
   - The pre-release manual pass is done on the dev artifact of the commit to be tagged.
2. **Pre-release manual pass.** This is one ordered list. Each area's steps keep their content and get a uniform id,
   so 01's `M1` becomes `M-UIF-1`, 02's `M3` becomes `M-REL-3`, and 07a/b/c steps become `M-OUX-n`. A mapping table
   from old to new ids sits at the end of the file. The order follows a player's first session:
   1. install and update (`M-DIST-*`);
   2. start-up, safe mode and recovery (`M-REL-*`, except Delete all data);
   3. setup guide and notices (07a `M-OUX-*`);
   4. capture per title and display mode (`M-CAP-*`);
   5. chat understanding (`M-CHAT-*`);
   6. translation (`M-TRR-*`);
   7. replies (`M-QSC-1…5`, 07c `M-OUX-*`);
   8. accessibility (07b `M-OUX-*`);
   9. settings UI (`M-UIF-*`);
   10. **Delete all Envoy data** last (`M-REL-*`).

   Three items are added to the pass:
   - **M-CHAT-CAL (once, before 1.0):** at least 3 anonymised "Copy as test capture" fixtures from at least 2 titles
     are committed under `tests/Envoy.Core.Tests/Golden/Captures`. After that, 04's thresholds stop being provisional,
     which is recorded in 04's Deviations (contract §8.3).
   - **M-CAP-SOAK (SHOULD):** `Envoy.exe --soak 120` passes 05's growth budget.
   - **M-QSC-6 (SHOULD, needs REQ-QSC-46):** `Envoy.exe --ocr-e2e report.md` on Windows 10 22H2 has no failed required
     case.

   Each run updates the cells it covered in `docs/compatibility.md`.
3. **Tag:** `git tag vX.Y.Z && git push origin vX.Y.Z`. Use `-rc.N` first for a new minor version. The `release` job
   waits for a reviewer in the `release` environment.
4. **After the release**
   - The release lists `Envoy.exe`, `Envoy.exe.sha256`, `SHA256SUMS.txt`, `Envoy-win-Setup.exe`, the Velopack files
     and `Envoy.cdx.json`.
   - On a clean machine, `Get-FileHash` matches, the Authenticode signature is valid (06), and, if the SHOULD is
     built, `gh attestation verify Envoy.exe -R PinnerSinner/Envoy` passes.
   - An installed previous version offers "Restart to update" (06), and a portable previous version links to the
     release page.
   - If anything fails, delete the release (not the tag), fix, and ship the next patch version. Never re-tag.

**`docs/compatibility.md`** (new). There is one row per supported title × display mode (5 × 3 = 15 rows), and the
columns are Win10 22H2 at 100 % and 150 %, and Win11 at 100 % and 150 %. Each cell reads `✓ vX.Y.Z`, `✗ vX.Y.Z (note)`
or `—` (not yet verified). The rules:

- Before 1.0, every cell is filled. `✗` is allowed with a note. Exclusive fullscreen is expected to be `✗`, with 05's
  `CaptureText.LikelyExclusiveFullscreen` advice.
- Every later release re-verifies at least the borderless 100 % cell for each title on one OS.
- The header states that CI runs only on Windows Server 2022, so this file is the only record of desktop Windows 10/11
  coverage.

**`SECURITY.md`** (changed). It gets these sections:

- **Supported versions:** the latest release only.
- **Verifying a download:** `SHA256SUMS.txt`, the Authenticode signer name, `gh attestation verify` (when available),
  and the SBOM and what it is for.
- Links to the threat model and `docs/privacy.md`.
- **For contributors:**
  - never commit real keys;
  - use obviously fake strings (`"FAKE-DEEPL-KEY"`), and build provider-shaped test keys at runtime by concatenation,
    so that push protection doesn't trip;
  - if a key leaks, revoke it at the provider first;
  - security-sensitive paths need maintainer review (CODEOWNERS).
- **Response target:** acknowledgement within 7 days, worded as a target.
- The existing data section stays.

**`docs/security/repository-settings.md`** (SHOULD) covers:

- the `main` ruleset:
  - PR required;
  - required checks `check`, `windows`, `app-tests`, `analyze (csharp)`, plus `review` when REQ-QSC-19 is built;
  - no force push;
  - linear history;
- a `v*` tag ruleset, so only maintainers create tags and no one updates or deletes them;
- private vulnerability reporting, Dependabot alerts and updates, secret scanning with push protection, and code
  scanning default setup **off** (the workflow is used instead);
- Actions settings: default token read-only, no PR approvals by Actions, approval for first-time fork contributors,
  and allowed actions limited to the pinned list;
- the `release` environment with required reviewers and the two signing secrets;
- 2FA for every account with write access.

### 11. Review gates (SHOULD)

- `.github/CODEOWNERS` covers `/.github/`, `/tools/ci/`, `/tools/release/`, `/Directory.Build.props`,
  `/BannedSymbols.txt`, `/.config/`, `/src/Envoy.App/Interop/`, `/src/Envoy.App/Security/`, `/src/Envoy.App/app.manifest`,
  `/src/Envoy.App/Services/KeyboardSender.cs`, `/src/Envoy.Core/Security/`, `/src/Envoy.Core/Settings/SettingsStore.cs`
  and `/docs/security/`, all owned by `@PinnerSinner`.
- The PR template gets `- [ ] Security-sensitive (Interop, keystrokes, launches, network, secrets, workflows)?
  policy.json / threat model updated`.
- The bug template gets a top note: "Please don't paste `settings.json` or API keys. Use About › Troubleshooting ›
  Copy diagnostics instead; it hides them."
- `dependabot.yml`: the NuGet entry uses `directories: ["/", "/tools/typecheck"]`.

### 12. Settings, UI, startup, smoke and allowlists

- **Settings:** none. **UI:** none. **`IShell`, `AppHostOptions`:** unchanged.
- **Startup:** only the SHOULD `--ocr-e2e` branch in 02's slot 3 (contract §4). Nothing else moves.
- **Smoke test:** no new section. The smoke step loses the dead `error.log` print and uploads `smoke.log`.
  `job_budget.py` reports the slowest sections. 01 already sets `WaitForExit(600000)`, and the contract's Warning
  logs at 420 s total and 30 s per palette come from the smoke test itself.
- **P/Invoke and network allowlists:** no additions. `OcrEndToEnd` uses WPF rendering and the existing `OcrService`.
- **Tool manifest:** adds CycloneDX next to 06's `vpk`.

## Acceptance criteria

| ID | REQs | Verified by |
| --- | --- | --- |
| AC-QSC-6 | 15 (SHOULD) | **CI check:** the `check` summary shows an "Envoy.Core coverage" table with a Total row and per-folder rows, and the `coverage` artifact holds the XML. |
| AC-QSC-7 | 14 | `test_coverage_summary.py` (all cases) passes. **CI check:** the "Coverage gate" step passes at 70/55 on the change. |
| AC-QSC-8 | 17, 18 | **CI check:** the `CodeQL` workflow is green on the PR, with `analyze (csharp)` (and `analyze (actions)` if built). |
| AC-QSC-9 | 19 (SHOULD) | **CI check:** `Dependency review / review` runs on the PR and passes. One-off step **P-QSC-3**. |
| AC-QSC-10 | 20 | `test_check_vulnerable.py` passes. **CI check:** the "Vulnerable packages" step passes. One-off step **P-QSC-3**. |
| AC-QSC-11 | 21, 22 | `test_check_sbom.py` passes. **CI check:** the windows "SBOM" step passes and the artifact contains `Envoy.cdx.json`. `check_release_assets.py --self-test` expects `Envoy.cdx.json`. One-off step **P-QSC-2**. |
| AC-QSC-12 | 23, 24, 25, 27 | **CI check:** the policy rules `workflow-permissions`, `workflow-pins` and `workflow-timeouts` pass for all three workflows. `check_signing_scope.py` and its `--self-test` pass with the `release`-job shape. `dependabot.yml` lists `/tools/typecheck`. |
| AC-QSC-13 | 26 | `test_job_budget.py` passes. **CI check:** the "Time budget" steps pass in `check` (≤ 8 min) and `windows` (≤ 20 min) on the change's run. The PR description records both times next to the previous `main` run. |
| AC-QSC-22 | 36, 37 | **CI check:** the policy rules `required-docs` (T1–T16 with "Verified by" filled) and `threat-model-refs` pass. |
| AC-QSC-23 | 38 | **CI check:** `required-docs` passes for `releasing.md` (headings, one step per prefix, unique ids) and `compatibility.md` (15 rows). **Manual:** the first full pass is run on the `-rc.1` of 1.0 (`docs/releasing.md` §2). |
| AC-QSC-24 | 43 (SHOULD) | **CI check:** `app-tests` runs `HotkeyTests` green. |
| AC-QSC-25 | 40 | **CI check:** `required-docs` passes for `SECURITY.md`. |
| AC-QSC-26 | 39, 41, 42 (SHOULD) | The files exist with the sections in Design §10–11. **Manual (maintainer):** settings are applied as described, and `gh api repos/PinnerSinner/Envoy/rulesets` lists the `main` and `v*` rulesets. |
| AC-QSC-27 | 44 | **CI check:** `app-tests` is green, `OcrEndToEndTests.Case_passes_or_is_skipped` passes for all 6 required en-US cases, and the job summary has the OCR table, with any missing recognizer shown as `Skipped (no recognizer)`. |
| AC-QSC-28 | 45, 46 (SHOULD) | **CI check:** the OCR table shows the ja/zh/ko/ru rows as `Passed`, or as `Skipped` with the install step's warning. **Manual:** M-QSC-6. |
| AC-QSC-29 | 12, 13 (SHOULD) | `test_check_policy.py` workflow and docs cases pass, and every new `tools/ci/*.py` script has a `test_*.py`. |

## Test plan

### Script tests (`tools/ci/`, standard-library `unittest`, run in `check`)

| File | Test | Asserts |
| --- | --- | --- |
| `test_coverage_summary.py` | `test_passes_above_threshold` | a fixture at 0.80/0.60 with 70/55 → exit 0, and the summary has Total and folder rows |
| | `test_fails_below_line_threshold` | 0.65 lines → exit 1 and `::error::` mentioning 70 |
| | `test_fails_below_branch_threshold` | 0.50 branches → exit 1 |
| | `test_writes_to_github_step_summary` | `GITHUB_STEP_SUMMARY` set to a temp file → the file holds the table |
| | `test_missing_package_exits_2` | package `Nope` → exit 2 |
| `test_check_vulnerable.py` | `test_high_fails` / `test_critical_fails` | fixture JSON with a High or Critical advisory → exit 1, and the message names the package |
| | `test_moderate_passes_and_is_listed` | Moderate only → exit 0, and the summary lists it |
| | `test_no_vulnerabilities_passes` | empty `vulnerablePackages` → exit 0 |
| `test_check_sbom.py` | `test_all_required_present` | fixture with the 4 components → exit 0 |
| | `test_missing_component_fails` | without `Velopack` → exit 1 naming it |
| | `test_invalid_json_exits_2` | broken file → exit 2 |
| `test_job_budget.py` | `test_under_budget_passes` / `test_over_budget_fails` | `JOB_START` = now − 7 min with budget 8 → 0; now − 9 min → 1 with `::error::` |
| | `test_missing_start_exits_2` | no `JOB_START` → exit 2 |
| | `test_smoke_log_top_sections` | fixture `smoke.log` → the 5 largest `elapsed_ms` lines appear in the summary |
| `test_check_policy.py` (additions) | `test_workflow_top_level_write_fails` | `permissions: contents: write` at column 0 → `workflow-permissions` |
| | `test_job_write_only_where_allowed` | `windows` job with `contents: write` → violation; `release` job → none |
| | `test_unpinned_action_fails` / `test_missing_timeout_fails` | `@v4` → `workflow-pins`; a job without `timeout-minutes` → `workflow-timeouts` |
| | `test_threat_model_rows_required` | a doc missing T9, or T3 with an empty "Verified by" → `required-docs` |
| | `test_releasing_needs_every_prefix_and_unique_ids` | missing `M-CAP` steps, or two `M-REL-3` → `required-docs` |
| | `test_compatibility_rows` | a missing "Age of Empires IV / Borderless" row → `required-docs` |
| | `test_security_md_links` | no link to `docs/privacy.md` → `required-docs` |
| | `test_threat_model_dangling_reference_fails` | `src/Nope.cs` and `FooTests.Missing` → two `threat-model-refs` violations |
| `tools/release/check_signing_scope.py --self-test` (06, updated) | — | a fixture with `secrets.ENVOY_SIGN_PFX_BASE64` in the `windows` job fails; in a `release` job without `environment: release` it fails; the real `build.yml` passes |

### App tests (`tests/Envoy.App.Tests`, Windows CI only)

| File | Test | Asserts |
| --- | --- | --- |
| `OcrEndToEndTests.cs` | `Case_passes_or_is_skipped` (Theory over `OcrEndToEnd.Cases`) | `Passed`, or `SkippedNoRecognizer` when not required; a Markdown row is written |
| | `Required_cases_cover_english` | at least 6 required cases with `OcrLanguage == "en-US"` |
| | `Render_is_deterministic` | rendering the same case twice gives identical bytes |
| `HotkeyTests.cs` (SHOULD) | `Round_trips` (Theory: `Ctrl+Shift+T`, `Alt+1`, `Win+F9`, `Ctrl+Alt+Space`) | `TryParse` true and `ToString()` returns the input |
| | `Digits_map_to_D_keys` | `"Ctrl+1"` → `Key.D1`, printed back as `"Ctrl+1"` |
| | `ToString_is_culture_invariant` | under `ar-SA` and `fa-IR`, `Ctrl+7` prints `"Ctrl+7"` |
| | `Rejects_unknown_tokens` / `Rejects_modifier_only` | `"Ctrl+Banana"`, `""`, `null`, `"Ctrl+Shift"` → false |

### Smoke test

There are no new sections. The existing smoke test passes within `WaitForExit(600000)` in the restructured `windows`
job, and its log is uploaded.

### One-off and manual steps

- **P-QSC-2:** push `v0.0.0-rc.1` on a fork that has a `release` environment and no signing secrets. Check that:
  - `release` waits for approval, then runs;
  - the draft becomes a prerelease holding `Envoy.cdx.json`;
  - `check_release_assets.py` passes;
  - `gh attestation verify` succeeds, if REQ-QSC-22 is built;
  - the log shows "Code signing skipped".

  Then delete the fork release.
- **P-QSC-3:** on a throwaway PR, add `<PackageReference Include="System.Text.Json" Version="8.0.0" />` to Core.
  "Vulnerable packages" fails and names the package, and dependency review fails if it is built. Close the PR.
- **M-QSC-6 (SHOULD):** run `Envoy.exe --ocr-e2e report.md` on Windows 10 22H2. No required case has failed, and the
  report is attached to the release PR.

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `.github/workflows/build.yml` | restructure into `check`, `app-tests`, `windows`, `screenshots` and `release`; permissions; concurrency; cache; timeouts; SHA pins; coverage, vulnerable-package, SBOM and budget steps; dead `error.log` print removed | yes (01, 06, 08a) |
| `.github/workflows/codeql.yml`, `dependency-review.yml` | new | |
| `.github/dependabot.yml`, `CODEOWNERS`, `pull_request_template.md`, `ISSUE_TEMPLATE/bug_report.yml` | SHOULD items | |
| `tools/ci/check_policy.py`, `policy.json`, `test_check_policy.py` | `workflow-*`, `required-docs` and `threat-model-refs` rules | yes (08a; 02 and 05 entries) |
| `tools/ci/coverage_summary.py`, `check_vulnerable.py`, `check_sbom.py`, `job_budget.py` and their `test_*.py` | new | |
| `tools/release/check_signing_scope.py`, `check_release_assets.py` | release-job shape; `Envoy.cdx.json` | yes (06) |
| `.config/dotnet-tools.json` | add CycloneDX | yes (08a, 06) |
| `tests/coverage.runsettings` | new | |
| `tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj` | `coverlet.collector` | yes |
| `tests/Envoy.App.Tests/Envoy.App.Tests.csproj`, `OcrEndToEndTests.cs`, `HotkeyTests.cs`, `StaRunner.cs` | new | |
| `Envoy.sln` | add `Envoy.App.Tests` | yes |
| `src/Envoy.App/OcrEndToEnd.cs` | new (harness) | |
| `src/Envoy.App/App.xaml.cs` | `--ocr-e2e` in the slot 3 smoke branch (SHOULD, about 6 lines) | **shared** |
| `docs/security/threat-model.md`, `docs/security/repository-settings.md`, `docs/compatibility.md` | new | |
| `docs/releasing.md` | restructured: checklist, consolidated pass, id mapping | yes (every area) |
| `SECURITY.md`, `docs/building.md`, `CLAUDE.md`, `CONTRIBUTING.md` | verification, coverage commands, CI layout | yes |
| `CHANGELOG.md` | Unreleased: SBOM and attestation on releases; signed builds only from protected tags | yes |
| `AppHost.cs`, `AppSettings.cs`, `SmokeTest.cs`, `IShell.cs`, `Native.cs`, pages and windows | **not touched** | |

Estimated size: about 250 lines of Python, 300 lines of workflow and config, 250 lines of C# (harness and tests) and
500 lines of docs. Most of the docs work consolidates text that already exists.

## Risks & open questions

1. **The restructure lands last and touches everything in CI.** Dry-run it with P-QSC-2 on a fork before merging. If
   `vpk upload` or signing turns out to need the build tree in the `release` job, also upload the needed folder as an
   artifact. Do not move signing back into `windows`.
2. **OCR on the runner.** `windows-latest` (Server 2022) already runs OCR in 05's smoke section, so the en-US engine is
   present. A future runner image without it would fail the required cases. That is the intended loud failure, and
   the workaround is to add `en-US` to the capability install step.
3. **Capability installs** can take minutes or fail (Windows Update throttling). They are SHOULD, `continue-on-error`
   and capped at 10 minutes, and the cases then report `Skipped`.
4. **Fonts on Server.** If a CJK font is missing, WPF's composite-font fallback still renders the text, in a different
   face. The case may then fail at 0.85, but only non-required cases use those fonts.
5. **Coverage after 02–07.** If coverage is under 70/55 at the start of this step, the step must add tests first. This
   may grow the step. The contract forbids lowering the gate.
6. **Budget flakiness.** Runner speed varies. The budgets come from the contract, and `job_budget.py` reports the
   slowest smoke sections, so a regression can be found quickly. If the budget is exceeded repeatedly without a code
   cause, the contract is amended. The budget step is never made non-fatal.
7. **CycloneDX flags and project references.** Option names differ between major versions. The pinned version's
   `--help` decides the flags, and `check_sbom.py` catches a run that misses Core's packages.
8. **Conditional `environment:` in 06.** This step removes it: the `release` job always has `environment: release`.
   That resolves 06's risk 3.

## Revision notes

- **Split.** This file is the second half of the old `08-quality-security-ci.md`: workflow restructure, coverage,
  CodeQL, dependency review, SBOM, release documents and the new OCR end-to-end test. It lands last (contract §1).
- **Signing scope (critique major on 06).** Signing and publishing move into a tag-only `release` job under
  `environment: release`. 06's step bodies move verbatim, and 06's `check_signing_scope.py` is updated to enforce the
  new shape. Untagged builds stay unsigned.
- **CI time (critique major).** REQ-QSC-26 is restated as in contract §6: `check` ≤ 8 min, `windows` ≤ 20 min,
  excluding tag-only steps. It is now enforced by `job_budget.py`, with the slowest smoke sections reported. The smoke
  `WaitForExit(600000)` belongs to 01, and the Warning logs belong to the smoke test.
- **New concerns from contract §8.3–8.4.**
  - REQ-QSC-38 now covers the consolidated, ordered pre-release pass (with 04's calibration and 05's soak) and
    `docs/compatibility.md`.
  - New REQ-QSC-44 (MUST): the OCR end-to-end harness, with en-US on every build.
  - New REQ-QSC-45 (SHOULD): the OCR language capability install.
  - New REQ-QSC-46 (SHOULD): `--ocr-e2e`, which fills the contract §4 slot 3. The harness lives in
    `src/Envoy.App/OcrEndToEnd.cs`, as `SmokeTest.cs` does, so both the test project and the exe can run it.
- **Threat model.** It grows from T1–T14 to T16: T15 covers provider egress without consent (07a's gate) and T16 covers
  personal data left behind (02's inventory). Rows now name the real tests from 02, 03, 06 and 08a. The 06 T8 line
  ("updater started by Velopack") is included, because the doc only exists from this step on.
- **Scope (contract §9).** There are 10 MUSTs. REQ-QSC-16 is folded into 14. REQ-QSC-15, 19, 39 and 43 are SHOULD, and
  so are 12, 13, 18, 22, 24, 25, 27, 37, 41 and 42. MUSTs that relied on review-only ACs (36, 38, 40) are now checked
  by the `required-docs` policy rule, and 20 and 21 have scripts with unit tests.
- **Permissions.** The policy rule for permissions is split from the SHOULD pin and timeout rules, so that MUST 23 has
  its own CI check.
- **Dependency review** is now SHOULD, with the license check off by default (NuGet license metadata is unreliable).
- **Removed:** the old "app-tests holds only Hotkey tests" design. The project now exists for REQ-QSC-44, and the
  Hotkey tests ride along as SHOULD.

## Deviations

Implemented: every MUST (REQ-QSC-14, 17, 20, 21, 23, 26, 36, 38, 40, 44) and every SHOULD (REQ-QSC-12, 13, 15, 18, 19,
22, 24, 25, 27, 37, 39, 41, 42, 43, 45, 46). Nothing is deferred. Changes from the design above:

1. **Coverage.** Re-measured at the start of the step: Envoy.Core **94.5 % lines, 87.6 % branches** (4,797 lines,
   3,871 branches), so no tests had to be added first. The gate stays at the contract's 70 % / 55 % (§9) so the step
   matches its acceptance criteria; the ratchet and the 1.0 target (80 % / 65 %) are documented in `docs/building.md`
   and `CLAUDE.md`, and the maintainer may raise the floor in a follow-up. `ExcludeByAttribute` also lists
   `CompilerGeneratedAttribute`. `coverage_summary.py` merges several XML files by unique (file, line) and compares
   at the printed precision (70.0 % passes a 70 % gate).
2. **Vulnerable packages.** `check_vulnerable.py` parses the real `dotnet list package --vulnerable --format json`
   shape (`projects[].frameworks[].topLevelPackages|transitivePackages[].vulnerabilities[]`), not a
   `vulnerablePackages` key; restore problems in the report, or a failing `dotnet list`, exit 2. Moderate and Low
   advisories are `::warning::` lines and summary rows.
3. **SBOM.** CycloneDX is pinned at **6.1.1**. Checked against its `--help`: the step adds `--recursive` (without it
   Envoy.Core's packages, `Anthropic` and `AWSSDK.Translate`, are missing) and `--exclude-dev` (drops the analyzer
   package). Verified locally on Linux: 13 components including the four required. `check_sbom.py` also requires
   `bomFormat: CycloneDX` and compares names case-insensitively.
4. **Action pins.** Resolved with `git ls-remote` (the GitHub API was not reachable from the session): checkout,
   setup-dotnet, cache, upload-artifact and download-artifact at their `v4` commits, attest-build-provenance `v2`,
   **codeql-action `v4`** (v3 is close to deprecation) and dependency-review-action **v4.9.0** (no floating `v4`
   tag exists). Dependabot already proposes newer majors for some; they arrive as SHA-pinned updates.
5. **Workflow details.** `OCR_E2E_SUMMARY` is written to `GITHUB_ENV` by a step, because the `runner` context is not
   available in job-level `env`; the summary step writes the table header and the tests append rows
   (`OcrEndToEnd.ToMarkdownRow`). The `Envoy-<v>-win-x64` artifact also carries `notes.md`, which the `release` job
   uses for `vpk pack` and the release body. The `screenshots` job pushes from its own checkout in bash (the old
   worktree dance is gone). The smoke log is uploaded as `smoke-log`. The release footer mentions the SBOM and the
   attestation.
6. **`workflow-timeouts`** checks per job (every job with `runs-on` has a job-level `timeout-minutes`; reusable-workflow
   jobs are exempt) instead of comparing line counts, so a step-level timeout cannot hide a missing job timeout.
   `workflow-permissions` also fails a workflow with no top-level `permissions:` block.
7. **`required-docs`.** Steps are bullets that start with a bold id (`- **M-XXX-n`) inside the "Pre-release manual
   pass" section; references elsewhere do not count. Placeholder "Verified by" cells (`—`, `-`, `n/a`, `TBD`) count
   as empty, so T14 reads "Review (accepted risk)". Compatibility rows are matched by their first two cells, and each
   cell must be `✓ vX.Y.Z`, `✗ vX.Y.Z (note)` or `—`. Headings match after dropping a leading number (`## 2. …`).
8. **`threat-model-refs`** also accepts `FooTests.*` (the class must exist). Methods of a nested helper class count
   towards the enclosing `*Tests` class. T9 cites the RS0030 ban and `bannedRequired` instead of
   `EndpointPolicyTests` (not built).
9. **Release pass ids.** The step already holds 04's calibration as **M-CHAT-5** and 05's soak as **M-CAP-6**, so the
   design's M-CHAT-CAL and M-CAP-SOAK are those ids (recorded in the old-to-new table) rather than duplicates. 01's
   M1–M3 became M-UIF-1…3; 07a, 07b and 07c's M-OUXA/B/C steps became M-OUX-1…14; M-QSC-6 (`--ocr-e2e`) sits in the
   capture section. M-TRR-3's "Delete all data" half moved to M-REL-3, which ends the pass.
10. **OCR harness.** The effect enum is `OcrTextEffect` (WPF already has `System.Windows.Media.TextEffect`). Text is
    compared after `TextSimilarity.Normalize` (case, punctuation and the spaces Windows OCR puts between CJK
    characters), as Envoy compares OCR readings elsewhere. The harness OCRs with the case's language only and treats
    `OcrService.Configure`'s profile-language fallback as "no recognizer". The enlargement uses WPF's bitmap scaler at
    `RegionMath.OutputSize`'s scale, not GDI's `StretchBlt`. Extra public members: `Flag`, `MinSimilarity`, `Score`,
    `Describe`, `ToMarkdownRow`, `Run`. `--ocr-e2e` also keeps the log bootstrap off the data folder, like the smoke
    and soak branches.
11. **App tests.** `tests/Envoy.App.Tests` mirrors the app's `SupportedOSPlatformVersion` and `CA1416`/`WFAC010`
    suppressions. `Envoy.sln` maps its Release configuration to Debug without building it (NETSDK1151). Added
    `Markdown_rows_name_the_outcome`, and `Rejects_unknown_tokens` also covers `Ctrl+12` and `Ctrl+-5`.
12. **Hotkey fix.** Writing `HotkeyTests.Digits_map_to_D_keys` exposed a bug: `Hotkey.TryParse("Ctrl+1")` returned
    `Key.Cancel` (`Enum.TryParse` reads "1" as the numeric value). Digits are now checked first, and purely numeric or
    undefined key names are rejected.
13. **Beyond the design.** `tools/typecheck/typecheck.sh` also compiles `tests/Envoy.App.Tests` against the
    type-checked app (`tools/typecheck/apptests/`), so the Windows-only test project is checked on Linux. Shared test
    helpers for the `tools/ci` scripts live in `tools/ci/ci_testing.py`.
14. **Verified only in CI.** The Windows jobs (app tests, SBOM on Windows, budgets) and the release job need the first
    CI run, P-QSC-2 (fork rehearsal) and P-QSC-3 (vulnerable-package PR); the PR description records both job times.

