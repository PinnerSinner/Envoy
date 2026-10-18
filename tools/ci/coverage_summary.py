#!/usr/bin/env python3
"""Envoy.Core coverage gate (REQ-QSC-14, 15; docs/specs/08b-ci-release.md §4).

Usage: coverage_summary.py --package Envoy.Core --min-line 70 --min-branch 55 <coverage.cobertura.xml>...

Reads Cobertura XML written by coverlet.collector, writes a Markdown table (the total, then one row per top-level
folder of the package) to $GITHUB_STEP_SUMMARY when it is set, otherwise to stdout, and exits:
  0  line and branch coverage are at or above both thresholds;
  1  either is below, with a `::error::` line naming the rate and the threshold;
  2  missing or invalid input, or no package with that name.
Standard library only. The thresholds are fixed by contract §9 (70 % lines, 55 % branches); never lower them.
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

CONDITION = re.compile(r"\((\d+)\s*/\s*(\d+)\)")


@dataclass
class Tally:
    lines: dict[tuple[str, int], int] = field(default_factory=dict)          # (file, line) -> hits
    branches: dict[tuple[str, int], tuple[int, int]] = field(default_factory=dict)  # (file, line) -> (covered, total)

    def add(self, file: str, number: int, hits: int, condition: str | None) -> None:
        key = (file, number)
        self.lines[key] = max(self.lines.get(key, 0), hits)
        if condition:
            match = CONDITION.search(condition)
            if match:
                covered, total = int(match.group(1)), int(match.group(2))
                old = self.branches.get(key, (0, 0))
                self.branches[key] = (max(old[0], covered), max(old[1], total))

    def rates(self, folder: str | None = None) -> tuple[float, float, int, int]:
        """(line rate, branch rate, lines valid, branches valid) for one folder, or for everything."""
        def mine(key: tuple[str, int]) -> bool:
            return folder is None or folder_of(key[0]) == folder
        lines = [hits for key, hits in self.lines.items() if mine(key)]
        branches = [cb for key, cb in self.branches.items() if mine(key)]
        valid = len(lines)
        covered = sum(1 for h in lines if h > 0)
        b_total = sum(t for _, t in branches)
        b_covered = sum(c for c, _ in branches)
        return (covered / valid if valid else 1.0, b_covered / b_total if b_total else 1.0, valid, b_total)


def folder_of(filename: str) -> str:
    """The first path segment of a class's file name; files at the package root are grouped as '(root)'."""
    parts = [p for p in re.split(r"[\\/]", filename) if p]
    return parts[0] if len(parts) > 1 else "(root)"


class InputError(Exception):
    pass


def read(paths: list[str], package: str) -> tuple[Tally, list[tuple[float, float]]]:
    """Merges every file's classes of `package`; also returns each file's own package rates."""
    tally = Tally()
    package_rates: list[tuple[float, float]] = []
    found = False
    for path in paths:
        try:
            root = ET.parse(path).getroot()
        except (OSError, ET.ParseError) as e:
            raise InputError(f"cannot read {path}: {e}") from e
        for pkg in root.iter("package"):
            if pkg.get("name") != package:
                continue
            found = True
            try:
                package_rates.append((float(pkg.get("line-rate", "")), float(pkg.get("branch-rate", ""))))
            except ValueError as e:
                raise InputError(f"{path}: package {package} has no numeric line-rate/branch-rate") from e
            for cls in pkg.iter("class"):
                filename = cls.get("filename", "")
                lines = cls.find("lines")
                for line in (lines if lines is not None else []):
                    try:
                        tally.add(filename, int(line.get("number", "0")), int(line.get("hits", "0")),
                                  line.get("condition-coverage"))
                    except ValueError as e:
                        raise InputError(f"{path}: a line of {filename} is not numeric") from e
    if not found:
        raise InputError(f"no package named {package} in {', '.join(paths)}")
    return tally, package_rates


def table(package: str, tally: Tally, totals: tuple[float, float], min_line: float, min_branch: float) -> str:
    def pct(rate: float) -> str:
        return f"{rate * 100:.1f}%"
    rows = [f"### {package} coverage", "",
            "| Area | Lines | Branches | Lines measured | Branches measured |",
            "| --- | ---: | ---: | ---: | ---: |"]
    _, _, valid, b_total = tally.rates()
    rows.append(f"| **Total** (gate {min_line:g}% / {min_branch:g}%) | **{pct(totals[0])}** | **{pct(totals[1])}** "
                f"| {valid} | {b_total} |")
    for folder in sorted({folder_of(f) for f, _ in tally.lines}):
        line, branch, valid, b_total = tally.rates(folder)
        rows.append(f"| {folder} | {pct(line)} | {pct(branch)} | {valid} | {b_total} |")
    return "\n".join(rows) + "\n"


def run(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--package", required=True)
    parser.add_argument("--min-line", type=float, required=True, help="minimum line coverage in percent")
    parser.add_argument("--min-branch", type=float, required=True, help="minimum branch coverage in percent")
    parser.add_argument("files", nargs="*")
    args = parser.parse_args(argv)
    files = [f for f in args.files if os.path.isfile(f)]
    if not files:
        print(f"::error::no coverage file found ({' '.join(args.files) or 'none given'})")
        return 2
    try:
        tally, package_rates = read(files, args.package)
    except InputError as e:
        print(f"::error::{e}")
        return 2
    # One file: the collector's own package totals. Several: the merged count of unique lines and branches.
    if len(package_rates) == 1:
        totals = package_rates[0]
    else:
        line, branch, _, _ = tally.rates()
        totals = (line, branch)

    text = table(args.package, tally, totals, args.min_line, args.min_branch)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(text + "\n")
    print(text)

    failed = False
    line_pct, branch_pct = totals[0] * 100, totals[1] * 100
    # Compare at the printed precision, so "70.0%" never fails a 70 % gate through rounding.
    if round(line_pct, 1) < args.min_line:
        print(f"::error::{args.package} line coverage {line_pct:.1f}% is below {args.min_line:g}%")
        failed = True
    if round(branch_pct, 1) < args.min_branch:
        print(f"::error::{args.package} branch coverage {branch_pct:.1f}% is below {args.min_branch:g}%")
        failed = True
    if not failed:
        print(f"{args.package} coverage ok: lines {line_pct:.1f}% (gate {args.min_line:g}%), "
              f"branches {branch_pct:.1f}% (gate {args.min_branch:g}%)")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(run(sys.argv[1:]))
