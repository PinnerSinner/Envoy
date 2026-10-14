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
