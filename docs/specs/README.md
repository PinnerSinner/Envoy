# Envoy 1.0 specs

This folder holds the specs that take Envoy from v0.1 to a 1.0 that strangers can trust. Each area has one spec.
`00-integration.md` is the binding contract that settles every question between areas. When an area spec and the
contract disagree, the contract wins.

## Process

Every area goes through the same six stages, in this order. An area does not start until the one before it has
finished the last stage.

| Stage | What happens | Output |
| --- | --- | --- |
| 1. Spec | The area's spec is written against today's code. It states the context, goals, requirements (MUST, SHOULD or COULD), design, acceptance criteria, files touched, risks and manual steps. | `NN-area.md`, `Status: Proposed` |
| 2. Critique | One reviewer reads all the specs as a set. The review covers contradictions, duplicated work, shared files with no owner, unowned bugs, missing areas and scope. Each finding has a severity (blocker, major or minor) and a proposed fix. | Critique, not stored in the repo |
| 3. Contract | The answers go into `00-integration.md`. It fixes the order, a single owner for each shared concept, the final shapes of shared types, the start-up sequence, the allowlists, the smoke-test structure, the settings rules and the MUST budget. Each spec is then revised to match it. | `00-integration.md`, `Status: Accepted (binding)`; specs say "revised against `00-integration.md`" |
| 4. Implement | One area at a time, in the order below. The first commit edits the area's spec if anything has changed. A step may use only what earlier steps delivered (the arrival rule in contract §1). | Code, tests, docs |
| 5. Review | The change is checked against its spec and the contract's definition of done (§10). Every difference goes under `## Deviations` in the spec, and the spec's status becomes `Implemented`. | Reviewed PR |
| 6. CI | The change lands on `main` only when CI is green, including the Windows smoke test. The next area starts after that. | Green `main` |

### Rules that hold across all areas

- **MUST is for a trustworthy 1.0.** Gold-plating is SHOULD or COULD. The budget is about 150 MUSTs in total
  (contract §9).
- **Every MUST can be checked.** Each MUST has an acceptance criterion of one of these kinds: a Core unit test,
  `tools/typecheck/typecheck.sh`, a named Windows smoke-test section, a CI check, or a numbered step in the
  `docs/releasing.md` manual pass. If an area can't verify a MUST, it changes the criterion or demotes the
  requirement and records that under Deviations.
- **Requirement ids are stable.** Folded, moved and removed requirements keep their row, with a pointer. The split
  specs keep the old numbers: `REQ-OUX-13` became `REQ-OUXA-13`, and `REQ-QSC-n` stays the same in 08a and 08b.
- **Shared concepts have one owner** (contract §2). Shared files change only in the slot the contract reserves for
  each area (§3, §4, §6).

## Index

MUST counts are the requirements whose level is MUST. Folded and removed rows are not counted.

| # | Spec | Area | Status | MUST | SHOULD | COULD |
| --- | --- | --- | --- | ---: | ---: | ---: |
| — | [00-integration.md](00-integration.md) | Integration contract (binding) | Accepted | — | — | — |
| 1 | [01-ui-foundation.md](01-ui-foundation.md) | Settings pages as UserControls, commands, `IShell`, `DialogService`, preview lease, smoke structure, stub typecheck | Implemented | 15 | 3 | 0 |
| 2 | [08a-quality-gates.md](08a-quality-gates.md) | Analyzers, `BannedSymbols.txt`, the policy check, `Security/*` (`ShellLauncher`, `OutgoingText`, `SendTargetGuard`, `ReleaseUrlPolicy`, `PhraseLimits`) | Implemented | 14 | 2 | 1 |
| 3 | [02-reliability-observability.md](02-reliability-observability.md) | Logging, global handlers, problem toasts and the notification throttle, diagnostics, settings durability, safe mode, data inventory and "Delete all Envoy data", start-up and exit skeleton | Implemented | 20 | 16 | 2 |
| 4 | [03-translation-resilience.md](03-translation-resilience.md) | Error kinds, retry, circuit breaker, throttling, provider health, lasting cache (lands as PR A, then PR B) | Implemented | 16 | 10 | 1 |
| 5 | [05-capture-engine.md](05-capture-engine.md) | `ChatWatcher` rewrite: window-relative regions, adaptive polling, OCR timeouts, watchdog, `GameWindowTracker`, `MergeLines` | Implemented | 19 | 19 | 2 |
| 6 | [04-chat-understanding.md](04-chat-understanding.md) | Wrapped lines, game notices, dedupe, cleaning, the pending-message lifecycle, "Understood as" | Implemented | 17 | 18 | 1 |
| 7 | [06-distribution-updates.md](06-distribution-updates.md) | Velopack installer, in-app updates, channels, tag-only signing, winget | Proposed | 17 | 11 | 1 |
| 8 | [07a-onboarding-notices.md](07a-onboarding-notices.md) | Setup guide, privacy disclosure and translation gate, advisory notices, tray balloons, War Room layout | Proposed | 12 | 8 | 0 |
| 9 | [07b-accessibility.md](07b-accessibility.md) | Focus ring, accessible names, `check_a11y.py`, contrast fixes, high-contrast palette | Proposed | 8 | 5 | 0 |
| 10 | [07c-reply-overlay-polish.md](07c-reply-overlay-polish.md) | Reply history, length counter, phrase order, compact overlay, copy | Proposed | 4 | 3 | 0 |
| 11 | [08b-ci-release.md](08b-ci-release.md) | Workflow restructure and `release` job, coverage gate, CodeQL, SBOM, time budget, OCR end-to-end test, threat model, compatibility matrix, release checklist | Proposed | 10 | 16 | 0 |
| | **Total** | | | **152** | **111** | **8** |

Superseded pointers, which are deleted when their split step starts (`08-quality-security-ci.md` went with 08a's
first commit):

| Spec | Replaced by | Deleted in |
| --- | --- | --- |
| [07-onboarding-ux-accessibility.md](07-onboarding-ux-accessibility.md) | 07a, 07b, 07c | 07a's first commit |

The MUST ids for each area are listed in contract §9. When an area changes a requirement's level, it updates its own
spec, contract §9 and this table in the same change.

## Implementation order

The steps run strictly one after another. Each lands on `main` with CI green before the next one starts.

```text
 1  01-ui-foundation
 2  08a-quality-gates            analyzers, banned APIs (incl. the sanctioned ShellLauncher launchers), Security/* types;
                                 no workflow restructure
 3  02-reliability-observability defines AppHostOptions, instance data paths, the OnStartup/ExitApp skeleton
 4  03-translation-resilience    PR A (core + wiring), then PR B (cache + Translation page health UI)
 5  05-capture-engine            ChatWatcher rewrite; passes CancellationToken.None to ProcessAsync
 6  04-chat-understanding        on top of 05's tick; owns the pending-message lifecycle
 7  06-distribution-updates
 8  07a-onboarding-notices
 9  07b-accessibility            check_a11y.py and the runtime audit land here, after every control-adding area
10  07c-reply-overlay-polish
11  08b-ci-release               workflow restructure, coverage gate, CodeQL, SBOM, release checklist,
                                 compatibility matrix, OCR end-to-end test
```

Why this order:

- 01 comes first because every later UI change lands on its pages, commands and `IShell`.
- 08a comes second so that later areas are written against the analyzers, the banned-API list and the policy
  allowlists from the start.
- 02 defines the host options, the logging and the start-up sequence that every later area plugs into.
- 05 comes before 04 because 04's changes sit on the rewritten watcher's tick and `CaptureFrame`.
- 07b's lint runs after 02–07a have added their controls.
- 08b comes last because it restructures the workflow that 06 extends.

## Where to look

| Question | Contract section |
| --- | --- |
| Who owns provider health, notifications, hotkey results, game-window probing or update state? | §2 |
| What do `AppHostOptions`, `IShell` and the constructor order look like? | §3 |
| In what order do start-up and exit run? | §4 |
| How are processes launched, and how do native and network allowlist entries get added? | §5 |
| How is the smoke test structured, and what is its time budget? | §6 |
| What are the rules for settings markers, thread safety and durability? | §7 |
| Where do privacy, data deletion, compatibility, OCR end-to-end, soak, War Room layout and UI strings live? | §8 |
| Which requirements are MUST? | §9 |
| When is an area done? | §10 |
