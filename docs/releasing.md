# Releasing

Releases are fully automated by `.github/workflows/build.yml`.

1. Update `CHANGELOG.md`: move items from *Unreleased* to a new version heading.
2. Commit, then tag and push:
   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. The workflow then:
   - runs the unit tests and the Linux type-check,
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
