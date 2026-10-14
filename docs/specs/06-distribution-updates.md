# Distribution & updates: Velopack installer, one-click auto-update, channels, signing hook, winget

Status: Proposed

Area id: `06-distribution-updates` · Requirement prefix: `DIST` · Depends on nothing; integrates with
`01-ui-foundation` (pages/commands) and `02-reliability-observability` (logging, `App.Restart`) if they land first.

## Context

### What exists today

| Topic | Today | Where |
| --- | --- | --- |
| Build artifact | `dotnet publish -c Release` makes one self-contained, compressed single-file `Envoy.exe` (~75 MB) for win-x64. | `src/Envoy.App/Envoy.App.csproj` (Release `PropertyGroup`) |
| CI | One workflow. The `check` job (Ubuntu) runs the unit tests and the typecheck. The `windows` job publishes the exe, runs `Envoy.exe --smoke-test`, pushes screenshots, writes `Envoy.exe.sha256`, uploads both as a workflow artifact and, on `v*` tags, creates a GitHub Release with `softprops/action-gh-release@v2` (`generate_release_notes: true`, `prerelease` when the tag contains `-`). | `.github/workflows/build.yml` |
| Signing | None. A comment marks where a signing step would go. `docs/releasing.md` says SmartScreen warns on first run. | `build.yml`, `docs/releasing.md` |
| Update check | `UpdateChecker.CheckAsync(Version)` calls `GET https://api.github.com/repos/{repo}/releases/latest`. It ignores drafts and prereleases, takes the first `*.exe` asset as `DownloadUrl`, and returns `null` on any non-success HTTP status. | `src/Envoy.Core/Updates/UpdateChecker.cs` |
| Update UX | `AppHost.Start()` fires `_ = CheckForUpdatesAsync()` once if `Settings.CheckForUpdates`. War Room shows a banner with `UpdateText` and a **Download** button that opens the release *page* in the browser. The user must download the new exe, close Envoy and replace the file by hand. | `src/Envoy.App/AppHost.cs`, `Windows/MainWindow.xaml` (lines 79–85), `MainWindow.xaml.cs` `OnDownloadUpdate` |
| Settings | `AppSettings.CheckForUpdates` (bool, default `true`), labelled "Tell me when a new version is released" on the Appearance page. | `src/Envoy.Core/Settings/AppSettings.cs`, `MainWindow.xaml` line 752 |
| Version | `AppHost.CurrentVersion` and `MainViewModel.Version` use the numeric assembly version. CI sets `-p:Version=<numeric>` and `-p:InformationalVersion=<tag>` only for tags. | `AppHost.cs` line 74, `ViewModels/MainViewModel.cs` line 208, `build.yml` |
| Entry point | WPF generates `Main` from `App.xaml` (`ApplicationDefinition`). `App.OnStartup` handles `--smoke-test`, then the single-instance mutex `Envoy.SingleInstance.v1`. | `src/Envoy.App/App.xaml.cs` |
| Start with Windows | `StartupRegistration.Apply(bool)` writes `"<ProcessPath>" --minimized` to `HKCU\…\Run\Envoy`, but only when settings are edited (`ApplySettings`), not at start-up. | `src/Envoy.App/Services/StartupRegistration.cs` |
| Repo constant | `AppHost.ReleaseRepository = "PinnerSinner/envoy"`. | `AppHost.cs` line 71 |
| Releases | `api.github.com/repos/PinnerSinner/Envoy/releases` returns `[]` (checked 2026-10-14). No release has been published, so no installed base has to be migrated. | — |

### What is weak or missing for production

1. **Updating is manual.** The user finds the download, closes Envoy, replaces a 75 MB file and hopes they replaced
   the right one. Most users won't, so old builds with known bugs stay in use.
2. **No installer.** There is no Start menu entry and no entry in *Apps & features*. Users keep the exe in
   `Downloads`. Windows treats an unsigned file from `Downloads` with suspicion.
3. **Prereleases are invisible.** `releases/latest` never returns them, so no one can opt into a beta. Version
   comparison drops the prerelease suffix: `v1.2.0-beta.1` parses as `1.2.0`. A beta user would therefore never
   be offered `1.2.0`. The app also reports its own version without the suffix.
4. **Errors look like "up to date".** A 403 (rate limit) or 5xx returns `null`, the same as "no update".
5. **The asset picker breaks with two exes.** "First `.exe`" becomes ambiguous once a release also contains
   `Envoy-win-Setup.exe`.
6. **Release notes come from commit titles.** `generate_release_notes` builds them from commit/PR titles, not
   from `CHANGELOG.md`, and the app never shows them.
7. **No signing hook and no winget.** There is no code-signing step and no winget manifest.
8. **Uninstall leaves a Run entry behind.** If Envoy started with Windows, deleting the exe leaves a Run entry
   that points at nothing.

### Velopack facts this spec relies on

`nuget.org` was reachable. `docs.velopack.io` was not (timeout), and `github.com` HTML was not (403). The facts
below come from the `Velopack` 1.2.161 package itself: public API via reflection, XML docs, and decompiled
`UpdateManager`, `VelopackApp`, `GitBase<T>` and `GithubSource`. CLI facts come from the `vpk` 1.2.161 tool, run
on Linux with the `[win]` cross-compile directive. That run packed a test Windows exe twice, producing a full
package, a delta package and a Setup.exe.

| Fact | Status |
| --- | --- |
| `Velopack` 1.2.161 (published 2026-09-29, MIT) targets net8.0 with no dependencies. `vpk` 1.2.161 is a dotnet tool. | verified (NuGet) |
| `VelopackApp.Build()` returns a builder with `SetAutoApplyOnStartup(bool)` (default ON), `OnFirstRun`, `OnRestarted`, `OnBeforeUninstallFastCallback`, `OnAfterInstallFastCallback`, `OnAfterUpdateFastCallback`, `SetLogger`, `SetArgs`, and `Run()`. `VelopackHook` is `void (SemanticVersion version)`. Fast-callback hooks run with args `--veloapp-install/-updated/-obsolete/-uninstall <ver>`, then the process exits. Uninstall hooks are killed after 30 s. | verified (reflection + decompile) |
| `Run()` returns immediately when the app is not installed (`CurrentlyInstalledVersion == null`). When installed with auto-apply ON and a newer local package exists, it applies that package and restarts **with the same args** before the app's own code runs. `OnRestarted` fires when the `VELOPACK_RESTART` env var is set. | verified (decompile) |
| `vpk pack` checks that the entry point calls `VelopackApp.Run()` ("Verified VelopackApp.Run() in …Main"). Without that call it fails unless `--skipVeloAppCheck` is passed. | verified (vpk run) |
| `UpdateManager(IUpdateSource source, UpdateOptions? options = null, IVelopackLocator? locator = null)`. The parameterless constructor uses `VelopackFlowSource` (Velopack's hosted service). **We never use it.** | verified |
| Members: `IsInstalled`, `IsPortable`, `CurrentVersion` (`SemanticVersion?`), `UpdatePendingRestart` (`VelopackAsset?`, set when a downloaded full package is newer than the installed one), `Task<UpdateInfo?> CheckForUpdatesAsync()` (throws `NotInstalledException` when not installed), `Task DownloadUpdatesAsync(UpdateInfo, Action<int>? progress = null, CancellationToken = default)`, `WaitExitThenApplyUpdates(VelopackAsset? toApply, bool silent = false, bool restart = true, string[]? restartArgs = null)`, `ApplyUpdatesAndRestart(VelopackAsset, string[]? = null)`. | verified |
| `UpdateInfo.TargetFullRelease` (`VelopackAsset`: `Version`, `NotesMarkdown`, `NotesHTML`, `SHA256`, `Size`), `IsDowngrade`, `DeltasToTarget`. Downloads try deltas first (when ≤ `MaximumDeltasBeforeFallback`, default 10, and smaller than the full package), fall back to the full package, and verify SHA1/SHA256. | verified (decompile) |
| `UpdateOptions { ExplicitChannel, AllowVersionDowngrade (default false), MaximumDeltasBeforeFallback }`. The default channel on Windows is `win`. | verified |
| `GithubSource(string repoUrl, string? accessToken, bool prerelease, IFileDownloader? downloader = null)` makes **one** API call (`/releases?per_page=10`), drops prereleases unless `prerelease`, downloads `releases.<channel>.json` from each of those releases, merges them, and picks the **highest version** full package. Unauthenticated requests are limited to 60 per hour per IP. The staged-rollout user id is passed to the source but `GitBase` does not send it. The User-Agent is `Velopack/<version>`. | verified (decompile) |
| `vpk pack` (Windows target) options: `--packId --packVersion --packDir --mainExe --packTitle --packAuthors --icon --releaseNotes --channel --runtime --delta {None,BestSpeed,BestSize} --noPortable --noInst --shortcuts --splashImage --signTemplate "…{{file}}…" --signExclude --signParallel --framework --aumid --exclude -o`. Without `--runtime` it defaults to x86, so we pass `win-x64`. | verified (vpk help + run) |
| Pack output: `Envoy-win-Setup.exe`, `Envoy-<ver>-full.nupkg`, `Envoy-<ver>-delta.nupkg` (when a previous full package is in the output folder), `releases.win.json` (every package in the folder, with `NotesMarkdown`/`NotesHTML`), `assets.win.json` (only this version's new files), `RELEASES` (legacy), and `Envoy-win-Portable.zip` unless `--noPortable`. | verified (vpk run) |
| `vpk download github --repoUrl --channel --token --pre -o` fetches the latest (pre)release's full package so `pack` can make a delta. `vpk upload github --repoUrl --token --channel -o --tag --releaseName --targetCommitish --pre --merge --publish` creates a **draft** unless `--publish` is passed. | verified (vpk help) |
| Setup.exe options include `--silent` ("Hides all dialogs and answers 'yes'"), `--verbose`, `--installto DIR`. It writes an uninstall entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall` (DisplayName, QuietUninstallString …). | verified (strings in a generated Setup.exe) |
| Install location `%LocalAppData%\<packId>\current\`, per-user and without UAC. Shortcuts point to a stub `%LocalAppData%\<packId>\Envoy.exe`. The uninstall registry key name equals the packId. | **not verified** (docs unreachable). AC-DIST-12 checks this manually. |
| `--signParams` / `--azureTrustedSignFile` (Windows-only vpk options). | **not verified**. This spec uses only the verified `--signTemplate`. |

## Goals / Non-goals

**Goals**

- Two artifacts per release:
  - `Envoy-win-Setup.exe`: the recommended download. Per-user install, Start menu shortcut, uninstall entry,
    auto-update.
  - `Envoy.exe`: the existing portable single file. It is never modified by Envoy and notifies only.
- One-click updates for installed copies: background download (delta when possible), then **Restart to update**.
  Updates apply at the next launch if the user doesn't restart.
- Stable/Beta channel, an opt-out that really means *no automatic requests*, release notes shown in the app, and
  correct SemVer handling of prereleases.
- A release workflow that never publishes a partial release, takes notes from `CHANGELOG.md`, publishes checksums,
  signs when secrets exist, and renders winget manifests.
- All decision logic in `Envoy.Core.Updates` with unit tests. Velopack calls stay in one small App class.

**Non-goals**

- Buying or setting up a certificate, or submitting to winget (documented manual steps only).
- MSI, Microsoft Store/MSIX, machine-wide installs, ARM64 builds.
- Auto-updating the portable exe in place (it is notify-only by design).
- Staged rollouts, Velopack Flow, any hosted update service, or telemetry of any kind.
- Migrating existing portable users automatically (there are none: no release exists yet). Portable users get a
  "Get the installer" button instead.
- Changing where settings live (`%APPDATA%\Envoy`, roaming). That stays as is.

## Requirements

| ID | Priority | Requirement |
| --- | --- | --- |
| REQ-DIST-1 | MUST | Every tagged build publishes `Envoy-win-Setup.exe`, a Velopack installer that installs per-user without elevation, creates a Start menu shortcut and registers an *Apps & features* uninstall entry. |
| REQ-DIST-2 | MUST | Every tagged GitHub Release contains `releases.win.json`, `assets.win.json` and `Envoy-<ver>-full.nupkg`, plus `Envoy-<ver>-delta.nupkg` whenever a previous release on the same track (stable, or any for betas) exists. |
| REQ-DIST-3 | MUST | The portable `Envoy.exe` (self-contained single file) and `Envoy.exe.sha256` are still built, smoke-tested and attached to every release exactly as today. |
| REQ-DIST-4 | MUST | Every CI run, not only tags, runs `vpk pack` on a `<csproj Version>-ci.<run_number>` version and uploads that Setup.exe as a workflow artifact, so a broken package or a missing `VelopackApp.Run()` fails the build. |
| REQ-DIST-5 | MUST | A release stays a draft until every asset (Velopack files, portable exe, checksums, notes) is uploaded, and is marked prerelease exactly when the tag contains `-`. |
| REQ-DIST-6 | MUST | Release notes for the GitHub release and for the Velopack package (`--releaseNotes`) are the tag's `CHANGELOG.md` section. A stable tag with no matching `## [x.y.z]` section fails the workflow before anything is published. |
| REQ-DIST-7 | MUST | Each release has a `SHA256SUMS.txt` with the SHA-256 of `Envoy.exe` and `Envoy-win-Setup.exe` in `sha256sum` format. |
| REQ-DIST-8 | MUST | Code signing runs only when the signing secrets are set. Without them the workflow passes unsigned and prints a notice. With them it Authenticode-signs (SHA-256, RFC 3161 timestamp) the portable `Envoy.exe`, the packed binaries and `Envoy-win-Setup.exe`. |
| REQ-DIST-9 | MUST | The `Velopack` NuGet version and the `vpk` CLI version are the same exact pinned version, and the `check` job fails if `Envoy.App.csproj`, `TypeCheck.csproj` and `build.yml` disagree. |
| REQ-DIST-10 | MUST | `Program.Main` calls `VelopackApp.Build()…Run()` before any WPF object exists, with `SetAutoApplyOnStartup(false)`. Portable, Debug and `--smoke-test` launches behave exactly as today. |
| REQ-DIST-11 | MUST | Uninstalling removes the `HKCU\…\Run\Envoy` value and does not touch `%APPDATA%\Envoy` (settings, API keys, phrases, logs, transcripts). |
| REQ-DIST-12 | MUST | A downloaded update is applied only when (a) the user clicks **Restart to update**, or (b) a later launch has acquired the single-instance mutex. It is never applied while another Envoy instance runs and never by an unprompted restart. |
| REQ-DIST-13 | MUST | When `CheckForUpdates` is false, Envoy makes no automatic update-related network request. A user-clicked **Check for updates** still runs. |
| REQ-DIST-14 | MUST | Update traffic goes only to GitHub for the repository `PinnerSinner/Envoy` (`api.github.com` and GitHub release-asset downloads). It carries no identifier beyond the User-Agent product/version, and the Velopack Flow constructor is never used. |
| REQ-DIST-15 | MUST | Installed copies download updates through Velopack (delta when possible, checksum-verified) and show percent progress. Portable copies never write to their own files and offer **Download** (release page) and **Get the installer** instead. |
| REQ-DIST-16 | MUST | With `Updates.AutoDownload` true (the default), an available update downloads in the background and the banner then offers **Restart to update**. While an Age game is detected the download waits and starts once the game closes. With it false, the banner offers **Update now**. |
| REQ-DIST-17 | MUST | The update banner shows the new version, the release notes as plain text (Markdown stripped, at most 1,200 characters) and a link to the release page. |
| REQ-DIST-18 | MUST | `Updates.Channel` is `Stable` or `Beta` (default `Stable`). Beta also considers GitHub prereleases. Changing channel never installs a lower version. |
| REQ-DIST-19 | MUST | All version comparisons use SemVer 2.0 precedence (prerelease < release, numeric identifiers compared numerically, build metadata ignored). The app's own version comes from `AssemblyInformationalVersion`, so a beta build knows it is a beta. |
| REQ-DIST-20 | MUST | A failed check or download never shows a modal dialog. Background failures leave no visible trace beyond a log line (when the logging area exists). A user-initiated failure shows one readable sentence and a **Retry** action. |
| REQ-DIST-21 | SHOULD | While Envoy keeps running, automatic checks repeat at most every 12 hours. The start-up check is skipped if the last check was under 1 hour ago. |
| REQ-DIST-22 | SHOULD | **Skip this version** hides that version from automatic checks but not from a user-initiated check. |
| REQ-DIST-23 | SHOULD | After Velopack restarts Envoy following an update, War Room shows a one-time dismissible "Updated to vX" notice linking to the release notes. |
| REQ-DIST-24 | SHOULD | When an update is ready, the tray menu has a **Restart to update to vX** item. |
| REQ-DIST-25 | SHOULD | The About page shows the version (with prerelease suffix), install type (Installed/Portable), channel, last-checked time, current update status and a **Check for updates** button. |
| REQ-DIST-26 | SHOULD | When `StartWithWindows` is true, start-up rewrites the Run value to the current exe path, so moving from portable to installed keeps start-at-sign-in working. |
| REQ-DIST-27 | MUST | The smoke test renders the update UI in the states Available, Downloading, ReadyToRestart, Failed and Portable-available, under every theme, without network access. |
| REQ-DIST-28 | MUST | The repo contains winget manifest templates (schema 1.6.0, version/installer/defaultLocale) for `Envoy-win-Setup.exe` as a user-scope `exe` installer with `--silent`. CI renders them on stable tags as a workflow artifact, and the docs describe manual submission. |
| REQ-DIST-29 | MUST | User and developer docs are updated: the README download section (installer recommended, portable alternative), a new `docs/installing.md`, `docs/releasing.md`, `THIRD-PARTY-NOTICES.md` (Velopack, MIT) and `CHANGELOG.md`. |
| REQ-DIST-30 | COULD | The `ENVOY_UPDATE_FEED` environment variable (a local folder or URL) replaces the GitHub source for installed builds, so updates can be tested without publishing a release. |

## Design

### Overview

```
Envoy.Core/Updates  (net8.0, unit tested)                 Envoy.App (Windows)
┌──────────────────────────────────────────────┐          ┌─────────────────────────────────────┐
│ ReleaseVersion   SemVer parse/compare        │          │ Program.Main  → VelopackApp.Run()   │
│ AppVersion       informational → version     │          │ Services/VelopackUpdateBackend      │
│ UpdateChecker    GitHub REST (portable)      │◄─────────│   : IUpdateBackend (installed)      │
│ GitHubReleaseBackend : IUpdateBackend        │          │ AppHost.Updates (UpdateCoordinator) │
│ UpdateCoordinator  state machine + policy    │          │ War Room banner / About / tray      │
│ UpdateView         state → banner text/btns  │          └─────────────────────────────────────┘
│ ReleaseNotesText   markdown → plain text     │
└──────────────────────────────────────────────┘
CI: publish single-file (portable) + publish folder → vpk pack → draft release → upload → publish
```

`UpdateBackendFactory.Create` in the App picks the backend once at start-up. A Velopack-installed copy gets
`VelopackUpdateBackend`. Anything else (portable exe, `dotnet run`, smoke test) gets `GitHubReleaseBackend`.
`UpdateCoordinator` doesn't know which one it has, beyond `InstallKind`.

### Core: new and changed types (`src/Envoy.Core/Updates/`)

```csharp
namespace Envoy.Core.Updates;

/// <summary>SemVer 2.0 version. Accepts "v1.2", "1.2.3", "1.2.3-beta.1+sha". Build metadata is dropped.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>Dot-separated prerelease identifiers; empty for a release.</summary>
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    public ReleaseVersion(int major, int minor, int patch, params string[] prerelease);
    public static bool TryParse(string? text, [NotNullWhen(true)] out ReleaseVersion? version);
    public static ReleaseVersion Parse(string text);          // FormatException on garbage
    public int CompareTo(ReleaseVersion? other);              // SemVer §11 precedence; null sorts first
    public bool Equals(ReleaseVersion? other);
    public override string ToString();                         // "1.2.3" or "1.2.3-beta.1" (no "v", no metadata)
    public static bool operator <(ReleaseVersion a, ReleaseVersion b); // plus >, <=, >=, ==, !=
}

public static class AppVersion
{
    /// <summary>"1.2.0-beta.1+0abc…" → 1.2.0-beta.1. Falls back to the numeric assembly version, then 0.0.0.</summary>
    public static ReleaseVersion Resolve(string? informationalVersion, Version? assemblyVersion);
}

public enum UpdateChannel { Stable, Beta }
public enum InstallKind { Installed, Portable }

/// <summary>One update the backend found. Handle is backend-private (the Velopack UpdateInfo).</summary>
public sealed record AvailableUpdate(
    ReleaseVersion Version,
    string? NotesMarkdown,
    string ReleasePageUrl,
    string? PortableDownloadUrl,
    string? InstallerDownloadUrl,
    object? Handle = null);

public sealed class UpdateCheckException(string message, Exception? inner = null) : Exception(message, inner);

public interface IUpdateBackend
{
    InstallKind Kind { get; }
    ReleaseVersion CurrentVersion { get; }
    /// <summary>Null when up to date. Throws UpdateCheckException for network/HTTP/feed errors; honours ct.</summary>
    Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct);
    /// <summary>Installed only (Portable throws NotSupportedException). progress: 0–100.</summary>
    Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken ct);
    /// <summary>Version of a fully downloaded, not yet applied update; null if none.</summary>
    ReleaseVersion? PendingRestart { get; }
    /// <summary>Starts the updater, which waits for this process to exit, applies, and relaunches with args.
    /// The caller must then exit normally. Installed only.</summary>
    void LaunchUpdaterAfterExit(string[] restartArgs);
}
```

**`UpdateChecker` (changed).** It stays the portable path and keeps today's public members, so existing tests
still compile.

```csharp
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? DownloadUrl, string? Notes)
{
    // New, init-only, default-valued so existing constructions still compile:
    public ReleaseVersion? SemVer { get; init; }
    public string? InstallerUrl { get; init; }   // asset named exactly "Envoy-win-Setup.exe"
    public bool IsPrerelease { get; init; }
}

public sealed class UpdateChecker(HttpClient http, string ownerAndRepo)
{
    public Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default);          // unchanged behaviour
    /// <summary>Stable: GET releases/latest. Beta: GET releases?per_page=10 and pick the highest SemVer.
    /// 404 → null (no releases). Other non-success, timeouts, bad JSON → UpdateCheckException.</summary>
    public Task<UpdateInfo?> CheckAsync(ReleaseVersion current, UpdateChannel channel, CancellationToken ct = default);
    public static UpdateInfo? Parse(string json);                                  // unchanged: skips drafts/prereleases
    public static UpdateInfo? Parse(string json, bool includePrereleases);
    public static UpdateInfo? PickLatest(string releasesArrayJson, UpdateChannel channel);
    public static bool TryParseVersion(string tag, out Version version);           // unchanged
}
```

`Parse` changes how it picks the portable `DownloadUrl`: it takes the asset named exactly `Envoy.exe`, and only
falls back to the first `.exe` that isn't `*Setup.exe`. Requests send only `User-Agent: Envoy/<version>` and
`Accept: application/vnd.github+json`.

```csharp
/// <summary>Portable / dev builds: notify only.</summary>
public sealed class GitHubReleaseBackend(UpdateChecker checker, ReleaseVersion current) : IUpdateBackend
{
    public InstallKind Kind => InstallKind.Portable;
    // CheckAsync → checker.CheckAsync(current, channel) mapped to AvailableUpdate(PortableDownloadUrl=DownloadUrl, InstallerDownloadUrl=InstallerUrl)
    // DownloadAsync / LaunchUpdaterAfterExit → NotSupportedException; PendingRestart → null
}

public static class UpdateEndpoints
{
    public const string Repository = "PinnerSinner/Envoy";
    public const string RepositoryUrl = "https://github.com/PinnerSinner/Envoy";
    public static string ReleasePage(ReleaseVersion v) => $"{RepositoryUrl}/releases/tag/v{v}";
}

public static class ReleaseNotesText
{
    /// <summary>Markdown → plain text for a TextBlock: drops heading marks, turns "- "/"* " into "• ",
    /// [text](url) → text, strips **, __, `; collapses blank lines; cuts at the last line break before
    /// maxChars and appends "…". Null/blank → "".</summary>
    public static string ToPlain(string? markdown, int maxChars = 1200);
}
```

**`UpdateCoordinator`** holds all policy and state. It is thread-safe and UI-agnostic.

```csharp
public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, ReadyToRestart, Failed }

public sealed record UpdateSnapshot(
    InstallKind Kind, ReleaseVersion Current, UpdateState State, AvailableUpdate? Available,
    int Percent, bool DeferredForGame, string? Error, bool LastCheckWasManual, DateTimeOffset? LastChecked);

public sealed class UpdateCoordinator
{
    public static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(12);
    public static readonly TimeSpan StartupMinInterval = TimeSpan.FromHours(1);

    public UpdateCoordinator(IUpdateBackend backend, Func<AppSettings> settings, Action settingsChanged,
                             Func<bool> isGameRunning, TimeProvider time);

    public UpdateSnapshot Snapshot { get; }          // immutable; replaced atomically
    public event Action? Changed;                    // raised on the calling/background thread

    /// <summary>Start-up: if PendingRestart != null → ReadyToRestart without network. Else, if CheckForUpdates
    /// and (LastCheckUtc is null or older than StartupMinInterval) → CheckAsync(manual: false).</summary>
    public Task StartAsync(CancellationToken ct = default);
    /// <summary>manual=false is a no-op when Settings.CheckForUpdates is false. Concurrent calls share one run.</summary>
    public Task CheckAsync(bool manual, CancellationToken ct = default);
    /// <summary>"Update now" / "Retry" after a failed download. Installed + Available/Failed only.</summary>
    public Task DownloadAsync(CancellationToken ct = default);
    /// <summary>Every 30 min from AppHost: periodic check when due; starts a deferred download when no game runs.</summary>
    public Task TickAsync(CancellationToken ct = default);
    public void SkipAvailableVersion();              // Settings.Updates.SkippedVersion = v; State → Idle; settingsChanged()
    public bool CanRestartToUpdate { get; }          // State == ReadyToRestart && Kind == Installed
    public void RestartToUpdate(string[] restartArgs); // backend.LaunchUpdaterAfterExit; caller exits
}
```

State rules (each rule has a unit test, see Test plan):

- **Check.** `Checking` → result:
  - `null` → `UpToDate`.
  - Update found, version ≤ current → `UpToDate`. This is a defence; backends already filter.
  - Update found, automatic check, version equals `SkippedVersion` → `Idle`.
  - Otherwise → `Available`. Then, if `Kind == Installed && AutoDownload`, start the download, or set
    `DeferredForGame = true` when `isGameRunning()`.
- **Exceptions.** An `UpdateCheckException`, or any non-cancellation exception from the backend:
  - manual → `Failed` with `Error` = the exception's message (one sentence).
  - automatic → back to the state before the check, with `Error` kept for diagnostics only.
- **Cancellation.** Cancellation returns to the previous state and nothing is raised.
- **Download.** `Downloading` (`Percent` updated, `Changed` raised only when the whole percent changes) →
  `ReadyToRestart`, or `Failed` ("Download failed: …", `Available` kept so **Retry** works).
- **LastCheckUtc.** `LastCheckUtc` is written after every completed check (success or failure) through the
  `settingsChanged` callback.
- **Locking.** One `SemaphoreSlim(1,1)` serialises check and download. Reading `Snapshot` takes no lock.

```csharp
public enum UpdateAction { None, CheckNow, UpdateNow, RestartToUpdate, OpenReleasePage, GetInstaller, Retry, Skip }

public sealed record UpdateView(
    bool ShowBanner, string Headline, string Notes,
    UpdateAction Primary, string PrimaryLabel,
    UpdateAction Secondary, string SecondaryLabel,
    bool CanSkip, string StatusLine);

public static class UpdateViewBuilder { public static UpdateView Build(UpdateSnapshot s); }
```

| Snapshot | Banner | Headline | Primary / Secondary |
| --- | --- | --- | --- |
| Installed, Available, not auto | yes | `Envoy 1.3.0 is available. You have 1.2.0.` | Update now / Release notes |
| Installed, Available, deferred | yes | `Envoy 1.3.0 will download after your game.` | Update now / Release notes |
| Installed, Downloading 42 % | yes | `Downloading Envoy 1.3.0… 42%` | None / Release notes |
| Installed, ReadyToRestart | yes | `Envoy 1.3.0 is ready. Restart to update. Takes a few seconds.` | Restart to update / Release notes |
| Portable, Available | yes | `Envoy 1.3.0 is available. You have 1.2.0.` | Download / Get the installer |
| Failed (manual) | yes | `Couldn't check for updates: <error>` or `Download failed: <error>` | Retry / None |
| UpToDate / Idle / Checking | no | — | — (`StatusLine` is shown on About: "Up to date · checked 14:02", "Checking…") |

`CanSkip` is true for Available (both kinds) and for ReadyToRestart. Skipping a ready update does not delete
the package. It is still applied at the next launch: Velopack can't un-download.

### Settings (shared file `src/Envoy.Core/Settings/AppSettings.cs`)

Add to `AppSettings`:

```csharp
/// <summary>Update channel, background download and bookkeeping. CheckForUpdates (existing) stays the master switch.</summary>
public UpdateSettings Updates { get; set; } = new();
```

Add next to the other settings classes:

```csharp
public sealed class UpdateSettings
{
    /// <summary>Stable = releases only; Beta = releases and prereleases.</summary>
    public Updates.UpdateChannel Channel { get; set; } = Updates.UpdateChannel.Stable;
    /// <summary>Installed copies download updates in the background (never during a game).</summary>
    public bool AutoDownload { get; set; } = true;
    /// <summary>Version the user chose to skip ("" = none). Automatic checks ignore it.</summary>
    public string SkippedVersion { get; set; } = "";
    public DateTimeOffset? LastCheckUtc { get; set; }
    /// <summary>Version whose "Updated to" notice was dismissed.</summary>
    public string UpdateNoticeDismissed { get; set; } = "";
}
```

`CheckForUpdates` keeps its name and default (`true`). Its label becomes "Check for updates automatically".
This needs no settings migration: a missing `updates` object deserialises to the defaults (tested).
`AppSettings.Version` stays 1.

### App: entry point (`src/Envoy.App/Program.cs`, new) and csproj

```csharp
namespace Envoy.App;

internal static class Program
{
    /// <summary>Set by Velopack's OnRestarted hook: the version we were just updated to.</summary>
    public static string? RestartedAfterUpdate { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)   // we apply after taking the single-instance mutex (REQ-DIST-12)
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Apply(false))
            .OnRestarted(v => RestartedAfterUpdate = v.ToString())
            .Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
```

`Envoy.App.csproj`:

- `<StartupObject>Envoy.App.Program</StartupObject>`. `App.xaml` stays `ApplicationDefinition`. The generated
  `App.Main` is ignored because `StartupObject` is set.
- `<PackageReference Include="Velopack" Version="1.2.161" />`.

`tools/typecheck/TypeCheck.csproj` gets the same `PackageReference` (shared tooling; keeping the two in sync is
already a rule). `gen_stubs.py` already emits `InitializeComponent()` for every `x:Class`, including `App`. The
typecheck builds a Library, so the duplicate `Main` question never comes up there.

The uninstall hook only touches `HKCU\…\Run`. It runs well under Velopack's 30 s limit and never touches
`%APPDATA%\Envoy` (REQ-DIST-11).

### App: `Services/VelopackUpdateBackend.cs` (new)

```csharp
public sealed class VelopackUpdateBackend : IUpdateBackend
{
    /// <summary>Returns null when this copy isn't Velopack-installed (portable exe, dotnet run, smoke test).</summary>
    public static VelopackUpdateBackend? TryCreate();
    public InstallKind Kind => InstallKind.Installed;
    public ReleaseVersion CurrentVersion { get; }        // from UpdateManager.CurrentVersion.ToNormalizedString()
    public ReleaseVersion? PendingRestart { get; }       // UpdatePendingRestart?.Version
    public Task<AvailableUpdate?> CheckAsync(UpdateChannel channel, CancellationToken ct);
    public Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken ct);
    public void LaunchUpdaterAfterExit(string[] restartArgs);
}

public static class UpdateBackendFactory
{
    public static IUpdateBackend Create(HttpClient http) =>
        (IUpdateBackend?)VelopackUpdateBackend.TryCreate()
        ?? new GitHubReleaseBackend(new UpdateChecker(http, UpdateEndpoints.Repository), AppVersionInfo.Current);
}
```

Implementation notes:

- **Source.** `IUpdateSource Source(UpdateChannel c) => new GithubSource(UpdateEndpoints.RepositoryUrl, accessToken: null, prerelease: c == UpdateChannel.Beta);`.
  With `ENVOY_UPDATE_FEED` set (COULD), the backend uses `new UpdateManager(feedPathOrUrl)` instead. A new
  `UpdateManager(Source(channel), new UpdateOptions { AllowVersionDowngrade = false })` is created per call,
  because the channel can change between calls. `ExplicitChannel` stays null, so the packaged channel `win` is
  used.
- **`TryCreate`.** It builds an `UpdateManager` and returns null unless `IsInstalled`. It catches any exception
  and returns null (portable fallback).
- **`CheckAsync`.**
  - `ct.ThrowIfCancellationRequested()`, then `await mgr.CheckForUpdatesAsync().WaitAsync(ct)`. Velopack's check
    takes no token, so a cancelled check finishes in the background and its result is discarded.
  - Map the result: `Version = ReleaseVersion.Parse(info.TargetFullRelease.Version.ToNormalizedString())`,
    `NotesMarkdown = info.TargetFullRelease.NotesMarkdown`,
    `ReleasePageUrl = UpdateEndpoints.ReleasePage(version)`, `Handle = info`.
  - Wrap every exception except `OperationCanceledException` in `UpdateCheckException`. Messages:
    - `HttpRequestException` with status 403 → "GitHub is rate-limiting update checks; try again later."
    - other HTTP/IO errors → "Couldn't reach GitHub."
    - anything else → the type name.
- **`DownloadAsync`.** Calls `mgr.DownloadUpdatesAsync((UpdateInfo)update.Handle!, p => progress.Report(p), ct)`.
  Velopack takes its own file lock, uses deltas, verifies checksums and removes `.partial` files. Failures are
  wrapped as above with the prefix "Download failed: ".
- **`LaunchUpdaterAfterExit`.** Calls `mgr.WaitExitThenApplyUpdates(mgr.UpdatePendingRestart, silent: false, restart: true, restartArgs)`.
  Velopack's updater waits up to 60 s for us to exit.

`AppVersionInfo.Current` (App, static) =
`AppVersion.Resolve(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, assembly.GetName().Version)`.

### App: `AppHost.cs` (shared file) changes

- Remove the fields `_update`, the property `Update` and `CheckForUpdatesAsync()`.
- Change `ReleaseRepository` to `=> UpdateEndpoints.Repository`, which fixes the casing.
- Change `CurrentVersion` to return `ReleaseVersion` (`AppVersionInfo.Current`).
- Change the User-Agent to `Envoy/{CurrentVersion}`.
- Add:

```csharp
public UpdateCoordinator Updates { get; }
public UpdateView UpdateView => UpdateViewBuilder.Build(Updates.Snapshot);
public bool HasUpdate => UpdateView.ShowBanner;        // kept: existing binding name
public string UpdateText => UpdateView.Headline;       // kept: existing binding name
public string? JustUpdatedTo { get; }                  // Program.RestartedAfterUpdate unless == Settings.Updates.UpdateNoticeDismissed
public void DismissUpdateNotice();
```

- **Constructor.** The constructor takes an optional `IUpdateBackend? updateBackend = null`; the smoke test
  passes a fake. If 02 has landed, this becomes `AppHostOptions.UpdateBackend` instead.
  - `Updates = new UpdateCoordinator(updateBackend ?? UpdateBackendFactory.Create(_http), () => Settings, ScheduleSave, () => _game is not null, TimeProvider.System);`
  - `Updates.Changed += () => _ui.BeginInvoke(() => { Notify(nameof(UpdateView)); Notify(nameof(HasUpdate)); Notify(nameof(UpdateText)); });`
- **`Start()`.**
  - Replace `if (Settings.CheckForUpdates) _ = CheckForUpdatesAsync();` with `_ = Updates.StartAsync(_cts.Token);`.
    With 02, use `.Forget(log, "update start")` instead of `_ =`.
  - Add a 30-minute `DispatcherTimer` (`DispatcherPriority.Background`) whose tick runs `_ = Updates.TickAsync(_cts.Token)`.
  - `RefreshGame()`: when a game closes (`_game` goes from non-null to null), also call `_ = Updates.TickAsync(_cts.Token)`
    so a deferred download starts promptly.
  - REQ-DIST-26: `if (Settings.StartWithWindows) StartupRegistration.Apply(true);`.
- **`Dispose()`.** Cancels `_cts` (new `CancellationTokenSource`), which aborts an in-flight download. Velopack
  leaves only a `.partial` file, which it cleans up next time. Then stop the timer.

### App: `App.xaml.cs` (shared file) changes

1. In `OnStartup`, immediately after the mutex check succeeds (`isFirst`) and **before** `new AppHost(...)`:

   ```csharp
   if (VelopackUpdateBackend.TryCreate() is { PendingRestart: not null } pending)
   {
       pending.LaunchUpdaterAfterExit(e.Args);   // same args, so --minimized is preserved
       _instanceMutex.ReleaseMutex();
       Shutdown();
       return;
   }
   ```

   This is the "apply on next launch" path (REQ-DIST-12 b). It runs before any window, overlay or hotkey
   exists. If `LaunchUpdaterAfterExit` throws (updater missing or corrupt), catch the exception, log it if 02
   has landed, and continue a normal start-up: never block launching.
2. `public void RestartToUpdate()`:
   - `if (!Host.Updates.CanRestartToUpdate) return;`
   - `var args = _main is { IsVisible: true } ? Array.Empty<string>() : new[] { "--minimized" };`
   - `try { Host.Updates.RestartToUpdate(args); } catch (Exception ex) { show the error in the banner via the coordinator's Failed state; return; }`
   - `ExitApp();`. The existing method disposes the tray and hotkeys, then `Host.Dispose()` saves settings,
     then `Shutdown`. If 02's `Restart` has landed, the mutex release order matches it.
3. **Tray.** `CreateTray` adds a hidden `ToolStripMenuItem _restartToUpdateItem` above the separator. On
   `Host.PropertyChanged` for `UpdateView`, set `Visible = Host.Updates.CanRestartToUpdate` and
   `Text = $"Restart to update to v{version}"`. Click → `RestartToUpdate()`.
4. **Commands.** `ShellActions` (or `App` directly before 01) exposes `RestartToUpdate()` and `OpenUrl` for the
   page commands.

### UI changes

All colours use existing `DynamicResource` keys (`Accent.Gradient`, `Text.OnAccent`, `Text.Secondary`,
`Text.Muted`). No new palette keys. No `ProgressBar`: progress is text ("42%"), because `Controls.xaml` has no
themed `ProgressBar` style.

**War Room page.** `MainWindow.xaml` lines 79–85 today, or `Pages/WarRoomPage.xaml` after 01. Replace the update
`Border` with:

- `Border` (`Background={DynamicResource Accent.Gradient}`, `Visibility={Binding Host.UpdateView.ShowBanner, Converter=BoolToVisibility}`) containing:
  - a `DockPanel`. The right side holds a `StackPanel` (horizontal) of:
    - a `Button` `Content={Binding Host.UpdateView.PrimaryLabel}` `Command={Binding UpdatePrimaryCommand}`,
      collapsed when the label is empty;
    - a `Button` `Style={StaticResource Button.Ghost}` `Content={Binding Host.UpdateView.SecondaryLabel}`
      `Command={Binding UpdateSecondaryCommand}`, collapsed when empty;
    - a `Button` `Style={StaticResource Button.Chip}` `Content="Skip"` `Command={Binding SkipUpdateCommand}`,
      visible when `CanSkip`.
  - On the left, a `TextBlock` `Text={Binding Host.UpdateView.Headline}` (`Text.OnAccent`, SemiBold).
  - Below it, an `Expander` `Header="What's new"`. It is collapsed when `Notes` is empty and holds a
    `TextBlock` `TextWrapping=Wrap` `Text={Binding Host.UpdateView.Notes}`. If an implicit `Expander` style is
    missing from `Controls.xaml`, use a `ToggleButton` + `TextBlock` pair with existing styles instead. Either
    way it must pass `check_resources.py`.
- A second, smaller `Border` (`Bg.PanelAlt`) shows when `Host.JustUpdatedTo` is non-null:
  "Envoy was updated to v{0}." It has buttons **What's new** (opens the release page) and **Dismiss**
  (`Host.DismissUpdateNotice()`).

**Appearance page → General** (line 752 today). Replace the one checkbox with:

- `CheckBox` "Check for updates automatically" → `Settings.CheckForUpdates`.
- `CheckBox` "Download updates in the background (never during a game)" → `Settings.Updates.AutoDownload`.
  `IsEnabled={Binding IsInstalled}`, with the tooltip "The portable Envoy.exe can't update itself. Use the
  installer to get automatic updates."
- `SettingRow` "Update channel": a `ComboBox` with `ItemsSource={Binding UpdateChannels}`
  `DisplayMemberPath=Label` `SelectedValuePath=Value` `SelectedValue={Binding Settings.Updates.Channel}`.
  - Options: `new(UpdateChannel.Stable, "Stable (recommended)")`, `new(UpdateChannel.Beta, "Beta: early builds, may be rough")`.
  - Caption: "Switching back to Stable keeps your current build until a newer stable release comes out."
- A channel change triggers a manual check (the user expects feedback). `MainViewModel` watches the setting:
  in its existing auto-apply path, if `Settings.Updates.Channel` differs from the last seen value, call
  `Host.Updates.CheckAsync(manual: true)`.

**About page** (under the version line). A "Updates" block:

- `TextBlock` "Version {Version} · {InstallKindText} · {ChannelText}". `MainViewModel.Version` becomes
  `AppVersionInfo.Current.ToString()`, which includes the prerelease suffix.
- `TextBlock` `Host.UpdateView.StatusLine`.
- `Button` "Check for updates" → `CheckForUpdatesCommand` (`AsyncRelayCommand` after 01; a click handler
  before).

**`MainViewModel`** (`ViewModels/MainViewModel.cs`) adds:

- `IReadOnlyList<Option> UpdateChannels`, `bool IsInstalled`, `string InstallKindText` ("Installed" /
  "Portable"), `string ChannelText`.
- Commands:
  - `UpdatePrimaryCommand`, `UpdateSecondaryCommand`: switch on `UpdateAction`.
    - `UpdateNow`/`Retry` → `Host.Updates.DownloadAsync()`, or `CheckAsync(true)` when the failure was a check.
    - `RestartToUpdate` → `shell.RestartToUpdate()`.
    - `OpenReleasePage` → `OpenUrl(Available.ReleasePageUrl)`.
    - `GetInstaller` → `OpenUrl(InstallerDownloadUrl ?? ReleasePageUrl)`.
  - `SkipUpdateCommand`, `CheckForUpdatesCommand`.
- 01's `DownloadUpdateCommand` is replaced by `UpdatePrimaryCommand`.

### Threading

- `UpdateCoordinator` methods run on the thread pool (`ConfigureAwait(false)`). `Changed` fires there. `AppHost`
  marshals to the dispatcher with `BeginInvoke`, the same as pipeline events.
- Velopack's progress callback fires on a pool thread. The coordinator throttles it to whole-percent changes.
- `Program.Main` hooks run before WPF. `RestartedAfterUpdate` is a plain static that is written once.
- The pending-apply check in `OnStartup` is synchronous and local: it reads the packages folder and makes no
  network call.

### Failure behaviour

| Failure | Behaviour |
| --- | --- |
| Offline / DNS / timeout on an automatic check | Silent; state unchanged; `LastCheckUtc` updated so we don't retry until the next interval. |
| GitHub 403 rate limit | Same as above when automatic; when manual: "GitHub is rate-limiting update checks; try again later." + Retry. |
| No releases yet (404 / empty feed) | `UpToDate`. |
| Download interrupted / checksum mismatch | `Failed` "Download failed: …" + Retry; Velopack removes partial files; no restart offered. |
| Updater can't start (`LaunchUpdaterAfterExit` throws) | Envoy keeps running; banner shows "Couldn't start the updater: …"; next launch tries again. |
| Update applied but new version crashes at start | Out of scope for automatic rollback. The user can reinstall an older `Envoy-win-Setup.exe` from Releases (documented in `installing.md`). |
| Portable exe in a read-only folder | Unaffected: portable never writes itself. |
| Beta user switches to Stable while on a newer beta | No downgrade; status line says "On a beta newer than the latest stable release". |
| `--smoke-test` | Fake backend; no network; `Program.Main` still runs `VelopackApp.Run()` (returns immediately when not installed). |

### Release workflow (`.github/workflows/build.yml`)

Workflow-level `env: { VELOPACK_VERSION: "1.2.161" }`. Changes to the `windows` job, in order:

1. **Version** (extended). For untagged builds, also output
   `pack = "$(dotnet msbuild src/Envoy.App/Envoy.App.csproj -getProperty:Version)-ci.${{ github.run_number }}"`.
   For tags, `pack = value`.
2. **Release notes.**
   - Tags: `python tools/release/changelog_notes.py CHANGELOG.md "${{ steps.version.outputs.value }}" --out notes.md`.
     - Stable tags exit non-zero when there is no `## [x.y.z]` section.
     - Prerelease tags use `## [x.y.z-…]` if present, else `## [Unreleased]`.
   - Untagged builds write a one-line `notes.md` ("Development build <sha>").
3. **Signing setup.** `env: ENVOY_SIGN_PFX_BASE64: ${{ secrets.ENVOY_SIGN_PFX_BASE64 }}` and
   `ENVOY_SIGN_PFX_PASSWORD`. A pwsh step:
   - When the PFX is present: decode it to `$env:RUNNER_TEMP\envoy-sign.pfx` and output `signing=true`.
   - Otherwise: `::notice::Code signing skipped (no ENVOY_SIGN_PFX_BASE64 secret)` and `signing=false`.
   - Fork PRs have no secrets and take the unsigned path.
4. **Publish single-file exe.** Unchanged. Then, if `signing == 'true'`:
   `pwsh tools/release/sign.ps1 publish/Envoy.exe`.
5. **Smoke test, screenshots, screenshots branch.** Unchanged.
6. **Publish folder build.**
   `dotnet publish src/Envoy.App/Envoy.App.csproj -c Release -o publish-velo -p:PublishSingleFile=false -p:InformationalVersion=<pack>`,
   plus `-p:Version=<numeric>` on tags.
7. **Install vpk.** `dotnet tool install --global vpk --version $env:VELOPACK_VERSION`.
8. **Previous release, tags only, for deltas.**
   `vpk download github --repoUrl https://github.com/${{ github.repository }} --channel win --token ${{ secrets.GITHUB_TOKEN }} -o Releases`,
   with `--pre` when the tag is a prerelease. `continue-on-error: true`: the first release has nothing to
   download, and a missing delta only costs bandwidth.
9. **vpk pack.**
   ```
   vpk pack --packId Envoy --packVersion <pack> --packDir publish-velo --mainExe Envoy.exe
            --packTitle Envoy --packAuthors "Envoy contributors" --icon src/Envoy.App/Assets/envoy.ico
            --releaseNotes notes.md --channel win --runtime win-x64 --noPortable --shortcuts StartMenuRoot
            --delta <BestSpeed on tags | None otherwise> -o Releases
            [--signTemplate "pwsh -NoProfile -File tools/release/sign.ps1 {{file}}"   # only if signing == 'true']
   ```
10. **Checksums.** Keep `publish/Envoy.exe.sha256` (REQ-DIST-3). Write `SHA256SUMS.txt` with `<hash>  Envoy.exe`
    and `<hash>  Envoy-win-Setup.exe`.
11. **Artifacts.**
    - Existing `Envoy-<v>-win-x64` (portable).
    - New `Envoy-<v>-setup` (`Releases/Envoy-win-Setup.exe`, `SHA256SUMS.txt`).
12. **Release (tags only).** This replaces `softprops/action-gh-release`. `env: GH_TOKEN: ${{ github.token }}`.
    ```
    vpk upload github --repoUrl https://github.com/${{ github.repository }} --token ${{ secrets.GITHUB_TOKEN }}
                      --channel win -o Releases --tag ${{ github.ref_name }} --releaseName "Envoy ${{ github.ref_name }}"
                      --targetCommitish ${{ github.sha }} --merge [--pre]       # creates/merges a DRAFT
    gh release upload ${{ github.ref_name }} publish/Envoy.exe publish/Envoy.exe.sha256 SHA256SUMS.txt --clobber
    gh release edit ${{ github.ref_name }} --notes-file release-body.md --draft=false [--prerelease]
    ```
    - `release-body.md` = `notes.md` + a fixed footer: which file to download, how to verify `SHA256SUMS.txt`,
      and a link to `docs/installing.md`.
    - `--merge` makes re-running a failed job idempotent.
    - `--draft=false` runs last (REQ-DIST-5).
    - Unauthenticated clients (the app) cannot see drafts, so nobody sees a half-uploaded release.
13. **winget (stable tags only).**
    `python tools/release/render_winget.py --version <v> --installer-url https://github.com/${{ github.repository }}/releases/download/${{ github.ref_name }}/Envoy-win-Setup.exe --sha256 <setup hash> --release-notes-url <release page> --out winget`
    → `actions/upload-artifact` `winget-manifests`.

**`check` job additions** (Ubuntu):

- `python3 tools/release/changelog_notes.py --self-test`
- `python3 tools/release/render_winget.py --self-test`
- `tools/release/check_velopack_pin.sh`: greps the `Velopack` version from `Envoy.App.csproj`,
  `tools/typecheck/TypeCheck.csproj` and `VELOPACK_VERSION` in `build.yml`, and fails if they differ.

### Release tooling (new, `tools/release/`)

| File | Purpose |
| --- | --- |
| `changelog_notes.py` | `changelog_notes.py <CHANGELOG> <version> [--out F]` prints the body of `## [<version>]` (up to the next `## `). For a prerelease with no exact section, it uses `## [Unreleased]`. A stable version with no section exits 2 with a message. A missing or empty section for a prerelease exits 0 with "No notes.". `--self-test` runs built-in cases: exact match, prerelease fallback, missing stable section → exit 2, last section in file, headings with dates `## [1.2.0] — 2026-10-01`. Standard library only. |
| `render_winget.py` | Replaces `{{VERSION}}`, `{{INSTALLER_URL}}`, `{{INSTALLER_SHA256}}`, `{{RELEASE_NOTES_URL}}`, `{{RELEASE_DATE}}` in `packaging/winget/*.yaml` and writes `<out>/manifests/p/PinnerSinner/Envoy/<version>/`. It fails if any `{{` remains or if the version is a prerelease. `--self-test` renders into a temp dir and checks there are no leftovers and that the file names match `PackageIdentifier`. |
| `sign.ps1` | `sign.ps1 <file>`: finds the newest `signtool.exe` under `${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\`, then runs `sign /fd SHA256 /f $env:RUNNER_TEMP\envoy-sign.pfx /p $env:ENVOY_SIGN_PFX_PASSWORD /tr http://timestamp.digicert.com /td SHA256 <file>`. Exits 0 with a notice when the PFX is missing. Fails on a signtool error. The header comment says how to swap in Azure Trusted Signing or SignPath by replacing the signtool call. |
| `check_velopack_pin.sh` | Version-pin consistency check (REQ-DIST-9). |

### winget templates (new, `packaging/winget/`)

- `PinnerSinner.Envoy.yaml` (`ManifestType: version`, `DefaultLocale: en-US`, `ManifestVersion: 1.6.0`).
- `PinnerSinner.Envoy.installer.yaml`:
  - `InstallerType: exe`, `Scope: user`, `InstallModes: [interactive, silent, silentWithProgress]`.
  - `InstallerSwitches: { Silent: --silent, SilentWithProgress: --silent }`, `UpgradeBehavior: install`.
  - One `x64` installer with `{{INSTALLER_URL}}` / `{{INSTALLER_SHA256}}`, and `ReleaseDate: {{RELEASE_DATE}}`.
- `PinnerSinner.Envoy.locale.en-US.yaml`:
  - `Publisher: PinnerSinner`, `PackageName: Envoy`, `License: MIT`, `PackageUrl`, `LicenseUrl`.
  - `ShortDescription: Live chat translation for Age of Empires and Age of Mythology`, `Tags`
    (`age-of-empires`, `translation`, `ocr`, `overlay`), `ReleaseNotesUrl: {{RELEASE_NOTES_URL}}`.
- `ProductCode` / `AppsAndFeaturesEntries` are omitted until AC-DIST-12 confirms the uninstall key name.

### Docs

- **`README.md`:**
  - The Quick start's first step becomes "Download **Envoy-win-Setup.exe**
    (`releases/latest/download/Envoy-win-Setup.exe`) and run it; it installs for your Windows account only (no
    admin) and keeps itself up to date. Prefer no install? Download the portable `Envoy.exe` instead."
  - The "One file." bullet becomes "Installer or one file."
  - The roadmap line "Signed releases and an optional installer with auto-update" becomes "Signed releases"
    (the installer part is done).
- **`docs/installing.md` (new):**
  - Installer vs portable.
  - Where things live: install `%LocalAppData%\Envoy`; settings `%APPDATA%\Envoy`.
  - How updates work (background download, never during a game, Restart to update, applied on next launch).
  - Channels and switching back.
  - Opting out (checkbox; what is contacted: GitHub only).
  - Uninstalling (Apps & features; settings kept; how to delete them).
  - SmartScreen while releases are unsigned.
  - Verifying `SHA256SUMS.txt`.
  - Installing an older version.
  - winget (`winget install PinnerSinner.Envoy` once accepted).
- **`docs/releasing.md`:** the new pipeline steps, the CHANGELOG section rule, beta tags, draft-then-publish,
  delta generation, the signing secrets (`ENVOY_SIGN_PFX_BASE64`, `ENVOY_SIGN_PFX_PASSWORD`; Azure
  Trusted Signing/SignPath as alternatives via `sign.ps1`), a manual winget submission (`wingetcreate submit`
  or a PR to `microsoft/winget-pkgs` from the `winget-manifests` artifact), and bumping `VELOPACK_VERSION` in
  all three places.
- **`docs/faq.md`:** "Does Envoy phone home?" now also says: update checks go to GitHub only and can be turned
  off.
- **`docs/architecture.md`:** the `Updates/` row lists the new types. The App section mentions `Program.cs` and
  `VelopackUpdateBackend`.
- **`THIRD-PARTY-NOTICES.md`:** Velopack (MIT, © Velopack Ltd).
- **`CHANGELOG.md`** (*Unreleased → Added*): the installer with auto-update, channels and in-app release notes.

## Acceptance criteria

| ID | REQ | Verified by |
| --- | --- | --- |
| AC-DIST-1 | REQ-DIST-19 | Unit tests `ReleaseVersionTests.*` and `AppVersionTests.*` pass. |
| AC-DIST-2 | REQ-DIST-13, 16, 20, 21, 22 | Unit tests `UpdateCoordinatorTests.*` pass. Each rule in the Design "State rules" list has a named test (Test plan). |
| AC-DIST-3 | REQ-DIST-15, 17 | Unit tests `UpdateViewBuilderTests.*` and `ReleaseNotesTextTests.*` pass. `GitHubReleaseBackendTests.Download_throws_not_supported` passes. |
| AC-DIST-4 | REQ-DIST-14, 18, 20 | `UpdateCheckerTests` (new cases with a fake `HttpMessageHandler`): requests go only to `api.github.com/repos/PinnerSinner/Envoy/...`, carry only `User-Agent` and `Accept`, Beta uses `/releases?per_page=10`, 403 → `UpdateCheckException`, 404 → null. Code review: `grep -n "new UpdateManager(" src` shows only calls with a `GithubSource` or the `ENVOY_UPDATE_FEED` path. |
| AC-DIST-5 | REQ-DIST-18 | `SettingsTests.Update_settings_default_and_old_files_load`: defaults are Stable / AutoDownload true, and a JSON without `updates` loads with defaults. |
| AC-DIST-6 | REQ-DIST-10 | `tools/typecheck/typecheck.sh` passes with `Program.cs` and the Velopack reference. The Windows CI step "vpk pack" logs `Verified VelopackApp.Run()`. The smoke test passes, which shows the portable/unpackaged start is unchanged. |
| AC-DIST-7 | REQ-DIST-4, 9 | CI on a non-tag push: the job uploads artifact `Envoy-<v>-setup` containing `Envoy-win-Setup.exe`. `check_velopack_pin.sh` passes. Changing one pin locally makes it fail. |
| AC-DIST-8 | REQ-DIST-6 | `changelog_notes.py --self-test` passes in the `check` job. |
| AC-DIST-9 | REQ-DIST-27 | The Windows smoke test log contains `updates: ok` for each theme (see smoke additions). With `--screenshots`, `<theme>-update.png` exists for every theme. |
| AC-DIST-10 | REQ-DIST-1, 2, 3, 5, 7 | **Release rehearsal** (manual, once, on the real repo or a fork): push `v0.9.0-beta.1`, then `v0.9.0-beta.2`. For each, the release appears only after the job finishes. It is marked prerelease and lists `Envoy.exe`, `Envoy.exe.sha256`, `SHA256SUMS.txt`, `Envoy-win-Setup.exe`, `Envoy-<v>-full.nupkg`, `releases.win.json` and `assets.win.json`. beta.2 also has `Envoy-0.9.0-beta.2-delta.nupkg`. The body is the CHANGELOG text. `Get-FileHash` matches `SHA256SUMS.txt`. |
| AC-DIST-11 | REQ-DIST-15, 16, 17, 12, 23 | **Manual, Windows** (after AC-DIST-10): install beta.1 with Setup.exe and set the channel to Beta. Restart Envoy. The banner shows "Downloading…" then "Envoy 0.9.0-beta.2 is ready" with the notes. Click **Restart to update**: Envoy closes, reopens on beta.2 within ~10 s and shows "Envoy was updated to v0.9.0-beta.2" once. `%LocalAppData%\Envoy\packages` contains the delta-built full package. In a second run, quit from the tray while the update is ready, then relaunch from the Start menu: Envoy comes up already on the new version. |
| AC-DIST-12 | REQ-DIST-1, 11 | **Manual, Windows:** Setup.exe runs without a UAC prompt. A Start menu entry "Envoy" exists. *Apps & features* lists Envoy. Record the `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\<key>` name in `docs/releasing.md`. Enable "Start with Windows", then uninstall: `HKCU\…\Run\Envoy` is gone, `%APPDATA%\Envoy\settings.json` is still there, and `%LocalAppData%\Envoy` is gone. |
| AC-DIST-13 | REQ-DIST-12 | **Manual, Windows:** with an update ready and Envoy running, start Envoy again from the Start menu. The running instance just comes to the front (existing show-signal), and no update or restart happens. |
| AC-DIST-14 | REQ-DIST-16 | **Manual, in game:** with AutoDownload on and an update available, start AoE2 DE. Before the start-up check the About status reads "…will download after your game". No `packages\*.partial` appears while the game runs. After closing the game the download starts within one game-detector cycle (3 s) plus `TickAsync`. |
| AC-DIST-15 | REQ-DIST-13 | **Manual, Windows:** untick "Check for updates automatically", restart Envoy and watch with Resource Monitor → Network (or `netstat -b`) for 2 minutes: no connection to `api.github.com`. Clicking **Check for updates** then makes one. |
| AC-DIST-16 | REQ-DIST-15 | **Manual, Windows:** run the portable `Envoy.exe` from beta.1 with Beta selected. The banner offers **Download** and **Get the installer**, and the latter opens the `Envoy-win-Setup.exe` URL. The exe's file timestamp is unchanged after an hour of running. |
| AC-DIST-17 | REQ-DIST-8 | CI without secrets: the log shows the "Code signing skipped" notice and the job is green. With a self-signed test PFX in a fork's secrets: `Get-AuthenticodeSignature` on `Envoy.exe`, `Envoy-win-Setup.exe` and `%LocalAppData%\Envoy\current\Envoy.exe` after install reports a signature (`UnknownError` for an untrusted root is expected; `NotSigned` fails). |
| AC-DIST-18 | REQ-DIST-28 | `render_winget.py --self-test` passes in `check`. On the stable rehearsal tag (`v0.9.0`), the `winget-manifests` artifact contains three YAML files with no `{{`. Optional: `winget validate --manifest <dir>` on a Windows machine passes. |
| AC-DIST-19 | REQ-DIST-24, 25, 26 | **Manual, Windows:** with an update ready, the tray menu shows "Restart to update to v…". About shows "Installed · Beta", the status line and a working **Check for updates**. Turn on Start with Windows in portable, then install and start the installed copy once: `HKCU\…\Run\Envoy` now points at `%LocalAppData%\Envoy\current\Envoy.exe`. |
| AC-DIST-20 | REQ-DIST-29 | Review: the README links `releases/latest/download/Envoy-win-Setup.exe`. `docs/installing.md` exists and is linked from the README docs table. `THIRD-PARTY-NOTICES.md` lists Velopack. `CHANGELOG.md` has the entry. |
| AC-DIST-21 | REQ-DIST-30 | (COULD) **Manual:** `vpk pack` two local versions into `C:\feed`, install the first, then set `ENVOY_UPDATE_FEED=C:\feed` and start Envoy: it offers the second. |

## Test plan

### Unit tests (`tests/Envoy.Core.Tests/`)

**`Updates/ReleaseVersionTests.cs`**

- `Parses_plain_and_v_prefixed(string text, int maj, int min, int pat)`: `"1.2.3"`, `"v1.2.3"`, `"V1.2"` → 1.2.0.
- `Parses_prerelease_identifiers`: `"1.2.0-beta.1"` → `Prerelease == ["beta","1"]`, `IsPrerelease`.
- `Drops_build_metadata`: `"1.2.0+abc"` equals `"1.2.0"`, and `ToString()` is `"1.2.0"`.
- `Prerelease_sorts_before_release`: `1.2.0-beta.1 < 1.2.0`.
- `Numeric_identifiers_compare_numerically`: `beta.2 < beta.10`.
- `Alphanumeric_vs_numeric_identifiers`: `1.0.0-1 < 1.0.0-alpha` and `1.0.0-alpha < 1.0.0-alpha.1` (SemVer §11
  examples).
- `Rejects_garbage(string)`: `"latest"`, `""`, `null`, `"1.2.3.4.5"`, `"1..2"`.
- `ToString_round_trips(string)`.

**`Updates/AppVersionTests.cs`**

- `Strips_source_revision_metadata`: `("1.2.0-beta.1+0abc", null)` → `1.2.0-beta.1`.
- `Falls_back_to_assembly_version`: `(null, new Version(1,2,3,0))` → `1.2.3`.
- `Falls_back_to_zero_when_nothing_parses`: `("dev", null)` → `0.0.0`.

**`Updates/ReleaseNotesTextTests.cs`**

- `Strips_headings_and_emphasis`: `"## Added\n- **Scan** it"` → `"Added\n• Scan it"`.
- `Links_become_their_text`: `"[docs](https://x)"` → `"docs"`.
- `Collapses_blank_lines`.
- `Truncates_at_line_boundary_with_ellipsis`: 3,000 chars → ≤ 1,201 chars, ends with `"…"`, no partial line.
- `Null_or_blank_gives_empty`.

**`PlayerAndUpdateTests.cs` → `UpdateCheckerTests`** (existing class; current tests stay unchanged)

- `Parse_prefers_portable_exe_over_setup`: assets `Envoy-win-Setup.exe` (first) and `Envoy.exe` →
  `DownloadUrl` ends `/Envoy.exe` and `InstallerUrl` ends `/Envoy-win-Setup.exe`.
- `Parse_with_prereleases_included_returns_prerelease`: `IsPrerelease == true`, `SemVer == 0.4.0-beta.1`.
- `PickLatest_stable_ignores_prereleases_and_drafts`: array of [draft 0.5.0, pre 0.4.1-beta.1, 0.4.0] → 0.4.0.
- `PickLatest_beta_picks_highest_semver_not_newest_published`: [0.4.1 published after 0.5.0-beta.1] →
  0.5.0-beta.1.
- `Check_stable_calls_latest_endpoint_with_minimal_headers`: a fake `HttpMessageHandler` records the URI
  `https://api.github.com/repos/PinnerSinner/Envoy/releases/latest`; headers are exactly `User-Agent` and `Accept`.
- `Check_beta_calls_list_endpoint`: URI ends with `/releases?per_page=10`.
- `Check_returns_null_when_not_newer`: current `0.4.0` and latest `0.4.0` → null; current `0.4.0-beta.2` and
  latest `0.4.0` → update.
- `Check_404_returns_null`.
- `Check_403_throws_UpdateCheckException`: message mentions rate limiting.
- `Check_bad_json_throws_UpdateCheckException`.

**`Updates/GitHubReleaseBackendTests.cs`**

- `Maps_update_info_to_available_update`: URLs, notes and version are carried over, and `Kind == Portable`.
- `Download_throws_not_supported`.
- `PendingRestart_is_null`.

**`Updates/UpdateCoordinatorTests.cs`.** Uses a `FakeUpdateBackend` (test-only; scripted results, call
counters, a `TaskCompletionSource` for blocking), `FakeTimeProvider`, and `Func<bool>` game flags.

- `Automatic_check_disabled_makes_no_backend_call`: `CheckForUpdates=false`; `StartAsync` and `TickAsync` → 0
  `CheckAsync` calls.
- `Manual_check_runs_even_when_disabled`.
- `Startup_skips_check_when_checked_within_an_hour`: `LastCheckUtc = now-30min` → 0 calls; `now-2h` → 1.
- `Startup_with_pending_package_goes_ready_without_network`: `PendingRestart=1.3.0` → `ReadyToRestart`, 0 calls.
- `Installed_autodownload_reaches_ReadyToRestart`: Available → Downloading → ReadyToRestart; `Changed` raised
  for each.
- `Autodownload_off_stops_at_Available`.
- `Download_deferred_while_game_running_then_resumes_on_tick`: game=true → `DeferredForGame`, 0 downloads; game=false
  then `TickAsync` → 1 download.
- `Portable_never_downloads`: `Kind=Portable`, AutoDownload true → Available, 0 downloads.
- `Skipped_version_hidden_for_automatic_check`: SkippedVersion "1.3.0" → `Idle`.
- `Skipped_version_shown_for_manual_check`.
- `SkipAvailableVersion_persists_and_calls_settingsChanged`.
- `Automatic_failure_is_silent`: backend throws → state returns to the previous one; `Error` set; State != Failed.
- `Manual_failure_sets_Failed_with_message`.
- `Download_failure_keeps_available_and_retry_succeeds`.
- `Cancellation_restores_previous_state`.
- `Concurrent_checks_share_one_backend_call`: two `CheckAsync` calls while the first is blocked → 1 call.
- `Periodic_tick_checks_after_12_hours`: advance `FakeTimeProvider` 11h → 0, 12h → 1.
- `LastCheckUtc_written_after_check`: on success and on failure.
- `Channel_is_passed_to_backend`: Beta setting → the backend receives `UpdateChannel.Beta`.
- `Older_or_equal_result_is_up_to_date`: a backend returning `<=` current → `UpToDate`.
- `RestartToUpdate_requires_ready_and_installed`: throws `InvalidOperationException` otherwise; when ready,
  calls `LaunchUpdaterAfterExit` with the given args.
- `Progress_raises_changed_only_on_whole_percent_change`.

**`Updates/UpdateViewBuilderTests.cs`**

- One `[Theory]` row per line of the snapshot→banner table in Design: `Builds_expected_view(case)` asserts
  `ShowBanner`, `Headline`, `Primary`, `PrimaryLabel`, `Secondary`, `SecondaryLabel` and `CanSkip`.
- `StatusLine_up_to_date_includes_check_time`.
- `StatusLine_beta_ahead_of_stable_explains_no_downgrade`.

**`SettingsTests`.** Add to the existing settings test file, or to a new `Settings/UpdateSettingsTests.cs` if
that file doesn't exist.

- `Update_settings_default_and_old_files_load`: `new AppSettings().Updates` has Channel Stable, AutoDownload
  true and SkippedVersion "". `SettingsStore.Load` of a JSON without `updates` gives the same.
- `Update_settings_round_trip`: Beta, AutoDownload false, `LastCheckUtc` survive save and load.

### Script self-tests (CI `check` job)

- `changelog_notes.py --self-test`, `render_winget.py --self-test`, `check_velopack_pin.sh`.

### Smoke-test additions (`src/Envoy.App/SmokeTest.cs`, shared file)

- `AppHost` is constructed with a `SmokeUpdateBackend : IUpdateBackend`, a private nested class:
  - `Kind` is configurable.
  - `CheckAsync` returns `AvailableUpdate(9.9.0, notes: "## Added\n- Faster OCR\n- New themes", …)`.
  - `DownloadAsync` reports 0/50/100 progress.
  - `LaunchUpdaterAfterExit` throws (it must never be reached).
  - No network.
- The smoke settings set `Updates.AutoDownload = false` so each state can be shown in turn.
- Per theme, after the existing `MainWindow` page loop:
  1. `await host.Updates.CheckAsync(manual: true)` → Navigate("Dashboard") → `Pump()`. Assert
     `host.UpdateView.Primary == UpdateNow`.
  2. Start `host.Updates.DownloadAsync()`. The backend blocks at 50% until released, then Pump, so Downloading
     is rendered. Release it, then await → Pump. Assert `ReadyToRestart`. Navigate("About") → Pump. Screenshot
     `{theme}-update.png` of the War Room (when `--screenshots`).
  3. Switch to a backend whose check throws, call `CheckAsync(manual: true)` → Pump. Assert `Failed`.
  4. Make a second host with `Kind=Portable` → check → Pump. Assert Primary `OpenReleasePage`, Secondary
     `GetInstaller`.
  5. `log.Add($"theme {theme.Id}: updates: ok")`.
- The smoke test never calls `RestartToUpdate`, and the tray isn't created in smoke mode, so nothing exits early.

### Manual checks

- The release rehearsal and install/update/uninstall walkthrough: AC-DIST-10 to AC-DIST-16, AC-DIST-19.
- The in-game check: AC-DIST-14, using AoE2 DE or any detected title.
- Signing with a test PFX on a fork: AC-DIST-17.

## Files touched

| Path | Change | Shared file? |
| --- | --- | --- |
| `src/Envoy.Core/Updates/ReleaseVersion.cs` | new | |
| `src/Envoy.Core/Updates/AppVersion.cs` | new | |
| `src/Envoy.Core/Updates/UpdateBackend.cs` | new: `IUpdateBackend`, `AvailableUpdate`, `UpdateChannel`, `InstallKind`, `UpdateCheckException`, `UpdateEndpoints` | |
| `src/Envoy.Core/Updates/GitHubReleaseBackend.cs` | new | |
| `src/Envoy.Core/Updates/UpdateCoordinator.cs` | new: coordinator, `UpdateState`, `UpdateSnapshot` | |
| `src/Envoy.Core/Updates/UpdateView.cs` | new: `UpdateView`, `UpdateAction`, `UpdateViewBuilder` | |
| `src/Envoy.Core/Updates/ReleaseNotesText.cs` | new | |
| `src/Envoy.Core/Updates/UpdateChecker.cs` | channel-aware `CheckAsync`, `PickLatest`, `Parse(json, includePrereleases)`, setup-vs-portable asset picking, typed errors; existing members unchanged | |
| `src/Envoy.Core/Settings/AppSettings.cs` | add `Updates` property and `UpdateSettings` class | **yes** (AppSettings.cs) |
| `src/Envoy.App/Program.cs` | new: `[STAThread] Main` with `VelopackApp` | |
| `src/Envoy.App/Services/VelopackUpdateBackend.cs` | new: backend + `UpdateBackendFactory` + `AppVersionInfo` | |
| `src/Envoy.App/Envoy.App.csproj` | `StartupObject`, `Velopack` 1.2.161 package | |
| `src/Envoy.App/AppHost.cs` | `Updates` coordinator, `UpdateView`/`HasUpdate`/`UpdateText`/`JustUpdatedTo`, ctor `IUpdateBackend?` parameter, 30 min timer, Run-key refresh, `CancellationTokenSource`, version/User-Agent; remove `CheckForUpdatesAsync`/`Update` | **yes** (AppHost.cs) |
| `src/Envoy.App/App.xaml.cs` | pending-update apply after mutex, `RestartToUpdate()`, tray item | **yes** (App.xaml.cs) |
| `src/Envoy.App/SmokeTest.cs` | `SmokeUpdateBackend`, per-theme update-state rendering, `{theme}-update.png` | **yes** (SmokeTest.cs) |
| `src/Envoy.App/Windows/MainWindow.xaml` (or `Pages/WarRoomPage.xaml`, `Pages/AppearancePage.xaml`, `Pages/AboutPage.xaml` after 01) | update banner, "updated to" notice, General update settings, About "Updates" block | **yes** (MainWindow*) |
| `src/Envoy.App/Windows/MainWindow.xaml.cs` | remove `OnDownloadUpdate` (before 01: add click handlers that forward to the VM commands) | **yes** (MainWindow*) |
| `src/Envoy.App/ViewModels/MainViewModel.cs` | `UpdateChannels`, `IsInstalled`, `InstallKindText`, `ChannelText`, update commands, `Version` from `AppVersionInfo` | |
| `tools/typecheck/TypeCheck.csproj` | add `Velopack` package reference | (shared tooling) |
| `.github/workflows/build.yml` | `VELOPACK_VERSION`, notes, signing setup/steps, folder publish, vpk download/pack/upload, gh release upload/edit, SHA256SUMS, setup artifact, winget render; `check` job script self-tests; remove `softprops/action-gh-release` | |
| `tools/release/changelog_notes.py` | new | |
| `tools/release/render_winget.py` | new | |
| `tools/release/sign.ps1` | new | |
| `tools/release/check_velopack_pin.sh` | new | |
| `packaging/winget/PinnerSinner.Envoy*.yaml` (3) | new templates | |
| `tests/Envoy.Core.Tests/Updates/*.cs` (6 files) | new tests | |
| `tests/Envoy.Core.Tests/PlayerAndUpdateTests.cs` | new `UpdateCheckerTests` cases | |
| `tests/Envoy.Core.Tests/Settings/UpdateSettingsTests.cs` | new (or add to an existing settings test file) | |
| `README.md`, `docs/installing.md` (new), `docs/releasing.md`, `docs/faq.md`, `docs/architecture.md`, `THIRD-PARTY-NOTICES.md`, `CHANGELOG.md` | docs | |

Expected size: about 600 lines of C# (Core ~350, App ~250), 350 lines of tests, 150 lines of scripts/templates,
120 lines of YAML, plus docs.

## Risks & open questions

1. **Velopack docs were unreachable.** Every API call in this spec was checked against the 1.2.161 binaries
   (reflection and decompile) and the CLI. Three things were not verified:
   - the exact install path and uninstall key name;
   - the hidden Windows-only `vpk` signing options (not used here);
   - how `--signTemplate` quotes `{{file}}` when paths contain spaces. CI paths have none; AC-DIST-17
     exercises signing once.

   If the uninstall key differs from `Envoy`, only the docs and the winget `AppsAndFeaturesEntries` change.
2. **`packId` is forever.** `Envoy` decides the install folder (`%LocalAppData%\Envoy`), the uninstall key and
   the `Envoy-win-Setup.exe` name. A more unique id (e.g. `PinnerSinner.Envoy`) would avoid a clash with another
   Velopack app called Envoy, but gives an uglier installer name. This spec picks `Envoy`. **Decide before the
   first release**: it can't be changed later without a migration.
3. **The install folder and the settings folder share the name "Envoy"** (`%LocalAppData%\Envoy` vs
   `%APPDATA%\Envoy`). They are different roots, so uninstall can't delete settings. Other areas must keep
   writing user data under `%APPDATA%`, never `%LocalAppData%\Envoy`. This should become a rule in
   `CLAUDE.md`.
4. **Two publishes per CI run.** One is the single-file portable, the other the folder for Velopack. This adds
   ~1–2 min. A single-file exe inside the Velopack package would make every delta almost as large as the full
   package, because the bundle is compressed as one blob. The smoke test runs only on the single-file build. The
   folder build has the same code but a different host layout. **Open:** also smoke-test `publish-velo\Envoy.exe`
   (+~2 min)? Recommended once CI time allows.
5. **GitHub rate limits.** Unauthenticated requests are limited to 60 per hour per IP. A Velopack check is 1 API
   call plus up to 10 feed downloads, and the asset downloads don't count. Users behind CGNAT or LAN-party NAT
   share the limit. The 1 h start-up floor and 12 h interval keep Envoy well under it, and a 403 is handled
   quietly.
6. **Beta → stable never downgrades.** A beta user who switches back stays on the beta until a higher stable
   release exists. This is a deliberate choice: `AllowVersionDowngrade=false` avoids data-format surprises. The
   UI and the docs explain it.
7. **No automatic rollback.** If a release breaks start-up, Velopack can't roll it back by itself. The fix is to
   ship a new version, or have users reinstall an older Setup.exe. This could combine with 02's crash marker,
   which could offer "Reinstall previous version" — future work.
8. **SmartScreen and antivirus false positives.** Unsigned `Setup.exe` files trigger SmartScreen "Unknown
   publisher". OCR + SendInput + a self-updater can look suspicious to heuristic AV engines. Signing is the real
   fix (SignPath Foundation is free for OSS). Until then, `installing.md` documents "More info → Run anyway" and
   the checksum check.
9. **Auto-apply on next launch while the game is running.** If the user starts Envoy mid-match with a package
   pending, the updater runs (a few seconds) before Envoy appears. This is acceptable; it never interrupts an
   already-running Envoy.
10. **Interaction with other areas.**
    - 01 (pages/commands): the UI goes into `Pages/*`, and `DownloadUpdateCommand` is replaced by
      `UpdatePrimaryCommand`.
    - 02 (logging): a backend error is logged at Warning and a check result at Info. `Program.Main` must stay
      the very first code that runs. 02's `LogBootstrap` belongs in `OnStartup`, after `VelopackApp.Run()`.
    - Onboarding: could offer the Beta channel in a first-run wizard. Not required.
    - Quality-security-ci: may want `SHA256SUMS.txt` signed or SLSA provenance
      (`actions/attest-build-provenance`). That fits after step 12 and is left to that area.
11. **Release body.** Changing from GitHub's generated notes to CHANGELOG-only notes drops the automatic
    contributor list. `gh release edit` can't generate notes. If wanted, the integrator can append
    `gh api repos/{repo}/releases/generate-notes` output to `release-body.md`.
12. **winget submission is manual.** Automating it needs a PAT with fork rights (`wingetcreate` with a
    `WINGET_TOKEN` secret). Left as a follow-up. The first submission must be manual anyway (moderator review).
