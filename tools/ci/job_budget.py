#!/usr/bin/env python3
"""CI time budget (REQ-QSC-26, contract §6; docs/specs/08b-ci-release.md §7).

Usage: job_budget.py --label <job> --max-minutes <n> [--smoke-log smoke.log]

Reads JOB_START (epoch seconds, written by the job's first step), writes `| job | elapsed | budget |` to
$GITHUB_STEP_SUMMARY (or stdout) and exits 1 with `::error::<job> took 21.4 min (budget 20)` when over budget.
With --smoke-log it also lists the 5 slowest `[smoke] <section> ok elapsed_ms=<n>` sections and their total.
Exit 2 when JOB_START is missing or not a number. The step runs with `if: always()`, so the times show even after an
earlier failure. Standard library only.
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import time

SECTION = re.compile(r"\[smoke\]\s+(?P<section>\S+)\s+ok\s+elapsed_ms=(?P<ms>\d+)")


def smoke_sections(path: str) -> list[tuple[str, int]]:
    """Every `[smoke] <section> ok elapsed_ms=<n>` line, in file order."""
    try:
        with open(path, encoding="utf-8-sig", errors="replace") as f:
            text = f.read()
    except OSError:
        return []
    return [(m.group("section"), int(m.group("ms"))) for m in SECTION.finditer(text)]


def report(label: str, elapsed_min: float, budget: float, sections: list[tuple[str, int]] | None) -> str:
    lines = [f"### Time budget: {label}", "", "| job | elapsed | budget |", "| --- | ---: | ---: |",
             f"| {label} | {elapsed_min:.1f} min | {budget:g} min |"]
    if sections is not None:
        lines += ["", "Slowest smoke-test sections:", "", "| section | elapsed |", "| --- | ---: |"]
        for name, ms in sorted(sections, key=lambda s: s[1], reverse=True)[:5]:
            lines.append(f"| {name} | {ms / 1000:.1f} s |")
        lines.append(f"| **all {len(sections)} sections** | **{sum(ms for _, ms in sections) / 1000:.1f} s** |")
    return "\n".join(lines) + "\n"


def run(argv: list[str], now: float | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--label", required=True)
    parser.add_argument("--max-minutes", type=float, required=True)
    parser.add_argument("--smoke-log")
    args = parser.parse_args(argv)
    raw = os.environ.get("JOB_START", "").strip()
    try:
        start = float(raw)
    except ValueError:
        print(f"::error::JOB_START is not set to epoch seconds ({raw!r}); the job's first step must write it")
        return 2
    elapsed = ((now if now is not None else time.time()) - start) / 60
    sections = smoke_sections(args.smoke_log) if args.smoke_log else None
    text = report(args.label, elapsed, args.max_minutes, sections)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(text + "\n")
    print(text)
    if elapsed > args.max_minutes:
        print(f"::error::{args.label} took {elapsed:.1f} min (budget {args.max_minutes:g})")
        return 1
    print(f"{args.label}: {elapsed:.1f} min of {args.max_minutes:g}")
    return 0


if __name__ == "__main__":
    sys.exit(run(sys.argv[1:]))
