"""Tests for check_a11y.py (spec 07b): each rule has a passing and a failing fixture tree under fixtures/a11y/<rule>/,
laid out like src/Envoy.App, and the real app tree passes."""
import contextlib
import io
import os
import re
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import check_a11y  # noqa: E402

FIXTURES = os.path.join(HERE, "fixtures", "a11y")
APP = os.path.normpath(os.path.join(HERE, "..", "..", "src", "Envoy.App"))

# rule directory -> (rule id, violations the failing tree must report)
EXPECTED = {
    "rule1": ("A11Y1", 2),
    "rule2": ("A11Y2", 10),
    "rule3": ("A11Y3", 2),
    "rule4": ("A11Y4", 4),
    "rule5": ("A11Y5", 3),
}


class CheckA11yTests(unittest.TestCase):
    def test_every_rule_has_fixtures(self):
        self.assertEqual(sorted(EXPECTED), sorted(os.listdir(FIXTURES)))

    def test_passing_fixtures_pass(self):
        for rule in EXPECTED:
            with self.subTest(rule=rule):
                self.assertEqual([], check_a11y.check(os.path.join(FIXTURES, rule, "pass")))

    def test_failing_fixtures_report_their_rule(self):
        for rule, (rule_id, count) in EXPECTED.items():
            with self.subTest(rule=rule):
                problems = check_a11y.check(os.path.join(FIXTURES, rule, "fail"))
                self.assertEqual(count, len(problems), "\n".join(problems))
                for problem in problems:
                    self.assertRegex(problem, r"^[\w/.]+\.xaml:\d+: " + re.escape(rule_id) + " ")

    def test_line_numbers_point_at_the_element(self):
        problems = check_a11y.check(os.path.join(FIXTURES, "rule3", "fail"))
        self.assertEqual(["Pages/Fixture.xaml:6", "Pages/Fixture.xaml:7"], [p.split(": ")[0] for p in problems])

    def test_private_use_glyphs_are_not_letters(self):
        node = check_a11y.Node("Button", {"Content": ""}, 1, None)
        self.assertFalse(check_a11y._letter_content(node))
        node = check_a11y.Node("Button", {"Content": "Save"}, 1, None)
        self.assertTrue(check_a11y._letter_content(node))
        node = check_a11y.Node("Button", {"Content": "{Binding Label}"}, 1, None)
        self.assertFalse(check_a11y._letter_content(node))

    def test_app_tree_passes(self):
        if not os.path.isdir(APP):
            self.skipTest("no src/Envoy.App next to tools/")
        self.assertEqual([], check_a11y.check(APP))

    def test_main_exit_codes(self):
        with contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(0, check_a11y.main(["check_a11y.py", os.path.join(FIXTURES, "rule1", "pass")]))
            self.assertEqual(1, check_a11y.main(["check_a11y.py", os.path.join(FIXTURES, "rule1", "fail")]))
        self.assertIn("A11Y1", out.getvalue())


if __name__ == "__main__":
    unittest.main()
