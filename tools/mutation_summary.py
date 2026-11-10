#!/usr/bin/env python3
"""Summarise the most recent Stryker run and optionally gate on score."""

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

# Each accepted timeout is pinned to the code it was reviewed at: the commit, and a print of its line
# with the nearest lines around it, so an edit near it takes the acceptance back until it is reviewed again.
CONTEXT = 3
COMMIT = re.compile(r"^[0-9a-f]{7,40}$")


def latest_report(root: Path) -> Path | None:
    reports = sorted(
        root.rglob("mutation-report.json"),
        key=lambda report: report.stat().st_mtime,
        reverse=True,
    )
    return reports[0] if reports else None


def site_line(lines: list[str], number: int) -> str:
    line = lines[number - 1].strip() if 0 < number <= len(lines) else ""
    if line == "{" and number > 1:
        # A bare brace would match every block in the file; name the block by its owner.
        line = lines[number - 2].strip() + " {"
    return line


def site_print(lines: list[str], number: int) -> str:
    if not 0 < number <= len(lines):
        return ""
    before = [line.strip() for line in lines[: number - 1] if line.strip()][-CONTEXT:]
    after = [line.strip() for line in lines[number:] if line.strip()][:CONTEXT]
    text = "\n".join(before + [lines[number - 1].strip()] + after)
    return hashlib.sha256(text.encode()).hexdigest()[:16]


def load_accepted(path: Path | None) -> dict[tuple[str, str, str, str], dict]:
    if path is None:
        return {}
    accepted = {}
    for entry in json.loads(path.read_text()):
        if not COMMIT.match(entry.get("commit", "")) or not entry.get("print"):
            raise ValueError(f"accepted timeout without its commit and print: {entry}")
        accepted[(entry["file"], entry["mutator"], entry["line"].strip(), entry["print"])] = entry
    return accepted


def site(path: str, lines: list[str], mutant: dict) -> tuple[str, str, str, str]:
    number = mutant.get("location", {}).get("start", {}).get("line", 0)
    return (
        path.replace("\\", "/").split("/")[-1],
        mutant.get("mutatorName", ""),
        site_line(lines, number),
        site_print(lines, number),
    )


def is_accepted(accepted: dict, path: str, lines: list[str], mutant: dict) -> bool:
    return site(path, lines, mutant) in accepted


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, default=Path(".artifacts/stryker"))
    parser.add_argument("--min-score", type=float)
    parser.add_argument(
        "--accepted-timeouts",
        type=Path,
        help="reviewed timeouts that can only end by timing out, each matched by file, mutator, source line and print",
    )
    parser.add_argument("--commit", default="", help="the commit run, named in the entries offered for timeouts not accepted")
    args = parser.parse_args()
    try:
        accepted = load_accepted(args.accepted_timeouts)
    except ValueError as error:
        print(error, file=sys.stderr)
        return 2

    report = latest_report(args.output_dir)
    if report is None:
        print("no mutation report found", file=sys.stderr)
        return 2

    data = json.loads(report.read_text())
    counts: dict[str, int] = {}
    survivors_by_file: dict[str, int] = {}

    accepted_count = 0
    seen = set()
    files = set()
    unaccepted = []
    for path, info in data.get("files", {}).items():
        lines = info.get("source", "").splitlines()
        files.add(path.replace("\\", "/").split("/")[-1])
        for mutant in info.get("mutants", []):
            status = mutant.get("status", "?")
            counts[status] = counts.get(status, 0) + 1
            if status == "Timeout":
                key = site(path, lines, mutant)
                if key in accepted:
                    accepted_count += 1
                    seen.add(key)
                else:
                    unaccepted.append(key)
            if status == "Survived":
                name = path.split("/")[-1]
                survivors_by_file[name] = survivors_by_file.get(name, 0) + 1

    killed = counts.get("Killed", 0)
    timed_out = counts.get("Timeout", 0)
    survived = counts.get("Survived", 0)
    no_coverage = counts.get("NoCoverage", 0)
    total = killed + timed_out + survived + no_coverage
    score = ((killed + accepted_count) / total * 100) if total else 0.0

    print("## Mutation testing\n")
    print(f"- **Score:** {score:.2f}%")
    print(f"- **Killed:** {killed}")
    print(f"- **Timed out:** {timed_out}" + (f" ({accepted_count} accepted)" if accepted_count else ""))
    print(f"- **Survived:** {survived}")
    print(f"- **No coverage:** {no_coverage}")

    if survivors_by_file:
        print("\n### Survivors by file\n")
        for name, count in sorted(survivors_by_file.items(), key=lambda item: -item[1]):
            print(f"- `{name}`: {count}")

    if unaccepted:
        # Ready to add to the list once reviewed, with a reason.
        print("\n### Timeouts not accepted\n")
        for name, mutator, line, mark in sorted(set(unaccepted)):
            entry = {"file": name, "mutator": mutator, "line": line, "commit": args.commit, "print": mark}
            print(f"- `{json.dumps(entry)}`")

    unseen = [entry for key, entry in accepted.items() if key[0] in files and key not in seen]
    if unseen:
        # Its code changed, or its mutant no longer times out: review it again, or take it off the list.
        print("\n### Accepted timeouts not seen\n")
        for entry in unseen:
            print(f"- `{entry['file']}` {entry['mutator']} `{entry['line']}` ({entry['commit']}, {entry['print']})")

    if args.min_score is not None and score < args.min_score:
        print(
            f"\nFAIL: mutation score {score:.2f}% below {args.min_score:g}%",
            file=sys.stderr,
        )
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
