---

**Which file?** Most people want **Envoy-win-Setup.exe**: it installs Envoy for your Windows account only (no admin
rights) and keeps it up to date. Prefer no install? Download the portable **Envoy.exe**; it tells you about new
versions but never changes itself. The `.nupkg`, `releases.win.json` and `RELEASES` files are for
Envoy's updater; `Envoy.cdx.json` lists every package inside this release (a CycloneDX SBOM).

**Verify your download:** `SHA256SUMS.txt` lists the SHA-256 of `Envoy.exe` and `Envoy-win-Setup.exe`. In PowerShell,
`Get-FileHash .\Envoy-win-Setup.exe -Algorithm SHA256` must match it (or run `sha256sum -c SHA256SUMS.txt`).
`gh attestation verify .\Envoy-win-Setup.exe -R PinnerSinner/Envoy` proves it was built by this repository's workflow.
More in [SECURITY.md](https://github.com/PinnerSinner/Envoy/blob/main/SECURITY.md#verifying-a-download).

Installing, updating and uninstalling: [docs/installing.md](https://github.com/PinnerSinner/Envoy/blob/main/docs/installing.md).
