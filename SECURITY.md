# Security

## Reporting a vulnerability

Please **don't** open a public issue. Use GitHub's
[private vulnerability reporting](../../security/advisories/new) for this repository instead.

We aim to acknowledge a report within 7 days and to agree a fix and disclosure date with you after that. This is a
target from a small volunteer project, not a guarantee.

## Supported versions

Only the **latest release** gets security fixes. Installed copies update themselves (see
[Installing & updates](docs/installing.md)); the portable `Envoy.exe` tells you when a newer version is out.

| Version | Supported |
| --- | --- |
| Latest release | Yes |
| Anything older | No: update first |

## Verifying a download

Every release is built by GitHub Actions from a protected `v*` tag, never on a developer's PC. You can check that what
you downloaded is that build:

1. **Checksums.** `SHA256SUMS.txt` lists the SHA-256 of `Envoy.exe` and `Envoy-win-Setup.exe` (and `Envoy.exe.sha256`
   the portable exe alone). In PowerShell, `Get-FileHash .\Envoy-win-Setup.exe -Algorithm SHA256` must print the same
   hash; on Linux or macOS, `sha256sum -c SHA256SUMS.txt`.
2. **Authenticode signature.** When the project has a code-signing certificate, `Envoy.exe`, `Envoy-win-Setup.exe` and
   everything the installer puts on disk are signed: right-click › Properties › Digital Signatures, or
   `Get-AuthenticodeSignature .\Envoy.exe`. Until the project has a certificate, releases are unsigned and Windows
   SmartScreen warns on first run; rely on the checksums and the attestation then.
3. **Build provenance.** Each release has a GitHub attestation that ties `Envoy.exe` and `Envoy-win-Setup.exe` to the
   exact workflow run and commit: `gh attestation verify .\Envoy.exe -R PinnerSinner/Envoy`.
4. **SBOM.** `Envoy.cdx.json` is a CycloneDX software bill of materials: every NuGet package (with version) inside
   the release. Use it to check whether a published advisory affects your copy.

How releases are made is described in [releasing](docs/releasing.md).

## What Envoy does with your data

- **API keys** are stored in `%APPDATA%\Envoy\settings.json`, encrypted with Windows DPAPI (current-user scope). They
  are sent only to the provider they belong to, over HTTPS.
- **Chat text** is sent only to the translation provider you choose, and only after you confirm "What leaves your PC".
  Nothing is sent anywhere else.
- **Update checks** (optional) go to GitHub only.
- **Transcripts** (optional, off by default) are plain-text files in `%APPDATA%\Envoy\logs`.
- Envoy never runs elevated, never reads other processes' memory, and only sends keystrokes when you press Send in
  the reply box.

The full list of what leaves your PC and what Envoy stores is in [privacy](docs/privacy.md). The developer view
(assets, trust boundaries, every host, and each threat with its mitigation and test) is the
[threat model](docs/security/threat-model.md).

## For contributors

- **Never commit real keys**, tokens or certificates, not even in a test or a fixture you plan to remove.
- Use obviously fake strings in tests (`"FAKE-DEEPL-KEY"`). When a test needs a provider-shaped key, build it at run
  time by concatenation so GitHub's secret scanning push protection does not trip on it.
- If a key leaks, **revoke it at the provider first**, then remove it from the repository; history rewriting does not
  un-leak it.
- Changes to security-sensitive paths (interop, keystrokes, process launches, network code, secrets storage,
  workflows, the policy check) need the maintainer's review: see [`.github/CODEOWNERS`](.github/CODEOWNERS). Update
  `tools/ci/policy.json` and the [threat model](docs/security/threat-model.md) in the same change.
