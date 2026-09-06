#!/usr/bin/env python3
"""Fail when merged NinePSharp coverage falls below configured floors."""

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

import coverage_report  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path, nargs="?")
    parser.add_argument("--min-line", type=float, default=0.0)
    parser.add_argument("--min-branch", type=float, default=0.0)
    args = parser.parse_args()

    report = coverage_report.resolve(args.report)
    root = ET.parse(report).getroot()
    line = float(root.get("line-rate", 0)) * 100
    branch = float(root.get("branch-rate", 0)) * 100

    print(
        f"coverage: line {line:.2f}% (min {args.min_line:g}%), "
        f"branch {branch:.2f}% (min {args.min_branch:g}%)"
    )

    failed = False
    if line < args.min_line:
        print(f"FAIL: line coverage {line:.2f}% below {args.min_line:g}%", file=sys.stderr)
        failed = True
    if branch < args.min_branch:
        print(f"FAIL: branch coverage {branch:.2f}% below {args.min_branch:g}%", file=sys.stderr)
        failed = True

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
