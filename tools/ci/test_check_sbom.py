"""Tests for check_sbom.py. Run: python3 -m unittest discover -s tools/ci -p "test_*.py"."""
from __future__ import annotations

import json
import unittest

from ci_testing import TempDir, call

import check_sbom  # noqa: E402

REQUIRED = ["Anthropic", "AWSSDK.Translate", "System.Security.Cryptography.ProtectedData", "Velopack"]


def bom(names: list[str]) -> str:
    return json.dumps({"bomFormat": "CycloneDX", "specVersion": "1.6", "version": 1,
                       "metadata": {"component": {"name": "Envoy", "version": "1.0.0", "type": "application"}},
                       "components": [{"type": "library", "name": n, "version": "1.0.0", "purl": f"pkg:nuget/{n}@1.0.0"}
                                      for n in names]})


class CheckSbomTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = TempDir()

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def check(self, text: str):
        return call(check_sbom.run, [self.tmp.write("Envoy.cdx.json", text), "--require", *REQUIRED])

    def test_all_required_present(self) -> None:
        code, out = self.check(bom(REQUIRED + ["AWSSDK.Core"]))
        self.assertEqual(0, code, out)

    def test_names_compare_case_insensitively(self) -> None:
        self.assertEqual(0, self.check(bom([n.lower() for n in REQUIRED]))[0])

    def test_missing_component_fails(self) -> None:
        code, out = self.check(bom([n for n in REQUIRED if n != "Velopack"]))
        self.assertEqual(1, code)
        self.assertIn("no component named Velopack", out)

    def test_invalid_json_exits_2(self) -> None:
        self.assertEqual(2, self.check("{ broken")[0])

    def test_not_cyclonedx_exits_2(self) -> None:
        self.assertEqual(2, self.check(json.dumps({"spdxVersion": "SPDX-2.3"}))[0])


if __name__ == "__main__":
    unittest.main()
