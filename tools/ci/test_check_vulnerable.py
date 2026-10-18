"""Tests for check_vulnerable.py. Run: python3 -m unittest discover -s tools/ci -p "test_*.py"."""
from __future__ import annotations

import json
import unittest

from ci_testing import TempDir, call

import check_vulnerable  # noqa: E402


def report(*packages: tuple[str, str, str, bool]) -> str:
    """packages: (id, version, severity, transitive)."""
    top = [p for p in packages if not p[3]]
    transitive = [p for p in packages if p[3]]

    def entry(p):
        return {"id": p[0], "requestedVersion": p[1], "resolvedVersion": p[1],
                "vulnerabilities": [{"severity": p[2], "advisoryurl": f"https://github.com/advisories/GHSA-{p[0]}"}]}

    framework = {"framework": "net8.0"}
    if top:
        framework["topLevelPackages"] = [entry(p) for p in top]
    if transitive:
        framework["transitivePackages"] = [entry(p) for p in transitive]
    project = {"path": "tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj"}
    if packages:
        project["frameworks"] = [framework]
    return json.dumps({"version": 1, "parameters": "--vulnerable --include-transitive",
                       "sources": ["https://api.nuget.org/v3/index.json"], "projects": [project]})


class CheckVulnerableTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = TempDir()

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def check(self, text: str, env: dict[str, str] | None = None):
        return call(check_vulnerable.run, ["--from-json", self.tmp.write("list.json", text)], env)

    def test_high_fails(self) -> None:
        code, out = self.check(report(("System.Text.Json", "8.0.0", "High", False)))
        self.assertEqual(1, code)
        self.assertIn("::error::System.Text.Json 8.0.0 (direct", out)
        self.assertIn("https://github.com/advisories/GHSA-System.Text.Json", out)

    def test_critical_fails(self) -> None:
        code, out = self.check(report(("Some.Transitive", "1.0.0", "Critical", True)))
        self.assertEqual(1, code)
        self.assertIn("::error::Some.Transitive 1.0.0 (transitive", out)

    def test_moderate_passes_and_is_listed(self) -> None:
        summary = self.tmp.write("summary.md", "")
        code, out = self.check(report(("Mild.Package", "2.0.0", "Moderate", True), ("Low.Package", "1.0.0", "Low", False)),
                               env={"GITHUB_STEP_SUMMARY": summary})
        self.assertEqual(0, code, out)
        self.assertIn("::warning::Mild.Package 2.0.0", out)
        with open(summary, encoding="utf-8") as f:
            text = f.read()
        self.assertIn("| Mild.Package | 2.0.0 | Moderate |", text)
        self.assertIn("| Low.Package | 1.0.0 | Low |", text)

    def test_no_vulnerabilities_passes(self) -> None:
        code, out = self.check(report())
        self.assertEqual(0, code)
        self.assertIn("No known vulnerabilities", out)

    def test_unreadable_report_exits_2(self) -> None:
        self.assertEqual(2, self.check("not json")[0])
        self.assertEqual(2, self.check(json.dumps({"version": 1}))[0])

    def test_restore_problem_exits_2(self) -> None:
        text = json.dumps({"version": 1, "projects": [], "problems": [{"text": "No assets file", "level": "error"}]})
        code, out = self.check(text)
        self.assertEqual(2, code)
        self.assertIn("No assets file", out)


if __name__ == "__main__":
    unittest.main()
