#!/usr/bin/env python3
"""The last gate before a release is published (REQ-DIST-2, 3, 5, 7): the GitHub release must still be a draft, hold
exactly the expected assets (a delta package only when a previous release was found, and the CycloneDX SBOM
Envoy.cdx.json, REQ-QSC-21), and SHA256SUMS.txt must match
the local Envoy.exe and Envoy-win-Setup.exe.

Standard library only; reads the release with `gh release view <tag> --json assets,isDraft`. The prerelease flag is
set by the publish step itself (`gh release edit --prerelease`).
Usage:
  check_release_assets.py --tag v1.2.0 --version 1.2.0 --delta true|false --exe publish/Envoy.exe
                          --setup Releases/Envoy-win-Setup.exe --sums SHA256SUMS.txt [--repo owner/repo] [--json file]
  check_release_assets.py --self-test
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile


def expected_assets(version: str, delta: bool) -> set[str]:
    names = {"Envoy.exe", "Envoy.exe.sha256", "SHA256SUMS.txt", "Envoy-win-Setup.exe", f"Envoy-{version}-full.nupkg",
             "releases.win.json", "RELEASES", "Envoy.cdx.json"}
    if delta:
        names.add(f"Envoy-{version}-delta.nupkg")
    return names


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_sums(path: str) -> dict[str, str]:
    """`<hash>  <name>` lines (sha256sum format; a `*` before the name marks binary mode)."""
    sums: dict[str, str] = {}
    with open(path, encoding="utf-8") as f:
        for n, line in enumerate(f.read().splitlines(), 1):
            if not line.strip():
                continue
            parts = line.split(None, 1)
            if len(parts) != 2 or len(parts[0]) != 64:
                raise ValueError(f"{path}:{n}: not a sha256sum line")
            sums[parts[1].lstrip("*").strip()] = parts[0].lower()
    return sums


def check(release: dict, tag: str, version: str, delta: bool, exe: str, setup: str, sums_path: str) -> list[str]:
    problems: list[str] = []
    if not release.get("isDraft", False):
        problems.append(f"release {tag} is not a draft; it must stay one until this check passes")
    names = [a.get("name", "") for a in release.get("assets", [])]
    expected = expected_assets(version, delta)
    missing = sorted(expected - set(names))
    extra = sorted(set(names) - expected)
    duplicates = sorted({n for n in names if names.count(n) > 1})
    if missing:
        problems.append("missing assets: " + ", ".join(missing))
    if extra:
        problems.append("unexpected assets: " + ", ".join(extra))
    if duplicates:
        problems.append("duplicate assets: " + ", ".join(duplicates))
    try:
        sums = read_sums(sums_path)
    except (OSError, ValueError) as e:
        return problems + [f"SHA256SUMS.txt unreadable: {e}"]
    if set(sums) != {"Envoy.exe", "Envoy-win-Setup.exe"}:
        problems.append("SHA256SUMS.txt must list exactly Envoy.exe and Envoy-win-Setup.exe, found: " + ", ".join(sorted(sums)))
    for name, path in (("Envoy.exe", exe), ("Envoy-win-Setup.exe", setup)):
        if not os.path.isfile(path):
            problems.append(f"{path} does not exist")
        elif name in sums and sha256(path) != sums[name]:
            problems.append(f"SHA256SUMS.txt does not match {path}")
    return problems


def gh_release(tag: str, repo: str | None) -> dict:
    cmd = ["gh", "release", "view", tag, "--json", "assets,isDraft"]
    if repo:
        cmd += ["--repo", repo]
    return json.loads(subprocess.run(cmd, check=True, capture_output=True, text=True).stdout)


def self_test() -> int:
    failures = 0
    with tempfile.TemporaryDirectory() as tmp:
        exe = os.path.join(tmp, "Envoy.exe")
        setup = os.path.join(tmp, "Envoy-win-Setup.exe")
        sums = os.path.join(tmp, "SHA256SUMS.txt")
        with open(exe, "wb") as f:
            f.write(b"portable")
        with open(setup, "wb") as f:
            f.write(b"installer")
        with open(sums, "w", encoding="utf-8", newline="\n") as f:
            f.write(f"{sha256(exe)}  Envoy.exe\n{sha256(setup)}  Envoy-win-Setup.exe\n")

        def release(names, draft=True, pre=False):
            return {"isDraft": draft, "isPrerelease": pre, "assets": [{"name": n} for n in names]}

        full = sorted(expected_assets("1.2.0", True))
        cases = [
            ("complete with delta", release(full), "v1.2.0", True, True),
            ("complete without delta", release(sorted(expected_assets("1.2.0", False))), "v1.2.0", False, True),
            ("prerelease", release(sorted(expected_assets("1.3.0-beta.1", False)), pre=True), "v1.3.0-beta.1", False, True),
            ("already published", release(full, draft=False), "v1.2.0", True, False),
            ("missing setup", release([n for n in full if n != "Envoy-win-Setup.exe"]), "v1.2.0", True, False),
            ("missing SBOM", release([n for n in full if n != "Envoy.cdx.json"]), "v1.2.0", True, False),
            ("missing delta", release(sorted(expected_assets("1.2.0", False))), "v1.2.0", True, False),
            ("extra asset", release(full + ["Envoy-win-Portable.zip"]), "v1.2.0", True, False),
            ("duplicate asset", release(full + ["Envoy.exe"]), "v1.2.0", True, False),
        ]
        for name, rel, tag, delta, ok in cases:
            version = tag[1:]
            problems = check(rel, tag, version, delta, exe, setup, sums)
            if (not problems) != ok:
                failures += 1
                print(f"self-test case '{name}': expected {'pass' if ok else 'failure'}, got {problems or 'pass'}")

        with open(exe, "ab") as f:
            f.write(b"tampered")
        if not check(release(full), "v1.2.0", "1.2.0", True, exe, setup, sums):
            failures += 1
            print("self-test case 'checksum mismatch': passed")
        with open(sums, "w", encoding="utf-8") as f:
            f.write("not a checksum line\n")
        if not check(release(full), "v1.2.0", "1.2.0", True, exe, setup, sums):
            failures += 1
            print("self-test case 'bad SHA256SUMS.txt': passed")
    if failures:
        return 1
    print("check_release_assets self-test ok")
    return 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--tag", required=True)
    p.add_argument("--version", required=True)
    p.add_argument("--delta", required=True, choices=["true", "false"])
    p.add_argument("--exe", required=True)
    p.add_argument("--setup", required=True)
    p.add_argument("--sums", required=True)
    p.add_argument("--repo")
    p.add_argument("--json", help="read the release JSON from this file instead of calling gh")
    args = p.parse_args(argv)
    if args.json:
        with open(args.json, encoding="utf-8") as f:
            release = json.load(f)
    else:
        release = gh_release(args.tag, args.repo)
    problems = check(release, args.tag, args.version, args.delta == "true", args.exe, args.setup, args.sums)
    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print(f"release {args.tag}: draft with the {len(expected_assets(args.version, args.delta == 'true'))} expected assets; checksums match")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
