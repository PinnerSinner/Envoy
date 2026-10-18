"""Tests for coverage_summary.py. Run: python3 -m unittest discover -s tools/ci -p "test_*.py"."""
from __future__ import annotations

import unittest

from ci_testing import TempDir, call

import coverage_summary  # noqa: E402


def cobertura(line_rate: float, branch_rate: float, package: str = "Envoy.Core") -> str:
    """Two folders: Chat/ (2 lines, one branch line 1/2) and Ux/ (2 lines, one hit), plus a root file."""
    return f"""<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="{line_rate}" branch-rate="{branch_rate}" version="1.9">
  <packages>
    <package name="{package}" line-rate="{line_rate}" branch-rate="{branch_rate}" complexity="3">
      <classes>
        <class name="A" filename="Chat/A.cs" line-rate="1" branch-rate="0.5">
          <methods><method name="M"><lines><line number="1" hits="3" branch="False" /></lines></method></methods>
          <lines>
            <line number="1" hits="3" branch="False" />
            <line number="2" hits="1" branch="True" condition-coverage="50% (1/2)" />
          </lines>
        </class>
        <class name="A+Nested" filename="Chat/A.cs" line-rate="1" branch-rate="1">
          <lines><line number="2" hits="1" branch="True" condition-coverage="50% (1/2)" /></lines>
        </class>
        <class name="B" filename="Ux/B.cs" line-rate="0.5" branch-rate="1">
          <lines>
            <line number="5" hits="0" branch="False" />
            <line number="6" hits="2" branch="False" />
          </lines>
        </class>
        <class name="C" filename="C.cs" line-rate="1" branch-rate="1">
          <lines><line number="1" hits="1" branch="False" /></lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"""


class CoverageSummaryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = TempDir()

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def gate(self, xml: str, *extra: str, env: dict[str, str] | None = None, package: str = "Envoy.Core"):
        path = self.tmp.write("coverage.cobertura.xml", xml)
        return call(coverage_summary.run, ["--package", package, "--min-line", "70", "--min-branch", "55", *extra, path], env)

    def test_passes_above_threshold(self) -> None:
        code, out = self.gate(cobertura(0.80, 0.60))
        self.assertEqual(0, code, out)
        self.assertIn("| **Total** (gate 70% / 55%) | **80.0%** | **60.0%** |", out)
        self.assertIn("| Chat | 100.0% | 50.0% | 2 | 2 |", out)  # line 2 counted once across the nested class
        self.assertIn("| Ux | 50.0% | 100.0% | 2 | 0 |", out)
        self.assertIn("| (root) |", out)

    def test_fails_below_line_threshold(self) -> None:
        code, out = self.gate(cobertura(0.65, 0.60))
        self.assertEqual(1, code)
        self.assertIn("::error::Envoy.Core line coverage 65.0% is below 70%", out)

    def test_fails_below_branch_threshold(self) -> None:
        code, out = self.gate(cobertura(0.80, 0.50))
        self.assertEqual(1, code)
        self.assertIn("::error::Envoy.Core branch coverage 50.0% is below 55%", out)

    def test_exact_threshold_passes(self) -> None:
        self.assertEqual(0, self.gate(cobertura(0.70, 0.55))[0])

    def test_writes_to_github_step_summary(self) -> None:
        summary = self.tmp.write("summary.md", "")
        code, _ = self.gate(cobertura(0.80, 0.60), env={"GITHUB_STEP_SUMMARY": summary})
        self.assertEqual(0, code)
        with open(summary, encoding="utf-8") as f:
            text = f.read()
        self.assertIn("### Envoy.Core coverage", text)
        self.assertIn("| **Total**", text)
        self.assertIn("| Chat |", text)

    def test_missing_package_exits_2(self) -> None:
        code, out = self.gate(cobertura(0.80, 0.60), package="Nope")
        self.assertEqual(2, code)
        self.assertIn("no package named Nope", out)

    def test_invalid_xml_exits_2(self) -> None:
        self.assertEqual(2, self.gate("<coverage>")[0])

    def test_missing_file_exits_2(self) -> None:
        code, _ = call(coverage_summary.run, ["--package", "Envoy.Core", "--min-line", "70", "--min-branch", "55",
                                              str(self.tmp.path / "nope.xml")])
        self.assertEqual(2, code)

    def test_several_files_merge_unique_lines(self) -> None:
        first = self.tmp.write("a/coverage.cobertura.xml", cobertura(0.5, 0.5))
        second = self.tmp.write("b/coverage.cobertura.xml", cobertura(0.5, 0.5))
        code, out = call(coverage_summary.run, ["--package", "Envoy.Core", "--min-line", "70", "--min-branch", "55",
                                                first, second])
        # Unique lines: Chat/A.cs 1, 2; Ux/B.cs 5, 6; C.cs 1 -> 4 of 5 hit; branches 1 of 2.
        self.assertIn("| **80.0%** | **50.0%** | 5 | 2 |", out)
        self.assertEqual(1, code)


if __name__ == "__main__":
    unittest.main()
