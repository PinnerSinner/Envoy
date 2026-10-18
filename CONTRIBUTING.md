# Contributing to Envoy

Thanks for helping! Native speakers improving translations are just as valuable as code.

## Easy ways to help

- **Fix or add phrasebook translations:** `src/Envoy.Core/Phrases/phrases.json`.
- **Teach Envoy slang:** add a pattern to `src/Envoy.Core/Translation/SlangGlossary.cs` (and a test in
  `tests/Envoy.Core.Tests/LanguageTests.cs`).
- **Report a chat format Envoy misparses:** open an issue with a screenshot of the chat and the Live view text.

## Development setup

See [docs/building.md](docs/building.md). In short:

```bash
dotnet test tests/Envoy.Core.Tests   # any OS
tools/typecheck/typecheck.sh         # type-check the Windows app (and the app tests) from Linux/macOS
python3 tools/ci/check_policy.py     # product promises: native calls, network, hosts, launches, manifest, CI, docs
dotnet run --project src/Envoy.App   # Windows only
```

CI treats every warning as an error (`CI=true`); run `CI=true dotnet test tests/Envoy.Core.Tests -c Release` to see
what CI sees. See [docs/building.md](docs/building.md#analyzers-and-the-policy-check).

## Ground rules

1. **Never touch the game process.** No memory reading, injection or hooking. Envoy only reads screen pixels and
   sends keyboard input. PRs that change this won't be merged; it's what keeps players safe from anti-cheat.
2. **Logic belongs in `Envoy.Core` with tests.** The WPF app should stay a thin shell over it.
3. **Themeable UI only:** use the `DynamicResource` keys from `Themes/Palette.*.xaml` and the styles in
   `Themes/Controls.xaml`; don't hard-code colours. See [docs/theming.md](docs/theming.md).
4. **No game assets.** Don't add logos, fonts, icons or art from the games; Envoy's look is original.
5. **No telemetry.** Chat goes only to the translation provider the user picks.
6. **Only `Security/ShellLauncher.cs` starts processes,** and every new P/Invoke or URL host is added to
   `tools/ci/policy.json` in the same PR. Fix analyzer warnings rather than silencing them; the rare
   `[SuppressMessage]` needs a `Justification`, and `#pragma warning disable` is not allowed.
7. Keep `CHANGELOG.md`'s *Unreleased* section up to date for user-facing changes.

## Pull requests

- One topic per PR; describe what you checked (unit tests, type-check, tried in which game).
- CI must be green: the policy check, unit tests and the coverage gate, the Linux type-check, the vulnerable-package
  gate, the Windows build, smoke test and SBOM, the app tests (OCR end-to-end) and CodeQL. See
  [docs/building.md](docs/building.md#ci).
- Security-sensitive changes (interop, keystrokes, launches, network, secrets, workflows) update
  `tools/ci/policy.json` and the [threat model](docs/security/threat-model.md) in the same PR, and need the
  maintainer's review (`.github/CODEOWNERS`). Never commit real keys; see [SECURITY.md](SECURITY.md#for-contributors).
