#!/usr/bin/env python3
"""The strict mutation gate must never disguise timeouts as assertion kills."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class MutationSummaryTests(unittest.TestCase):
    def summarize(self, statuses):
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "mutation-report.json").write_text(json.dumps({
                "files": {"example.cs": {"mutants": [{"status": status} for status in statuses]}}
            }))
            return subprocess.run([
                sys.executable, str(Path(__file__).with_name("mutation_summary.py")),
                "--output-dir", directory, "--min-score", "100",
            ], capture_output=True, text=True, check=False)

    def test_timeouts_reduce_score_and_fail_gate(self):
        result = self.summarize(["Killed", "Timeout"])
        self.assertEqual(1, result.returncode)
        self.assertIn("**Score:** 50.00%", result.stdout)
        self.assertIn("**Killed:** 1", result.stdout)
        self.assertIn("**Timed out:** 1", result.stdout)

    def test_only_assertion_kills_pass(self):
        result = self.summarize(["Killed", "Killed", "Ignored", "CompileError"])
        self.assertEqual(0, result.returncode)
        self.assertIn("**Score:** 100.00%", result.stdout)
        self.assertIn("**Timed out:** 0", result.stdout)

    def test_survived_and_uncovered_still_fail(self):
        for status in ("Survived", "NoCoverage"):
            with self.subTest(status=status):
                self.assertEqual(1, self.summarize(["Killed", status]).returncode)

    def test_empty_report_cannot_claim_complete_detection(self):
        self.assertEqual(1, self.summarize([]).returncode)


if __name__ == "__main__":
    unittest.main()
