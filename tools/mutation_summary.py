#!/usr/bin/env python3
"""Summarise the most recent Stryker run and optionally gate on score."""

import argparse
import json
import sys
from pathlib import Path


def latest_report(root: Path) -> Path | None:
    reports = sorted(
        root.rglob("mutation-report.json"),
        key=lambda report: report.stat().st_mtime,
        reverse=True,
    )
    return reports[0] if reports else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, default=Path(".artifacts/stryker"))
    parser.add_argument("--min-score", type=float)
    args = parser.parse_args()

    report = latest_report(args.output_dir)
    if report is None:
        print("no mutation report found", file=sys.stderr)
        return 2

    data = json.loads(report.read_text())
    counts: dict[str, int] = {}
    survivors_by_file: dict[str, int] = {}

    for path, info in data.get("files", {}).items():
        for mutant in info.get("mutants", []):
            status = mutant.get("status", "?")
            counts[status] = counts.get(status, 0) + 1
            if status == "Survived":
                name = path.split("/")[-1]
                survivors_by_file[name] = survivors_by_file.get(name, 0) + 1

    killed = counts.get("Killed", 0) + counts.get("Timeout", 0)
    survived = counts.get("Survived", 0)
    no_coverage = counts.get("NoCoverage", 0)
    total = killed + survived + no_coverage
    score = (killed / total * 100) if total else 0.0

    print("## Mutation testing\n")
    print(f"- **Score:** {score:.2f}%")
    print(f"- **Killed:** {killed}")
    print(f"- **Survived:** {survived}")
    print(f"- **No coverage:** {no_coverage}")

    if survivors_by_file:
        print("\n### Survivors by file\n")
        for name, count in sorted(survivors_by_file.items(), key=lambda item: -item[1]):
            print(f"- `{name}`: {count}")

    if args.min_score is not None and score < args.min_score:
        print(
            f"\nFAIL: mutation score {score:.2f}% below {args.min_score:g}%",
            file=sys.stderr,
        )
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
