#!/usr/bin/env python3
"""Envoy's policy check: makes the product promises mechanical (docs/specs/08a-quality-gates.md §4).

Usage: python3 tools/ci/check_policy.py [--root <repo>] [--policy <policy.json>]

Prints one `path:line: rule: message` line per violation and exits 1 if there are any, 0 otherwise.
Standard library only. The allowlists live in tools/ci/policy.json; contract §5 says who may add to them.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Iterator

SKIP_DIRS = {"bin", "obj", ".git", ".vs", ".idea", "node_modules", "publish", "TestResults"}
SKIP_PREFIXES = ("tools/typecheck/stubs/",)
PROJECT_SUFFIXES = (".csproj", ".props", ".targets")
EDITORCONFIG_NAMES = (".editorconfig", ".globalconfig")
# Properties that, set to false, switch analyzers (RS0030 included) or the root Directory.Build.props off.
ANALYZER_SWITCHES = ("RunAnalyzers", "RunAnalyzersDuringBuild", "EnableNETAnalyzers", "ImportDirectoryBuildProps")
ANALYZER_ITEMS = ("Microsoft.CodeAnalysis.BannedApiAnalyzers", "BannedSymbols.txt")

APP_PROJECT = "src/Envoy.App/Envoy.App.csproj"
NULLABLE_PROJECTS = ("src/Envoy.Core/Envoy.Core.csproj", "tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj")
REQUIRED_PROPS = {
    "LangVersion": "latest",
    "Nullable": "enable",
    "ImplicitUsings": "enable",
    "Deterministic": "true",
    "AnalysisLevel": "8.0-recommended",
    "EnforceCodeStyleInBuild": "true",
}

# The start of an import attribute that sits alone in its own [ ] section, with an optional target and a qualified
# name: [DllImport(, [method: LibraryImportAttribute(, [global::System.Runtime.InteropServices.DllImport(.
NATIVE_ATTRIBUTE = re.compile(
    r"\[\s*(?:\w+\s*:(?!:)\s*)?(?:global\s*::\s*)?(?:\w+\s*\.\s*)*(?P<kind>DllImport|LibraryImport)(?:Attribute)?\s*\(")
# Any mention of the import attributes; each one must be an attribute the parser above resolves.
NATIVE_WORD = re.compile(r"\b(?:DllImport|LibraryImport)(?:Attribute)?\b")
NATIVE_LIBRARY = re.compile(r'\(\s*"([^"\\]+)"\s*[,)]')
# The declaration directly after the attribute: modifiers in any order, a return type, then the method name.
NATIVE_DECLARATION = re.compile(
    r"(?:(?:public|internal|private|protected|static|extern|partial|unsafe|new)\s+)+"
    r"(?:global\s*::\s*)?[\w.]+(?:\s*<[^;{}()]*>)?\??(?:\s*\*)*(?:\s*\[\s*\])*"
    r"(?:\s+|(?<=[*\]>])\s*)(\w+)\s*\(")
ENTRY_POINT = re.compile(r'\bEntryPoint\s*=\s*"([^"\\]+)"')
ENTRY_POINT_ANY = re.compile(r"\bEntryPoint\s*=")
HOST = re.compile(r"https?://([A-Za-z0-9.-]+)")
PROCESS_LAUNCH = re.compile(r"\bProcess\s*\.\s*Start\b|\bProcessStartInfo\b|\bUseShellExecute\b")
PRAGMA_DISABLE = re.compile(r"#\s*pragma\s+warning\s+disable\b(.*)")
SUPPRESS_MESSAGE = re.compile(r"\bSuppressMessage(?:Attribute)?\s*\(")
RS0030_SEVERITY = re.compile(r"^\s*dotnet_diagnostic\.RS0030\.severity\s*=\s*(\w+)", re.IGNORECASE | re.MULTILINE)


@dataclass(frozen=True)
class Violation:
    path: str
    line: int
    rule: str
    message: str

    def __str__(self) -> str:
        return f"{self.path}:{self.line}: {self.rule}: {self.message}"


def _rel(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def _walk(root: Path, start: Path, predicate) -> Iterator[Path]:
    if not start.exists():
        return
    for path in sorted(start.rglob("*")):
        rel = _rel(root, path)
        parts = path.relative_to(root).parts
        if any(p in SKIP_DIRS for p in parts) or any(rel.startswith(p) for p in SKIP_PREFIXES):
            continue
        if path.is_file() and predicate(path):
            yield path


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace")


def _line_of(text: str, index: int) -> int:
    return text.count("\n", 0, index) + 1


def _whole_word(words: Iterable[str]) -> re.Pattern[str]:
    return re.compile(r"\b(" + "|".join(re.escape(w) for w in sorted(set(words), key=len, reverse=True)) + r")\b")


# ---------------------------------------------------------------------------------------------------------------------
# Rules over src/**/*.cs (and *.xaml for hosts)

def _skip_trivia(text: str, i: int) -> int:
    """Skips whitespace, comments and further attribute sections between an import attribute and its method."""
    while i < len(text):
        if text[i].isspace():
            i += 1
        elif text.startswith("//", i):
            newline = text.find("\n", i)
            i = len(text) if newline < 0 else newline + 1
        elif text.startswith("/*", i):
            close = text.find("*/", i + 2)
            i = len(text) if close < 0 else close + 2
        elif text[i] == "[":
            i += len(_call_text(text, i, "[", "]"))
        else:
            break
    return i


def _resolve_native(text: str, match: re.Match[str]) -> tuple[str | None, str]:
    """(key, "") for an import attribute that resolves to dll!EntryPoint, otherwise (None, why it doesn't)."""
    args = _call_text(text, match.end() - 1)
    library = NATIVE_LIBRARY.match(args)
    if not library:
        return None, "its library name must be a string literal"
    after = match.end() - 1 + len(args)
    close = re.compile(r"\s*\]").match(text, after)
    if not close:
        return None, "it must sit alone in its own [ ] section"
    entry = ENTRY_POINT.search(args)
    if ENTRY_POINT_ANY.search(args) and not entry:
        return None, "its EntryPoint must be a string literal"
    decl = NATIVE_DECLARATION.match(text, _skip_trivia(text, close.end()))
    if not decl:
        return None, "it must be directly followed by its static extern or static partial method"
    dll = library.group(1).strip().lower()
    if dll.endswith(".dll"):
        dll = dll[:-4]
    return f"{dll}!{entry.group(1) if entry else decl.group(1)}", ""


def check_native(root: Path, policy: dict, sources: dict[str, str]) -> list[Violation]:
    """native-unlisted and native-denied (REQ-QSC-8)."""
    out: list[Violation] = []
    allowed = set(policy["native"]["allowed"])
    denied = _whole_word(policy["native"]["denied"])
    for rel, text in sources.items():
        reasons: dict[int, str] = {}  # where each import attribute's name starts → why it doesn't resolve ("" if it does)
        for match in NATIVE_ATTRIBUTE.finditer(text):
            key, reasons[match.start("kind")] = _resolve_native(text, match)
            if key is not None and key not in allowed:
                out.append(Violation(rel, _line_of(text, match.start()), "native-unlisted",
                                     f"P/Invoke {key} is not in policy.json native.allowed; add it there and to "
                                     "Native.cs in the same change (contract §5)"))
        for match in NATIVE_WORD.finditer(text):
            why = reasons.get(match.start(), "write it as [DllImport(\"x.dll\")] or [LibraryImport(\"x.dll\")], "
                                             "alone in its [ ] section, directly above the method; "
                                             "comments must not name these attributes")
            if why:
                out.append(Violation(rel, _line_of(text, match.start()), "native-unlisted",
                                     f"'{match.group(0)}' cannot be checked against policy.json native.allowed: {why}"))
        for number, line in enumerate(text.splitlines(), 1):
            for word in denied.findall(line):
                out.append(Violation(rel, number, "native-denied",
                                     f"'{word}' is on the denied list (process memory, remote threads, hooks, debugging, "
                                     "dynamic loading); Envoy never uses it, and comments must not name it either"))
    return out


def check_network(root: Path, policy: dict, sources: dict[str, str]) -> list[Violation]:
    """network-location (REQ-QSC-9)."""
    out: list[Violation] = []
    types = _whole_word(policy["network"]["types"])
    allowed = tuple(policy["network"]["allowedPaths"])
    for rel, text in sources.items():
        if rel.startswith(allowed):
            continue
        for number, line in enumerate(text.splitlines(), 1):
            for word in types.findall(line):
                out.append(Violation(rel, number, "network-location",
                                     f"network type '{word}' outside {', '.join(allowed)} (contract §5)"))
    return out


def check_hosts(root: Path, policy: dict, sources: dict[str, str], xaml: dict[str, str]) -> list[Violation]:
    """host (REQ-QSC-10)."""
    out: list[Violation] = []
    known = {h.lower() for h in policy["hosts"]["allowed"]} | {h.lower() for h in policy["hosts"]["ignored"]}
    for rel, text in {**sources, **xaml}.items():
        for match in HOST.finditer(text):
            host = match.group(1).lower().rstrip(".")
            if host not in known:
                out.append(Violation(rel, _line_of(text, match.start()), "host",
                                     f"host '{host}' is not in policy.json hosts.allowed; add it in the same change "
                                     "(contract §5: 03 and 06 only)"))
    return out


def check_process_launch(root: Path, policy: dict, sources: dict[str, str]) -> list[Violation]:
    """process-launch (REQ-QSC-30): a text backstop for BannedSymbols.txt."""
    out: list[Violation] = []
    launcher = policy["launcher"]
    for rel, text in sources.items():
        if rel == launcher:
            continue
        for number, line in enumerate(text.splitlines(), 1):
            if PROCESS_LAUNCH.search(line):
                out.append(Violation(rel, number, "process-launch",
                                     f"processes start only in {launcher} (contract §5)"))
    return out


def _call_text(text: str, open_paren: int, opener: str = "(", closer: str = ")") -> str:
    """The text from an opening bracket to its match (a call's '(' to ')' by default), skipping string literals."""
    depth, i, in_string = 0, open_paren, False
    while i < len(text):
        c = text[i]
        if in_string:
            if c == "\\":
                i += 1
            elif c == '"':
                in_string = False
        elif c == '"':
            in_string = True
        elif c == opener:
            depth += 1
        elif c == closer:
            depth -= 1
            if depth == 0:
                return text[open_paren:i + 1]
        i += 1
    return text[open_paren:]


def check_pragmas(root: Path, policy: dict, all_cs: dict[str, str]) -> list[Violation]:
    """pragma (REQ-QSC-4, REQ-QSC-30)."""
    out: list[Violation] = []
    launcher = policy["launcher"]
    launcher_pragmas = 0
    for rel, text in all_cs.items():
        for number, line in enumerate(text.splitlines(), 1):
            match = PRAGMA_DISABLE.search(line)
            if not match:
                continue
            ids = [i for i in re.split(r"[\s,]+", match.group(1).split("//", 1)[0].strip()) if i]
            if rel != launcher or ids != ["RS0030"]:
                out.append(Violation(rel, number, "pragma",
                                     "no '#pragma warning disable' except the one RS0030 line in " + launcher +
                                     "; fix the code or use [SuppressMessage(..., Justification = \"...\")]"))
            else:
                launcher_pragmas += 1
                if launcher_pragmas > 1:
                    out.append(Violation(rel, number, "pragma", "the launcher may disable RS0030 only once"))
        for match in SUPPRESS_MESSAGE.finditer(text):
            call = _call_text(text, match.end() - 1)
            number = _line_of(text, match.start())
            if "RS0030" in call:
                out.append(Violation(rel, number, "pragma", "RS0030 (banned API) must never be suppressed"))
            if not re.search(r"\bJustification\s*=", call):
                out.append(Violation(rel, number, "pragma", "[SuppressMessage] needs a Justification = \"...\""))
    return out


# ---------------------------------------------------------------------------------------------------------------------
# Rules over repository files

def check_manifest(root: Path, policy: dict) -> list[Violation]:
    """manifest (REQ-QSC-11)."""
    rel = policy["manifest"]
    path = root / rel
    if not path.is_file():
        return [Violation(rel, 1, "manifest", "application manifest is missing")]
    text = _read(path)
    out: list[Violation] = []
    level = re.search(r"\blevel\s*=\s*\"([^\"]*)\"", text)
    if not level or level.group(1) != "asInvoker":
        out.append(Violation(rel, _line_of(text, level.start()) if level else 1, "manifest",
                             'requestedExecutionLevel must be level="asInvoker": Envoy never elevates'))
    for match in re.finditer(r"\buiAccess\s*=\s*\"\s*true\s*\"", text, re.IGNORECASE):
        out.append(Violation(rel, _line_of(text, match.start()), "manifest", 'uiAccess="true" is not allowed'))
    return out


def check_banned_symbols(root: Path, policy: dict) -> list[Violation]:
    """banned-symbols (REQ-QSC-6)."""
    path = root / "BannedSymbols.txt"
    if not path.is_file():
        return [Violation("BannedSymbols.txt", 1, "banned-symbols", "BannedSymbols.txt is missing")]
    ids = {line.split(";", 1)[0].strip() for line in _read(path).splitlines() if line.strip() and not line.startswith("#")}
    return [Violation("BannedSymbols.txt", 1, "banned-symbols", f"required banned symbol {sid} is missing")
            for sid in policy["bannedRequired"] if sid not in ids]


def _xml(path: Path) -> ET.Element | None:
    try:
        return ET.fromstring(_read(path))
    except ET.ParseError:
        return None


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _elements(root_el: ET.Element, name: str) -> list[ET.Element]:
    return [e for e in root_el.iter() if _local(e.tag) == name]


def _codes(value: str | None) -> list[str]:
    return [c.strip().lower() for c in (value or "").split(";") if c.strip()]


def check_build_config(root: Path, policy: dict) -> list[Violation]:
    """build-config (REQ-QSC-1, 2, 3, 6)."""
    out: list[Violation] = []
    props_rel = "Directory.Build.props"
    props = _xml(root / props_rel) if (root / props_rel).is_file() else None
    if props is None:
        out.append(Violation(props_rel, 1, "build-config", "Directory.Build.props is missing or not valid XML"))
    else:
        unconditional = [g for g in _elements(props, "PropertyGroup") if not g.get("Condition")]
        for name, expected in REQUIRED_PROPS.items():
            values = [(e.text or "").strip() for g in unconditional for e in g if _local(e.tag) == name]
            if expected not in values:
                out.append(Violation(props_rel, 1, "build-config", f"<{name}>{expected}</{name}> must be set for every project"))
        ci_groups = [g for g in _elements(props, "PropertyGroup")
                     if re.sub(r"\s", "", g.get("Condition", "")) == "'$(CI)'=='true'"]
        if not any((e.text or "").strip().lower() == "true" for g in ci_groups for e in g if _local(e.tag) == "TreatWarningsAsErrors"):
            out.append(Violation(props_rel, 1, "build-config",
                                 "a PropertyGroup with Condition=\"'$(CI)' == 'true'\" must set TreatWarningsAsErrors=true"))
        if not any(e.get("Include") == "Microsoft.CodeAnalysis.BannedApiAnalyzers" for e in _elements(props, "PackageReference")):
            out.append(Violation(props_rel, 1, "build-config", "the BannedApiAnalyzers PackageReference is missing"))
        if not any("BannedSymbols.txt" in (e.get("Include") or "") for e in _elements(props, "AdditionalFiles")):
            out.append(Violation(props_rel, 1, "build-config", "BannedSymbols.txt must be an AdditionalFiles item"))

    for rel in NULLABLE_PROJECTS:
        path = root / rel
        doc = _xml(path) if path.is_file() else None
        if doc is None or not any("nullable" in _codes(e.text) for e in _elements(doc, "WarningsAsErrors")):
            out.append(Violation(rel, 1, "build-config", "WarningsAsErrors must include 'nullable' (REQ-QSC-2)"))

    for path in _walk(root, root, lambda p: p.name.endswith(PROJECT_SUFFIXES)):
        rel = _rel(root, path)
        text = _read(path)
        doc = _xml(path)
        if path.name.lower() == "directory.build.props" and rel != "Directory.Build.props":
            out.append(Violation(rel, 1, "build-config",
                                 "only the root Directory.Build.props may exist: a nested one stops MSBuild importing "
                                 "the root settings and analyzers for its folder"))
        if doc is None:
            continue
        if rel != APP_PROJECT:
            for e in _elements(doc, "TreatWarningsAsErrors"):
                if (e.text or "").strip().lower() == "false":
                    out.append(Violation(rel, _line_of(text, text.find("TreatWarningsAsErrors")), "build-config",
                                         f"only {APP_PROJECT} may set TreatWarningsAsErrors=false"))
        for e in doc.iter():
            name = _local(e.tag)
            if name.lower() in {n.lower() for n in ANALYZER_SWITCHES} and (e.text or "").strip().lower() == "false":
                out.append(Violation(rel, _line_of(text, text.find(name)), "build-config",
                                     f"<{name}> must not be false: it turns off the analyzers or the root build settings"))
            removed = e.get("Remove") or ""
            for item in ANALYZER_ITEMS:
                if item.lower() in removed.lower():
                    out.append(Violation(rel, _line_of(text, text.find(removed)), "build-config",
                                         f"{item} must not be removed (REQ-QSC-6)"))
        for name in ("NoWarn", "WarningsNotAsErrors"):
            for e in _elements(doc, name):
                if "rs0030" in _codes(e.text):
                    out.append(Violation(rel, _line_of(text, text.find("RS0030")), "build-config",
                                         f"RS0030 (banned API) must not be in <{name}>"))

    root_rs0030 = False
    for path in _walk(root, root, lambda p: p.name in EDITORCONFIG_NAMES):
        rel = _rel(root, path)
        text = _read(path)
        for match in RS0030_SEVERITY.finditer(text):
            if match.group(1).lower() != "error":
                out.append(Violation(rel, _line_of(text, match.start()), "build-config",
                                     "RS0030 (banned API) must stay at severity error"))
            elif rel == ".editorconfig":
                root_rs0030 = True
    if not root_rs0030:
        out.append(Violation(".editorconfig", 1, "build-config", "dotnet_diagnostic.RS0030.severity = error is missing"))
    return out


# ---------------------------------------------------------------------------------------------------------------------

def load_policy(root: Path, policy_path: Path | None = None) -> dict:
    return json.loads((policy_path or root / "tools" / "ci" / "policy.json").read_text(encoding="utf-8"))


def check(root: Path, policy: dict | None = None) -> list[Violation]:
    """Runs every rule against the repository at root and returns the violations."""
    root = root.resolve()
    policy = policy if policy is not None else load_policy(root)
    src = root / "src"
    sources = {_rel(root, p): _read(p) for p in _walk(root, src, lambda p: p.suffix == ".cs")}
    xaml = {_rel(root, p): _read(p) for p in _walk(root, src, lambda p: p.suffix == ".xaml")}
    all_cs = {_rel(root, p): _read(p) for p in _walk(root, root, lambda p: p.suffix == ".cs")}
    violations = [
        *check_native(root, policy, sources),
        *check_network(root, policy, sources),
        *check_hosts(root, policy, sources, xaml),
        *check_process_launch(root, policy, sources),
        *check_pragmas(root, policy, all_cs),
        *check_manifest(root, policy),
        *check_banned_symbols(root, policy),
        *check_build_config(root, policy),
    ]
    return sorted(set(violations), key=lambda v: (v.path, v.line, v.rule, v.message))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2], help="repository root")
    parser.add_argument("--policy", type=Path, default=None, help="policy file (default: <root>/tools/ci/policy.json)")
    args = parser.parse_args(argv)
    root = args.root.resolve()
    violations = check(root, load_policy(root, args.policy))
    for v in violations:
        print(v)
    if violations:
        print(f"Policy check failed: {len(violations)} violation(s). See docs/specs/08a-quality-gates.md §4.", file=sys.stderr)
        return 1
    print("Policy check passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
