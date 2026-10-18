# Releasing

Releases are built and published by `.github/workflows/build.yml`. A release has two downloads, the per-user
installer `Envoy-win-Setup.exe` (Velopack; it keeps itself up to date) and the portable `Envoy.exe`, plus the files the
updater reads, the checksums and the SBOM. See [Installing & updates](installing.md) for what users see.

A release goes through four stages, in this order: **Before tagging** (a checklist), the **Pre-release manual pass**
(one ordered list of steps that need a person and a game), **Tag** (the workflow builds, signs and publishes) and
**After the release** (checks on the published files). `python3 tools/ci/check_policy.py` (`required-docs`) checks
that these sections exist and that the pass has steps from every area with unique ids.

## Before tagging

- [ ] `main` is green for the commit to tag: `check`, `app-tests` and `windows` in Build, and `analyze (csharp)` in
      CodeQL. There are no open high or critical code-scanning or Dependabot alerts.
- [ ] `CHANGELOG.md` has the version's section: move the items from *Unreleased* to a new `## [x.y.z] - YYYY-MM-DD`
      heading. **A stable tag without its section fails the build before anything is uploaded**; the section becomes
      the release notes on GitHub and in the app's update banner. A beta tag (`v1.3.0-beta.1`) uses its own
      `## [1.3.0-beta.1]` section if there is one, else *Unreleased*.
- [ ] The `tools/ci/policy.json` diff since the last release has been reviewed (`git diff <last tag> -- tools/ci/policy.json`):
      every new P/Invoke, host or network path is intended.
- [ ] The [threat model](security/threat-model.md) has been reviewed against the changes since the last release, and
      its review log has a row for this version.
- [ ] The pre-release manual pass below is done on the dev artifact (`Envoy-dev.<run>-win-x64`, and for installer steps
      `Envoy-<version>-ci.<run>-setup`) of the commit to be tagged, and [compatibility](compatibility.md) is updated.

## Pre-release manual pass

One ordered pass, following a player's first session. Each step keeps the id it has in its area's spec, in the form
`M-<AREA>-n` (the old per-area ids are mapped at the end of this file). Use any supported title in a skirmish against
AI unless a step says otherwise, and fill the cells you covered in [compatibility](compatibility.md). For the first
full pass of 1.0, run it on `v1.0.0-rc.1`.

### 1. Install and update

Run these on a fork (or with throwaway tags) before the first release, after a Velopack bump and before 1.0. After
M-DIST-2, install the build under test again for the rest of the pass.

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

### 2. Start-up, safe mode and recovery

- **M-REL-1** Run `Envoy.exe --safe-mode`. The safe-mode banner shows above every page, there is no overlay,
  `Ctrl+Shift+T` does nothing, the Hotkeys page says "Hotkeys are off in safe mode." and the tray tooltip reads
  "Envoy (safe mode)". **Restart normally** brings back the overlay and the hotkeys.
- **M-REL-2** Quit Envoy, then start it from the Start menu while holding Shift until the window appears: safe mode.
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

### 3. Setup guide and notices

Use a fresh profile: rename `%APPDATA%\Envoy` first and put it back afterwards.

- **M-OUX-1 Setup guide** (REQ-OUXA-1, 6, 10, 47). Start Envoy: the setup guide opens instead of the main window.
  **Next** stays disabled on Language & privacy until "I understand what leaves my PC" is ticked. Walk every step with
  AoE II: DE or AoE IV in borderless: **Select chat area** hides the guide and brings it back with a live view of the
  chat. **Finish** lands on the War Room. Repeat with a fresh profile and **Skip setup**: the next start opens the main
  window, the War Room shows "Finish setting up Envoy", and tray › **Setup guide…** reopens the guide (single window;
  a second click brings it to the front).
- **M-OUX-2 Text recognition** (REQ-OUXA-7). On a PC without the Japanese pack, the Text recognition step shows
  Japanese **Missing**. **Install** → UAC → after it finishes, **Check again** shows Installed and the
  "Text recognition pack missing" tip does not come back.
- **M-OUX-3 Provider tip** (REQ-OUXA-13, 17). DeepL first with a bogus key; receive chat: one "Translation provider
  problem" tip appears; clicking it opens the Translation page. Fix the key and break it again with the game in front:
  no tip until you alt-tab out, then it appears within about 5 s.
- **M-OUX-4 Update tip** (REQ-OUXA-17). Install an older release with `Envoy-win-Setup.exe` while a newer one exists
  on its channel, then start it with a game in front: no tip. Close the game: one "Envoy update" tip; clicking it opens
  the War Room and runs the banner's main action. Restart Envoy: no second tip for the same version.
- **M-OUX-5 Nothing sent before confirming** (REQ-OUXA-47). With translation not confirmed, play a match: the overlay
  shows the original lines with no translation, and Fiddler or Wireshark shows no request to
  `translate.googleapis.com` (or any other provider).
- **M-OUX-6 FAQ policy note** (docs). The FAQ answer "Is Envoy allowed in ranked/anti-cheat games?" either links each
  publisher's published third-party-tool policy (check the links open) or its "checked" date is from this release
  cycle.

### 4. Capture, per title and display mode

Run M-CAP-1 to M-CAP-5 on each title and display mode you are verifying, and record the result in
[compatibility](compatibility.md).

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
- **M-CAP-6** (the soak, SHOULD; M-CAP-SOAK in spec 08b) Run `Envoy.exe --soak 120`. It exits 0, and
  `soak-test.log` shows no metric growing by more than 20%.
- **M-CAP-7** Alt-tab to a browser on a second monitor with the game still visible and "Pause when the game isn't the
  active window" off. Chat is still read.
- **M-QSC-6 OCR end-to-end on desktop Windows** (REQ-QSC-46). On Windows 10 22H2, run
  `Envoy.exe --ocr-e2e report.md` (no game needed; it uses no settings or network). It exits 0, `report.md` has no
  failed required (en-US) case, and the report is attached to the release PR. Non-English rows read `Passed`, or
  `Skipped (no recognizer)` when that OCR language is not installed. Record the run in the Windows 10 column of
  [compatibility](compatibility.md).

### 5. Chat understanding

The steps are described in full in [chat formats](chat-formats.md#manual-release-checks).

- **M-CHAT-1** A message long enough to wrap (from an AI ally or a second account, AoE II: DE or AoE IV, English
  client) shows as one overlay entry.
- **M-CHAT-2** Keep chatting until that message's first line scrolls out of the chat area. Nothing new appears.
- **M-CHAT-3** Add `*resign*` (or the client-language equivalent) to the notice list and resign an AI game. Nothing is
  translated, and the Translation usage counter does not move.
- **M-CHAT-4** With a translation in flight (slow provider or no network), click **New match**, and separately pause
  capture. No overlay line stays on "translating…".
- **M-CHAT-5** (calibration, once before 1.0; M-CHAT-CAL in spec 08b) At least 3 anonymised "Copy as test capture" fixtures from at least 2 games in
  `tests/Envoy.Core.Tests/Golden/Captures/real/` pass `dotnet test`; adjust the stitching defaults if one needs it,
  then drop "provisional" from REQ-CHAT-5 and the docs.
- **M-CHAT-6** With a wrapped message visible, the Live view's "Understood as" shows `(2 lines)`, and "Copy as test
  capture" puts text on the clipboard that `CaptureFixture.Parse` accepts.

### 6. Translation

- **M-TRR-1** Disconnect the network, type in the Translation "Try it" box and press Translate. Within 15 s you see a
  failure, not an endless "…". The Provider health card shows Google and Microsoft "Skipping … after repeated
  failures" (after a few tries). Reconnect and press **Retry now**; the row turns green with the next translation.
- **M-TRR-2** Put an invalid DeepL key as the main provider. The first message is translated by the fallback; the DeepL
  row is red ("Your key was rejected"). Fix the key; the row returns to "Not used yet", then "Working".
- **M-TRR-3** Turn on "Remember translations", translate "gg wp", quit, restart. The result is instant. The file
  `translation-cache.json` exists in the data folder and does not contain "gg wp". Turn the option off; the file is
  gone. Turn it on again and translate: the file is back (M-REL-3 at the end of the pass checks that **Delete all Envoy data…** removes it).
- **M-TRR-4** In a live match, or with the demo feed, send a burst of 8 or more chat lines. All are translated, and the
  War Room status never shows Google as unavailable from the burst alone.

### 7. Replies

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
- **M-OUX-12 Reply history and length** (REQ-OUXC-39, 40, 41). In game, send two replies; open the reply box: Up, Up,
  Down recall them in order, and a further Down returns what you had typed. Set Replying › "Warn above" to 10 and type a
  long reply: the counter under the box turns red and Send still works. Turn "Show message length" off: the counter is
  gone.
- **M-OUX-13 Compact overlay** (REQ-OUXC-44). Toggle Overlay › "Compact layout" during a match: the overlay switches
  without a restart; pending lines show "…" and then the translation, a failed one ends in a red ⚠.
- **M-OUX-14 Phrase order and copy** (REQ-OUXC-42, 43, 45). Send "gg" twice to a Korean target and reopen: the "gg"
  chip is first for Korean but not for Russian. `Ctrl+Shift+E` → ⧉ on a message → paste into Notepad shows
  "Sender: translation" and the original on the next line; the button says "Copied" for a moment.

### 8. Accessibility

- **M-OUX-7 Keyboard only** (REQ-OUXB-24, 29). Without the mouse, Tab through the setup guide: the focus ring is
  visible on every control, Tab wraps inside the window, Enter = Next (Finish on the last step), Esc = Skip setup. Open
  the reply box: Tab goes message → phrases → Copy → Send → language → Close and wraps, Enter sends, Esc closes,
  Alt+I jumps to the language. Repeat the ring check in each theme.
- **M-OUX-8 High contrast, live** (REQ-OUXB-33). With Envoy open, Windows Settings → Accessibility → Contrast themes →
  Night sky: the settings window, overlay and reply box switch to the contrast colours within a second. Switch to
  Desert: they update again. Turn contrast themes off: the game theme returns. Set Appearance › Accessibility › High
  contrast to **Never**: Envoy keeps the game theme under Night sky; **Always** uses contrast colours with contrast
  themes off.
- **M-OUX-9 Readable overlay** (REQ-OUXB-36, 37). Overlay background at 20% over a bright map (snow or desert): with
  "Keep text readable" on, the text stays readable and the Overlay page says "Raised to NN% so text stays readable";
  turn it off and the card is as transparent as chosen.
- **M-OUX-10 Narrator** (REQ-OUXB-28). Narrator on (Win+Ctrl+Enter): the title-bar buttons announce Minimise,
  Maximise and Close; hotkey boxes announce their action and how to enter a combination; text recognition rows, phrase
  chips, theme cards and the guide's step list announce meaningful names.
- **M-OUX-11 Page keys** (REQ-OUXB-30). In the settings window Ctrl+Tab cycles the nine pages and wraps from About to
  War Room; Ctrl+Shift+Tab goes the other way.

### 9. Settings UI

- **M-UIF-1 Settings buttons.** Click every page button once and compare the result with the previous release:
  - **War Room:** Select chat area, Pause/resume, Show sample messages, Move the overlay, Open reply box, Scan &
    translate, New match.
  - **Translation:** "Translate" shows `…` and then the result, and the two "Get a key" links open the browser.
  - **Reading Chat:** Select chat area, and Install. Declining UAC opens Language settings.
  - **Overlay:** Drag to place, Show sample messages, and Test voice, which uses the just-edited voice settings.
  - **Phrasebook:** Edit my phrases opens the file, shows an info box owned by the main window, and reloads the list.
  - **Appearance:** Open logs.
  - **About:** Open settings folder, Quit Envoy.
- **M-UIF-2 Settings round trip.** Change "Main provider" on Translation and a hotkey on Hotkeys. Wait 1 s, then quit
  from the tray: both values are in `settings.json`. Type in the Translation test box, switch pages, and change the
  phrase preview language: the settings file's modified time does not change. Reopen Envoy: there is no first-run
  behaviour.
- **M-UIF-3 Over the game** (any title, borderless). With Envoy reading chat, open settings over the game, switch
  pages including Reading Chat, then close the window. Capture keeps running and the overlay is unaffected.
- **M-HRD-1 The Herald's Writ** (REQ-HRD-3, 6 to 9). On a PC that has none of the fonts installed, at 100 % and 150 %
  display scale, compare the settings window, the overlay (full and compact), the reply box, the setup guide and a
  problem toast with `docs/design/herald/*.dc.html`, in the herald livery and one game livery: the knotwork band,
  bosses and filigree sit on the charter's edges; each banner tab shows its playful and plain name; the drop cap and
  title follow the selected tab; the ink drawing shows from 1100 px wide and hides below; Chinese, Korean and Russian
  names and originals render without boxes; no text spills out of a plaque, ribbon or seal.

### 10. Delete all Envoy data (last)

- **M-REL-3** About › Troubleshooting › **Delete all Envoy data…**: the confirmation lists the files. After the
  restart Envoy is at first run, `%APPDATA%\Envoy` holds only the new session's files (no `translation-cache.json`
  from M-TRR-3), and the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Envoy` is gone.

## Tag

1. Commit the CHANGELOG section, then tag and push. Use `-rc.N` first for a new minor version:
   ```bash
   git tag v1.0.0-rc.1
   git push origin v1.0.0-rc.1
   ```
2. The Build workflow runs `check`, then `windows` and `app-tests` in parallel, then the tag-only **`release`** job.
   `release` runs under the protected **`release` environment** and waits for a required reviewer before it starts.
   Never re-tag: if a release is wrong, delete the release (not the tag), fix, and ship the next patch version.

What each job does on a tag:

1. **`check`** (Ubuntu, read-only, budget 8 min): the policy check, the release-tooling checks, the unit tests with the
   coverage gate, the Linux type-check and the vulnerable-package gate.
2. **`app-tests`** (Windows, read-only): the OCR end-to-end harness on the real Windows OCR engine and the Hotkey tests.
3. **`windows`** (Windows, read-only, budget 20 min): takes the release notes from `CHANGELOG.md`
   (`tools/release/changelog_notes.py`); publishes the portable single-file `Envoy.exe` with the version from the tag
   and runs the smoke test against it; writes the CycloneDX SBOM `Envoy.cdx.json` and checks it
   (`tools/ci/check_sbom.py`); uploads `Envoy-<version>-win-x64` (the exe, the SBOM and the notes). After its budget
   step it publishes the folder build for the installer (artifact `publish-velo`).
4. **`release`** (Windows, tags only, `environment: release`, `contents: write`): builds and runs no project code. It
   downloads those two artifacts and:
   1. signs `Envoy.exe` if the environment holds a certificate (see Code signing), then writes `Envoy.exe.sha256`, so
      the released exe differs from the smoke-tested one only by its signature;
   2. runs `dotnet vpk pack` on the folder build (Start menu shortcut, no portable zip, a delta against the previous
      release on the same track when `vpk download` finds one), signing every packed binary and `Envoy-win-Setup.exe`
      with the same certificate;
   3. writes `SHA256SUMS.txt` (`Envoy.exe` and `Envoy-win-Setup.exe`, `sha256sum` format);
   4. attests build provenance for `Envoy.exe` and `Envoy-win-Setup.exe` (`actions/attest-build-provenance`);
   5. creates or merges a **draft** release with `dotnet vpk upload github`, uploads `Envoy.exe`, `Envoy.exe.sha256`,
      `SHA256SUMS.txt` and `Envoy.cdx.json`, and runs `tools/release/check_release_assets.py`: the release must still
      be a draft and hold exactly `Envoy.exe`, `Envoy.exe.sha256`, `SHA256SUMS.txt`, `Envoy.cdx.json`,
      `Envoy-win-Setup.exe`, `Envoy-<v>-full.nupkg`, `releases.win.json`, `assets.win.json`, `RELEASES` (plus
      `Envoy-<v>-delta.nupkg` when a previous release existed), and the checksums must match the files. Only then is
      the draft published, with the notes and a fixed footer (`tools/release/release-footer.md`), and marked
      prerelease when the tag contains `-`. Users' copies never see a draft, so a failed run never offers a partial
      release;
   6. for stable tags, renders winget manifests (artifact `winget-manifests`).

Every push to any branch uploads `Envoy-dev.<run>-win-x64` (unsigned `Envoy.exe`, its checksum, the SBOM and notes)
and `smoke-log`. Pushes to `main` also build an unsigned `Envoy-win-Setup.exe` (version `<csproj Version>-ci.<run>`,
artifact `Envoy-<version>-setup`), so a broken package shows up before a tag, and the `screenshots` job refreshes the
`ci-screenshots` branch. Re-running a failed `release` job is safe: `vpk upload --merge` reuses the draft.

## After the release

- [ ] The release lists `Envoy.exe`, `Envoy.exe.sha256`, `SHA256SUMS.txt`, `Envoy-win-Setup.exe`, the Velopack files
      (`Envoy-<v>-full.nupkg`, any delta, `releases.win.json`, `assets.win.json`, `RELEASES`) and `Envoy.cdx.json`.
- [ ] On a clean machine, `Get-FileHash` matches `SHA256SUMS.txt`, `Get-AuthenticodeSignature` shows a valid signature
      (when a certificate is configured, M-DIST-5), and `gh attestation verify Envoy.exe -R PinnerSinner/Envoy` passes.
- [ ] An installed previous version offers **Restart to update**, and a portable previous version links to the
      release page.
- [ ] For a stable release, submit the winget manifests (see winget).
- [ ] If anything fails, delete the release (not the tag), fix, and ship the next patch version. Never re-tag.

## Versioning

[Semantic versioning](https://semver.org/). The app compares versions by SemVer precedence, prerelease suffix
included (`1.2.0-beta.2` < `1.2.0-beta.10` < `1.2.0`), and shows its own version with the suffix (from the
informational version the tag sets). Tags with a `-` (`v0.9.0-beta.1`) are published as GitHub prereleases: copies on
the **Beta** channel are offered them, Stable copies are not. Moving from Beta back to Stable never downgrades.

## Verifying a download

Each release lists `SHA256SUMS.txt`, `Envoy.exe.sha256`, the SBOM `Envoy.cdx.json` and a build-provenance
attestation. [SECURITY.md](../SECURITY.md#verifying-a-download) explains how users check them; in short, on Windows:

```powershell
Get-FileHash .\Envoy-win-Setup.exe -Algorithm SHA256
gh attestation verify .\Envoy-win-Setup.exe -R PinnerSinner/Envoy
```

The hash should match the line in `SHA256SUMS.txt`. The release's workflow run (linked from the release and from the
attestation) shows the exact commit it was built from.

## Code signing

Signing runs **only in the `release` job**, which runs only for `v*` tags and always under the protected `release`
environment (contract §8.8, REQ-QSC-23). The jobs that build and run code (`check`, `app-tests`, `windows`) have
read-only tokens and no environment, so they cannot read the signing secrets; untagged builds print "Unsigned build
(not a v* tag)". `tools/release/check_signing_scope.py` fails CI if a signing secret appears outside the `release`
job, if that job loses `environment: release` or its tag condition, or if any other job declares an environment.

To enable it, in the repository's *Settings › Environments*, create `release` with required reviewers and add two
**environment secrets**:

- `ENVOY_SIGN_PFX_BASE64`: the code-signing certificate (`.pfx`), base64-encoded
  (`[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`);
- `ENVOY_SIGN_PFX_PASSWORD`: its password.

`tools/release/sign.ps1 <file>` signs with the newest `signtool.exe` on the runner (SHA-256 digest, RFC 3161 timestamp
from DigiCert). Without a certificate a tag build publishes unsigned with the notice "Code signing skipped (no
certificate in the release environment)", and Windows SmartScreen warns on first run. Other services (Azure Trusted
Signing, [SignPath Foundation](https://signpath.org/), free for open source) replace the `signtool` call in `sign.ps1`;
its header explains the contract. The recommended repository settings around this (rulesets, the environment,
Actions defaults) are in [repository settings](security/repository-settings.md).

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

## Step ids

Before spec 08b each area numbered its own manual steps. The pass above uses one form, `M-<AREA>-n`; steps whose ids
already had that form (`M-REL-n`, `M-TRR-n`, `M-CAP-n`, `M-CHAT-n`, `M-DIST-n`, `M-QSC-n`) kept them. Renamed or
merged ids:

| Area | Old id | New id |
| --- | --- | --- |
| 01 UI foundation | M1 | M-UIF-1 |
| 01 UI foundation | M2 | M-UIF-2 |
| 01 UI foundation | M3 | M-UIF-3 |
| 07a onboarding | M-OUXA-1 | M-OUX-1 |
| 07a onboarding | M-OUXA-2 | M-OUX-2 |
| 07a onboarding | M-OUXA-3 | M-OUX-3 |
| 07a onboarding | M-OUXA-4 | M-OUX-4 |
| 07a onboarding | M-OUXA-5 | M-OUX-5 |
| 07a onboarding | M-OUXA-6 | M-OUX-6 |
| 07b accessibility | M-OUXB-1 | M-OUX-7 |
| 07b accessibility | M-OUXB-2 | M-OUX-8 |
| 07b accessibility | M-OUXB-3 | M-OUX-9 |
| 07b accessibility | M-OUXB-4 | M-OUX-10 |
| 07b accessibility | M-OUXB-5 | M-OUX-11 |
| 07c reply box | M-OUXC-1 | M-OUX-12 |
| 07c reply box | M-OUXC-2 | M-OUX-13 |
| 07c reply box | M-OUXC-3 | M-OUX-14 |
| 04 chat understanding | M-CHAT-CAL (spec 08b) | M-CHAT-5 |
| 05 capture engine | M-CAP-SOAK (spec 08b) | M-CAP-6 |
