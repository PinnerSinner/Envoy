#!/usr/bin/env python3
"""Renders the winget manifests (REQ-DIST-28): fills the `{{name}}` placeholders of packaging/winget/*.yaml into
<out>/manifests/p/PinnerSinner/Envoy/<version>/. Refuses a prerelease version and fails on any placeholder left over.

Usage: render_winget.py --version 1.2.0 --installer-url <url> --sha256 <hex> --out <dir> [--date YYYY-MM-DD]
       render_winget.py --self-test
"""
from __future__ import annotations

import argparse
import datetime
import glob
import os
import re
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
TEMPLATES = os.path.join(HERE, "..", "..", "packaging", "winget")
STABLE = re.compile(r"^\d+\.\d+\.\d+$")
SHA = re.compile(r"^[0-9a-fA-F]{64}$")
PLACEHOLDER = re.compile(r"\{\{\s*([a-z0-9_]+)\s*\}\}")


def render(version: str, installer_url: str, sha256: str, out: str, date: str, templates: str = TEMPLATES) -> list[str]:
    version = version.lstrip("vV")
    if not STABLE.match(version):
        raise ValueError(f"winget gets stable releases only, not {version}")
    if not SHA.match(sha256):
        raise ValueError("the installer SHA-256 must be 64 hex characters")
    if not installer_url.startswith("https://github.com/") or not installer_url.endswith("/Envoy-win-Setup.exe"):
        raise ValueError("the installer URL must be the release's Envoy-win-Setup.exe on github.com")
    values = {
        "version": version,
        "installer_url": installer_url,
        "installer_sha256": sha256.upper(),
        "release_date": date,
        "release_notes_url": f"https://github.com/PinnerSinner/Envoy/releases/tag/v{version}",
    }
    target = os.path.join(out, "manifests", "p", "PinnerSinner", "Envoy", version)
    os.makedirs(target, exist_ok=True)
    written = []
    files = sorted(glob.glob(os.path.join(templates, "*.yaml")))
    if not files:
        raise ValueError(f"no templates in {templates}")
    for path in files:
        with open(path, encoding="utf-8") as f:
            text = f.read()
        text = PLACEHOLDER.sub(lambda m: values.get(m.group(1), m.group(0)), text)
        if "{{" in text or "}}" in text:
            raise ValueError(f"{os.path.basename(path)}: placeholder left over")
        dest = os.path.join(target, os.path.basename(path))
        with open(dest, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        written.append(dest)
    return written


def self_test() -> int:
    failures = []
    sha = "a" * 64
    url = "https://github.com/PinnerSinner/Envoy/releases/download/v1.2.0/Envoy-win-Setup.exe"
    with tempfile.TemporaryDirectory() as tmp:
        files = render("v1.2.0", url, sha, tmp, "2026-10-14")
        if len(files) != 3:
            failures.append(f"expected 3 manifests, got {len(files)}")
        for path in files:
            text = open(path, encoding="utf-8").read()
            if "{{" in text or "PackageVersion: 1.2.0" not in text:
                failures.append(f"{os.path.basename(path)} not rendered")
        installer = next((p for p in files if p.endswith(".installer.yaml")), "")
        if installer and ("InstallerSha256: " + "A" * 64) not in open(installer, encoding="utf-8").read():
            failures.append("installer hash missing")
        if not os.path.isdir(os.path.join(tmp, "manifests", "p", "PinnerSinner", "Envoy", "1.2.0")):
            failures.append("wrong output folder")
        for name, args in {
            "prerelease": ("1.3.0-beta.1", url, sha),
            "bad hash": ("1.2.0", url, "xyz"),
            "foreign url": ("1.2.0", "https://example.invalid/Envoy-win-Setup.exe", sha),
        }.items():
            try:
                render(*args, out=tmp, date="2026-10-14")
                failures.append(f"{name} was accepted")
            except ValueError:
                pass
        bad = os.path.join(tmp, "templates")
        os.makedirs(bad)
        with open(os.path.join(bad, "x.yaml"), "w", encoding="utf-8") as f:
            f.write("PackageVersion: {{version}}\nOops: {{unknown}}\n")
        try:
            render("1.2.0", url, sha, os.path.join(tmp, "o"), "2026-10-14", templates=bad)
            failures.append("a leftover placeholder was accepted")
        except ValueError:
            pass
    for f in failures:
        print("self-test:", f)
    if failures:
        return 1
    print("render_winget self-test ok")
    return 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--version", required=True)
    p.add_argument("--installer-url", required=True)
    p.add_argument("--sha256", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--date", default=datetime.date.today().isoformat())
    a = p.parse_args(argv)
    try:
        for path in render(a.version, a.installer_url, a.sha256, a.out, a.date):
            print(path)
    except ValueError as e:
        print(f"::error::{e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
