# 08 — Quality, security & CI (split)
Status: Superseded. This area is split in two (`00-integration.md` §1). Requirement ids keep their `REQ-QSC-n` numbers.

- [`08a-quality-gates.md`](08a-quality-gates.md) — step 2, right after 01: `Directory.Build.props`, analyzers, `BannedSymbols.txt`,
  `tools/ci/check_policy.py` + `policy.json`, `Security/*` (`OutgoingText`, `SendTargetGuard`, `LaunchTarget`, `ShellLauncher`,
  `ReleaseUrlPolicy`, `PhraseLimits`), baseline warning fixes. REQ-QSC-1–11, 28–35.
- [`08b-ci-release.md`](08b-ci-release.md) — step 11, last: workflow restructure and `release` job, coverage gate, CodeQL, vulnerable
  packages, SBOM, time budget, OCR end-to-end test, threat model, release pass, compatibility matrix. REQ-QSC-12–27, 36–46.

This pointer is deleted in 08a's first implementation commit (contract: old files go when the split step starts).
