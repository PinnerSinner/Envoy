#!/usr/bin/env python3
"""Tag-only code signing (REQ-DIST-8): fails unless every `secrets.ENVOY_SIGN_` reference in the workflow sits inside a
step whose `if:` requires a v* tag, directly (`startsWith(github.ref, 'refs/tags/v')`) or through the tag-gated
`steps.sign.outputs.signing`, and the `windows` job's `environment:` is the tag expression.

Line-based, standard library only. Usage: check_signing_scope.py [workflow] | --self-test
"""
from __future__ import annotations

import os
import re
import sys
import tempfile

SECRET = "secrets.ENVOY_SIGN_"
TAG_CONDITION = "startsWith(github.ref, 'refs/tags/v')"
SIGN_OUTPUT = re.compile(r"steps\.sign\.outputs\.signing\s*==\s*'true'")
ENVIRONMENT = "${{ startsWith(github.ref, 'refs/tags/v') && 'release' || '' }}"
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


def check(path: str) -> list[str]:
    with open(path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    problems: list[str] = []
    jobs, steps = _parse(lines)

    windows = jobs.get("windows")
    if windows is None:
        problems.append(f"{path}:1: no `windows` job")
    else:
        start, end, indent = windows
        env = [(i, _value(lines[i].strip())) for i in range(start + 1, end)
               if _indent(lines[i]) == indent + 2 and lines[i].strip().startswith("environment:")]
        if not env:
            problems.append(f"{path}:{start + 1}: the windows job has no `environment:` line")
        elif env[0][1] != ENVIRONMENT:
            problems.append(f"{path}:{env[0][0] + 1}: the windows job's environment must be {ENVIRONMENT}")

    sign_steps = [s for s in steps if s["id"] == "sign"]
    sign_gated = bool(sign_steps) and all(TAG_CONDITION in (s["if"] or "") for s in sign_steps)
    if sign_steps and not sign_gated:
        problems.append(f"{path}:{sign_steps[0]['start'] + 1}: the `sign` step must run only on v* tags ({TAG_CONDITION})")

    for i, line in enumerate(lines):
        if SECRET not in line or line.strip().startswith("#"):
            continue
        step = next((s for s in steps if s["start"] <= i < s["end"]), None)
        if step is None:
            problems.append(f"{path}:{i + 1}: {SECRET}* is used outside a step")
            continue
        condition = step["if"] or ""
        if TAG_CONDITION in condition:
            continue
        if SIGN_OUTPUT.search(condition) and sign_gated:
            continue
        problems.append(f"{path}:{i + 1}: {SECRET}* is used in a step that can run without a v* tag (if: {condition or 'none'})")
    return problems


GOOD = """name: Build
on: [push]
jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - run: echo hi
  windows:
    runs-on: windows-latest
    environment: ${{ startsWith(github.ref, 'refs/tags/v') && 'release' || '' }}
    steps:
      - name: Signing setup
        id: sign
        if: startsWith(github.ref, 'refs/tags/v')
        env:
          PFX: ${{ secrets.ENVOY_SIGN_PFX_BASE64 }}
        run: echo setup
      - name: Sign
        if: steps.sign.outputs.signing == 'true'
        env:
          ENVOY_SIGN_PFX_PASSWORD: ${{ secrets.ENVOY_SIGN_PFX_PASSWORD }}
        run: echo sign
      - name: Other
        run: echo ${{ secrets.GITHUB_TOKEN }}
"""


def self_test() -> int:
    cases = {
        "good": (GOOD, True),
        "secret in an ungated step": (GOOD.replace("        if: steps.sign.outputs.signing == 'true'\n", ""), False),
        "secret in job env": (GOOD.replace("    environment:", "    env:\n      X: ${{ secrets.ENVOY_SIGN_PFX_PASSWORD }}\n    environment:"), False),
        "sign step not tag-gated": (GOOD.replace("        if: startsWith(github.ref, 'refs/tags/v')\n", "        if: always()\n"), False),
        "environment always release": (GOOD.replace("${{ startsWith(github.ref, 'refs/tags/v') && 'release' || '' }}", "release"), False),
        "no environment": (GOOD.replace("    environment: ${{ startsWith(github.ref, 'refs/tags/v') && 'release' || '' }}\n", ""), False),
        "secret in the check job": (GOOD.replace("      - run: echo hi", "      - run: echo ${{ secrets.ENVOY_SIGN_PFX_PASSWORD }}"), False),
        "secret in a later ungated step": (GOOD + "      - name: Leak\n        run: echo ${{ secrets.ENVOY_SIGN_PFX_BASE64 }}\n", False),
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
    print("signing scope ok: signing secrets are used only in v* tag steps")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
