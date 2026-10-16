# Installing and updating Envoy

Every release on the [releases page](https://github.com/PinnerSinner/Envoy/releases/latest) has two ways to get
Envoy. Both are the same app with the same settings.

| | **Envoy-win-Setup.exe** (recommended) | **Envoy.exe** (portable) |
| --- | --- | --- |
| Install | Installs for your Windows account only. No admin rights, no UAC prompt. | Nothing to install: run it from any folder. |
| Start menu and *Apps & features* | Yes | No |
| Updates | Downloads new versions in the background and asks you to **Restart to update**. | Tells you about new versions. It never changes itself: download the new file, or switch to the installer. |
| Uninstall | *Settings › Apps › Installed apps › Envoy › Uninstall* | Delete the file. |

## Where things live

- **The program.** The installer puts Envoy in `%LocalAppData%\Envoy` (the Start menu shortcut points there). The
  portable exe stays wherever you put it.
- **Your data.** Settings, API keys (encrypted with Windows DPAPI), your phrases, logs, transcripts and saved
  translations are in the data folder shown on **About** (`%APPDATA%\Envoy` by default). Both kinds of Envoy use the
  same data folder, and updating or uninstalling never touches it. [Privacy](privacy.md) lists every file.

## How updates work

With **Check for updates automatically** on (Appearance › Startup & updates), Envoy asks GitHub for a newer release
when it starts (at most once an hour) and every 12 hours while it runs.

- **Installed copies** download the update in the background, as a small difference to your version when one is
  available, and check it against the release's checksum. **The download never starts while a game is running**; it
  waits until the game closes. When it is ready, the War Room shows **Restart to update** (so does the tray menu).
  Envoy closes, updates and opens again within a few seconds, in the same window state.
- If you don't restart, the update is applied **the next time Envoy starts**, before it opens. Hold **Shift** while
  starting Envoy (safe mode) to start without applying it.
- An update is never applied while another Envoy is running, and Envoy never restarts by itself.
- Turn off **Download updates in the background** to be asked first: the banner then offers **Update now**.
- **Skip** hides that version from automatic checks. **Check for updates** on About still shows it.
- **Portable copies** show **Download** (the release page) and **Get the installer**.
- The banner's **What's new** shows the release notes; **Release notes** opens them on GitHub.

After an update, the War Room says once "Envoy was updated to …" with a link to what's new.

### Channels

**Update channel** (Appearance › Startup & updates):

- **Stable (recommended):** releases only.
- **Beta: early builds, may be rough:** also GitHub prereleases such as `v1.3.0-beta.1`.

Switching from Beta back to Stable never installs an older version: you keep your beta until a newer stable release
comes out. About says "On a beta newer than the latest stable release" meanwhile.

### Turning update checks off

Untick **Check for updates automatically**. Envoy then makes no update request at all until you press **Check for
updates** on About, which always works (also in safe mode). In safe mode Envoy never checks by itself.

Update checks and downloads go only to GitHub (`api.github.com` and the release downloads of
`github.com/PinnerSinner/Envoy`). They carry nothing but the request and a `User-Agent` naming the Envoy version: no
ID, no settings, no chat.

## Uninstalling

*Settings › Apps › Installed apps › Envoy › Uninstall* removes the program, the Start menu entry and the "Start
Envoy when I sign in to Windows" entry. **Your data folder is kept**, so a reinstall picks up your settings and keys.
To remove your data as well, first use **About › Troubleshooting › Delete all Envoy data…**, then uninstall.

## Windows SmartScreen

Until releases are code-signed, Windows may say "Windows protected your PC" the first time you run
`Envoy-win-Setup.exe` or `Envoy.exe`. Click **More info**, check the file name, then **Run anyway**. You can confirm
the file is the one GitHub built by checking its checksum (below). Signed releases will say so on the release page.

## Verifying a download

Each release has `SHA256SUMS.txt` with the SHA-256 of `Envoy.exe` and `Envoy-win-Setup.exe`. In PowerShell:

```powershell
Get-FileHash .\Envoy-win-Setup.exe -Algorithm SHA256
```

The hash must match the line for that file (letter case doesn't matter). On Linux or WSL, `sha256sum -c SHA256SUMS.txt`
checks both. `Envoy.exe.sha256` holds the portable exe's hash on its own, as before.

## Installing an older version

There is no automatic rollback. If a new version misbehaves:

1. Start Envoy with **Shift** held (safe mode) if it doesn't start normally, and **Copy diagnostics** for a bug report.
2. Untick **Check for updates automatically** (Appearance › Startup & updates), so the newer version isn't downloaded
   again.
3. Uninstall Envoy (your data folder is kept), then download `Envoy-win-Setup.exe` from the
   [older release](https://github.com/PinnerSinner/Envoy/releases) you want and run it.
4. Turn update checks back on once a fixed version is out.

## winget

Once the package is accepted into the Windows Package Manager community repository:

```powershell
winget install PinnerSinner.Envoy
```

Until then, use the releases page.
