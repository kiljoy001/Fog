#!/usr/bin/env bash
# Coverage-guided fuzzing for NinePSharp via SharpFuzz and AFL++.

set -euo pipefail

TARGET="${1:-parser}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/NinePSharp.Fuzzer"
OUT="$ROOT/.artifacts/fuzz/bin/$TARGET"
FUZZ_SECONDS="${FUZZ_SECONDS:-10}"

if ! command -v sharpfuzz >/dev/null 2>&1; then
  echo "SKIP: sharpfuzz is not on PATH"
  exit 0
fi

if ! command -v afl-fuzz >/dev/null 2>&1; then
  echo "SKIP: afl-fuzz is not on PATH"
  exit 0
fi

case "$TARGET" in
  parser)
    CORPUS="$ROOT/corpus/parser/valid"
    INSTRUMENT=("NinePSharp.Parser.dll" "NinePSharp.dll")
    ;;
  filesystem|inmemory)
    CORPUS="$ROOT/corpus/backend"
    INSTRUMENT=("NinePSharp.Server.Abstractions.dll" "NinePSharp.Server.dll" "NinePSharp.dll")
    ;;
  *)
    echo "usage: FUZZ_SECONDS=30 scripts/fuzz.sh [parser|filesystem|inmemory]" >&2
    exit 2
    ;;
esac

if [ ! -d "$CORPUS" ] || [ -z "$(find "$CORPUS" -maxdepth 1 -type f -print -quit)" ]; then
  echo "SKIP: no seed corpus files in $CORPUS"
  exit 0
fi

echo "building fuzz target: $TARGET"
rm -rf "$OUT"
dotnet publish "$PROJECT/NinePSharp.Fuzzer.csproj" -c Release -o "$OUT"

for assembly in "${INSTRUMENT[@]}"; do
  if [ -f "$OUT/$assembly" ]; then
    echo "instrumenting $assembly"
    sharpfuzz "$OUT/$assembly"
  fi
done

RUN_ID="${TARGET}-$(date +%Y%m%d%H%M%S)-$$"
FINDINGS="$ROOT/.artifacts/fuzz/findings/$RUN_ID"
LOG="$ROOT/.artifacts/fuzz/logs/$RUN_ID.log"
DOTNET_BIN="$(command -v dotnet)"
mkdir -p "$(dirname "$FINDINGS")"
mkdir -p "$(dirname "$LOG")"

export AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES="${AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES:-1}"
export AFL_SKIP_BIN_CHECK="${AFL_SKIP_BIN_CHECK:-1}"
export AFL_SKIP_CPUFREQ="${AFL_SKIP_CPUFREQ:-1}"
export AFL_NO_UI="${AFL_NO_UI:-1}"
export AFL_QUIET="${AFL_QUIET:-1}"

echo "starting afl-fuzz for ${FUZZ_SECONDS}s; findings land in $FINDINGS"
set +e
timeout "${FUZZ_SECONDS}s" afl-fuzz -i "$CORPUS" -o "$FINDINGS" -t 5000 -m none \
  -- "$DOTNET_BIN" "$OUT/NinePSharp.Fuzzer.dll" "$TARGET" >"$LOG" 2>&1
status=$?
set -e

STATS="$FINDINGS/default/fuzzer_stats"
if [ -f "$STATS" ]; then
  stat_value() {
    awk -F: -v key="$1" '{gsub(/ /, "", $1); if ($1 == key) {gsub(/ /, "", $2); print $2; exit}}' "$STATS"
  }
  crashes=$(stat_value saved_crashes)
  hangs=$(stat_value saved_hangs)
  corpus=$(stat_value corpus_count)
  execs=$(stat_value execs_done)
  echo "afl-fuzz stats: corpus=${corpus:-?} execs=${execs:-?} crashes=${crashes:-?} hangs=${hangs:-?}"
fi

echo "afl-fuzz log: $LOG"

if [ "$status" -eq 124 ]; then
  echo "afl-fuzz smoke completed after ${FUZZ_SECONDS}s"
  exit 0
fi

if [ "$status" -ne 0 ]; then
  tail -n 80 "$LOG" >&2
fi

exit "$status"
