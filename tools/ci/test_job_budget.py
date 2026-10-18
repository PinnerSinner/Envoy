"""Tests for job_budget.py. Run: python3 -m unittest discover -s tools/ci -p "test_*.py"."""
from __future__ import annotations

import unittest

from ci_testing import TempDir, call

import job_budget  # noqa: E402

NOW = 1_800_000_000.0

SMOKE_LOG = """2026-10-18 10:00:00 [INF] Smoke: [smoke] host ok elapsed_ms=900
[smoke] ocr-languages ok elapsed_ms=12
[smoke] capture-engine ok elapsed_ms=10400
[smoke] palette-dark ok elapsed_ms=21000
[smoke] palette-light ok elapsed_ms=19500
[smoke] palette-highcontrast ok elapsed_ms=18000
[smoke] teardown ok elapsed_ms=300
[smoke] something failed elapsed_ms=99999
"""


class JobBudgetTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = TempDir()

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def budget(self, minutes_ago: float | None, *extra: str, env: dict[str, str] | None = None):
        environ = dict(env or {})
        if minutes_ago is not None:
            environ["JOB_START"] = str(int(NOW - minutes_ago * 60))
        return call(job_budget.run, ["--label", "check", "--max-minutes", "8", *extra], environ, now=NOW)

    def test_under_budget_passes(self) -> None:
        code, out = self.budget(7)
        self.assertEqual(0, code, out)
        self.assertIn("| check | 7.0 min | 8 min |", out)

    def test_over_budget_fails(self) -> None:
        code, out = self.budget(9)
        self.assertEqual(1, code)
        self.assertIn("::error::check took 9.0 min (budget 8)", out)

    def test_missing_start_exits_2(self) -> None:
        self.assertEqual(2, self.budget(None)[0])
        self.assertEqual(2, call(job_budget.run, ["--label", "x", "--max-minutes", "1"], {"JOB_START": "soon"}, now=NOW)[0])

    def test_smoke_log_top_sections(self) -> None:
        summary = self.tmp.write("summary.md", "")
        code, _ = self.budget(5, "--smoke-log", self.tmp.write("smoke.log", SMOKE_LOG), env={"GITHUB_STEP_SUMMARY": summary})
        self.assertEqual(0, code)
        with open(summary, encoding="utf-8") as f:
            text = f.read()
        rows = [line for line in text.splitlines() if line.startswith("| palette") or line.startswith("| capture")
                or line.startswith("| host") or line.startswith("| teardown") or line.startswith("| ocr")]
        self.assertEqual(["| palette-dark | 21.0 s |", "| palette-light | 19.5 s |", "| palette-highcontrast | 18.0 s |",
                          "| capture-engine | 10.4 s |", "| host | 0.9 s |"], rows)
        self.assertIn("| **all 7 sections** | **70.1 s** |", text)  # the failed line is not a section

    def test_missing_smoke_log_still_reports(self) -> None:
        code, out = self.budget(1, "--smoke-log", str(self.tmp.path / "missing.log"))
        self.assertEqual(0, code)
        self.assertIn("all 0 sections", out)


if __name__ == "__main__":
    unittest.main()
