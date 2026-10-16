# Releasing

Releases are fully automated by `.github/workflows/build.yml`.

1. Update `CHANGELOG.md`: move items from *Unreleased* to a new version heading.
2. Commit, then tag and push:
   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. The workflow then:
   - runs the policy check, the unit tests and the Linux type-check,
   - publishes `Envoy.exe` with the version from the tag,
   - runs the Windows smoke test against that exe,
   - creates a GitHub Release with `Envoy.exe`, its SHA-256 checksum and generated notes.

Every push to any branch also uploads `Envoy.exe` as a workflow artifact, so testers can grab a build without a
release.

## Versioning

[Semantic versioning](https://semver.org/). The in-app update check compares the running version with the latest
non-prerelease GitHub Release. Tags such as `v0.3.0-beta.1` are published as prereleases and don't trigger update
prompts.

## Verifying a download

Each release lists `Envoy.exe.sha256`. On Windows:

```powershell
Get-FileHash .\Envoy.exe -Algorithm SHA256
```

It should match the checksum file. The release's workflow run (linked from the release) shows the exact commit it
was built from.

## Code signing

Release builds aren't code-signed yet, so Windows SmartScreen warns on first run. Signing needs a certificate.
Options include [SignPath Foundation](https://signpath.org/) (free for open source) or Azure Trusted Signing. The
workflow has a marked place where a signing step would go.

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
