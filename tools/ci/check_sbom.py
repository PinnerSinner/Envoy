#!/usr/bin/env python3
"""SBOM presence check (REQ-QSC-21; docs/specs/08b-ci-release.md §6).

Usage: check_sbom.py <Envoy.cdx.json> --require <component name>...

Parses a CycloneDX JSON SBOM and fails (exit 1, one `::error::` line per name) when a required component name is
absent, so a broken SBOM shows up on every Windows build instead of at a tag. Exit 2 when the file is missing or is
not a CycloneDX JSON document. Names compare case-insensitively; nested components count. Standard library only.
"""
from __future__ import annotations

import argparse
import json
import sys


def component_names(bom: dict) -> set[str]:
    names: set[str] = set()

    def walk(components) -> None:
        for c in components or []:
            if isinstance(c, dict):
                if isinstance(c.get("name"), str):
                    names.add(c["name"].lower())
                walk(c.get("components"))

    walk(bom.get("components"))
    return names


def run(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("sbom")
    parser.add_argument("--require", nargs="+", required=True, metavar="NAME")
    args = parser.parse_args(argv)
    try:
        with open(args.sbom, encoding="utf-8-sig") as f:
            bom = json.load(f)
    except (OSError, json.JSONDecodeError) as e:
        print(f"::error::cannot read the SBOM {args.sbom}: {e}")
        return 2
    if not isinstance(bom, dict) or bom.get("bomFormat") != "CycloneDX":
        print(f"::error::{args.sbom} is not a CycloneDX JSON SBOM (bomFormat is not CycloneDX)")
        return 2
    names = component_names(bom)
    missing = [n for n in args.require if n.lower() not in names]
    for name in missing:
        print(f"::error::the SBOM {args.sbom} has no component named {name}")
    if missing:
        return 1
    print(f"SBOM ok: {len(names)} components, including {', '.join(args.require)}")
    return 0


if __name__ == "__main__":
    sys.exit(run(sys.argv[1:]))
