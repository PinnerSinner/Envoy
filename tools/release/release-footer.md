---

**Which file?** Most people want **Envoy-win-Setup.exe**: it installs Envoy for your Windows account only (no admin
rights) and keeps it up to date. Prefer no install? Download the portable **Envoy.exe**; it tells you about new
versions but never changes itself. The `.nupkg`, `releases.win.json`, `assets.win.json` and `RELEASES` files are for
Envoy's updater.

**Verify your download:** `SHA256SUMS.txt` lists the SHA-256 of `Envoy.exe` and `Envoy-win-Setup.exe`. In PowerShell,
`Get-FileHash .\Envoy-win-Setup.exe -Algorithm SHA256` must match it (or run `sha256sum -c SHA256SUMS.txt`).

Installing, updating and uninstalling: [docs/installing.md](https://github.com/PinnerSinner/Envoy/blob/main/docs/installing.md).
