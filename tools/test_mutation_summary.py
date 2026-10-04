#!/usr/bin/env python3
"""The strict mutation gate must never disguise timeouts as assertion kills."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SOURCE = "class Example\n{\n    void Spin() { while (true) { } }\n}\n"


class MutationSummaryTests(unittest.TestCase):
    def summarize(self, statuses, accepted=None):
        mutants = [
            {"status": status, "mutatorName": "Boolean mutation", "location": {"start": {"line": 3}}}
            for status in statuses
        ]
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "mutation-report.json").write_text(json.dumps({
                "files": {"src/Example.cs": {"source": SOURCE, "mutants": mutants}}
            }))
            arguments = [
                sys.executable, str(Path(__file__).with_name("mutation_summary.py")),
                "--output-dir", directory, "--min-score", "100",
            ]
            if accepted is not None:
                path = Path(directory, "accepted.json")
                path.write_text(json.dumps(accepted))
                arguments += ["--accepted-timeouts", str(path)]
            return subprocess.run(arguments, capture_output=True, text=True, check=False)

    def accepted(self, **changes):
        entry = {
            "file": "Example.cs",
            "mutator": "Boolean mutation",
            "line": "void Spin() { while (true) { } }",
            "reason": "the loop can only spin",
        }
        entry.update(changes)
        return [entry]

    def test_a_listed_timeout_is_accepted(self):
        result = self.summarize(["Killed", "Timeout"], self.accepted())
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("**Score:** 100.00%", result.stdout)
        self.assertIn("**Timed out:** 1 (1 accepted)", result.stdout)

    def test_a_timeout_the_list_does_not_name_still_fails(self):
        for changes in ({"line": "void Other() { }"}, {"mutator": "Statement mutation"}, {"file": "Other.cs"}):
            with self.subTest(changes=changes):
                result = self.summarize(["Killed", "Timeout"], self.accepted(**changes))
                self.assertEqual(1, result.returncode)
                self.assertIn("**Score:** 50.00%", result.stdout)

    def test_the_list_never_excuses_a_survivor(self):
        self.assertEqual(1, self.summarize(["Killed", "Survived"], self.accepted()).returncode)

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
