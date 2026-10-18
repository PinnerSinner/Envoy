#!/usr/bin/env python3
"""Vulnerable-package gate (REQ-QSC-20; docs/specs/08b-ci-release.md §5).

Usage:
  check_vulnerable.py <project>...            runs `dotnet list <project> package --vulnerable --include-transitive
                                              --format json` for each project (already restored by earlier steps)
  check_vulnerable.py --from-json <file>...   parses saved output instead (the unit tests; no network)

Fails (exit 1) with one `::error::` line per package, naming its version, severity and advisory URL, when any advisory
is High or Critical. Moderate and Low advisories are listed (stdout and $GITHUB_STEP_SUMMARY) and do not fail.
Exit 2 when `dotnet list` fails or its output cannot be read. Standard library only.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from dataclasses import dataclass

FAILING = {"high", "critical"}


@dataclass(frozen=True)
class Advisory:
    project: str
    package: str
    version: str
    severity: str
    url: str
    transitive: bool

    def describe(self) -> str:
        kind = "transitive" if self.transitive else "direct"
        return f"{self.package} {self.version} ({kind}, {self.project}): {self.severity} advisory {self.url}"


class InputError(Exception):
    pass


def parse(report: dict) -> list[Advisory]:
    """Advisories in one `dotnet list package --vulnerable --format json` report."""
    if not isinstance(report, dict) or not isinstance(report.get("projects"), list):
        raise InputError("not a `dotnet list package --format json` report (no projects list)")
    problems = [p for p in report.get("problems", []) if isinstance(p, dict) and p.get("level") == "error"]
    if problems:
        raise InputError("; ".join(str(p.get("text", "")) for p in problems))
    found: list[Advisory] = []
    for project in report["projects"]:
        path = str(project.get("path", "?"))
        for framework in project.get("frameworks", []) or []:
            for key, transitive in (("topLevelPackages", False), ("transitivePackages", True)):
                for package in framework.get(key, []) or []:
                    for vulnerability in package.get("vulnerabilities", []) or []:
                        found.append(Advisory(path, str(package.get("id", "?")),
                                              str(package.get("resolvedVersion") or package.get("requestedVersion") or "?"),
                                              str(vulnerability.get("severity", "Unknown")),
                                              str(vulnerability.get("advisoryurl") or vulnerability.get("advisoryUrl") or ""),
                                              transitive))
    return sorted(set(found), key=lambda a: (a.package, a.version, a.url, a.project))


def list_project(project: str) -> dict:
    cmd = ["dotnet", "list", project, "package", "--vulnerable", "--include-transitive", "--format", "json"]
    result = subprocess.run(cmd, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise InputError(f"`{' '.join(cmd)}` exited {result.returncode}: {(result.stderr or result.stdout).strip()[:500]}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as e:
        raise InputError(f"`{' '.join(cmd)}` printed no JSON: {e}") from e


def run(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--from-json", action="store_true", help="the arguments are saved JSON reports")
    parser.add_argument("inputs", nargs="+")
    args = parser.parse_args(argv)
    advisories: list[Advisory] = []
    try:
        for item in args.inputs:
            if args.from_json:
                try:
                    with open(item, encoding="utf-8-sig") as f:
                        report = json.load(f)
                except (OSError, json.JSONDecodeError) as e:
                    raise InputError(f"cannot read {item}: {e}") from e
            else:
                report = list_project(item)
            advisories.extend(parse(report))
    except InputError as e:
        print(f"::error::vulnerable-package check could not run: {e}")
        return 2

    failing = [a for a in advisories if a.severity.lower() in FAILING]
    listed = [a for a in advisories if a.severity.lower() not in FAILING]
    for a in failing:
        print(f"::error::{a.describe()}")
    for a in listed:
        print(f"::warning::{a.describe()}")

    lines = ["### Vulnerable packages", ""]
    if not advisories:
        lines.append(f"No known vulnerabilities in {', '.join(args.inputs)}.")
    else:
        lines += ["| Package | Version | Severity | Advisory | Fails? |", "| --- | --- | --- | --- | --- |"]
        lines += [f"| {a.package} | {a.version} | {a.severity} | {a.url} | {'yes' if a in failing else 'no'} |"
                  for a in advisories]
    text = "\n".join(lines) + "\n"
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(text + "\n")
    print(text)
    if failing:
        print(f"{len(failing)} High or Critical advisory(ies): update or replace the package before merging.")
        return 1
    print("vulnerable packages ok: no High or Critical advisories")
    return 0


if __name__ == "__main__":
    sys.exit(run(sys.argv[1:]))
