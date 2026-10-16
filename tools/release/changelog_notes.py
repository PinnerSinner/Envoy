#!/usr/bin/env python3
"""Release notes from CHANGELOG.md (REQ-DIST-6): prints the body of `## [<version>]` (a trailing " - date" is fine) up
to the next `## ` heading. A prerelease without its own section falls back to `## [Unreleased]`; a stable version
without a section exits 2, so a tag build fails before anything is uploaded.

Usage: changelog_notes.py CHANGELOG.md <version> [--out notes.md] | --self-test
"""
from __future__ import annotations

import os
import re
import sys
import tempfile

HEADING = re.compile(r"^##\s+\[([^\]]+)\]")


def sections(text: str) -> dict[str, str]:
    """`## [name]` → its body (stripped), in file order."""
    found: dict[str, str] = {}
    name = None
    body: list[str] = []
    for line in text.splitlines():
        if line.startswith("## "):
            if name is not None:
                found.setdefault(name, "\n".join(body).strip())
            m = HEADING.match(line)
            name = m.group(1).strip() if m else None
            body = []
        elif name is not None:
            body.append(line)
    if name is not None:
        found.setdefault(name, "\n".join(body).strip())
    return found


def notes(text: str, version: str) -> str | None:
    version = version.strip().lstrip("vV")
    found = sections(text)
    if found.get(version):
        return found[version]
    if "-" in version and found.get("Unreleased"):
        return found["Unreleased"]
    return None


SAMPLE = """# Changelog

## [Unreleased]

### Added
- Coming soon.

## [1.2.0] - 2026-10-01

### Fixed
- A fix.

## [1.1.0]
- Older.
"""


def self_test() -> int:
    checks = [
        ("exact match", notes(SAMPLE, "1.2.0") == "### Fixed\n- A fix."),
        ("v prefix", notes(SAMPLE, "v1.2.0") == "### Fixed\n- A fix."),
        ("last section", notes(SAMPLE, "1.1.0") == "- Older."),
        ("prerelease fallback", notes(SAMPLE, "1.3.0-beta.1") == "### Added\n- Coming soon."),
        ("prerelease own section", notes(SAMPLE.replace("## [1.1.0]", "## [1.3.0-beta.1]"), "1.3.0-beta.1") == "- Older."),
        ("missing stable", notes(SAMPLE, "1.3.0") is None),
        ("dated heading", notes("## [2.0.0] - 2027-01-01\nBody\n", "2.0.0") == "Body"),
    ]
    failures = [name for name, ok in checks if not ok]
    with tempfile.TemporaryDirectory() as tmp:
        path = os.path.join(tmp, "CHANGELOG.md")
        with open(path, "w", encoding="utf-8") as f:
            f.write(SAMPLE)
        if main([path, "1.3.0", "--out", os.path.join(tmp, "n.md")]) != 2:
            failures.append("missing stable exits 2")
        out = os.path.join(tmp, "notes.md")
        if main([path, "1.2.0", "--out", out]) != 0 or open(out, encoding="utf-8").read() != "### Fixed\n- A fix.\n":
            failures.append("writes --out")
    for name in failures:
        print(f"self-test case '{name}' failed")
    if failures:
        return 1
    print("changelog_notes self-test ok")
    return 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    out = None
    if "--out" in argv:
        i = argv.index("--out")
        out = argv[i + 1]
        argv = argv[:i] + argv[i + 2:]
    if len(argv) != 2:
        print(__doc__, file=sys.stderr)
        return 64
    with open(argv[0], encoding="utf-8") as f:
        text = f.read()
    body = notes(text, argv[1])
    if body is None:
        print(f"{argv[0]} has no '## [{argv[1].lstrip('vV')}]' section; add one before tagging a stable release.", file=sys.stderr)
        return 2
    if out:
        with open(out, "w", encoding="utf-8", newline="\n") as f:
            f.write(body + "\n")
    else:
        print(body)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
