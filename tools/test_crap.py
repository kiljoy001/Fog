#!/usr/bin/env python3
"""Self-tests for the CRAP score calculator used by the quality gate."""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from crap import coverage_needed, crap_score, default_exclusion_reason, risk_band  # noqa: E402


def method(class_name: str, file_name: str, method_name: str = "Run()"):
    return {"class": class_name, "file": file_name, "method": method_name}


class TestCrapScore(unittest.TestCase):
    def test_full_coverage_equals_complexity(self):
        for complexity in (1, 3, 12, 40):
            self.assertAlmostEqual(crap_score(complexity, 1.0), complexity, places=6)

    def test_zero_coverage_is_complexity_squared_plus_complexity(self):
        for complexity in (1, 5, 12):
            self.assertAlmostEqual(crap_score(complexity, 0.0), complexity**2 + complexity, places=6)

    def test_known_values(self):
        self.assertAlmostEqual(crap_score(12, 0.45), 35.958, places=3)
        self.assertAlmostEqual(crap_score(8, 0.90), 8.064, places=3)

    def test_monotonic_in_coverage(self):
        scores = [crap_score(10, coverage / 10) for coverage in range(11)]
        self.assertEqual(scores, sorted(scores, reverse=True))

    def test_monotonic_in_complexity(self):
        scores = [crap_score(complexity, 0.5) for complexity in range(1, 12)]
        self.assertEqual(scores, sorted(scores))

    def test_coverage_above_one_is_clamped(self):
        self.assertAlmostEqual(crap_score(10, 1.5), 10, places=6)


class TestRiskBands(unittest.TestCase):
    def test_boundaries(self):
        self.assertEqual(risk_band(4.99), "low")
        self.assertEqual(risk_band(5), "moderate")
        self.assertEqual(risk_band(14.99), "moderate")
        self.assertEqual(risk_band(15), "high")
        self.assertEqual(risk_band(30), "high")
        self.assertEqual(risk_band(30.01), "critical")


class TestCoverageNeeded(unittest.TestCase):
    def test_round_trips_through_crap_score(self):
        for complexity in (3, 8, 12, 14):
            needed = coverage_needed(complexity, 15)
            self.assertIsNotNone(needed)
            self.assertAlmostEqual(crap_score(complexity, needed), 15, places=4)

    def test_returns_none_when_complexity_exceeds_target(self):
        self.assertIsNone(coverage_needed(15, 15))
        self.assertIsNone(coverage_needed(18, 15))
        self.assertIsNone(coverage_needed(40, 30))

    def test_matches_known_example(self):
        self.assertAlmostEqual(coverage_needed(12, 15), 0.725, places=2)


class TestDefaultExclusions(unittest.TestCase):
    def test_compiler_generated_fsharp_classes_are_excluded(self):
        reason = default_exclusion_reason(method(
            "<StartupCode$NinePSharp-Server-FSharp>.$Dispatcher/clo@112-4",
            "NinePSharp.Server.FSharp/Dispatcher.fs"))

        self.assertIn("compiler-generated", reason)

    def test_fscheck_generator_project_is_excluded(self):
        reason = default_exclusion_reason(method(
            "NinePSharp.Generators.Generators/genAggressiveStat@861-2",
            "NinePSharp.Generators/Generators.fs",
            "Invoke(System.String)"))

        self.assertIn("test-support", reason)

    def test_known_fsharp_du_match_false_positives_are_excluded(self):
        dispatcher = default_exclusion_reason(method(
            "NinePSharp.Server.FSharp.NinePFSDispatcherEngine",
            "NinePSharp.Server.FSharp/Dispatcher.fs",
            "getTag(NinePSharp.Parser.NinePMessage)"))
        validation = default_exclusion_reason(method(
            "NinePSharp.Parser.Validation",
            "NinePSharp.Parser/Validation.fs",
            "validate(NinePSharp.Parser.NinePMessage)"))

        self.assertIn("discriminated-union", dispatcher)
        self.assertIn("discriminated-union", validation)

    def test_normal_first_party_method_is_not_excluded(self):
        reason = default_exclusion_reason(method(
            "NinePSharp.ProtocolActions",
            "NinePSharp/protocol/ProtocolActions.cs",
            "Version(NinePSharp.Messages.Tversion)"))

        self.assertIsNone(reason)


if __name__ == "__main__":
    unittest.main(verbosity=2)
