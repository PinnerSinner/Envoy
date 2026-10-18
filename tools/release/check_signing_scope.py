#!/usr/bin/env python3
"""Tag-only code signing (REQ-DIST-8, REQ-QSC-23, contract §8.8): fails unless
- every `secrets.ENVOY_SIGN_` reference sits inside the `release` job;
- that job has `environment: release` and `if: startsWith(github.ref, 'refs/tags/v')` (job level);
- no other job declares an `environment:` (only the release environment holds the signing secrets).

Line-based, standard library only. Usage: check_signing_scope.py [workflow] | --self-test
"""
from __future__ import annotations

import os
import sys
import tempfile

SECRET = "secrets.ENVOY_SIGN_"
TAG_CONDITION = "startsWith(github.ref, 'refs/tags/v')"
RELEASE_JOB = "release"
ENVIRONMENT = "release"
DEFAULT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".github", "workflows", "build.yml")


def _indent(line: str) -> int:
    return len(line) - len(line.lstrip(" "))


def _value(line: str) -> str:
    value = line.split(":", 1)[1].strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
        value = value[1:-1]
    return value


def _parse(lines: list[str]):
    """Returns (jobs, steps): jobs maps a job name to its (start, end, indent); steps is a list of dicts with the
    job, the line range, `if` and `id`."""
    jobs: dict[str, tuple[int, int, int]] = {}
    steps: list[dict] = []
    starts: list[tuple[str, int, int]] = []
    in_jobs = False
    job_indent = None
    jobs_end = len(lines)
    for i, line in enumerate(lines):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        indent = _indent(line)
        if indent == 0:
            if in_jobs:
                jobs_end = i
            in_jobs = stripped == "jobs:"
            continue
        if not in_jobs:
            continue
        if job_indent is None:
            job_indent = indent
        if indent == job_indent and stripped.endswith(":"):
            starts.append((stripped[:-1], i, indent))
    for n, (name, start, indent) in enumerate(starts):
        end = starts[n + 1][1] if n + 1 < len(starts) else jobs_end
        jobs[name] = (start, end, indent)
    for name, (start, end, indent) in jobs.items():
        steps_indent = None
        step = None
        for i in range(start + 1, end):
            line = lines[i]
            stripped = line.strip()
            if not stripped or stripped.startswith("#"):
                continue
            ind = _indent(line)
            if stripped == "steps:":
                steps_indent = ind
                continue
            if steps_indent is None:
                continue
            if ind <= steps_indent:
                if step is not None:
                    step["end"] = i
                    steps.append(step)
                    step = None
                if ind <= indent + 2 and stripped != "steps:":
                    steps_indent = None
                continue
            if stripped.startswith("- ") and (step is None or ind <= step["indent"]):
                if step is not None:
                    step["end"] = i
                    steps.append(step)
                step = {"job": name, "start": i, "end": end, "indent": ind, "if": None, "id": None}
                stripped = stripped[2:].strip()
                ind += 2
            if step is not None and ind == step["indent"] + 2:
                if stripped.startswith("if:"):
                    step["if"] = _value(stripped)
                elif stripped.startswith("id:"):
                    step["id"] = _value(stripped)
        if step is not None:
            step["end"] = end
            steps.append(step)
    return jobs, steps


def _job_keys(lines: list[str], start: int, end: int, indent: int) -> dict[str, tuple[int, str]]:
    """The job's own keys (one level below its name): key -> (line index, value)."""
    keys: dict[str, tuple[int, str]] = {}
    for i in range(start + 1, end):
        line = lines[i]
        stripped = line.strip()
        if not stripped or stripped.startswith("#") or _indent(line) != indent + 2 or ":" not in stripped:
            continue
        key = stripped.split(":", 1)[0].strip()
        keys.setdefault(key, (i, _value(stripped)))
    return keys


def check(path: str) -> list[str]:
    with open(path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    problems: list[str] = []
    jobs, _ = _parse(lines)

    release = jobs.get(RELEASE_JOB)
    if release is None:
        problems.append(f"{path}:1: no `{RELEASE_JOB}` job")
    else:
        start, end, indent = release
        keys = _job_keys(lines, start, end, indent)
        if "environment" not in keys:
            problems.append(f"{path}:{start + 1}: the {RELEASE_JOB} job has no `environment: {ENVIRONMENT}` line")
        elif keys["environment"][1] != ENVIRONMENT:
            problems.append(f"{path}:{keys['environment'][0] + 1}: the {RELEASE_JOB} job's environment must be "
                            f"`{ENVIRONMENT}`, not `{keys['environment'][1]}`")
        if "if" not in keys or TAG_CONDITION not in keys["if"][1]:
            problems.append(f"{path}:{start + 1}: the {RELEASE_JOB} job must run only on v* tags (if: {TAG_CONDITION})")

    for name, (start, end, indent) in jobs.items():
        if name == RELEASE_JOB:
            continue
        keys = _job_keys(lines, start, end, indent)
        if "environment" in keys:
            problems.append(f"{path}:{keys['environment'][0] + 1}: only the {RELEASE_JOB} job may declare an "
                            f"environment (job `{name}` does)")

    first_job = min((s for s, _, _ in jobs.values()), default=len(lines))
    for i, line in enumerate(lines):
        if SECRET not in line or line.strip().startswith("#"):
            continue
        owner = next((n for n, (s, e, _) in jobs.items() if s <= i < e), None)
        if owner != RELEASE_JOB:
            where = f"the `{owner}` job" if owner else ("the workflow header" if i < first_job else "no job")
            problems.append(f"{path}:{i + 1}: {SECRET}* is used in {where}; signing secrets belong only in the "
                            f"tag-only `{RELEASE_JOB}` job")
    return problems


GOOD = """name: Build
on: [push]
permissions:
  contents: read
jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - run: echo hi
  windows:
    runs-on: windows-latest
    steps:
      - name: Other
        run: echo ${{ secrets.GITHUB_TOKEN }}
  release:
    if: startsWith(github.ref, 'refs/tags/v')
    needs: [windows]
    runs-on: windows-latest
    environment: release
    steps:
      - name: Signing setup
        id: sign
        env:
          PFX: ${{ secrets.ENVOY_SIGN_PFX_BASE64 }}
        run: echo setup
      - name: Sign
        if: steps.sign.outputs.signing == 'true'
        env:
          ENVOY_SIGN_PFX_PASSWORD: ${{ secrets.ENVOY_SIGN_PFX_PASSWORD }}
        run: echo sign
"""


def self_test() -> int:
    cases = {
        "good": (GOOD, True),
        "secret in the windows job": (GOOD.replace("        run: echo ${{ secrets.GITHUB_TOKEN }}",
                                                   "        run: echo ${{ secrets.ENVOY_SIGN_PFX_BASE64 }}"), False),
        "secret in the check job": (GOOD.replace("      - run: echo hi", "      - run: echo ${{ secrets.ENVOY_SIGN_PFX_PASSWORD }}"), False),
        "secret in workflow env": (GOOD.replace("permissions:\n", "env:\n  X: ${{ secrets.ENVOY_SIGN_PFX_PASSWORD }}\npermissions:\n"), False),
        "release without environment": (GOOD.replace("    environment: release\n", ""), False),
        "release in another environment": (GOOD.replace("    environment: release\n", "    environment: staging\n"), False),
        "release environment by expression": (GOOD.replace("    environment: release\n",
            "    environment: ${{ startsWith(github.ref, 'refs/tags/v') && 'release' || '' }}\n"), False),
        "release not tag-only": (GOOD.replace("    if: startsWith(github.ref, 'refs/tags/v')\n", ""), False),
        "release on every push": (GOOD.replace("    if: startsWith(github.ref, 'refs/tags/v')\n", "    if: always()\n"), False),
        "another job with an environment": (GOOD.replace("  windows:\n    runs-on: windows-latest\n",
                                                         "  windows:\n    runs-on: windows-latest\n    environment: release\n"), False),
        "no release job": (GOOD.replace("  release:\n", "  publish:\n"), False),
        "tag condition only on a step": (GOOD.replace("    if: startsWith(github.ref, 'refs/tags/v')\n", "")
                                         .replace("        id: sign\n", "        id: sign\n        if: startsWith(github.ref, 'refs/tags/v')\n"), False),
    }
    failures = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name, (text, ok) in cases.items():
            path = os.path.join(tmp, "build.yml")
            with open(path, "w", encoding="utf-8") as f:
                f.write(text)
            problems = check(path)
            if (not problems) != ok:
                failures += 1
                print(f"self-test case '{name}': expected {'pass' if ok else 'failure'}, got {problems or 'pass'}")
    real = check(DEFAULT)
    if real:
        failures += 1
        print("self-test: the real workflow fails:", *real, sep="\n  ")
    if failures:
        return 1
    print("check_signing_scope self-test ok")
    return 0


def main(argv: list[str]) -> int:
    if argv[1:] == ["--self-test"]:
        return self_test()
    path = argv[1] if len(argv) > 1 else DEFAULT
    problems = check(path)
    for p in problems:
        print(p)
    if problems:
        return 1
    print("signing scope ok: signing secrets are used only in the tag-only release job (environment: release)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
