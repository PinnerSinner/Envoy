# Recommended repository settings

The workflows enforce least privilege inside CI, but some protections live only in the repository's settings on
GitHub. This page lists them (REQ-QSC-39, docs/specs/08b-ci-release.md); the maintainer applies them by hand and
re-checks them before each minor release. `gh api repos/PinnerSinner/Envoy/rulesets` should list the `main` and `v*`
rulesets below.

## Rulesets

**`main` (branch ruleset, target the default branch):**

- Require a pull request before merging (at least the maintainer's review for paths in `.github/CODEOWNERS`; require
  review from Code Owners).
- Required status checks: `check`, `windows`, `app-tests` (Build), `analyze (csharp)` (CodeQL) and `review`
  (Dependency review).
- Block force pushes and deletions.
- Require linear history.

**`v*` (tag ruleset):**

- Only maintainers may create matching tags (bypass list: the maintainer role only).
- Nobody may update or delete them: a published tag is never moved (see [releasing](../releasing.md#tag)).

## Code security

- Private vulnerability reporting: **on** ([SECURITY.md](../../SECURITY.md)).
- Dependabot alerts and Dependabot security updates: **on**. Version updates come from `.github/dependabot.yml` (NuGet
  for `/` and `/tools/typecheck`, and GitHub Actions).
- Secret scanning with **push protection**: on.
- Code scanning: **default setup off**. `.github/workflows/codeql.yml` (advanced setup) runs instead, with
  `security-extended` queries for C# and the workflows.

## Actions

- Workflow permissions (default `GITHUB_TOKEN`): **read repository contents only**. Each workflow also declares
  `permissions: contents: read`; only the `screenshots` and `release` jobs ask for `contents: write`, and CodeQL's
  `analyze` for `security-events: write` (the policy rule `workflow-permissions` checks this).
- **Do not** allow GitHub Actions to create or approve pull requests.
- Fork pull requests: require approval for first-time contributors.
- Allowed actions: GitHub-owned actions only (`actions/*`, `github/*`), which covers every action the workflows use;
  they are pinned to commit SHAs (policy rule `workflow-pins`).

## The `release` environment

- Settings › Environments › `release`: **required reviewers** (the maintainer) and **deployment branches and tags**
  limited to tags matching `v*`.
- Environment secrets: `ENVOY_SIGN_PFX_BASE64` and `ENVOY_SIGN_PFX_PASSWORD` (see
  [code signing](../releasing.md#code-signing)). No repository-level secrets hold signing material.

## Accounts

- Two-factor authentication is required for every account with write access (organisation setting, or checked by hand
  for collaborators).
- Keep the number of accounts with write or admin access to the minimum; remove access that is no longer used.
