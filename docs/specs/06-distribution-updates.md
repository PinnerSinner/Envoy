# Distribution & updates: Velopack installer, one-click auto-update, channels, tag-only signing, winget

Status: Implemented (revised against `00-integration.md`; see Deviations)

Area id: `06-distribution-updates` · Requirement prefix: `DIST` · Step 7 of the contract's order (§1).

**Depends on (may assume):** 01 (`Pages/*`, `PageIds`, `MainViewModel.*` partials, `IShell`, `AsyncRelayCommand` with
`ReportCommandError`, three-phase smoke test), 08a (`ShellLauncher`, `RestartFlags`, `LaunchTarget`,
`ReleaseUrlPolicy`, `BannedSymbols.txt`, `tools/ci/policy.json`, `.config/dotnet-tools.json`), 02 (`ILog`/`LogHub`,
`Task.Forget`, `AppHostOptions`, instance `DataFolder`, the `OnStartup`/`ExitApp` skeleton with a 06 slot,
`StartupPlan`, `DiagnosticsSnapshot`), 03, 05 (`AppHost.GameWindow`, `GameWindowChanged`) and 04.

**Delivers for later steps:** `Program.Main`, Velopack, `UpdateCoordinator` (+ `Changed`), `UpdateViewBuilder` /
`UpdateView`, `UpdateActions.Run`, `AppHost.AppVersion`, `IShell.RestartToUpdate`, tag-only signing in the `windows`
job (08b later moves it into a release job), the `vpk` pin in `.config/dotnet-tools.json`.

## Context

### What exists when this step starts

Today's code plus steps 1–6. Rows marked *(01/02/08a)* describe what those steps have already changed.

| Topic | State | Where |
| --- | --- | --- |
| Build artifact | `dotnet publish -c Release` makes one self-contained, compressed single-file `Envoy.exe` (~75 MB) for win-x64. | `src/Envoy.App/Envoy.App.csproj` (Release `PropertyGroup`) |
| CI | One workflow. The `check` job (Ubuntu) runs unit tests, the typecheck and *(08a)* `check_policy.py`. The `windows` job publishes the exe, runs `Envoy.exe --smoke-test` (*(01)* `WaitForExit(600000)`), pushes screenshots, writes `Envoy.exe.sha256`, uploads both as a workflow artifact and, on `v*` tags, creates a GitHub Release with `softprops/action-gh-release@v2` (`generate_release_notes: true`, `prerelease` when the tag contains `-`). | `.github/workflows/build.yml` |
| Signing | None. A comment marks where a signing step would go. `docs/releasing.md` says SmartScreen warns on first run. | `build.yml`, `docs/releasing.md` |
| Update check | `UpdateChecker.CheckAsync(Version)` calls `GET https://api.github.com/repos/{repo}/releases/latest`. It ignores drafts and prereleases, takes the first `*.exe` asset as `DownloadUrl`, and returns `null` on any non-success HTTP status. *(08a)* `Parse` and `CheckAsync` drop URLs that fail `ReleaseUrlPolicy.IsTrusted`. | `src/Envoy.Core/Updates/UpdateChecker.cs` |
| Update UX | `AppHost.Start()` runs `CheckForUpdatesAsync().Forget(_log, "update check")` *(02)* once if `Settings.CheckForUpdates`. War Room shows a banner with `Host.UpdateText` and a **Download** button (`DownloadUpdateCommand`, *(01)*) that opens the release *page*. The user must download the new exe, close Envoy and replace the file by hand. | `AppHost.cs`, `Pages/WarRoomPage.xaml`, `MainViewModel.Actions.cs` |
| Settings | `AppSettings.CheckForUpdates` (bool, default `true`), labelled "Tell me when a new version is released" in the Appearance page's "General" group, next to "Start Envoy when I sign in to Windows" and the transcripts row. | `AppSettings.cs`, `Pages/AppearancePage.xaml` |
| Version | `AppHost.CurrentVersion` (static `Version`, assembly version) feeds `MainViewModel.Version`, 02's log header and crash marker. CI sets `-p:Version=<numeric>` and `-p:InformationalVersion=<tag>` only for tags. | `AppHost.cs`, `MainViewModel.cs`, `build.yml` |
| HTTP client | One shared `HttpClient` (12 s timeout) with default UA `Mozilla/5.0 (Windows NT 10.0; Win64; x64) Envoy/0.1`. The keyless Google/Edge providers (03) depend on that browser-like UA. `UpdateChecker` sets its own per-request UA. | `AppHost.cs` lines 42–43 |
| Entry point | WPF generates `Main` from `App.xaml` (`ApplicationDefinition`). *(02)* `OnStartup` follows the contract §4 skeleton with a comment at slot 1 (Program.Main), slot 5 (pending update apply) and slot 9 (`Updates.StartAsync`). | `src/Envoy.App/App.xaml.cs` |
| Process launching | *(08a)* `Process.Start`/`ProcessStartInfo` are banned except in `Security/ShellLauncher.cs` (`TryOpen`, `InstallOcrPack`, `RestartSelf(RestartFlags)`, `RevealFile`). `IShell.OpenUrl` → `ShellLauncher.TryOpen(target, Host.DataFolder)`. | `BannedSymbols.txt`, `ShellLauncher.cs` |
| Start with Windows | `StartupRegistration.Apply(bool)` writes `"<ProcessPath>" --minimized` to `HKCU\…\Run\Envoy`, only from `ApplySettings`. *(02)* "Delete all Envoy data" also removes it. | `Services/StartupRegistration.cs` |
| Repo constant | `AppHost.ReleaseRepository = "PinnerSinner/envoy"` (wrong case). | `AppHost.cs` line 72 |
| Releases | `api.github.com/repos/PinnerSinner/Envoy/releases` returns `[]` (checked 2026-10-14). No installed base has to be migrated. | — |

### What is weak or missing for production

1. **Updating is manual.** The user finds the download, closes Envoy, replaces a 75 MB file and hopes it was the
   right one. Most users won't, so builds with known bugs stay in use.
2. **No installer.** No Start menu entry, no *Apps & features* entry. Users keep the exe in `Downloads`.
3. **Prereleases are invisible and mis-compared.** `releases/latest` never returns them, and `v1.2.0-beta.1` parses
   as `1.2.0`, so a beta user is never offered `1.2.0`. The app reports its own version without the suffix.
4. **Errors look like "up to date".** A 403 (rate limit) or 5xx returns `null`.
5. **The asset picker breaks with two exes.** "First `.exe`" is ambiguous once `Envoy-win-Setup.exe` exists.
6. **Release notes come from commit titles**, and the app never shows them.
7. **No signing hook and no winget.**
8. **Uninstall leaves a Run entry behind** pointing at nothing.

### Velopack facts this spec relies on

`nuget.org` was reachable. `docs.velopack.io` was not (timeout), and `github.com` HTML was not (403). The facts below
come from the `Velopack` 1.2.161 package itself (reflection, XML docs, decompiled `UpdateManager`, `VelopackApp`,
`GitBase<T>`, `GithubSource`) and the `vpk` 1.2.161 tool run on Linux with the `[win]` cross-compile directive (it
packed a test exe twice: full package, delta package, Setup.exe).

| Fact | Status |
| --- | --- |
| `Velopack` 1.2.161 (published 2026-09-29, MIT) targets net8.0 with no dependencies. `vpk` 1.2.161 is a dotnet tool. | verified (NuGet) |
| `VelopackApp.Build()` returns a builder with `SetAutoApplyOnStartup(bool)` (default ON), `OnFirstRun`, `OnRestarted`, `OnBeforeUninstallFastCallback`, `OnAfterInstallFastCallback`, `OnAfterUpdateFastCallback`, `SetLogger`, `SetArgs`, and `Run()`. Fast-callback hooks run with args `--veloapp-install/-updated/-obsolete/-uninstall <ver>`, then the process exits. Uninstall hooks are killed after 30 s. | verified (reflection + decompile) |
| `Run()` returns immediately when the app is not installed. When installed with auto-apply ON and a newer local package exists, it applies it and restarts **with the same args** before the app's own code runs. | verified (decompile) |
| `vpk pack` checks that the entry point calls `VelopackApp.Run()` ("Verified VelopackApp.Run() in …Main") and fails without it unless `--skipVeloAppCheck`. | verified (vpk run) |
| `UpdateManager(IUpdateSource source, UpdateOptions? options = null, IVelopackLocator? locator = null)`. The parameterless constructor uses `VelopackFlowSource` (hosted service). **We never use it.** | verified |
| Members: `IsInstalled`, `IsPortable`, `CurrentVersion`, `UpdatePendingRestart` (`VelopackAsset?`), `CheckForUpdatesAsync()` (throws `NotInstalledException` when not installed), `DownloadUpdatesAsync(UpdateInfo, Action<int>?, CancellationToken)`, `WaitExitThenApplyUpdates(VelopackAsset?, bool silent, bool restart, string[]? restartArgs)`, `ApplyUpdatesAndRestart(...)`. | verified |
| `UpdateInfo.TargetFullRelease` (`Version`, `NotesMarkdown`, `NotesHTML`, `SHA256`, `Size`), `IsDowngrade`, `DeltasToTarget`. Downloads try deltas first, fall back to the full package, and verify SHA1/SHA256. | verified (decompile) |
| `UpdateOptions { ExplicitChannel, AllowVersionDowngrade (default false), MaximumDeltasBeforeFallback }`. Default channel on Windows is `win`. | verified |
| `GithubSource(string repoUrl, string? accessToken, bool prerelease, IFileDownloader? downloader = null)` makes **one** API call (`/releases?per_page=10`), drops prereleases unless `prerelease`, downloads `releases.<channel>.json` from those releases and picks the **highest version** full package. Unauthenticated: 60 requests/hour/IP. UA `Velopack/<version>`. No user id is sent. | verified (decompile) |
| `vpk pack` (Windows target) options include `--packId --packVersion --packDir --mainExe --packTitle --packAuthors --icon --releaseNotes --channel --runtime --delta {None,BestSpeed,BestSize} --noPortable --shortcuts --signTemplate "…{{file}}…" -o`. Without `--runtime` it defaults to x86, so we pass `win-x64`. | verified (vpk help + run) |
| Pack output: `Envoy-win-Setup.exe`, `Envoy-<ver>-full.nupkg`, `Envoy-<ver>-delta.nupkg` (when a previous full package is in the output folder), `releases.win.json`, `assets.win.json`, `RELEASES` (legacy), and `Envoy-win-Portable.zip` unless `--noPortable`. | verified (vpk run) |
| `vpk download github --repoUrl --channel --token --pre -o` fetches the latest (pre)release's full package for deltas. `vpk upload github … --tag --releaseName --targetCommitish --pre --merge --publish` creates a **draft** unless `--publish`. | verified (vpk help) |
| Setup.exe supports `--silent`, `--verbose`, `--installto DIR`, and writes an uninstall entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall`. | verified (strings in a generated Setup.exe) |
| Install location `%LocalAppData%\<packId>\current\`, per-user, no UAC; shortcuts point to a stub `%LocalAppData%\<packId>\Envoy.exe`; uninstall key name equals packId. | **not verified** (docs unreachable). Release-pass step M-DIST-2 records it. |
| `--signParams` / `--azureTrustedSignFile`. | **not verified**; only `--signTemplate` is used. |

## Goals / Non-goals

**Goals**

- Two artifacts per release:
  - `Envoy-win-Setup.exe`: the recommended download. Per-user install, Start menu shortcut, uninstall entry,
    auto-update.
  - `Envoy.exe`: the existing portable single file. Envoy never modifies it; it only notifies.
- One-click updates for installed copies: background download (delta when possible), then **Restart to update**.
  A downloaded update is applied at the next launch if the user doesn't restart.
- An opt-out that really means *no automatic requests*, release notes shown in the app, correct SemVer handling of
  prereleases. Stable/Beta channels (SHOULD).
- A release workflow that never publishes a partial release, publishes checksums, and signs **only on tags**, under
  the protected `release` environment.
- All decision logic and user-facing text in `Envoy.Core.Updates` with unit tests. Velopack calls stay in one small
  App class.

**Non-goals**

- Buying a certificate or submitting to winget (documented manual steps only).
- MSI, Store/MSIX, machine-wide installs, ARM64.
- Auto-updating the portable exe in place (notify-only by design).
- Staged rollouts, Velopack Flow, any hosted update service, any telemetry.
- Migrating existing portable users (there are none). Portable users get a **Get the installer** button.
- Changing where user data lives. It stays under `Host.DataFolder` (`%APPDATA%\Envoy` by default, 02). The install
  folder is not user data and is not in 02's `DataInventory`.
- The update **balloon** and its "announced" bookkeeping (07a, contract §2.5). The CI **release job**, SBOM and
  attestation (08b).

## Requirements

MUST count: **17** (contract §9: DIST-1, 2, 3, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 19, 20, 27).

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-DIST-1 | MUST | Every `v*` tag build publishes `Envoy-win-Setup.exe`, a Velopack installer that installs per-user without elevation, creates a Start menu shortcut and registers an *Apps & features* uninstall entry. |
| REQ-DIST-2 | MUST | Every tagged GitHub Release contains `releases.win.json`, `assets.win.json` and `Envoy-<ver>-full.nupkg`, plus `Envoy-<ver>-delta.nupkg` whenever `vpk download` found a previous release on the same track (stable; any release for betas). |
| REQ-DIST-3 | MUST | The portable `Envoy.exe` (self-contained single file) and `Envoy.exe.sha256` are still built, smoke-tested and attached to every release as today. |
| REQ-DIST-4 | SHOULD | Pushes to `main` (not PRs) also publish the folder build and run `vpk pack` on `<csproj Version>-ci.<run_number>`, unsigned, and upload that Setup.exe as a workflow artifact, so a broken package shows up before a tag. |
| REQ-DIST-5 | MUST | A release stays a draft until every asset (Velopack files, portable exe, checksums) is uploaded **and** a workflow step has asserted the uploaded asset list. It is marked prerelease exactly when the tag contains `-`. |
| REQ-DIST-6 | SHOULD | Release notes (GitHub body and `vpk --releaseNotes`) are the tag's `CHANGELOG.md` section. A stable tag with no `## [x.y.z]` section fails before anything is uploaded. Without this, the body is the fixed footer plus a link to `CHANGELOG.md`. |
| REQ-DIST-7 | MUST | Each release has `SHA256SUMS.txt` with the SHA-256 of `Envoy.exe` and `Envoy-win-Setup.exe` in `sha256sum` format. |
| REQ-DIST-8 | MUST | Code signing runs **only** in `v*` tag builds, in steps whose job runs under the protected `release` environment, which holds the signing secrets as environment secrets. Untagged builds and forks are never signed, and their log says so. A tag build with a configured certificate Authenticode-signs (SHA-256, RFC 3161 timestamp) the portable `Envoy.exe`, the packed binaries and `Envoy-win-Setup.exe`. A tag build without one publishes unsigned with a notice. |
| REQ-DIST-9 | MUST | The `Velopack` NuGet version (`Envoy.App.csproj`, `tools/typecheck/TypeCheck.csproj`) and the `vpk` tool version (`.config/dotnet-tools.json`) are one exact pinned version. CI runs `vpk` only through `dotnet tool restore` + `dotnet vpk`. The `check` job fails when the three disagree. |
| REQ-DIST-10 | MUST | `Program.Main` calls `VelopackApp.Build().SetAutoApplyOnStartup(false)…Run()` before any WPF object exists (contract §4 step 1). Portable, Debug and `--smoke-test` launches behave as before. |
| REQ-DIST-11 | MUST | Uninstalling removes the `HKCU\…\Run\Envoy` value and does not touch `Host.DataFolder` (settings, keys, phrases, logs, transcripts, caches). |
| REQ-DIST-12 | MUST | A downloaded update is applied only when (a) the user chooses **Restart to update**, or (b) a later launch has acquired the single-instance mutex (contract §4 step 5). Step (b) is skipped with `--safe-mode` or Shift held, and runs before `StartupSentinel.BeginStartup`. An update is never applied while another Envoy instance runs, and never by an unprompted restart. |
| REQ-DIST-13 | MUST | When `CheckForUpdates` is false, or in safe mode, Envoy makes no automatic update-related network request. The About page's **Check for updates** button always works, including in safe mode. |
| REQ-DIST-14 | MUST | Update traffic goes only to GitHub for `PinnerSinner/Envoy` (`api.github.com` and release-asset downloads). It carries no identifier beyond the User-Agent product/version. The Velopack Flow source is never used. Release-page and installer URLs pass 08a's `ReleaseUrlPolicy.IsTrusted(url, UpdateEndpoints.Repository)` before they reach `IShell.OpenUrl`. |
| REQ-DIST-15 | MUST | Installed copies download updates through Velopack (delta when possible, checksum-verified) and show percent progress. Portable copies never write to their own files and offer **Download** (release page) and **Get the installer**. |
| REQ-DIST-16 | MUST | With `Updates.AutoDownload` true (default), an available update downloads in the background and the War Room banner then offers **Restart to update**. While a game is running (`Host.Game` or `Host.GameWindow` non-null) the download waits and starts once the game closes. With it false, the banner offers **Update now**. The banner shows the new version, the release notes as plain text (Markdown stripped, at most 1,200 characters) and a release-notes link (absorbs REQ-DIST-17). |
| REQ-DIST-17 | — | Folded into REQ-DIST-16. |
| REQ-DIST-18 | SHOULD | `Updates.Channel` is `Stable` or `Beta` (default `Stable`). Beta also considers GitHub prereleases. Changing channel never installs a lower version and triggers one manual check. |
| REQ-DIST-19 | MUST | All version comparisons use SemVer 2.0 precedence (prerelease < release, numeric identifiers compared numerically, build metadata ignored). `AppHost.AppVersion` (a `ReleaseVersion`) comes from `AssemblyInformationalVersion`, so a beta build knows it is a beta; all user-visible version text uses it. `AppHost.CurrentVersion` stays the assembly `Version`. The shared client's default UA keeps its browser-like form; only the `0.1` token becomes `{Major}.{Minor}.{Patch}`. |
| REQ-DIST-20 | MUST | A failed check or download never shows a modal dialog. A background failure is logged at Warning (category `Updates`) and shows nothing. A user-initiated failure shows one sentence from `UpdateText` (no raw exception text, no "Error:" prefix) and a **Retry** action. |
| REQ-DIST-21 | SHOULD | While Envoy runs, automatic checks repeat at most every 12 hours. The start-up check is skipped if the last check was under 1 hour ago. |
| REQ-DIST-22 | SHOULD | **Skip this version** hides that version from automatic checks but not from a user-initiated check. |
| REQ-DIST-23 | SHOULD | After an update (installed or a replaced portable exe), the War Room update-banner slot shows a one-time "Envoy was updated to vX" notice with **What's new** and **Dismiss**, when no update banner is active. |
| REQ-DIST-24 | SHOULD | When an update is ready, the tray menu has a **Restart to update to vX** item. |
| REQ-DIST-25 | SHOULD | The About page's "Version & updates" block also shows install type, channel, last-checked time and the status line. |
| REQ-DIST-26 | SHOULD | When `StartWithWindows` is true, start-up rewrites the Run value to the current exe path, so moving from portable to installed keeps start-at-sign-in working. |
| REQ-DIST-27 | MUST | The smoke test (contract §6) renders the War Room update banner in Available, Downloading, ReadyToRestart and Failed under every `ThemeManager.PaletteIds` entry, inside the binding-error window, and asserts the Portable-available view from a reused Portable host. No network is used. |
| REQ-DIST-28 | SHOULD | winget manifest templates (schema 1.6.0) for `Envoy-win-Setup.exe` as a user-scope `exe` installer with `--silent`, rendered on stable tags as a workflow artifact; manual submission documented. |
| REQ-DIST-29 | — | Moved to the definition of done (contract §10.4): README, `docs/installing.md`, `docs/releasing.md`, `THIRD-PARTY-NOTICES.md`, `CHANGELOG.md`. |
| REQ-DIST-30 | COULD | `ENVOY_UPDATE_FEED` (a **local folder path** only; URLs are ignored) replaces the GitHub source for installed builds, for testing updates without a release. |
| REQ-DIST-31 | SHOULD | 02's diagnostics report gains an "Updates" line: install kind, channel, state, last check, last background error kind. |

## Design

### Overview

```
Envoy.Core/Updates  (net8.0, unit tested)                 Envoy.App (Windows)
┌──────────────────────────────────────────────┐          ┌──────────────────────────────────────────┐
│ ReleaseVersion   SemVer parse/compare        │          │ Program.Main  → VelopackApp.Run()        │
│ UpdateChecker    GitHub REST (portable)      │◄─────────│ Services/VelopackUpdateBackend           │
│ GitHubReleaseBackend : IUpdateBackend        │          │   : IUpdateBackend (installed; no        │
│ UpdatePreferences  immutable settings snap.  │          │     network types named)                 │
│ UpdateCoordinator  state machine + policy    │          │ AppHost.cs: UpdateBackendFactory,        │
│ UpdateViewBuilder  state → banner view       │          │   Updates, AppVersion                    │
│ UpdateText / ReleaseNotesText  UI strings    │          │ Services/UpdateActions (banner, tray,    │
│ PendingUpdatePolicy                          │          │   later 07a's balloon)                   │
└──────────────────────────────────────────────┘          │ WarRoom banner / Appearance / About/tray │
                                                          └──────────────────────────────────────────┘
CI windows job: publish single-file (portable) → smoke → [main/tags] publish folder → vpk pack
                [tags, environment: release] sign → draft release → upload → assert assets → publish
```

`UpdateBackendFactory.Create` (in `AppHost.cs`, contract §5) picks the backend once. A Velopack-installed copy gets
`VelopackUpdateBackend`; anything else (portable exe, `dotnet run`, smoke test) gets `GitHubReleaseBackend`.
`AppHostOptions.UpdateBackend` overrides both (smoke test, and App passing the backend it already created at start-up
slot 5). `UpdateCoordinator` knows only `InstallKind`.

### Core: new and changed types (`src/Envoy.Core/Updates/`)

```csharp
namespace Envoy.Core.Updates;

/// <summary>SemVer 2.0 version. Accepts "v1.2", "1.2.3", "1.2.3-beta.1+sha". Build metadata is dropped.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }   // empty for a release
    public bool IsPrerelease => Prerelease.Count > 0;

    public ReleaseVersion(int major, int minor, int patch, params string[] prerelease);
    public static bool TryParse(string? text, [NotNullWhen(true)] out ReleaseVersion? version);
    public static ReleaseVersion Parse(string text);          // FormatException on garbage
    /// <summary>"1.2.0-beta.1+0abc…" → 1.2.0-beta.1. Falls back to the numeric assembly version, then 0.0.0.</summary>
    public static ReleaseVersion FromInformational(string? informationalVersion, Version? assemblyVersion);
    public int CompareTo(ReleaseVersion? other);              // SemVer §11 precedence; null sorts first
    public bool Equals(ReleaseVersion? other);
    public string CoreString { get; }                          // "1.2.3" (no prerelease)
    public override string ToString();                         // "1.2.3" or "1.2.3-beta.1" (no "v", no metadata)
    public static bool operator <(ReleaseVersion a, ReleaseVersion b); // plus >, <=, >=, ==, !=
}

public enum UpdateChannel { Stable, Beta }
public enum InstallKind { Installed, Portable }
public enum UpdateErrorKind { Offline, RateLimited, BadResponse, DownloadFailed, UpdaterFailed, Unknown }

/// <summary>One update the backend found. Handle is backend-private (the Velopack UpdateInfo).</summary>
public sealed record AvailableUpdate(
    ReleaseVersion Version, string? NotesMarkdown, string ReleasePageUrl,
    string? PortableDownloadUrl, string? InstallerDownloadUrl, object? Handle = null);

public sealed class UpdateCheckException(UpdateErrorKind kind, Exception? inner = null)
    : Exception(kind.ToString(), inner)
{ public UpdateErrorKind Kind { get; } = kind; }

public interface IUpdateBackend
{
    InstallKind Kind { get; }
    ReleaseVersion CurrentVersion { get; }
    /// <summary>Null when up to date. Throws UpdateCheckException for network/HTTP/feed errors; honours ct.</summary>
    Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct);
    /// <summary>Installed only (Portable throws NotSupportedException). progress: 0–100.</summary>
    Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken ct);
    /// <summary>Version of a fully downloaded, not yet applied update; null if none. Local, no network.</summary>
    ReleaseVersion? PendingRestart { get; }
    /// <summary>Starts Velopack's updater, which waits for this process to exit, applies, and relaunches with
    /// restartArgs. The caller must then exit. Installed only.</summary>
    void LaunchUpdaterAfterExit(IReadOnlyList<string> restartArgs);
}

public static class UpdateEndpoints
{
    public const string Repository = "PinnerSinner/Envoy";
    public const string RepositoryUrl = "https://github.com/PinnerSinner/Envoy";
    public static string ReleasePage(ReleaseVersion v) => $"{RepositoryUrl}/releases/tag/v{v}";
}

/// <summary>Start-up slot 5 decision (REQ-DIST-12 b).</summary>
public static class PendingUpdatePolicy
{
    public static bool ShouldApplyAtStartup(bool hasPendingPackage, bool safeModeArg, bool shiftHeld)
        => hasPendingPackage && !safeModeArg && !shiftHeld;
}
```

`AppHost.ReleaseRepository` becomes `=> UpdateEndpoints.Repository`, which fixes the casing that 08a's
`ReleaseUrlPolicy` check compares (ordinal-ignore-case, so the old value also passed).

**`UpdateChecker` (changed).** It stays the portable path and keeps today's public members and 08a's trusted-URL
checks, so existing tests still compile and pass.

```csharp
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? DownloadUrl, string? Notes)
{
    public ReleaseVersion? SemVer { get; init; }
    public string? InstallerUrl { get; init; }   // asset named exactly "Envoy-win-Setup.exe"; null if untrusted
    public bool IsPrerelease { get; init; }
}

public sealed class UpdateChecker(HttpClient http, string ownerAndRepo)
{
    public Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default);          // unchanged
    /// <summary>Stable: GET releases/latest. Beta: GET releases?per_page=10 and pick the highest SemVer.
    /// 404 → null. 403/429 → UpdateCheckException(RateLimited). Other non-success, bad JSON →
    /// BadResponse. HttpRequestException/timeout → Offline. Untrusted html_url → null (08a rule).</summary>
    public Task<UpdateInfo?> CheckAsync(ReleaseVersion current, UpdateChannel channel, CancellationToken ct = default);
    public static UpdateInfo? Parse(string json);                                  // unchanged: skips drafts/prereleases
    public static UpdateInfo? Parse(string json, bool includePrereleases);         // applies ReleaseUrlPolicy
    public static UpdateInfo? PickLatest(string releasesArrayJson, UpdateChannel channel); // applies ReleaseUrlPolicy
    public static bool TryParseVersion(string tag, out Version version);           // unchanged
}
```

`Parse` picks the portable `DownloadUrl` as the asset named exactly `Envoy.exe`, falling back to the first `.exe`
that isn't `*Setup.exe`. Requests send only `User-Agent: Envoy/<AppVersion>` and
`Accept: application/vnd.github+json` (per-request headers; the client default UA is not used for update traffic).

```csharp
/// <summary>Portable / dev builds: notify only.</summary>
public sealed class GitHubReleaseBackend(UpdateChecker checker, ReleaseVersion current) : IUpdateBackend
{
    public InstallKind Kind => InstallKind.Portable;
    // CheckAsync → checker.CheckAsync(current, channel) mapped to AvailableUpdate(PortableDownloadUrl = DownloadUrl,
    //   InstallerDownloadUrl = InstallerUrl, ReleasePageUrl = PageUrl)
    // DownloadAsync / LaunchUpdaterAfterExit → NotSupportedException; PendingRestart → null
}
```

**User-facing text (`UpdateText.cs`, `ReleaseNotesText.cs`, contract §8.7).** Every sentence the update UI shows
comes from `public static class UpdateText`: whole-sentence format strings with named placeholders, second person,
"what happened, then what to do", no "Error:" prefix and no exception text.

```csharp
public static class UpdateText
{
    public static string Available(ReleaseVersion next, ReleaseVersion current);   // "Envoy {next} is available. You have {current}."
    public static string AvailableAfterGame(ReleaseVersion next);                  // "Envoy {next} will download after your game."
    public static string Downloading(ReleaseVersion next, int percent);            // "Downloading Envoy {next}… {percent}%"
    public static string Ready(ReleaseVersion next);                               // "Envoy {next} is ready. Restart to update; it takes a few seconds."
    public static string UpdatedTo(ReleaseVersion v);                              // "Envoy was updated to {v}."
    public static string CheckFailed(UpdateErrorKind kind);                        // e.g. RateLimited → "GitHub is limiting update checks right now. Try again in an hour."
    public static string DownloadFailed(UpdateErrorKind kind);                     // e.g. "The update didn't finish downloading. Check your connection and try again."
    public static string Status(UpdateSnapshot s, TimeZoneInfo zone);              // About status line: "Up to date · checked 14:02", "Checking…", "On a beta newer than the latest stable release"
    public static string ActionLabel(UpdateAction a);                              // "Update now", "Restart to update", "Download", "Get the installer", "Retry", "Release notes", "What's new", "Dismiss"
    public const string TrayRestart = "Restart to update to {version}";                  // whole sentence, one placeholder
    public const string PortableAutoDownloadTooltip = "The portable Envoy.exe can't update itself. Use the installer to get automatic updates.";
}

public static class ReleaseNotesText
{
    /// <summary>Markdown → plain text: drops heading marks, "- "/"* " → "• ", [text](url) → text, strips **, __, `;
    /// collapses blank lines; cuts at the last line break before maxChars and appends "…". Null/blank → "".</summary>
    public static string ToPlain(string? markdown, int maxChars = 1200);
}
```

**`UpdatePreferences`** is the immutable snapshot of the update settings (contract §7). AppHost builds it in
`ApplySettings` and passes it to the coordinator; background code never reads `AppSettings`.

```csharp
public sealed record UpdatePreferences(bool CheckAutomatically, bool AutoDownload, UpdateChannel Channel,
                                       ReleaseVersion? SkippedVersion, DateTimeOffset? LastCheckUtc)
{
    public static UpdatePreferences From(AppSettings s);   // copies values; SkippedVersion parsed ("" → null)
}
```

**`UpdateCoordinator`** holds all policy and state. Thread-safe, UI-agnostic, uses `TimeProvider` (contract §3).

```csharp
public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, ReadyToRestart, Failed }

public sealed record UpdateSnapshot(
    InstallKind Kind, ReleaseVersion Current, UpdateState State, AvailableUpdate? Available,
    int Percent, bool DeferredForGame, UpdateErrorKind? Error, bool FailedWhileDownloading,
    bool LastCheckWasManual, DateTimeOffset? LastChecked, UpdateChannel Channel);

public sealed class UpdateCoordinator
{
    public static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(12);
    public static readonly TimeSpan StartupMinInterval = TimeSpan.FromHours(1);

    /// <param name="lastCheckChanged">Called (any thread) after every completed check; AppHost marshals it to the
    /// UI thread with Dispatcher.BeginInvoke and writes Settings.Updates.LastCheckUtc.</param>
    public UpdateCoordinator(IUpdateBackend backend, UpdatePreferences initial,
                             Action<DateTimeOffset> lastCheckChanged, TimeProvider time, ILog log);

    public UpdateSnapshot Snapshot { get; }          // immutable; replaced atomically (Volatile.Write)
    /// <summary>Raised on a background thread. UI consumers use AppHost's marshalled notification.</summary>
    public event Action? Changed;

    /// <summary>Volatile.Write of the new preferences. LastCheckUtc keeps the later of the stored and the incoming
    /// value. Returns a manual CheckAsync task when Channel changed since the previous call (not the first), else
    /// Task.CompletedTask.</summary>
    public Task Configure(UpdatePreferences prefs);
    /// <summary>UI thread pushes game state; a true→false transition does not itself download (TickAsync does).</summary>
    public void SetGameRunning(bool running);

    /// <summary>Not called in safe mode. PendingRestart != null → ReadyToRestart without network. Else, if
    /// CheckAutomatically and (LastCheckUtc null or older than StartupMinInterval) → CheckAsync(manual: false).</summary>
    public Task StartAsync(CancellationToken ct = default);
    /// <summary>manual=false is a no-op when CheckAutomatically is false. Concurrent calls share one run.</summary>
    public Task CheckAsync(bool manual, CancellationToken ct = default);
    /// <summary>"Update now" / "Retry" after a failed download. Installed + Available/Failed only.</summary>
    public Task DownloadAsync(CancellationToken ct = default);
    /// <summary>Periodic check when due (SHOULD, REQ-DIST-21); starts a deferred download when no game runs.</summary>
    public Task TickAsync(CancellationToken ct = default);
    /// <summary>SHOULD (REQ-DIST-22). State → Idle. Returns the skipped version for the caller to persist.</summary>
    public ReleaseVersion? SkipAvailableVersion();
    public bool CanRestartToUpdate { get; }          // State == ReadyToRestart && Kind == Installed
    /// <summary>backend.LaunchUpdaterAfterExit. Allowed after the AppHost CTS is cancelled (ExitApp step 8).
    /// Throws InvalidOperationException unless CanRestartToUpdate.</summary>
    public void RestartToUpdate(IReadOnlyList<string> restartArgs);
}
```

State rules (each has a unit test, see Test plan):

- **Check.** `Checking` → result:
  - `null`, or a version ≤ current (defence; backends already filter) → `UpToDate`.
  - Automatic check, version equals `SkippedVersion` → `Idle`. A skipped version therefore never reaches 07a's
    notice path (contract §2.5).
  - While `ReadyToRestart` and the result is ≤ the pending version → stays `ReadyToRestart`.
  - Otherwise → `Available`. Then, if `Kind == Installed && AutoDownload`: start the download, or set
    `DeferredForGame = true` while the game is running.
- **Exceptions.** Any non-cancellation exception from the backend (non-`UpdateCheckException` → `Unknown`):
  - manual → `Failed`, `Error` = kind;
  - automatic → back to the previous state, `Error` kept for diagnostics only, logged at Warning.
- **Cancellation** returns to the previous state and raises nothing.
- **Download.** `Downloading` (`Changed` raised only when the whole percent changes) → `ReadyToRestart`, or `Failed`
  with `FailedWhileDownloading = true` and `Available` kept so **Retry** works. A game starting mid-download does not
  stop it (Velopack cannot pause; the download is background-priority network only).
- **LastCheckUtc** is updated internally and reported through `lastCheckChanged` after every completed check.
- **Locking.** One `SemaphoreSlim(1,1)` serialises check and download. Reading `Snapshot` takes no lock.
- **Logging.** Category `Updates`: check results at Info (version only), failures at Warning with the kind and the
  exception, never a URL with query strings.

**`UpdateView` / `UpdateViewBuilder`.**

```csharp
public enum UpdateAction { None, CheckNow, UpdateNow, RestartToUpdate, OpenReleasePage, GetInstaller, Retry, Skip, DismissNotice }

public sealed record UpdateView(
    bool ShowBanner, string Headline, string Notes,
    UpdateAction Primary, string PrimaryLabel,
    UpdateAction Secondary, string SecondaryLabel,
    bool CanSkip, string StatusLine);

public static class UpdateViewBuilder
{
    /// <param name="justUpdatedTo">REQ-DIST-23 (SHOULD); shown only when no update banner is active.</param>
    public static UpdateView Build(UpdateSnapshot s, ReleaseVersion? justUpdatedTo = null, TimeZoneInfo? zone = null);
}
```

| Snapshot | Banner | Headline (`UpdateText`) | Primary / Secondary |
| --- | --- | --- | --- |
| Installed, Available, not auto | yes | `Available` | Update now / Release notes |
| Installed, Available, deferred | yes | `AvailableAfterGame` | Update now / Release notes |
| Installed, Downloading 42 % | yes | `Downloading` | None / Release notes |
| Installed, ReadyToRestart | yes | `Ready` | Restart to update / Release notes |
| Portable, Available | yes | `Available` | Download (`OpenReleasePage`) / Get the installer |
| Failed (manual check or download) | yes | `CheckFailed` / `DownloadFailed` | Retry / None |
| none of the above, `justUpdatedTo` set (SHOULD) | yes | `UpdatedTo` | What's new (`OpenReleasePage`) / Dismiss |
| UpToDate / Idle / Checking | no | — | — (`StatusLine` on About) |

`Notes` = `ReleaseNotesText.ToPlain(Available.NotesMarkdown)`. `CanSkip` is true for Available (both kinds) and
ReadyToRestart. Skipping a ready update does not delete the package; it is still applied at the next launch.

### Settings (`src/Envoy.Core/Settings/AppSettings.cs`, shared)

```csharp
/// <summary>Update channel, background download and bookkeeping. CheckForUpdates (existing) stays the master switch.</summary>
public UpdateSettings Updates { get; set; } = new();

public sealed class UpdateSettings
{
    public Updates.UpdateChannel Channel { get; set; } = Updates.UpdateChannel.Stable;   // SHOULD (REQ-DIST-18)
    /// <summary>Installed copies download updates in the background (never during a game).</summary>
    public bool AutoDownload { get; set; } = true;
    /// <summary>Version the user chose to skip ("" = none). Automatic checks ignore it. SHOULD (REQ-DIST-22).</summary>
    public string SkippedVersion { get; set; } = "";
    /// <summary>Written only on the UI thread via Dispatcher.BeginInvoke from the coordinator callback.</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }
    /// <summary>Version that last ran; drives the one-time "Updated to" notice. SHOULD (REQ-DIST-23).</summary>
    public string LastRunVersion { get; set; } = "";
}
```

- Additive only (contract §7). `CheckForUpdates` keeps its name and default `true`; its label becomes "Check for
  updates automatically". A missing `updates` object loads as defaults (tested). `AppSettings.CurrentVersion` stays 1.
- No property holds names, chat text or positions, so no `[Personal]`/`[Location]`/`[Secret]` marker is needed;
  `SettingsRedactionTests` is unaffected. No file is added under `DataFolder`, so `DataInventory` is unchanged.
- A property is added only together with the requirement that uses it; if a SHOULD is not built, its property is not
  added (arrival rule).
- **`Updates.LastAnnouncedVersion`** (contract §2.5) is the one persisted "announced" field. It belongs to this class,
  but 07a adds it in its own step, because 07a's balloon is its only writer and reader (arrival rule, contract §1).
  Its meaning is fixed here: it only stops a second balloon for the same version; it never hides the banner;
  `SkippedVersion` makes automatic checks report `Idle`, so a skipped version never reaches the notice path.

### App: entry point (`src/Envoy.App/Program.cs`, new) and csproj

```csharp
namespace Envoy.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)   // applied at OnStartup slot 5, after the mutex (REQ-DIST-12)
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Apply(false))
            .Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
```

- `Envoy.App.csproj`: `<StartupObject>Envoy.App.Program</StartupObject>` (`App.xaml` stays `ApplicationDefinition`;
  the generated `App.Main` is ignored) and `<PackageReference Include="Velopack" Version="1.2.161" />`.
- `tools/typecheck/TypeCheck.csproj` gets the same reference. The typecheck builds a Library, so the duplicate `Main`
  question doesn't arise there.
- The uninstall hook only touches `HKCU\…\Run`, well under Velopack's 30 s limit, and never touches `DataFolder`.
  It runs before logging exists (02's bootstrap is step 2), so it does not log.

### App: `Services/VelopackUpdateBackend.cs` (new)

Names no network type (`HttpClient` etc.), so 08a's `network-location` rule passes without a new allowed path.

```csharp
public sealed class VelopackUpdateBackend : IUpdateBackend
{
    /// <summary>Null when this copy isn't Velopack-installed (portable exe, dotnet run, smoke test). Never throws.</summary>
    public static VelopackUpdateBackend? TryCreate(ILog log);
    public InstallKind Kind => InstallKind.Installed;
    public ReleaseVersion CurrentVersion { get; }        // UpdateManager.CurrentVersion.ToNormalizedString()
    public ReleaseVersion? PendingRestart { get; }       // UpdatePendingRestart?.Version
    public Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct);
    public Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken ct);
    public void LaunchUpdaterAfterExit(IReadOnlyList<string> restartArgs);
}
```

- **Source.** `new GithubSource(UpdateEndpoints.RepositoryUrl, accessToken: null, prerelease: channel == UpdateChannel.Beta)`.
  A new `UpdateManager(source, new UpdateOptions { AllowVersionDowngrade = false })` per call, because the channel
  can change. `ExplicitChannel` stays null (`win`). With `ENVOY_UPDATE_FEED` (COULD) set to an existing absolute
  local folder, the source is a `SimpleFileSource` on that folder; any other value is ignored and logged.
- **`TryCreate`** returns null unless `IsInstalled`; any exception → null, logged at Debug.
- **`CheckAsync`.** `ct.ThrowIfCancellationRequested()`, then `await mgr.CheckForUpdatesAsync().WaitAsync(ct)`
  (Velopack's check takes no token; a cancelled check finishes in the background and is discarded). Map
  `TargetFullRelease` to `AvailableUpdate` (`ReleasePageUrl = UpdateEndpoints.ReleasePage(version)`, `Handle = info`).
  Wrap every non-cancellation exception in `UpdateCheckException`: `HttpRequestException` with 403/429 →
  `RateLimited`; other `HttpRequestException`/`IOException`/timeout → `Offline`; JSON/feed errors → `BadResponse`;
  anything else → `Unknown`.
- **`DownloadAsync`** → `mgr.DownloadUpdatesAsync((UpdateInfo)update.Handle!, p => progress.Report(p), ct)`. Velopack
  locks, uses deltas, verifies checksums and removes `.partial` files. Failures → `DownloadFailed` (or `Offline`).
- **`LaunchUpdaterAfterExit`** → `mgr.WaitExitThenApplyUpdates(mgr.UpdatePendingRestart, silent: false, restart: true, restartArgs.ToArray())`.
  Failures → `UpdateCheckException(UpdaterFailed)`. Velopack's updater waits up to 60 s for us to exit.
  `ApplyUpdatesAndRestart` is never used.

### App: `AppHost.cs` (shared) changes

```csharp
public sealed record AppHostOptions { /* 02's members */ public IUpdateBackend? UpdateBackend { get; init; } }

internal static class UpdateBackendFactory      // in AppHost.cs (contract §5: network types stay in this file)
{
    public static IUpdateBackend Create(HttpClient http, ReleaseVersion current, ILog log) =>
        (IUpdateBackend?)VelopackUpdateBackend.TryCreate(log)
        ?? new GitHubReleaseBackend(new UpdateChecker(http, UpdateEndpoints.Repository), current);
}

public static ReleaseVersion AppVersion { get; } =                    // contract §3
    ReleaseVersion.FromInformational(
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(AppHost).Assembly.GetName().Version);
// CurrentVersion (Version) is unchanged; 02's header and crash marker keep using it.

public UpdateCoordinator Updates { get; }
public UpdateView UpdateView { get; }                // rebuilt on the UI thread on every Changed; raises PropertyChanged
public ReleaseVersion? JustUpdatedTo { get; }        // SHOULD (REQ-DIST-23)
public void DismissJustUpdated();                    // SHOULD
public void SkipUpdate();                            // SHOULD: Updates.SkipAvailableVersion() → Settings.Updates.SkippedVersion; ScheduleSave()
public void StartUpdates();                          // App calls it at OnStartup slot 9 unless safe mode
```

- **Removed:** `_update`, `Update`, `HasUpdate`, `UpdateText` (the banner binds `Host.UpdateView` now) and
  `CheckForUpdatesAsync()`. `ReleaseRepository` → `UpdateEndpoints.Repository`.
- **User-Agent.** Only the version token changes: `_http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgents.Default(AppVersion))`
  → `Mozilla/5.0 (Windows NT 10.0; Win64; x64) Envoy/{AppVersion.CoreString}` (contract §3; Core helper
  `UserAgents.Default` so the form is unit-tested). Update requests keep their
  per-request `Envoy/{AppVersion}` agent.
- **Constructor, position 8** (contract §3):
  - `var log = Log.For("Updates");`
  - `Updates = new UpdateCoordinator(options?.UpdateBackend ?? UpdateBackendFactory.Create(_http, AppVersion, log), UpdatePreferences.From(Settings), t => _ui.BeginInvoke(() => { Settings.Updates.LastCheckUtc = t; ScheduleSave(); }), Time, log);`
  - `Updates.Changed += () => _ui.BeginInvoke(RefreshUpdateView);` — `RefreshUpdateView` rebuilds `UpdateView` and
    raises `PropertyChanged(nameof(UpdateView))`. This is the UI-thread signal for 06's banner and tray item and for
    07a later (contract §2.5).
  - `GameWindowChanged += () => Updates.SetGameRunning(Game is not null || GameWindow is not null);`
  - (SHOULD, REQ-DIST-23) `JustUpdatedTo` = `AppVersion` when `LastRunVersion` parses and is lower; then
    `Settings.Updates.LastRunVersion = AppVersion.ToString()` and `ScheduleSave()`.
- **`ApplySettings`, position 7:** `Updates.Configure(UpdatePreferences.From(Settings)).Forget(_log, "update channel check")`.
- **`RefreshGame`, position 3:** `Updates.SetGameRunning(Game is not null || GameWindow is not null)`; when the game
  closed, `Updates.TickAsync(_updatesCts.Token).Forget(_log, "update tick")`.
- **`StartUpdates()`:** `Updates.StartAsync(_updatesCts.Token).Forget(_log, "update start")`, then a 30-minute
  `DispatcherTimer` (`Background` priority) whose tick runs `Updates.TickAsync(_updatesCts.Token).Forget(...)`.
  (SHOULD, REQ-DIST-26) `if (Settings.StartWithWindows) StartupRegistration.Apply(true);`.
- **`Dispose()`:** cancels `_updatesCts` (aborts an in-flight download; Velopack cleans the `.partial` next time) and
  stops the timer (contract §4 exit step 3).
- Every `catch` added here logs (contract §3).

### App: `App.xaml.cs` and `IShell` (shared)

**`IShell`, 06 block (contract §3):** `void RestartToUpdate();`

**OnStartup slot 5 — pending update apply** (after the mutex, before the sentinel; `_hub` exists from step 2):

```csharp
var velo = VelopackUpdateBackend.TryCreate(_hub.For("Updates"));
if (PendingUpdatePolicy.ShouldApplyAtStartup(velo?.PendingRestart is not null, e.Args.Contains("--safe-mode"), shiftHeld))
{
    try
    {
        var flags = e.Args.Contains("--minimized") ? RestartFlags.Minimized : RestartFlags.None;
        velo!.LaunchUpdaterAfterExit(LaunchTarget.RestartArguments(flags));   // 08a's flag → args mapping
        _hub.For("Updates").Info("Applying downloaded update", LogField.Of("version", velo.PendingRestart));
        _hub.Dispose(); ReleaseMutex(); Shutdown(); return;
    }
    catch (Exception ex) { _hub.For("Updates").Warn("Couldn't start the updater; starting normally", ex); }
}
// slot 7: new AppHostOptions { …, UpdateBackend = velo }   (null → AppHost's factory)
```

- Only the 08a flag set (`--minimized`, never arbitrary args) is passed on. 06 reuses 08a's `RestartFlags` → argument
  mapping (`LaunchTarget`); if 08a kept it private, 06 makes it public rather than writing a second one.
- No window, overlay or hotkey exists yet. A failure never blocks launching.

**OnStartup slot 9:** `if (plan.AutoCheckUpdates) Host.StartUpdates();`. 06 adds `bool AutoCheckUpdates` to 02's
`StartupPlan` (`= safeMode is null`). In safe mode the coordinator still exists, so the About page's manual
**Check for updates** works.

**`RestartToUpdate()`** (IShell): `if (!Host.Updates.CanRestartToUpdate) return;`
`ExitApp(_main is { IsVisible: true } ? RestartFlags.None : RestartFlags.Minimized, applyUpdate: true);`

**`ExitApp(RestartFlags? restart = null, bool wipeData = false, bool applyUpdate = false)`** — 06 adds the third
parameter. Steps 1–7 are 02's, unchanged (settings are saved normally at step 2, contract §4). Step 8:

```text
8. if applyUpdate:
       try   Host.Updates.RestartToUpdate(LaunchTarget.RestartArguments(restart ?? None))
       catch ShellLauncher.RestartSelf(restart ?? None)   // Envoy always comes back; slot 5 retries the apply and logs
   else if restart is { } f: ShellLauncher.RestartSelf(f)
```

The log is already closed at step 8, so a failure there is not logged in this session; the relaunched instance's
slot 5 retries and logs.

**Tray (SHOULD, REQ-DIST-24).** `CreateTray` adds a hidden `_restartToUpdateItem` above the separator. On
`Host.PropertyChanged(UpdateView)`: `Visible = Host.Updates.CanRestartToUpdate`,
`Text = TextTemplate.Format(UpdateText.TrayRestart, null, ("version", version))`. Click → `UpdateActions.Run(UpdateAction.RestartToUpdate, …)`.

**`Services/UpdateActions.cs` (new).** One dispatcher for an `UpdateAction`, used by the banner commands and the tray
item now and by 07a's balloon later:

```csharp
internal static class UpdateActions
{
    public static void Run(UpdateAction action, AppHost host, IShell shell, ILog log);
    // UpdateNow / Retry → FailedWhileDownloading || Kind==Installed&&Available ? DownloadAsync() : CheckAsync(true) — Forget(log)
    // RestartToUpdate   → shell.RestartToUpdate()
    // OpenReleasePage   → shell.OpenUrl(trusted(Available?.ReleasePageUrl) ?? UpdateEndpoints.RepositoryUrl + "/releases")
    // GetInstaller      → shell.OpenUrl(trusted(InstallerDownloadUrl) ?? trusted(ReleasePageUrl) ?? …/releases)
    // CheckNow          → CheckAsync(manual: true); Skip → host.SkipUpdate(); DismissNotice → host.DismissJustUpdated()
}
```

`trusted(u)` = `ReleaseUrlPolicy.IsTrusted(u, UpdateEndpoints.Repository) ? u : null`. `shell.OpenUrl` goes through
`ShellLauncher.TryOpen`.

### UI changes (contract §8.6 order)

All colours use existing `DynamicResource` keys (`Accent.Gradient`, `Text.OnAccent`, `Text.Secondary`, `Text.Muted`,
`Bg.PanelAlt`). No new palette keys. Progress is text ("42%"); there is no themed `ProgressBar`. Every new interactive
control gets `AutomationProperties.Name` (01's `CLAUDE.md` rule); buttons with bound labels bind the name to the same
label.

**War Room (`Pages/WarRoomPage.xaml`) — slot 2, below 02's safe-mode banner, above 07a's notice list.** Replace the
update `Border` with one `Border x:Name="UpdateBanner"` (`Accent.Gradient`, visible when `Host.UpdateView.ShowBanner`):

- left: `TextBlock x:Name="UpdateHeadline"` `Text={Binding Host.UpdateView.Headline}` (`Text.OnAccent`, SemiBold);
  below it a "What's new" `ToggleButton` + `TextBlock TextWrapping=Wrap Text={Binding Host.UpdateView.Notes}`
  pair built from existing styles, collapsed when `Notes` is empty (`check_resources.py` must pass);
- right: Primary button (`Content`/`AutomationProperties.Name` = `PrimaryLabel`, `UpdatePrimaryCommand`), Secondary
  `Button.Ghost` (`UpdateSecondaryCommand`), and `Button.Chip` "Skip" (`SkipUpdateCommand`, visible when `CanSkip`,
  SHOULD); each collapsed when its label is empty.

The slot shows at most one item: the update banner, else (SHOULD) the "Updated to" notice, which the builder already
folds into the same `UpdateView`.

**Appearance page — "Startup & updates" group, last** (after Theme, and after the Accessibility and Notifications
groups that 07b/07a insert later). The existing "General" group becomes this group:

- `CheckBox` "Start Envoy when I sign in to Windows" (existing).
- `CheckBox` "Check for updates automatically" → `Settings.CheckForUpdates`.
- `CheckBox` "Download updates in the background (never during a game)" → `Settings.Updates.AutoDownload`,
  `IsEnabled={Binding IsInstalled}`, tooltip `UpdateText.PortableAutoDownloadTooltip`.
- (SHOULD) `SettingRow` "Update channel": `ComboBox` over `UpdateChannels` (`Stable (recommended)`,
  `Beta: early builds, may be rough`), caption "Switching back to Stable keeps your current build until a newer
  stable release comes out." The channel-change check happens in `ApplySettings` position 7, not in the view model.
- The transcripts row and **Open logs** stay where they are, under a "Data" subheading.

**About page — "Version & updates" block, first** (contract §8.6):

- `TextBlock` "Version {Version}" (MUST; `MainViewModel.Version` = `AppHost.AppVersion.ToString()`), and (SHOULD,
  REQ-DIST-25) " · {InstallKindText} · {ChannelText}" plus `Host.UpdateView.StatusLine`.
- `Button` "Check for updates" → `CheckForUpdatesCommand` (MUST, REQ-DIST-13).

**`ViewModels/MainViewModel.Updates.cs` (new partial):** `UpdateChannels`, `IsInstalled`, `InstallKindText`,
`ChannelText`; `UpdatePrimaryCommand`/`UpdateSecondaryCommand` (`RelayCommand` →
`UpdateActions.Run(Host.UpdateView.Primary|Secondary, …)`), `SkipUpdateCommand`, and `CheckForUpdatesCommand`
(`AsyncRelayCommand(() => Host.Updates.CheckAsync(manual: true), ReportCommandError)`). 01's
`DownloadUpdateCommand` is deleted.

### Threading

- `UpdateCoordinator` work runs on the thread pool (`ConfigureAwait(false)`); `Changed` fires there. AppHost
  marshals to the dispatcher with `BeginInvoke` before touching `UpdateView` or `Settings` (contract §2.5, §7).
- Preferences reach the coordinator only as `UpdatePreferences` (immutable, `Volatile.Write`). Game state arrives via
  `SetGameRunning` from the UI thread (volatile bool).
- Velopack's progress callback fires on a pool thread; the coordinator throttles it to whole-percent changes.
- Start-up slot 5 is synchronous and local: it reads the packages folder and makes no network call.

### Failure behaviour

| Failure | Behaviour |
| --- | --- |
| Offline / DNS / timeout, automatic | Silent; state unchanged; Warning logged; `LastCheckUtc` updated so the next try waits for the interval. |
| GitHub 403/429 | Automatic: as above. Manual: `UpdateText.CheckFailed(RateLimited)` + Retry. |
| No releases yet (404 / empty feed) | `UpToDate`. |
| Download interrupted / checksum mismatch | `Failed` + `DownloadFailed(kind)` + Retry; Velopack removes partial files; no restart offered. |
| Updater can't start at **Restart to update** | Envoy relaunches through `RestartSelf`; the next start's slot 5 retries the apply and logs the failure. |
| Updater can't start at slot 5 | Warning logged; normal start-up continues. |
| Update applied but the new version crashes at start | No automatic rollback. 02's safe mode (Shift / `--safe-mode`) starts without applying anything further; the user can reinstall an older `Envoy-win-Setup.exe` (documented in `installing.md`). |
| Portable exe in a read-only folder | Unaffected: portable never writes itself. |
| Beta user switches to Stable while on a newer beta | No downgrade; status line `UpdateText.Status` says "On a beta newer than the latest stable release". |
| `--smoke-test` | `SmokeUpdateBackend`; no network; `Program.Main` still runs `VelopackApp.Run()` (returns at once when not installed). |

### Policy allowlists and banned APIs (contract §5)

- **Native (`policy.json` → `native.allowed`):** no additions. 06 declares no P/Invoke.
- **Network `allowedPaths`:** no additions. `HttpClient` is named only in `src/Envoy.Core/Updates/` (existing
  `UpdateChecker`, `GitHubReleaseBackend` constructor chain) and in `src/Envoy.App/AppHost.cs`
  (`UpdateBackendFactory.Create`). `VelopackUpdateBackend.cs` names no network type (`HttpRequestException` does not
  match the word-boundary rule).
- **Hosts (`hosts.allowed`):** no additions. The only literals are `https://github.com/PinnerSinner/Envoy` and
  `https://api.github.com/…`, both already seeded by 08a. `http://timestamp.digicert.com` lives in
  `tools/release/sign.ps1`, outside `src/`.
- **`BannedSymbols.txt`:** no new exception. 06 starts no process itself: `RestartSelf` is 08a's, and Velopack's
  `WaitExitThenApplyUpdates` starts `Update.exe` inside the Velopack library. 06 adds one line to the threat model's
  T8 row: "the updater process is started by Velopack (pinned, hash-verified packages), never by Envoy code".
- **Static rules** added to 01's `tools/typecheck/check_ui_rules.py` (named to avoid number clashes):
  - `UPD1`: `new UpdateManager(` appears only in `src/Envoy.App/Services/VelopackUpdateBackend.cs`; `VelopackFlowSource`
    and `ApplyUpdatesAndRestart` appear nowhere in `src/`.
  - `UPD2`: `VelopackApp.Build()` appears only in `src/Envoy.App/Program.cs`, and that file contains
    `SetAutoApplyOnStartup(false)`.

### Release workflow (`.github/workflows/build.yml`)

The layout stays one `check` job plus one `windows` job (08b restructures later). Changes to the `windows` job:

- **Environment.** `environment: ${{ startsWith(github.ref, 'refs/tags/v') && 'release' || '' }}`. Tag builds run
  under the protected `release` environment (required reviewers), whose **environment secrets** are
  `ENVOY_SIGN_PFX_BASE64` and `ENVOY_SIGN_PFX_PASSWORD`. Untagged runs get no environment and therefore cannot read
  the secrets at all. `permissions: contents: write` at job level (needed for the tag release; 08b narrows it).
- **Tools.** `dotnet tool restore` once; every `vpk` call is `dotnet vpk …` (version from `.config/dotnet-tools.json`).

Steps, in order:

1. **Version** (extended). Untagged: `pack = "<csproj Version>-ci.${{ github.run_number }}"`. Tags: `pack = value`.
2. **Release notes** (SHOULD, REQ-DIST-6). Tags: `python tools/release/changelog_notes.py CHANGELOG.md <version> --out notes.md`
   (stable without section → exit 2, before any upload; prerelease → `## [x.y.z-…]` or `## [Unreleased]`). Without
   REQ-DIST-6, and on untagged builds: a one-line `notes.md`.
3. **Publish single-file exe.** Unchanged.
4. **Smoke test, screenshots, screenshots branch.** Unchanged.
5. **Signing setup** — `if: startsWith(github.ref, 'refs/tags/v')`. Decode the PFX to `$env:RUNNER_TEMP\envoy-sign.pfx`
   and output `signing=true`, or print `::notice::Code signing skipped (no certificate in the release environment)`.
   Untagged runs skip the step; a separate always-run step prints `::notice::Unsigned build (not a v* tag)`.
6. **Sign portable exe** — `if: steps.sign.outputs.signing == 'true'`: `pwsh tools/release/sign.ps1 publish/Envoy.exe`
   (after the smoke test, so the smoke-tested bytes differ only by the signature).
7. **Publish folder build** — tags, and (SHOULD, REQ-DIST-4) pushes to `main`:
   `dotnet publish … -o publish-velo -p:PublishSingleFile=false -p:InformationalVersion=<pack>` (+ `-p:Version=<numeric>` on tags).
8. **Previous release for deltas** — tags only: `dotnet vpk download github --repoUrl https://github.com/${{ github.repository }} --channel win --token ${{ secrets.GITHUB_TOKEN }} -o Releases`
   (`--pre` for prerelease tags), `continue-on-error: true`; output `has_previous`.
9. **vpk pack** — same condition as step 7:
   ```
   dotnet vpk pack --packId Envoy --packVersion <pack> --packDir publish-velo --mainExe Envoy.exe
        --packTitle Envoy --packAuthors "Envoy contributors" --icon src/Envoy.App/Assets/envoy.ico
        --releaseNotes notes.md --channel win --runtime win-x64 --noPortable --shortcuts StartMenuRoot
        --delta <BestSpeed on tags | None otherwise> -o Releases
        [--signTemplate "pwsh -NoProfile -File tools/release/sign.ps1 {{file}}"   # only if signing == 'true']
   ```
   The log must contain `Verified VelopackApp.Run()` (pack fails otherwise).
10. **Checksums** — tags: keep `publish/Envoy.exe.sha256`; write `SHA256SUMS.txt` (`<hash>  Envoy.exe`,
    `<hash>  Envoy-win-Setup.exe`).
11. **Artifacts.** Existing `Envoy-<v>-win-x64`; when step 9 ran, `Envoy-<v>-setup` (`Envoy-win-Setup.exe`, plus
    `SHA256SUMS.txt` on tags).
12. **Release** — tags only; replaces `softprops/action-gh-release`. `env: GH_TOKEN: ${{ github.token }}`.
    ```
    dotnet vpk upload github --repoUrl https://github.com/${{ github.repository }} --token ${{ secrets.GITHUB_TOKEN }}
        --channel win -o Releases --tag ${{ github.ref_name }} --releaseName "Envoy ${{ github.ref_name }}"
        --targetCommitish ${{ github.sha }} --merge [--pre]        # creates/merges a DRAFT
    gh release upload ${{ github.ref_name }} publish/Envoy.exe publish/Envoy.exe.sha256 SHA256SUMS.txt --clobber
    python tools/release/check_release_assets.py --tag ${{ github.ref_name }} --version <pack> --delta <has_previous>
    gh release edit ${{ github.ref_name }} --notes-file release-body.md --draft=false [--prerelease]
    ```
    - `check_release_assets.py` reads `gh release view <tag> --json assets,isDraft` and fails unless the release is
      still a draft and holds exactly the expected names: `Envoy.exe`, `Envoy.exe.sha256`, `SHA256SUMS.txt`,
      `Envoy-win-Setup.exe`, `Envoy-<v>-full.nupkg`, `releases.win.json`, `assets.win.json`, `RELEASES`, plus
      `Envoy-<v>-delta.nupkg` when `has_previous`. It also recomputes the two hashes from the local files and compares
      them with `SHA256SUMS.txt`.
    - `release-body.md` = `notes.md` + a fixed footer (which file to download, how to verify `SHA256SUMS.txt`, link to
      `docs/installing.md`). `--merge` makes re-runs idempotent. `--draft=false` runs last. Unauthenticated clients
      (the app) cannot see drafts.
13. **winget** (SHOULD, REQ-DIST-28) — stable tags: `python tools/release/render_winget.py …` → artifact
    `winget-manifests`.

CI time (contract §6 budget, windows job ≤ 20 min excluding tag-only release steps): PR runs gain only step 5's
notice; `main` pushes gain steps 7 and 9 (~1–2 min, SHOULD); everything else is tag-only.

**`check` job additions** (Ubuntu):

- `tools/release/check_velopack_pin.sh` (MUST): compares the `Velopack` version in `Envoy.App.csproj` and
  `tools/typecheck/TypeCheck.csproj` with `vpk` in `.config/dotnet-tools.json`; fails if they differ.
  `--self-test` runs it on a temp copy with one pin changed and expects failure.
- `python3 tools/release/check_signing_scope.py` (MUST) and its `--self-test`: line-based, stdlib only. Fails unless
  every `secrets.ENVOY_SIGN_` reference in `build.yml` is inside a step whose `if:` requires
  `startsWith(github.ref, 'refs/tags/v')` (directly or via `steps.sign.outputs.signing`), and the `windows` job's
  `environment:` line is the tag expression above.
- `python3 tools/release/check_release_assets.py --self-test` (MUST; fixture JSON, no network).
- `python3 tools/release/changelog_notes.py --self-test`, `python3 tools/release/render_winget.py --self-test` (SHOULD,
  with their requirements).

### Release tooling (new, `tools/release/`)

| File | Purpose |
| --- | --- |
| `check_velopack_pin.sh` | Pin consistency (REQ-DIST-9). |
| `check_signing_scope.py` | Tag-only signing scope (REQ-DIST-8). |
| `check_release_assets.py` | Draft + asset list + checksum assertion before publish (REQ-DIST-2, 3, 5, 7). |
| `sign.ps1` | `sign.ps1 <file>`: newest `signtool.exe` under `${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\`, then `sign /fd SHA256 /f $env:RUNNER_TEMP\envoy-sign.pfx /p $env:ENVOY_SIGN_PFX_PASSWORD /tr http://timestamp.digicert.com /td SHA256 <file>`. Exits 0 with a notice when the PFX is missing; fails on a signtool error. Header comment explains swapping in Azure Trusted Signing or SignPath. |
| `changelog_notes.py` (SHOULD) | Prints the body of `## [<version>]` up to the next `## `; prerelease falls back to `## [Unreleased]`; stable without section exits 2. `--self-test`: exact match, prerelease fallback, missing stable → 2, last section, dated headings. |
| `render_winget.py` (SHOULD) | Fills `packaging/winget/*.yaml` placeholders into `<out>/manifests/p/PinnerSinner/Envoy/<version>/`; fails on leftover `{{` or a prerelease. `--self-test`. |

### winget templates (SHOULD, `packaging/winget/`)

`PinnerSinner.Envoy.yaml` (version, `ManifestVersion: 1.6.0`), `.installer.yaml` (`InstallerType: exe`, `Scope: user`,
`InstallModes: [interactive, silent, silentWithProgress]`, `InstallerSwitches: { Silent: --silent, SilentWithProgress: --silent }`,
`UpgradeBehavior: install`, one x64 installer), `.locale.en-US.yaml` (publisher, name, MIT, short description, tags,
`ReleaseNotesUrl`). `ProductCode`/`AppsAndFeaturesEntries` are omitted until M-DIST-2 records the uninstall key.

### Docs (definition of done, contract §10.4)

- **`README.md`:** Quick start step 1 becomes "Download **Envoy-win-Setup.exe**
  (`releases/latest/download/Envoy-win-Setup.exe`) and run it; it installs for your Windows account only (no admin)
  and keeps itself up to date. Prefer no install? Download the portable `Envoy.exe`." "One file." → "Installer or one
  file." The roadmap's installer item is removed ("Signed releases" stays).
- **`docs/installing.md` (new):** installer vs portable; where things live (install `%LocalAppData%\Envoy`, data in
  the data folder shown on About); how updates work (background download, never during a game, Restart to update,
  applied on next launch, Shift skips it); channels; opting out (GitHub only is contacted); uninstalling (data is
  kept; use About › Troubleshooting › "Delete all Envoy data…" first to remove it); SmartScreen while unsigned;
  verifying `SHA256SUMS.txt`; installing an older version; winget once accepted.
- **`docs/releasing.md`:** pipeline steps, CHANGELOG rule, beta tags, draft-then-assert-then-publish, deltas, the
  `release` environment and its secrets (and that tag builds wait for a reviewer), alternatives via `sign.ps1`,
  manual winget submission, bumping Velopack in the three pinned places; plus the 06 manual pass M-DIST-1…6 below,
  which 08b folds into the consolidated pre-release pass.
- **`docs/faq.md`:** "Does Envoy phone home?" adds: update checks go to GitHub only and can be turned off.
- **`docs/architecture.md`:** the `Updates/` row lists the new types; the App section mentions `Program.cs`,
  `VelopackUpdateBackend`, `UpdateActions`, and the start-up slot 5 apply.
- **`CLAUDE.md`:** "Never write user data under the install folder (`%LocalAppData%\Envoy`); all user data lives under
  `Host.DataFolder`." and "Update UI text comes from `UpdateText`."
- **`docs/security/threat-model.md`:** the T8 line above.
- **`THIRD-PARTY-NOTICES.md`:** Velopack (MIT). **`CHANGELOG.md`:** Unreleased → Added.

## Acceptance criteria

Kinds per contract §9: unit = Core unit test; typecheck = `tools/typecheck/typecheck.sh`; smoke = named Windows smoke
section; CI = check in the `check` job or a workflow step; M = numbered step in `docs/releasing.md`'s manual pass.

| ID | REQ | Verified by | Kind |
| --- | --- | --- | --- |
| AC-DIST-1 | 19 | `ReleaseVersionTests.*` (incl. `FromInformational_*`) pass. `UserAgentTests.Default_agent_keeps_browser_form` asserts `UserAgents.Default(1.2.3-beta.1)` is `Mozilla/5.0 (Windows NT 10.0; Win64; x64) Envoy/1.2.3`. The typecheck compiles `AppHost.CurrentVersion` as `Version` (02's consumers unchanged) and `AppHost.AppVersion` as `ReleaseVersion`. | unit, typecheck |
| AC-DIST-2 | 13, 16, 20 | `UpdateCoordinatorTests.*` pass; each State rule has a named test. `StartupPlanTests.Safe_mode_disables_automatic_update_checks` passes. | unit |
| AC-DIST-3 | 15, 16, 20 | `UpdateViewBuilderTests.*`, `UpdateTextTests.*`, `ReleaseNotesTextTests.*` and `GitHubReleaseBackendTests.*` pass. `UpdateTextTests.Failure_sentences_have_no_error_prefix_or_exception_text` covers every `UpdateErrorKind`. | unit |
| AC-DIST-4 | 14 | `UpdateCheckerTests` (fake `HttpMessageHandler`): requests go only to `https://api.github.com/repos/PinnerSinner/Envoy/…`, carry only `User-Agent` and `Accept`; untrusted `html_url`/installer URLs are dropped. `check_policy.py` passes with no `policy.json` change. `check_ui_rules.py` `UPD1` passes. | unit, CI, typecheck |
| AC-DIST-5 | 13, 16 | `UpdatePreferencesTests.Snapshot_is_unaffected_by_later_settings_changes` and `UpdateSettingsTests.*` (defaults; old file without `updates` loads). | unit |
| AC-DIST-6 | 10 | typecheck passes with `Program.cs` and the Velopack reference; `check_ui_rules.py` `UPD2` passes. Smoke passes (unpackaged start unchanged). On tags (and `main` with REQ-DIST-4) the `vpk pack` step succeeds, which requires `Verified VelopackApp.Run()`. | typecheck, smoke, CI |
| AC-DIST-7 | 9 | `check_velopack_pin.sh` and its `--self-test` pass in `check`. | CI |
| AC-DIST-8 | 8 | `check_signing_scope.py` and `--self-test` pass in `check`. Every untagged `windows` run prints `Unsigned build (not a v* tag)`. **M-DIST-5.** | CI, M |
| AC-DIST-9 | 27 | Smoke sections `updates(<palette>) ok` for every `ThemeManager.PaletteIds` entry and `updates-portable ok`; zero binding errors in each palette's window; with `--screenshots`, `{palette}-update.png` of the **War Room** exists for every palette. | smoke |
| AC-DIST-10 | 2, 3, 5, 7 | On every tag, `check_release_assets.py` passes before `--draft=false` (draft state, exact asset list, delta when a previous release existed, checksums match). `--self-test` passes in `check`. **M-DIST-1** (rehearsal). | CI, M |
| AC-DIST-11 | 1, 11 | **M-DIST-2.** | M |
| AC-DIST-12 | 12 | `PendingUpdatePolicyTests.*` (pending + no flags → apply; `--safe-mode` → skip; Shift → skip; no package → skip). `UPD2` (auto-apply off). **M-DIST-3.** | unit, typecheck, M |
| AC-DIST-13 | 15, 16 | **M-DIST-1** (install beta.1 → banner → Restart to update → beta.2 and "Updated to" once; second run: quit while ready, relaunch → already updated). **M-DIST-4** (in game). **M-DIST-6** (portable). | M |
| AC-DIST-14 | 13 | **M-DIST-6** (opt-out: no `api.github.com` connection for 2 min; **Check for updates** makes one; same in safe mode). | M |
| AC-DIST-15 | 6 (SHOULD) | `changelog_notes.py --self-test` passes in `check`. | CI |
| AC-DIST-16 | 28 (SHOULD) | `render_winget.py --self-test` passes; the stable rehearsal tag produces `winget-manifests` with no `{{`. | CI |
| AC-DIST-17 | 18, 21–26, 31 (SHOULD) | The matching `UpdateCoordinatorTests`/`UpdateViewBuilderTests` cases; tray item and Run-key in M-DIST-2/3 when built. | unit, M |
| AC-DIST-18 | 30 (COULD) | Manual: pack two local versions into `C:\feed`, install the first, set `ENVOY_UPDATE_FEED=C:\feed`: it offers the second; a URL value is ignored and logged. | manual |

### Manual pass (06 section of `docs/releasing.md`)

- **M-DIST-1 Release rehearsal** (fork or real repo): push `v0.9.0-beta.1`, then `v0.9.0-beta.2`. Each release appears
  only after the job finishes, is marked prerelease, the body is the CHANGELOG text (if REQ-DIST-6) and
  `Get-FileHash` matches `SHA256SUMS.txt`. Install beta.1 with Setup.exe (channel Beta), restart Envoy: the banner
  shows "Downloading…", then "Envoy 0.9.0-beta.2 is ready" with notes. **Restart to update**: Envoy reopens on beta.2
  within ~10 s, in the same window state; "Envoy was updated to 0.9.0-beta.2" shows once (if REQ-DIST-23).
  Repeat with a ready update, quit from the tray, relaunch from the Start menu: already on the new version.
- **M-DIST-2 Install/uninstall:** Setup.exe runs without UAC; Start menu "Envoy" exists; *Apps & features* lists it.
  Record the `HKCU\…\Uninstall\<key>` name. Enable Start with Windows, uninstall: `HKCU\…\Run\Envoy` is gone, the data
  folder still holds `settings.json`, `%LocalAppData%\Envoy` is gone.
- **M-DIST-3 No apply under another instance / in safe mode:** with an update ready and Envoy running, start Envoy
  from the Start menu: the running instance comes to the front, nothing is applied. Quit, start with Shift held: safe
  mode starts on the old version, nothing applied; the next normal start applies it.
- **M-DIST-4 In game:** AutoDownload on, update available, AoE2 DE (or any detected title) running: About/banner reads
  "…will download after your game"; no `packages\*.partial` appears; after closing the game the download starts
  within one detector cycle (3 s) plus a tick.
- **M-DIST-5 Signing:** with a certificate in the `release` environment, the tag build's `Get-AuthenticodeSignature`
  on `Envoy.exe`, `Envoy-win-Setup.exe` and the installed `current\Envoy.exe` reports a signature (`NotSigned` fails;
  `UnknownError` is expected only for a self-signed test certificate). A push to `main` in the same repo produces an
  unsigned artifact.
- **M-DIST-6 Portable and opt-out:** portable beta.1 with Beta selected offers **Download** and **Get the installer**
  (opens the Setup.exe URL); the exe's timestamp is unchanged after an hour. Untick "Check for updates automatically",
  restart, watch Resource Monitor → Network for 2 minutes: no `api.github.com` connection; **Check for updates** then
  makes one. Repeat the 2-minute watch in safe mode with the box ticked.

## Test plan

### Unit tests (`tests/Envoy.Core.Tests/`)

**`Updates/ReleaseVersionTests.cs`** — `Parses_plain_and_v_prefixed` (`"1.2.3"`, `"v1.2.3"`, `"V1.2"` → 1.2.0);
`Parses_prerelease_identifiers`; `Drops_build_metadata`; `Prerelease_sorts_before_release`;
`Numeric_identifiers_compare_numerically` (`beta.2 < beta.10`); `Alphanumeric_vs_numeric_identifiers` (SemVer §11);
`Rejects_garbage` (`"latest"`, `""`, `null`, `"1.2.3.4.5"`, `"1..2"`); `ToString_round_trips`; `CoreString_drops_prerelease`;
`FromInformational_strips_source_revision` (`"1.2.0-beta.1+0abc"` → `1.2.0-beta.1`);
`FromInformational_falls_back_to_assembly_version`; `FromInformational_falls_back_to_zero`.

**`Updates/UserAgentTests.cs`** — `Default_agent_keeps_browser_form`.

**`Updates/ReleaseNotesTextTests.cs`** — `Strips_headings_and_emphasis`; `Links_become_their_text`;
`Collapses_blank_lines`; `Truncates_at_line_boundary_with_ellipsis` (3,000 chars → ≤ 1,201, ends "…");
`Null_or_blank_gives_empty`.

**`Updates/UpdateTextTests.cs`** — `Failure_sentences_have_no_error_prefix_or_exception_text` (every kind, both
phases); `Headlines_contain_versions`; `Status_up_to_date_includes_local_check_time`;
`Status_beta_ahead_of_stable_explains_no_downgrade`.

**`PlayerAndUpdateTests.cs` → `UpdateCheckerTests`** (existing tests unchanged) —
`Parse_prefers_portable_exe_over_setup`; `Parse_with_prereleases_included_returns_prerelease`;
`Parse_drops_untrusted_installer_url`; `PickLatest_stable_ignores_prereleases_and_drafts`;
`PickLatest_beta_picks_highest_semver_not_newest_published`; `PickLatest_skips_untrusted_html_url`;
`Check_stable_calls_latest_endpoint_with_minimal_headers`; `Check_beta_calls_list_endpoint`;
`Check_returns_null_when_not_newer` (incl. `0.4.0-beta.2` → `0.4.0` is an update); `Check_404_returns_null`;
`Check_403_and_429_throw_RateLimited`; `Check_bad_json_throws_BadResponse`; `Check_network_error_throws_Offline`.

**`Updates/GitHubReleaseBackendTests.cs`** — `Maps_update_info_to_available_update`; `Download_throws_not_supported`;
`PendingRestart_is_null`.

**`Updates/PendingUpdatePolicyTests.cs`** — the four cases in AC-DIST-12.

**`Updates/UpdatePreferencesTests.cs`** — `Snapshot_is_unaffected_by_later_settings_changes`;
`Empty_skipped_version_is_null`.

**`Updates/UpdateCoordinatorTests.cs`** — `FakeUpdateBackend` (scripted results, counters, `TaskCompletionSource`
gates), `FakeTimeProvider`, `NullLog`:

- `Automatic_check_disabled_makes_no_backend_call` (`StartAsync`, `TickAsync` → 0 calls)
- `Manual_check_runs_even_when_disabled`
- `Startup_skips_check_when_checked_within_an_hour` (−30 min → 0; −2 h → 1)
- `Startup_with_pending_package_goes_ready_without_network`
- `Installed_autodownload_reaches_ReadyToRestart` (`Changed` for each state)
- `Autodownload_off_stops_at_Available`
- `Download_deferred_while_game_running_then_resumes_on_tick` (`SetGameRunning(true)` → deferred, 0 downloads;
  `SetGameRunning(false)` + `TickAsync` → 1)
- `Game_starting_mid_download_does_not_cancel`
- `Portable_never_downloads`
- `Skipped_version_hidden_for_automatic_check` / `Skipped_version_shown_for_manual_check` /
  `SkipAvailableVersion_returns_version_and_goes_idle` (SHOULD)
- `Automatic_failure_is_silent` (state restored, `Error` set, not `Failed`)
- `Manual_failure_sets_Failed_with_kind`
- `Download_failure_keeps_available_and_retry_succeeds`
- `Cancellation_restores_previous_state`
- `Concurrent_checks_share_one_backend_call`
- `Periodic_tick_checks_after_12_hours` (SHOULD; 11 h → 0, 12 h → 1)
- `LastCheck_callback_after_success_and_failure`
- `Configure_keeps_later_LastCheckUtc`
- `Configure_channel_change_triggers_one_manual_check` / `First_configure_does_not_check`
- `Channel_is_passed_to_backend`
- `Older_or_equal_result_is_up_to_date`
- `Check_while_ready_keeps_ready_for_same_version`
- `RestartToUpdate_requires_ready_and_installed` (throws otherwise; when ready, passes args through)
- `RestartToUpdate_works_after_token_cancelled`
- `Progress_raises_changed_only_on_whole_percent_change`

**`Updates/UpdateViewBuilderTests.cs`** — one `[Theory]` row per line of the snapshot→banner table;
`JustUpdated_shown_only_without_active_banner` (SHOULD).

**`Settings/UpdateSettingsTests.cs`** — `Update_settings_default_and_old_files_load`; `Update_settings_round_trip`.

**02's `StartupPlanTests`** — `Safe_mode_disables_automatic_update_checks`; normal start enables them.

### Script self-tests (CI `check` job)

`check_velopack_pin.sh --self-test`, `check_signing_scope.py --self-test`, `check_release_assets.py --self-test`;
SHOULD: `changelog_notes.py --self-test`, `render_winget.py --self-test`. `test_check_ui_rules.py` gains `UPD1`/`UPD2`
fixture cases.

### Smoke-test additions (`src/Envoy.App/SmokeTest.cs`, contract §6)

`SmokeUpdateBackend : IUpdateBackend` (private nested): configurable `Kind`; `NextVersion` settable; `FailNextCheck`;
`DownloadAsync` reports 0 → 50, waits on a gate, then 100; `LaunchUpdaterAfterExit` throws (must never be reached);
no network.

- **Phase 1 (setup, once):** the main host is created with `AppHostOptions.UpdateBackend = new SmokeUpdateBackend(Installed)`
  and its temp `DataFolder`; set `Settings.Updates.AutoDownload = false` and `ApplySettings()`. Create **one** Portable
  host (own temp `DataFolder` under the smoke root, `SmokeUpdateBackend(Portable)`), run its manual check, and assert
  `UpdateViewBuilder.Build(portable.Updates.Snapshot)` has Primary `OpenReleasePage`, Secondary `GetInstaller` →
  `[smoke] updates-portable ok`. It is kept for teardown only (contract: reused, not rebuilt per palette).
- **Phase 2, per palette, step 3 (MainWindow open, binding listener running),** section `updates(<palette>)`, with
  `NextVersion = 9.9.<palette index>` so each palette sees a newer version than the last ready one:
  1. `FailNextCheck`; `await Updates.CheckAsync(manual: true)`; `Navigate(PageIds.Dashboard)`; pump. Assert
     `UpdateBanner` visible, `UpdateView.Primary == Retry`.
  2. `CheckAsync(manual: true)`; pump. Assert `Primary == UpdateNow` and `UpdateHeadline.Text == UpdateView.Headline`.
  3. Start `DownloadAsync()`; wait for 50 %; pump; assert `State == Downloading` and the headline contains "50%".
     Open the gate; await; pump. Assert `Primary == RestartToUpdate`.
  4. With `--screenshots`, screenshot the **War Room** as `{palette}-update.png`.
- **Phase 3:** both hosts are disposed with the others; the temp root is deleted.
- The smoke test never calls `RestartToUpdate`, and the tray isn't created in smoke mode.

Expected cost: < 1 s per palette plus ~0.5 s once.

## Files touched

| Path | Change | Shared? |
| --- | --- | --- |
| `src/Envoy.Core/Updates/ReleaseVersion.cs` | new | |
| `src/Envoy.Core/Updates/UpdateBackend.cs` | new: `IUpdateBackend`, `AvailableUpdate`, `UpdateChannel`, `InstallKind`, `UpdateErrorKind`, `UpdateCheckException`, `UpdateEndpoints`, `PendingUpdatePolicy` | |
| `src/Envoy.Core/Updates/GitHubReleaseBackend.cs` | new | |
| `src/Envoy.Core/Updates/UpdatePreferences.cs` | new | |
| `src/Envoy.Core/Updates/UpdateCoordinator.cs` | new: coordinator, `UpdateState`, `UpdateSnapshot` | |
| `src/Envoy.Core/Updates/UpdateView.cs` | new: `UpdateView`, `UpdateAction`, `UpdateViewBuilder` | |
| `src/Envoy.Core/Updates/UpdateText.cs`, `ReleaseNotesText.cs`, `UserAgents.cs` | new | |
| `src/Envoy.Core/Updates/UpdateChecker.cs` | channel-aware `CheckAsync`, `PickLatest`, `Parse(json, includePrereleases)`, setup-vs-portable picking, typed errors; 08a's URL checks kept | |
| `src/Envoy.Core/Settings/AppSettings.cs` | `Updates` + `UpdateSettings` | **yes** |
| `src/Envoy.Core/Diagnostics/SafeMode.cs` (02, `StartupPlan`) | `AutoCheckUpdates` | **yes** |
| `src/Envoy.Core/Diagnostics/DiagnosticsReport.cs` (02) | "Updates" line (SHOULD, REQ-DIST-31) | **yes** |
| `src/Envoy.App/Program.cs` | new | |
| `src/Envoy.App/Services/VelopackUpdateBackend.cs` | new | |
| `src/Envoy.App/Services/UpdateActions.cs` | new | |
| `src/Envoy.App/Envoy.App.csproj` | `StartupObject`, `Velopack` 1.2.161 | **yes** |
| `src/Envoy.App/AppHost.cs` | `AppHostOptions.UpdateBackend`, `UpdateBackendFactory`, `AppVersion`, `Updates`, `UpdateView`, `StartUpdates`, `SkipUpdate`, `JustUpdatedTo`, positions 8/7/3, `_updatesCts`, UA token; remove `CheckForUpdatesAsync`/`Update`/`HasUpdate`/`UpdateText` | **yes** |
| `src/Envoy.App/IShell.cs` | 06 block: `RestartToUpdate()` | **yes** |
| `src/Envoy.App/App.xaml.cs` | slots 1 (comment removed), 5 and 9; `ExitApp(…, applyUpdate)`; `RestartToUpdate`; tray item (SHOULD) | **yes** |
| `src/Envoy.App/SmokeTest.cs` | `SmokeUpdateBackend`, setup Portable host, per-palette `updates(<palette>)`, `{palette}-update.png` | **yes** |
| `src/Envoy.App/Pages/WarRoomPage.xaml`, `AppearancePage.xaml`, `AboutPage.xaml` | banner (slot 2), "Startup & updates" group, "Version & updates" block | **yes** |
| `src/Envoy.App/ViewModels/MainViewModel.Updates.cs` | new partial; `Version` from `AppVersion`; delete `DownloadUpdateCommand` | **yes** (partials) |
| `src/Envoy.App/Security/` or Core `LaunchTarget` (08a) | make the `RestartFlags` → args mapping public, only if 08a kept it private | **yes** |
| `tools/typecheck/TypeCheck.csproj` | `Velopack` reference | **yes** |
| `tools/typecheck/check_ui_rules.py` + `test_check_ui_rules.py` | `UPD1`, `UPD2` | **yes** |
| `.config/dotnet-tools.json` | `vpk` 1.2.161 | **yes** (08a) |
| `.github/workflows/build.yml` | environment expression, tool restore, notes, tag-only signing, folder publish, vpk download/pack/upload, asset assertion, SHA256SUMS, setup artifact, winget (SHOULD), `check` additions; remove `softprops/action-gh-release` | **yes** |
| `tools/release/check_velopack_pin.sh`, `check_signing_scope.py`, `check_release_assets.py`, `sign.ps1`, `changelog_notes.py`, `render_winget.py` | new | |
| `packaging/winget/PinnerSinner.Envoy*.yaml` (3, SHOULD) | new | |
| `tests/Envoy.Core.Tests/Updates/*.cs`, `Settings/UpdateSettingsTests.cs`, `PlayerAndUpdateTests.cs`, 02's `StartupPlanTests` | tests | |
| `README.md`, `docs/installing.md` (new), `docs/releasing.md`, `docs/faq.md`, `docs/architecture.md`, `docs/security/threat-model.md`, `CLAUDE.md`, `THIRD-PARTY-NOTICES.md`, `CHANGELOG.md` | docs | **yes** |

Expected size: ~650 lines of C# (Core ~400, App ~250), ~400 lines of tests, ~250 lines of scripts/templates,
~120 lines of YAML, plus docs.

## Risks & open questions

1. **Velopack docs were unreachable.** Every API call here was checked against the 1.2.161 binaries and CLI. Not
   verified: the install path and uninstall key name (M-DIST-2 records them), the Windows-only `vpk` signing options
   (unused), and how `--signTemplate` quotes `{{file}}` with spaces (CI paths have none; M-DIST-5 exercises it).
2. **`packId` is forever.** `Envoy` fixes the install folder, uninstall key and installer name. `PinnerSinner.Envoy`
   would avoid clashing with another Velopack app called Envoy. **Decide before the first release.**
3. **Conditional `environment:` expression.** An empty name is expected to mean "no environment" on GitHub-hosted
   runners. If it doesn't, move steps 5–12 into a tag-only `release` job (`needs: windows`, `environment: release`,
   downloads the artifacts) now — the shape 08b adopts anyway — and record it under Deviations. Either way
   `check_signing_scope.py` keeps secrets out of untagged runs. Tag builds wait for a reviewer before the job starts.
4. **Two publishes.** Single-file (portable, smoke-tested) and folder (Velopack; a single-file bundle would make every
   delta nearly full size). Same code, different host layout. **Open:** also smoke `publish-velo\Envoy.exe` once CI
   time allows; M-DIST-1 covers it per release meanwhile.
5. **GitHub rate limits.** 60 unauthenticated requests/hour/IP; a Velopack check is 1 API call plus up to 10 feed
   downloads. The 1 h start-up floor and 12 h interval keep Envoy under it; 403/429 is quiet when automatic.
6. **Beta → stable never downgrades** (`AllowVersionDowngrade=false`); UI and docs explain it.
7. **No automatic rollback.** A broken release needs a new release or a reinstall of an older Setup.exe. 02's safe
   mode (Shift) at least starts the broken build without applying anything further.
8. **SmartScreen and AV heuristics.** Unsigned Setup.exe warns; OCR + SendInput + a self-updater can look suspicious.
   Signing is the real fix (SignPath Foundation is free for OSS). `installing.md` documents "More info → Run anyway"
   and the checksum check.
9. **Apply on next launch while a game runs.** Starting Envoy mid-match with a package pending runs the updater for a
   few seconds first. Acceptable; it never interrupts a running Envoy.
10. **Restart-to-update failures after the log is closed** (ExitApp step 8) are not logged in that session; the
    `RestartSelf` fallback and slot 5's retry make the failure visible in the next session's log.
11. **`Updates.LastAnnouncedVersion` arrives with 07a**, not here, under the arrival rule; its semantics are fixed in
    this spec's Settings section. If the integrator reads contract §2.5 as "06 adds the property", the only change is
    who adds one string property.
12. **Release body.** CHANGELOG-only notes drop GitHub's contributor list; `gh api …/releases/generate-notes` can be
    appended if wanted.
13. **winget submission is manual** (moderator review for the first one; automation needs a PAT).

## Revision notes

Revised against `00-integration.md` and the set critique.

- **Contract compliance.**
  - Depends on 01, 08a, 02, 03, 05, 04 (step 7); all "if 01/02 has landed" branches removed.
  - `AppHostOptions.UpdateBackend` replaces the extra constructor parameter. `UpdateBackendFactory.Create` moved into
    `AppHost.cs`; `VelopackUpdateBackend.cs` names no network type (critique blocker on `network.allowedPaths`).
  - Constructor position 8, `ApplySettings` position 7 (`Updates.Configure`), `RefreshGame` position 3.
  - Start-up slot 1 (`Program.Main`), slot 5 (pending apply, skipped with `--safe-mode`/Shift, before the sentinel,
    `PendingUpdatePolicy` tested), slot 9 (`StartUpdates` gated by new `StartupPlan.AutoCheckUpdates`).
  - `ExitApp(…, applyUpdate)` launches the updater at step 8 with a `RestartSelf` fallback, and saves normally.
  - `IShell.RestartToUpdate()` replaces "ShellActions".
- **Versions and UA (critique major).** `AppHost.CurrentVersion` stays `Version`; new `AppHost.AppVersion`
  (`ReleaseVersion`). The Core `AppVersion` class became `ReleaseVersion.FromInformational` to avoid a name clash.
  The default UA keeps its browser-like form; only the `0.1` token changes.
- **Signing (critique major).** Tag-only, under the `release` environment with environment secrets. A new
  `check_signing_scope.py` CI check enforces it. REQ-DIST-8 reworded.
- **`vpk` pin.** Moved to `.config/dotnet-tools.json` (`dotnet vpk`); `check_velopack_pin.sh` reads it.
  `VELOPACK_VERSION` env removed.
- **Settings thread safety (contract §7).** `Func<AppSettings>` replaced by the immutable `UpdatePreferences`
  snapshot. `LastCheckUtc` is written via `Dispatcher.BeginInvoke`. `isGameRunning` replaced by `SetGameRunning` from
  the UI thread, fed by `Host.Game`/`AppHost.GameWindow` (05).
- **Notifications / 07a seam.** `UpdateCoordinator.Changed` is documented as a background event, marshalled through
  `AppHost.UpdateView`. New `UpdateActions.Run` lets 07a's balloon run `UpdateView.Primary`. The
  `LastAnnouncedVersion` semantics are fixed here, and 07a adds the field. The "Updated to" notice now shares
  the banner slot. `Program.RestartedAfterUpdate` and `UpdateNoticeDismissed` were replaced by `LastRunVersion`.
- **UI strings (contract §8.7).** New `UpdateText`. Errors are `UpdateErrorKind` values, not exception messages,
  and nothing shows raw exception text. `AppHost.HasUpdate`/`UpdateText` were removed.
- **Information architecture (§8.6).** The banner sits in War Room slot 2. "Startup & updates" is the last
  Appearance group. "Version & updates" is the first block on About.
- **Smoke (critique minor; contract §6).** The War Room is screenshotted, not About. One Portable host is created
  in setup with a temp `DataFolder` and reused. The checks run per palette inside the binding window, with
  `PaletteIds` wording. Rendering of the Portable state became a view assertion.
- **Release integrity.** New `check_release_assets.py` asserts the draft, the asset list and the checksums before
  publishing. This makes REQ-DIST-2/3/5/7 CI-verifiable.
- **Scope (contract §9).** 17 MUSTs: 1, 2, 3, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 19, 20, 27.
  - Demoted to SHOULD: 4 (now `main` pushes only, for the CI time budget), 6, 18, 28.
  - Folded: 17 into 16. Moved to the DoD: 29.
  - New SHOULD: 31 (diagnostics line). REQ-DIST-30 (COULD) is now limited to local folders.
  - Every MUST has a unit, typecheck, smoke, CI or numbered M-step acceptance criterion.
- **Allowlists.** No native, network-path or host additions. No new `BannedSymbols` exception. Velopack's own
  updater launch is noted in the threat model. New static rules `UPD1`/`UPD2`.

## Deviations

Every MUST (DIST-1, 2, 3, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 19, 20, 27) and every SHOULD (4, 6, 18, 21–26, 28,
31) is built, and the COULD (30, `ENVOY_UPDATE_FEED`) too. MUST levels are unchanged, so contract §9 is unchanged.
Differences from the text above, with reasons:

1. **Background download failures are silent** (REQ-DIST-20 wins over the state rule "Download → Failed"). An automatic
   download that fails goes back to `Available` with `Error` kept and a Warning logged; the banner then offers **Update
   now**. `Failed` with `FailedWhileDownloading` and **Retry** comes only from a download the user started (Update now /
   Retry). Test `Background_download_failure_is_silent`.
2. **Cancellation** restores the previous state and raises `Changed` once for that restore (otherwise the UI could stay
   on "Checking…"); it sets no `Error` and does not record a check.
3. **`Configure`** compares the channel with the previous preferences, the constructor's `initial` included: a first
   call with an unchanged channel never checks (`First_configure_does_not_check`), but a changed channel checks even on
   the first call, because AppHost does not call `ApplySettings` at start-up, so the user's first edit can be the
   channel switch. `SkipAvailableVersion` also writes the skipped version into the coordinator's own preferences, so
   the next automatic check hides it before `ApplySettings` runs.
4. **`UpdateText`**: `OpenReleasePage` has three labels by place, so `UpdateText` has `DownloadLabel`, `ReleaseNotesLabel`
   and `WhatsNewLabel` constants; `ActionLabel(OpenReleasePage)` is "Release notes". Added `TrayRestartItem`,
   `KindText`, `ChannelText`, the template constants and `Checking`/`NeverChecked`/"Last checked {time}" status lines.
   The status time is the local short time (`"t"`, current culture).
5. **`UpdateView`** has computed `HasNotes`, `HasPrimary`, `HasSecondary` and a `Hidden` value: there is no
   string-to-visibility converter, and no palette or converter was added. The "What's new" toggle is a `ToggleButton`
   with the existing `Button.Base` style (`c:AutoApply.Ignore`); the banner's Skip button reads "Skip" with the
   accessible name "Skip this version".
6. **`StartupPlan.AutoCheckUpdates`** is a computed property (`=> !SafeMode`) rather than a new positional parameter, so
   02's `Startup_plan` theory is unchanged. Its tests (`Safe_mode_disables_automatic_update_checks`,
   `Normal_start_enables_automatic_update_checks`) live in 02's `SafeModeTests.cs`, which holds the StartupPlan tests.
   The new `UpdateCheckerTests` cases stay in `PlayerAndUpdateTests.cs` as the spec says.
7. **AppHost**: exposes `UpdatesToken` (the update CTS's token; banner, tray and About commands pass it) and keeps
   `ReleaseRepository` as an alias of `UpdateEndpoints.Repository`; `UpdateEndpoints.ReleasesUrl` was added for the
   fallback link. Instead of a `GameWindowChanged` subscription, `PushGameRunning()` runs where `GameWindow` is
   published and in `RefreshGame` (position 3); it ticks when the last of game process and game window is gone (so a
   deferred download starts within about a second of the window disappearing, not 30 minutes later), and only after
   `StartUpdates`, so safe mode never ticks. `RefreshGame`'s tick therefore covers both close paths.
8. **App**: slot 5 builds the backend with `VelopackUpdateBackend.TryCreate` only (no download, no network) and logs
   before launching the updater; a shared `ReleaseInstanceMutex()` serves slot 5 and exit step 7. Exit step 8 falls
   back to `RestartSelf` on `UpdateCheckException` or `InvalidOperationException`. The tray item is shown when
   `CanRestartToUpdate` and is unsubscribed at exit step 5. `Program.Main` also passes `args` to Velopack (`SetArgs`).
9. **About/Appearance**: the version line binds `Version`, `InstallKindText` and `ChannelText` with `Run`s; the
   background-download tooltip binds `AutoDownloadTooltip`, which is null (no tooltip) for installed copies.
10. **`UpdateChecker`**: an `HttpClient` timeout (an `OperationCanceledException` not caused by the caller's token)
    maps to `Offline`, like `HttpRequestException`. An untrusted installer URL is dropped like an untrusted download URL.
11. **Workflow**: there are two `vpk pack` steps (signed, gated on `steps.sign.outputs.signing == 'true'`, and
    unsigned), so the password secret is only ever in a signing-gated step. `check_release_assets.py` does not assert
    the prerelease flag, because `gh release edit --prerelease` sets it at publish time; it fails on duplicate assets
    and on a malformed `SHA256SUMS.txt`. The release footer lives in `tools/release/release-footer.md`.
    `check_signing_scope.py --self-test` also checks the real `build.yml`. The asset list (`RELEASES` included) follows
    the spec's verified pack output; that `vpk upload` uploads exactly these files is unverified (docs unreachable) and
    is confirmed by M-DIST-1; if it differs, `expected_assets` changes with the evidence.
12. **Conditional `environment:`** (risk 3) is used as specified. Whether GitHub accepts the empty name for untagged
    runs is only observable in CI; if it does not, steps 5–12 move into a tag-only `release` job and this list says so.
13. **Threat model**: `docs/security/threat-model.md` does not exist yet (08b creates it). The T8 line ("the updater
    process is started by Velopack (pinned, hash-verified packages), never by Envoy code") is in
    `docs/architecture.md` (Updates) for 08b to carry over.
14. **Smoke**: `updates-portable` also asserts that `DownloadAsync` on a portable copy changes nothing and that no
    restart is offered; `CheckDiagnostics` asserts the report's `Updates:` line (REQ-DIST-31).
15. **REQ-DIST-30 (COULD)** is built: `ENVOY_UPDATE_FEED` set to an existing, fully qualified, non-UNC local folder
    makes the installed copy use a Velopack `SimpleFileSource`; any other value is ignored with a Warning.
16. **Docs**: `docs/building.md` gained "build the installer locally"; `README.md`'s documentation table links
    `docs/installing.md`. `docs/privacy.md` is unchanged (no new data file; 07a adds the data-flow section).

