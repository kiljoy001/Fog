#!/usr/bin/env python3
"""Self-tests for selecting a fresh merged coverage report."""

import os
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from coverage_report import MAX_AGE_SECONDS, check, find  # noqa: E402


class Repo:
    def __init__(self, stack: tempfile.TemporaryDirectory):
        self.root = Path(stack.name)
        (self.root / "tools").mkdir()
        (self.root / "tools" / "crap.py").write_text("# marker\n")
        (self.root / "NinePSharp").mkdir()
        self.source = self.root / "NinePSharp" / "Protocol.cs"
        self.source.write_text("// source\n")
        (self.root / ".artifacts" / "coverage").mkdir(parents=True)
        self.report = self.root / ".artifacts" / "coverage" / "dotnet.xml"

    def write_report(self, age_seconds: float = 0.0) -> Path:
        self.report.write_text("<coverage line-rate='0.96' />\n")
        self.age_report(age_seconds)
        return self.report

    def age_report(self, seconds: float) -> None:
        when = time.time() - seconds
        os.utime(self.report, (when, when))

    def age_source(self, seconds: float) -> None:
        when = time.time() - seconds
        os.utime(self.source, (when, when))


class TestFind(unittest.TestCase):
    def setUp(self):
        self.stack = tempfile.TemporaryDirectory()
        self.addCleanup(self.stack.cleanup)
        self.repo = Repo(self.stack)

    def test_finds_the_report_from_the_root(self):
        self.assertEqual(find(self.repo.root), self.repo.report)

    def test_finds_the_report_from_deep_inside_the_tree(self):
        deep = self.repo.root / "NinePSharp"
        self.assertEqual(find(deep), self.repo.report)

    def test_refuses_when_there_is_no_repository(self):
        with tempfile.TemporaryDirectory() as bare:
            with self.assertRaises(FileNotFoundError):
                find(Path(bare))


class TestCheck(unittest.TestCase):
    def setUp(self):
        self.stack = tempfile.TemporaryDirectory()
        self.addCleanup(self.stack.cleanup)
        self.repo = Repo(self.stack)

    def test_a_fresh_report_has_nothing_wrong_with_it(self):
        self.repo.age_source(60)
        report = self.repo.write_report()
        self.assertEqual(check(report), [])

    def test_a_missing_report_is_refused(self):
        problems = check(self.repo.report)
        self.assertEqual(len(problems), 1)
        self.assertIn("no coverage report", problems[0])

    def test_an_old_report_is_refused(self):
        self.repo.age_source(MAX_AGE_SECONDS * 3)
        report = self.repo.write_report(age_seconds=MAX_AGE_SECONDS + 60)
        self.assertEqual(len(check(report)), 1)

    def test_a_report_just_inside_the_limit_is_accepted(self):
        self.repo.age_source(MAX_AGE_SECONDS * 3)
        report = self.repo.write_report(age_seconds=MAX_AGE_SECONDS - 60)
        self.assertEqual(check(report), [])

    def test_a_report_older_than_its_sources_is_refused(self):
        report = self.repo.write_report(age_seconds=120)
        self.repo.age_source(30)
        problems = check(report)
        self.assertEqual(len(problems), 1)
        self.assertIn("source changed", problems[0])

    def test_build_output_does_not_count_as_changed_source(self):
        self.repo.age_source(600)
        report = self.repo.write_report(age_seconds=300)
        stale = self.repo.root / "NinePSharp" / "obj" / "Debug"
        stale.mkdir(parents=True)
        (stale / "Generated.cs").write_text("// built\n")
        self.assertEqual(check(report), [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
