#!/usr/bin/env python3
"""Compute CRAP scores from the merged NinePSharp Cobertura report."""

from __future__ import annotations

import argparse
import collections
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

import coverage_report  # noqa: E402


DU_MATCH_FALSE_POSITIVES = {
    (
        "NinePSharp.Server.FSharp.NinePFSDispatcherEngine",
        "getTag(",
    ): "F# discriminated-union tag dispatch",
    (
        "NinePSharp.Parser.Validation",
        "validate(",
    ): "F# discriminated-union validation dispatch",
}


def crap_score(complexity: float, coverage: float) -> float:
    uncovered = max(0.0, 1.0 - coverage)
    return complexity**2 * uncovered**3 + complexity


def risk_band(score: float) -> str:
    if score < 5:
        return "low"
    if score < 15:
        return "moderate"
    if score <= 30:
        return "high"
    return "critical"


def coverage_needed(complexity: float, target: float) -> float | None:
    if complexity >= target:
        return None
    return 1.0 - ((target - complexity) / complexity**2) ** (1 / 3)


def default_exclusion_reason(method: dict[str, object]) -> str | None:
    class_name = str(method.get("class", ""))
    file_name = str(method.get("file", "")).replace("\\", "/")
    method_name = str(method.get("method", ""))

    if class_name.startswith("<StartupCode$"):
        return "compiler-generated F# closure/async state machine"

    if file_name.startswith("NinePSharp.Generators/"):
        return "FsCheck test-support generator project"

    for (excluded_class, method_prefix), reason in DU_MATCH_FALSE_POSITIVES.items():
        if class_name == excluded_class and method_name.startswith(method_prefix):
            return reason

    return None


def parse(report: Path) -> list[dict[str, object]]:
    root = ET.parse(report).getroot()
    methods: list[dict[str, object]] = []

    for cls in root.iter("class"):
        class_name = cls.get("name", "?")
        filename = cls.get("filename", "?")

        for method in cls.iter("method"):
            name = method.get("name", "?")
            signature = method.get("signature", "")
            complexity = float(method.get("complexity", 1))
            coverage = float(method.get("line-rate", 0))
            lines = method.findall(".//line")
            covered_lines = sum(1 for line in lines if int(line.get("hits", 0)) > 0)
            score = crap_score(complexity, coverage)

            methods.append({
                "class": class_name,
                "file": filename,
                "method": f"{name}{signature}",
                "complexity": complexity,
                "coverage": coverage,
                "lines": len(lines),
                "covered_lines": covered_lines,
                "crap": round(score, 2),
                "risk": risk_band(score),
            })

    return methods


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path, nargs="?")
    parser.add_argument("--threshold", type=float, default=30.0)
    parser.add_argument("--top", type=int, default=20)
    parser.add_argument("--json", type=Path)
    parser.add_argument("--fail-over", type=float)
    parser.add_argument("--target", type=float, default=15.0)
    parser.add_argument("--raw", action="store_true", help="include compiler/test-support noise")
    args = parser.parse_args()

    report = coverage_report.resolve(args.report)
    all_methods = parse(report)
    if not all_methods:
        print("no methods found in coverage report", file=sys.stderr)
        return 2

    excluded: list[dict[str, object]] = []
    if args.raw:
        methods = all_methods
    else:
        methods = []
        for method in all_methods:
            reason = default_exclusion_reason(method)
            if reason is None:
                methods.append(method)
            else:
                excluded_method = dict(method)
                excluded_method["excluded_reason"] = reason
                excluded.append(excluded_method)

    if not methods:
        print("no methods remained after default CRAP exclusions", file=sys.stderr)
        return 2

    methods.sort(key=lambda method: -float(method["crap"]))
    crappy = [method for method in methods if float(method["crap"]) > args.threshold]
    total_complexity = sum(float(method["complexity"]) for method in methods)
    weighted_coverage = (
        sum(float(method["coverage"]) * float(method["complexity"]) for method in methods)
        / total_complexity
        if total_complexity else 0.0
    )
    bands = collections.Counter(str(method["risk"]) for method in methods)

    print(f"CRAP report - {report}")
    print(f"  methods analysed      {len(methods)}")
    if not args.raw:
        print(f"  methods excluded      {len(excluded)}  (use --raw for all Cobertura methods)")
    print(
        f"  risk bands            low {bands['low']}, moderate {bands['moderate']}, "
        f"high {bands['high']}, critical {bands['critical']}"
    )
    print(f"  above threshold ({args.threshold:g}) {len(crappy)}")
    print(f"  mean CRAP             {sum(float(method['crap']) for method in methods) / len(methods):.2f}")
    print(f"  max CRAP              {float(methods[0]['crap']):.2f}  ({methods[0]['method']})")
    print(f"  mean complexity       {total_complexity / len(methods):.2f}")
    print(f"  complexity-weighted coverage  {weighted_coverage:.2%}")
    print()

    print(f"  {'CRAP':>7}  {'cplx':>4}  {'cov':>7}  method")
    for method in methods[:args.top]:
        flag = "!" if float(method["crap"]) > args.threshold else " "
        short = str(method["class"]).split(".")[-1]
        print(
            f" {flag}{float(method['crap']):>7.2f}  {float(method['complexity']):>4.0f}  "
            f"{float(method['coverage']):>6.1%}  {short}.{str(method['method'])[:56]}"
        )

    if crappy:
        print()
        print(f"  {len(crappy)} method(s) over threshold:")
        for method in crappy:
            print(
                f"    {float(method['crap']):.2f}  [{method['risk']}]  "
                f"{str(method['class']).split('.')[-1]}.{method['method']}"
            )
            print(
                f"          complexity {float(method['complexity']):.0f}, "
                f"coverage {float(method['coverage']):.1%}"
            )

            needed = coverage_needed(float(method["complexity"]), args.target)
            if needed is None:
                print(
                    f"          complexity alone exceeds CRAP {args.target:g}: "
                    "extract sub-methods"
                )
            elif needed > float(method["coverage"]):
                print(
                    f"          raise coverage to {needed:.1%} "
                    f"to reach CRAP < {args.target:g}"
                )
            else:
                print(f"          already covered enough for CRAP < {args.target:g}")

    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps({
            "summary": {
                "methods": len(methods),
                "excluded_methods": len(excluded),
                "risk_bands": dict(bands),
                "above_threshold": len(crappy),
                "threshold": args.threshold,
                "mean_crap": round(sum(float(method["crap"]) for method in methods) / len(methods), 3),
                "max_crap": methods[0]["crap"],
                "mean_complexity": round(total_complexity / len(methods), 3),
                "weighted_coverage": round(weighted_coverage, 5),
            },
            "methods": methods,
            "excluded": excluded,
        }, indent=2))
        print(f"\n  wrote {args.json}")

    if args.fail_over is not None and float(methods[0]["crap"]) > args.fail_over:
        print(
            f"\nFAIL: max CRAP {float(methods[0]['crap']):.2f} exceeds limit {args.fail_over:g}",
            file=sys.stderr,
        )
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
