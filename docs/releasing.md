# Releasing

Releases are built and published by `.github/workflows/build.yml`. A release has two downloads, the per-user
installer `Envoy-win-Setup.exe` (Velopack; it keeps itself up to date) and the portable `Envoy.exe`, plus the files the
updater reads. See [Installing & updates](installing.md) for what users see.

## Cutting a release

1. Update `CHANGELOG.md`: move the items from *Unreleased* to a new `## [x.y.z] - YYYY-MM-DD` heading. **A stable tag
   without its section fails the build before anything is uploaded**; the section becomes the release notes on GitHub
   and in the app's update banner. A beta tag (`v1.3.0-beta.1`) uses its own `## [1.3.0-beta.1]` section if there is
   one, else *Unreleased*.
2. Commit, then tag and push:
   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. Tag builds run under the protected **`release` environment**: a required reviewer approves the `windows` job
   before it starts. Then the workflow:
   1. runs the policy check, the unit tests, the Linux type-check and the release-tooling checks (`check` job);
   2. takes the release notes from `CHANGELOG.md` (`tools/release/changelog_notes.py`);
   3. publishes the portable single-file `Envoy.exe` with the version from the tag and runs the Windows smoke test
      against it;
   4. signs `Envoy.exe` if the environment holds a certificate (see Code signing), then writes `Envoy.exe.sha256`;
   5. publishes a folder build and runs `dotnet vpk pack` (Start menu shortcut, no portable zip, a delta against the
      previous release on the same track when `vpk download` finds one), signing every packed binary and
      `Envoy-win-Setup.exe` with the same certificate;
   6. writes `SHA256SUMS.txt` (`Envoy.exe` and `Envoy-win-Setup.exe`, `sha256sum` format);
   7. creates or merges a **draft** release with `dotnet vpk upload github`, uploads `Envoy.exe`,
      `Envoy.exe.sha256` and `SHA256SUMS.txt`, and runs `tools/release/check_release_assets.py`: the release must still
      be a draft and hold exactly `Envoy.exe`, `Envoy.exe.sha256`, `SHA256SUMS.txt`, `Envoy-win-Setup.exe`,
      `Envoy-<v>-full.nupkg`, `releases.win.json`, `assets.win.json`, `RELEASES` (plus `Envoy-<v>-delta.nupkg` when a
      previous release existed), and the checksums must match the files. Only then is the draft published, with the
      notes and a fixed footer (`tools/release/release-footer.md`), and marked prerelease when the tag contains `-`.
      Users' copies never see a draft, so a failed run never offers a partial release;
   8. for stable tags, renders winget manifests (artifact `winget-manifests`).

Every push to any branch uploads `Envoy.exe` as a workflow artifact. Pushes to `main` also build an unsigned
`Envoy-win-Setup.exe` (version `<csproj Version>-ci.<run>`, artifact `Envoy-<version>-setup`), so a broken package
shows up before a tag. Re-running a failed tag build is safe: `vpk upload --merge` reuses the draft.

## Versioning

[Semantic versioning](https://semver.org/). The app compares versions by SemVer precedence, prerelease suffix
included (`1.2.0-beta.2` < `1.2.0-beta.10` < `1.2.0`), and shows its own version with the suffix (from the
informational version the tag sets). Tags with a `-` (`v0.9.0-beta.1`) are published as GitHub prereleases: copies on
the **Beta** channel are offered them, Stable copies are not. Moving from Beta back to Stable never downgrades.

## Verifying a download

Each release lists `SHA256SUMS.txt` and `Envoy.exe.sha256`. On Windows:

```powershell
Get-FileHash .\Envoy-win-Setup.exe -Algorithm SHA256
```

It should match the line in `SHA256SUMS.txt`. The release's workflow run (linked from the release) shows the exact
commit it was built from.

## Code signing

Signing runs **only in `v*` tag builds**, in steps of the `windows` job that runs under the protected `release`
environment (`environment: ${{ startsWith(github.ref, 'refs/tags/v') && 'release' || '' }}`). Untagged builds and forks
get no environment, cannot read the secrets, and print "Unsigned build (not a v* tag)".
`tools/release/check_signing_scope.py` fails CI if a signing secret is used anywhere else.

To enable it, in the repository's *Settings › Environments*, create `release` with required reviewers and add two
**environment secrets**:

- `ENVOY_SIGN_PFX_BASE64`: the code-signing certificate (`.pfx`), base64-encoded
  (`[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`);
- `ENVOY_SIGN_PFX_PASSWORD`: its password.

`tools/release/sign.ps1 <file>` signs with the newest `signtool.exe` on the runner (SHA-256 digest, RFC 3161 timestamp
from DigiCert). Without a certificate a tag build publishes unsigned with the notice "Code signing skipped (no
certificate in the release environment)", and Windows SmartScreen warns on first run. Other services (Azure Trusted
Signing, [SignPath Foundation](https://signpath.org/), free for open source) replace the `signtool` call in `sign.ps1`;
its header explains the contract.

## winget

Stable tags produce `winget-manifests` (schema 1.6.0, `PinnerSinner.Envoy`, user-scope `exe` installer with
`--silent`), rendered from `packaging/winget/` by `tools/release/render_winget.py`. Submission is manual: download
the artifact, then open a pull request to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) with
`manifests/p/PinnerSinner/Envoy/<version>/` (or run `wingetcreate submit` on the folder). The first submission is
reviewed by a moderator. Add `ProductCode` and `AppsAndFeaturesEntries` once M-DIST-2 has recorded the uninstall key.

## Updating Velopack

The `Velopack` package and the `vpk` tool must be the same exact version, in three places: `src/Envoy.App/Envoy.App.csproj`,
`tools/typecheck/TypeCheck.csproj` and `.config/dotnet-tools.json`. `tools/release/check_velopack_pin.sh` fails CI
when they disagree. CI runs `vpk` only through `dotnet tool restore` and `dotnet vpk`. After a bump, run the M-DIST-1
rehearsal on a fork before the next real release.

## Manual checks

Steps from each area's spec that need a person and a game. Run them before a release (08b gathers them into one
ordered pass). Each uses any supported title in a skirmish against AI.

### Quality and security (08a)

- **M-QSC-1** Reply mode "Type it into game chat and send". Open the reply box with the hotkey over the game, type
  "hello" and press Enter. The translated "hello" appears once in game chat, and nothing else happens in the game.
  Then open "Reply…" from the **tray** while the game runs. The text goes to the game, not the taskbar.
- **M-QSC-2** Add a user phrase whose `en` text is `"gl\nhf"`, reload and send it. Chat shows one message, `gl hf`.
  No units are selected or commanded.
- **M-QSC-3** Set "Delay between keystrokes" to 100 ms. Send a 60-character reply and alt-tab to Notepad after about
  1 s. Typing stops, Notepad receives nothing, the "another window came to the front" sentence appears, and the text
  is on the clipboard.
- **M-QSC-4** Open the reply box over the game, close the game, then press Send. Nothing is typed anywhere, the
  "window you were replying to has closed" sentence appears, and the text is on the clipboard.
- **M-QSC-5** Check each launcher: the About links open the browser; "Open data folder" and "Open logs" open
  Explorer; Phrasebook "Edit" opens `phrases.json`; OCR "Install" shows a UAC prompt, and declining it opens Windows
  language settings.

### Reliability and observability (02)

- **M-REL-1** Run `Envoy.exe --safe-mode`. The safe-mode banner shows above every page, there is no overlay,
  `Ctrl+Shift+T` does nothing, the Hotkeys page says "Hotkeys are off in safe mode." and the tray tooltip reads
  "Envoy (safe mode)". **Restart normally** brings back the overlay and the hotkeys.
- **M-REL-2** Quit Envoy, then start it from the Start menu while holding Shift until the window appears: safe mode.
- **M-REL-3** About › Troubleshooting › **Delete all Envoy data…**: the confirmation lists the files. After the
  restart Envoy is at first run, `%APPDATA%\Envoy` holds only the new session's files, and the
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Envoy` is gone.
- **M-REL-4** (Debug build) In a borderless match, press Ctrl+Alt+Shift+F12 on the About page, then return to the
  game. A toast appears on the game's monitor, the game keeps focus (WASD and hotkeys work), it closes after 10 s,
  and **Details** opens Troubleshooting.
- **M-REL-5** (Debug build) Tray › **Throw test error**: a toast, and no WinForms error dialog.
- **M-REL-6** Set a DeepL key and a player name, then **Copy diagnostics** into Notepad. Every section is there,
  `DeepL [deepl]: key set`, and neither the key nor the name appears; the chat area is a size only.
- **M-REL-7** Quit, delete a brace from `settings.json`, start. The previous settings and the decrypted key are back
  (from `.bak`), `settings.json.broken` exists, and a toast says the settings file was damaged.
- **M-REL-8** (Debug build) Tray › **Crash (background thread)**: `last-crash.json` exists; the next start shows
  "Envoy closed unexpectedly last time", and Troubleshooting shows the last crash with **Dismiss**.
- **M-REL-9** Make `%APPDATA%\Envoy\logs` read-only and use Envoy for a minute: no errors appear, and the report shows
  `Dropped log writes:` above 0. Make it writable again.

### Translation resilience (03)

- **M-TRR-1** Disconnect the network, type in the Translation "Try it" box and press Translate. Within 15 s you see a
  failure, not an endless "…". The Provider health card shows Google and Microsoft "Skipping … after repeated
  failures" (after a few tries). Reconnect and press **Retry now**; the row turns green with the next translation.
- **M-TRR-2** Put an invalid DeepL key as the main provider. The first message is translated by the fallback; the DeepL
  row is red ("Your key was rejected"). Fix the key; the row returns to "Not used yet", then "Working".
- **M-TRR-3** Turn on "Remember translations", translate "gg wp", quit, restart. The result is instant. The file
  `translation-cache.json` exists in the data folder and does not contain "gg wp". Turn the option off; the file is
  gone. Turn it on again, translate, then About › **Delete all Envoy data…**: after the restart the file is absent.
- **M-TRR-4** In a live match, or with the demo feed, send a burst of 8 or more chat lines. All are translated, and the
  War Room status never shows Google as unavailable from the burst alone.

### Capture engine (05)

- **M-CAP-1** In AoE II: DE windowed or borderless, minimise the game. Within 2 s the War Room shows "Paused while the
  game is minimised." and Task Manager shows Envoy below 1% CPU. Restore it: "Reading chat" returns within 2 s. Drag
  a browser over the chat: the status shows the covered text. Move the browser away: reading resumes.
- **M-CAP-2** Use two monitors, the primary at 150% and the secondary at 100%, then swap them. Select the chat area on
  each monitor. The Live view shows exactly the dragged area, within 1 px at 4× zoom, and reopening the selector draws
  the rectangle in the same place.
- **M-CAP-3** Drag a windowed game 300 px. The captured area follows, and the overlay anchored "Above chat" follows
  within about 1 s. Then move the game to the other-DPI monitor.
- **M-CAP-4** In a quiet match the Live view shows `every 1.5 s` with a skip rate of at least 90%. When a friend
  types, it shows `every 0.3 s`. Then lock the PC (Win+L) for 20 s and unlock it: the status returns to "Reading chat"
  within 6 s without toggling.
- **M-CAP-5** Use exclusive fullscreen where the game offers it, or select an all-black area. The blank-area hint
  appears within 5 frames.
- **M-CAP-6** Run `Envoy.exe --soak 120`. It exits 0, and `soak-test.log` shows no metric growing by more than 20%.
- **M-CAP-7** Alt-tab to a browser on a second monitor with the game still visible and "Pause when the game isn't the
  active window" off. Chat is still read.

### Chat understanding (04)

The steps are described in full in [chat formats](chat-formats.md#manual-release-checks).

- **M-CHAT-1** A message long enough to wrap (from an AI ally or a second account, AoE II: DE or AoE IV, English
  client) shows as one overlay entry.
- **M-CHAT-2** Keep chatting until that message's first line scrolls out of the chat area. Nothing new appears.
- **M-CHAT-3** Add `*resign*` (or the client-language equivalent) to the notice list and resign an AI game. Nothing is
  translated, and the Translation usage counter does not move.
- **M-CHAT-4** With a translation in flight (slow provider or no network), click **New match**, and separately pause
  capture. No overlay line stays on "translating…".
- **M-CHAT-5** (calibration, before 1.0) At least 3 anonymised "Copy as test capture" fixtures from at least 2 games in
  `tests/Envoy.Core.Tests/Golden/Captures/real/` pass `dotnet test`; adjust the stitching defaults if one needs it,
  then drop "provisional" from REQ-CHAT-5 and the docs.
- **M-CHAT-6** With a wrapped message visible, the Live view's "Understood as" shows `(2 lines)`, and "Copy as test
  capture" puts text on the clipboard that `CaptureFixture.Parse` accepts.

### Distribution and updates (06)

Run these on a fork (or with throwaway tags) before the first release, after a Velopack bump and before 1.0.

- **M-DIST-1 Release rehearsal.** Push `v0.9.0-beta.1`, then `v0.9.0-beta.2`, each with a CHANGELOG section. Each
  release appears only after its job finishes, is marked prerelease, its body is the CHANGELOG text plus the footer,
  and `Get-FileHash` matches `SHA256SUMS.txt`. Install beta.1 with its `Envoy-win-Setup.exe`, choose the **Beta**
  channel and restart Envoy: the banner shows "Downloading…", then "Envoy 0.9.0-beta.2 is ready" with notes behind
  **What's new**. **Restart to update**: Envoy reopens on beta.2 within about 10 s in the same window state, and "Envoy
  was updated to 0.9.0-beta.2" shows once. Repeat with a ready update, quit from the tray and start Envoy from the
  Start menu: it is already on the new version.
- **M-DIST-2 Install and uninstall.** `Envoy-win-Setup.exe` runs without a UAC prompt; the Start menu has "Envoy";
  *Apps & features* lists it. Record the `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\<key>` name and
  the install folder here. Turn on "Start Envoy when I sign in to Windows", then uninstall: the
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Envoy` is gone, the data folder still holds
  `settings.json`, and `%LocalAppData%\Envoy` is gone. With "Start with Windows" on, switching from the portable exe to
  the installed one points the Run value at the installed exe after the first start.
- **M-DIST-3 No apply under another instance or in safe mode.** With an update ready and Envoy running, start Envoy
  from the Start menu: the running instance comes to the front and nothing is applied. Quit, then start with Shift
  held: safe mode starts on the old version and applies nothing; the next normal start applies it. With an update
  ready, the tray menu shows "Restart to update to …". Make one apply fail (for example, keep a file in `current\`
  open in another program, then **Restart to update**): Envoy comes back on the old version, still offers **Restart
  to update**, logs "Started by the updater with an update still pending" and does not start the updater again by
  itself.
- **M-DIST-4 In game.** Background downloads on, an update available, AoE II: DE (or any detected title) running:
  About and the banner read "… will download after your game", and no `packages\*.partial` file appears under
  `%LocalAppData%\Envoy`. After closing the game the download starts within a few seconds.
- **M-DIST-5 Signing.** With a certificate in the `release` environment, `Get-AuthenticodeSignature` on the tag
  build's `Envoy.exe`, `Envoy-win-Setup.exe` and the installed `current\Envoy.exe` reports a signature (`NotSigned`
  fails; `UnknownError` is expected only for a self-signed test certificate). A push to `main` in the same repository
  produces an unsigned artifact and the log line "Unsigned build (not a v* tag)".
- **M-DIST-6 Portable and opt-out.** The portable beta.1 `Envoy.exe` with Beta selected offers **Download** and **Get
  the installer** (which opens the `Envoy-win-Setup.exe` URL); the exe's timestamp is unchanged after an hour. Untick
  "Check for updates automatically", restart and watch Resource Monitor › Network for 2 minutes: no `api.github.com`
  connection. **Check for updates** on About then makes one; on an installed copy it offers **Update now** and does
  not download by itself. Repeat the 2-minute watch in safe mode with the box ticked. Finally delete
  `%LocalAppData%\velopack\velopack.log` if it exists and start the portable exe (and `Envoy.exe --smoke-test`): no
  `velopack*.log` appears in `%LocalAppData%\velopack` or `%TEMP%`.

### Onboarding and notices (07a)

Use a fresh profile: rename `%APPDATA%\Envoy` first and put it back afterwards.

- **M-OUXA-1 Setup guide** (REQ-OUXA-1, 6, 10, 47). Start Envoy: the setup guide opens instead of the main window.
  **Next** stays disabled on Language & privacy until "I understand what leaves my PC" is ticked. Walk every step with
  AoE II: DE or AoE IV in borderless: **Select chat area** hides the guide and brings it back with a live view of the
  chat. **Finish** lands on the War Room. Repeat with a fresh profile and **Skip setup**: the next start opens the main
  window, the War Room shows "Finish setting up Envoy", and tray › **Setup guide…** reopens the guide (single window;
  a second click brings it to the front).
- **M-OUXA-2 Text recognition** (REQ-OUXA-7). On a PC without the Japanese pack, the Text recognition step shows
  Japanese **Missing**. **Install** → UAC → after it finishes, **Check again** shows Installed and the
  "Text recognition pack missing" tip does not come back.
- **M-OUXA-3 Provider tip** (REQ-OUXA-13, 17). DeepL first with a bogus key; receive chat: one "Translation provider
  problem" tip appears; clicking it opens the Translation page. Fix the key and break it again with the game in front:
  no tip until you alt-tab out, then it appears within about 5 s.
- **M-OUXA-4 Update tip** (REQ-OUXA-17). Install an older release with `Envoy-win-Setup.exe` while a newer one exists
  on its channel, then start it with a game in front: no tip. Close the game: one "Envoy update" tip; clicking it opens
  the War Room and runs the banner's main action. Restart Envoy: no second tip for the same version.
- **M-OUXA-5 Nothing sent before confirming** (REQ-OUXA-47). With translation not confirmed, play a match: the overlay
  shows the original lines with no translation, and Fiddler or Wireshark shows no request to
  `translate.googleapis.com` (or any other provider).
- **M-OUXA-6 FAQ policy note** (docs). The FAQ answer "Is Envoy allowed in ranked/anti-cheat games?" either links each
  publisher's published third-party-tool policy (check the links open) or its "checked" date is from this release
  cycle.
