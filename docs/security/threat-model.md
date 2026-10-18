# Envoy threat model

This document lists what Envoy protects, where untrusted input crosses into it, every host it talks to, and the
threats we design against, each with the code that mitigates it and the test or check that keeps the mitigation in
place. It is the developer-facing companion of [privacy](../privacy.md), which says the same things for players.

`python3 tools/ci/check_policy.py` checks this file (`required-docs`, `threat-model-refs`): every threat row T1–T16
exists with a "Verified by" cell, and every back-ticked repository path or test name (class `…Tests`, then `.Method`
or `.*`) below exists. A rename that breaks a reference fails CI.

## Scope and method

STRIDE-lite over the shipped app (`src/Envoy.App`, `src/Envoy.Core`), its update channel and the repository that
builds it. Reviewed before every minor release, as a checklist item of [releasing](../releasing.md#before-tagging);
add a row to the review log when you do.

Envoy's core promise is **screen pixels in, keystrokes out**: it never reads or writes game memory, injects code or
hooks input. Most rows below protect that promise or the player's text and keys.

## Assets

| ID | Asset |
| --- | --- |
| A1 | Provider keys (DeepL, Claude, Amazon, LibreTranslate) in `settings.json`. |
| A2 | Chat text: incoming OCR lines, outgoing replies, optional transcripts and the opt-in translation cache. |
| A3 | The ability to send keystrokes to another window and to set the clipboard. |
| A4 | The player's game-account standing (anti-cheat). |
| A5 | The integrity of `Envoy.exe`, `Envoy-win-Setup.exe` and the update channel. |
| A6 | The user's PC: no code execution from data Envoy reads. |
| A7 | Personal data at rest: player names, screen positions, logs. |

## Trust boundaries

| ID | Boundary | Untrusted side |
| --- | --- | --- |
| TB1 | Screen pixels → OCR → chat understanding | Anything another player types; anything drawn on screen. |
| TB2 | Envoy ↔ translation providers | Provider responses (and the network in between). |
| TB3 | User-writable files in the data folder (`settings.json`, `phrases.json`, `chat-formats.json`) | Hand edits, other same-user programs. |
| TB4 | GitHub API and Releases → updates (portable checker and Velopack) | Release metadata and packages. |
| TB5 | Envoy → the foreground window (keystrokes, clipboard) | Whatever window is in front at send time. |
| TB6 | Repository, CI and dependencies → release binary | Pull requests, NuGet packages, GitHub Actions. |
| TB7 | Named kernel objects (the single-instance mutex and show event) | Other processes of the same user. |

## Egress

Every host Envoy contacts. [Privacy](../privacy.md#what-leaves-your-pc) is the user-facing version; `tools/ci/policy.json`
→ `hosts` is the mechanical list of literal hosts in the source, and the `host` policy rule fails on any other. No
chat or reply text leaves the PC until the user confirms "What leaves your PC" (07a's privacy gate, T15).

| Host | Feature | Sends | Controlled by |
| --- | --- | --- | --- |
| `translate.googleapis.com` | Google Translate (free), the default and the last resort | Message text and languages | `Translation.Primary` / `Translation.Fallbacks`; always the last resort |
| `edge.microsoft.com`, `api-edge.cognitive.microsofttranslator.com` | Microsoft Translator (free) | Message text and languages | `Translation.Primary` / `Translation.Fallbacks` |
| `api.deepl.com`, `api-free.deepl.com` | DeepL | Text, languages, the user's key | Chosen provider with `Translation.DeepLKey` |
| `api.anthropic.com` (inside the Anthropic SDK) | Claude | Text, languages, the user's key | Chosen provider with `Translation.ClaudeKey` |
| `translate.<region>.amazonaws.com` (inside the AWS SDK) | Amazon Translate | Text, languages, signed with the user's key | Chosen provider with `Translation.AwsRegion` and keys |
| The user's LibreTranslate server (`http://localhost:5000` by default) | LibreTranslate | Text, languages, optional key | `Translation.LibreTranslateUrl` |
| `api.github.com` | Portable update check (`UpdateChecker`) | The request and Envoy's version | `CheckForUpdates`; never in safe mode |
| `github.com` and its release downloads | Velopack update feed and packages (installed copies), "Get the installer" | The request and Envoy's version | `CheckForUpdates`, `Updates.Channel`, `Updates.AutoDownload`; never in safe mode |

OCR and speech use Windows' own engines and send nothing. Links the user clicks open in the browser through
`ShellLauncher`. There is no telemetry, crash upload or Envoy server.

## Threats

| ID | Threat | Asset | Mitigations → code | Verified by | Residual risk |
| --- | --- | --- | --- | --- | --- |
| T1 | Chat-borne manipulation: prompt injection against an LLM provider, huge or hostile text | A2, A3 | Translations are display-only and incoming chat is never typed or executed; analysis is pure and bounded per capture → `src/Envoy.Core/Pipeline/ChatPipeline.cs`, `src/Envoy.Core/Chat/ChatCaptureAnalyzer.cs` | `ChatPipelineTests.*`, `ChatPipelineLifecycleTests.*`; review | An LLM provider may return a misleading translation. |
| T2 | Malicious `phrases.json`: newlines that become Enter presses, control keys, a huge file | A3 | `OutgoingText` strips control characters and caps length; `PhraseLimits` caps the file and entry sizes → `src/Envoy.Core/Security/OutgoingText.cs`, `src/Envoy.Core/Security/PhraseLimits.cs` | `PhraseBookLimitsTests.*`, `OutgoingTextTests.*`; M-QSC-2 | None known. |
| T3 | A provider response injects control characters or runaway length into a typed reply | A3 | HTTPS; every typed or pasted text goes through `OutgoingText.Sanitize`; the reply box previews the text → `src/Envoy.Core/Security/OutgoingText.cs`, `src/Envoy.App/Services/KeyboardSender.cs` | `OutgoingTextTests.*` | A wrong but printable translation is still sent if the user presses Send. |
| T4 | Keystrokes go to the wrong window (game closed, alt-tab, reply opened from the tray) | A3, A4 | `SendTargetGuard.ChooseTarget`/`Check` before and during typing; the text falls back to the clipboard → `src/Envoy.Core/Security/SendTargetGuard.cs`, `src/Envoy.App/Services/KeyboardSender.cs` | `SendTargetGuardTests.*`; M-QSC-1, M-QSC-3, M-QSC-4 | A window that takes focus between two keystrokes can receive one key. |
| T5 | API key disclosure at rest | A1 | DPAPI (CurrentUser) for every `[Secret]` setting; `.bak` copies stay encrypted → `src/Envoy.Core/Settings/SettingsStore.cs`, `src/Envoy.App/Services/DpapiProtector.cs` | `SettingsSecretTests.*`, `SettingsStoreTests.Round_trips_settings_and_encrypts_secrets_on_disk` | Malware running as the same user can decrypt (out of scope). |
| T6 | Key or chat disclosure through logs, diagnostics or bundles | A1, A2, A7 | `LogRedactor` masks keys and URL queries; chat and names only via `LogField.Sensitive`; `[Personal]`/`[Location]` settings are masked in diagnostics; provider health text is sanitised → `src/Envoy.Core/Diagnostics/LogRedactor.cs`, `src/Envoy.Core/Translation/Resilience/ProviderHealthText.cs` | `LoggingTests.Redacts_secrets_everywhere`, `SettingsRedactionTests.Every_marked_property_is_masked`, `DiagnosticsTests.Report_has_all_sections_and_no_private_values` | "Include chat text in logs" (off by default) writes chat on purpose. |
| T7 | Settings or data-file tampering leads to code execution | A6 | Plain JSON with no polymorphic deserialization; `BinaryFormatter` and `XamlReader` banned; processes start only through `ShellLauncher`, with targets validated by `LaunchTarget` → `src/Envoy.App/Security/ShellLauncher.cs`, `src/Envoy.Core/Security/LaunchTarget.cs`, `BannedSymbols.txt` | `LaunchTargetTests.*`; RS0030 in the type-check and the policy `process-launch` rule | A same-user attacker can already run code (out of scope). |
| T8 | Update hijack: spoofed release URL, fake release, tampered package | A5 | `ReleaseUrlPolicy` and `ShellLauncher` accept only this repository's release pages; releases come only from CI on protected `v*` tags in the `release` environment; Authenticode signing; Velopack verifies package hashes and its updater is started by Velopack, never by Envoy code; SHA-256 sums; SBOM; build-provenance attestation → `src/Envoy.Core/Security/ReleaseUrlPolicy.cs`, `src/Envoy.App/Services/VelopackUpdateBackend.cs`, `.github/workflows/build.yml` | `UpdateUrlTests.*`, `UpdateCheckerTests.*`; `tools/release/check_release_assets.py`, `tools/release/check_signing_scope.py` | A compromised maintainer account; an unsigned build while no certificate is configured. |
| T9 | Provider man-in-the-middle | A1, A2 | HTTPS with the OS certificate validation; validation-bypass APIs are banned → `BannedSymbols.txt`, `src/Envoy.Core/Translation/Providers/ProviderHttp.cs` | RS0030 in the type-check; `tools/ci/test_check_policy.py` (`bannedRequired`) | A user-chosen `http://` LibreTranslate server is unencrypted by the user's choice. |
| T10 | Supply chain: NuGet packages and GitHub Actions | A5, A6 | Dependabot; the vulnerable-package gate; actions pinned to commit SHAs; least-privilege jobs; CodeQL; SBOM; dependency review → `.github/workflows/build.yml`, `.github/workflows/codeql.yml`, `.github/dependabot.yml` | CI checks: `tools/ci/check_vulnerable.py`, `tools/ci/check_sbom.py`, the `workflow-*` policy rules | A malicious release of a pinned dependency before an advisory exists. |
| T11 | Anti-cheat regression: memory reads, hooks, injection | A4 | The native allowlist and deny list; `Process.MainModule` banned; CODEOWNERS on `src/Envoy.App/Interop/` → `src/Envoy.App/Interop/Native.cs`, `tools/ci/policy.json`, `.github/CODEOWNERS` | `tools/ci/test_check_policy.py`; the policy check in CI | A publisher may still object to screen reading or synthetic input (FAQ). |
| T12 | Elevation | A6 | `asInvoker` and no `uiAccess`; the OCR-pack install is the only elevated launch, user-started through UAC with a validated language tag → `src/Envoy.App/app.manifest`, `src/Envoy.App/Security/ShellLauncher.cs` | Policy `manifest` rule; `LaunchTargetTests.Ocr_install_arguments_only_for_catalog_languages` | None known. |
| T13 | Telemetry or unexpected egress | A2, A7 | Network types only in `src/Envoy.Core/Translation/`, `src/Envoy.Core/Updates/` and `src/Envoy.App/AppHost.cs`; a literal-host allowlist | Policy `network-location` and `host` rules (`tools/ci/check_policy.py`) | SDK-internal endpoints (AWS, Anthropic) are reviewed, not checked mechanically. |
| T14 | Single-instance object squatting | A6 | Accepted: needs a same-user attacker, and the result is only that Envoy does not start → `src/Envoy.App/App.xaml.cs` | Review (accepted risk) | Denial of service by a same-user process. |
| T15 | Chat text sent to a third-party provider without the user knowing | A2 | "What leaves your PC" in the setup guide and War Room; every translator stack is wrapped in `PrivacyGatedTranslator`, so no provider call happens before acknowledgement → `src/Envoy.Core/Translation/PrivacyGatedTranslator.cs` | `TranslationGateTests.*`; M-OUX-5 | The default free providers are unofficial endpoints (disclosed). |
| T16 | Personal data left behind | A7 | `DataInventory` lists every Envoy file, and "Delete all Envoy data" removes exactly those; every inventory file is documented → `src/Envoy.Core/Diagnostics/DataInventory.cs`, `docs/privacy.md` | `DataInventoryTests.Enumerate_lists_only_inventory`, `DataInventoryTests.Every_item_is_documented_in_privacy_md` | Copies the user made (exported diagnostics) stay where they saved them. |

## Out of scope

- Malware running as the same Windows user, a compromised OS or certificate store.
- Game publishers changing their third-party-tool policy. The [FAQ](../faq.md) states what Envoy does and does not do,
  with no claim of approval.
- Physical access to an unlocked PC.

## Review log

| Date | Version | Reviewer | Notes |
| --- | --- | --- | --- |
| 2026-10-18 | 0.1.0 (pre-1.0) | Spec 08b implementation | First version: T1–T16 written against the code after steps 1–10. |
