#!/usr/bin/env bash
# Runs wasi-testsuite's Preview 1 tests under Fog through fog-wasi. Each test the divergence ledger
# names must fail, and nothing else may: a test that fails unnamed, or a named one that passes,
# fails the run, and so does a ledger entry whose scenario tag no WASI feature holds. The suite is
# run from a copy, since its tests write into their directories.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SUITE="${WASI_TESTSUITE:-$HOME/src/wasi-testsuite}"
PYTHON="${WASI_TESTSUITE_PYTHON:-$SUITE/.venv/bin/python}"
LEDGER="$ROOT/quality/wasi-divergences.json"
OUT="$ROOT/.artifacts/wasi-testsuite"

if [ ! -x "$PYTHON" ]; then
  echo "ERROR: $PYTHON is not wasi-testsuite's runner Python; set WASI_TESTSUITE_PYTHON" >&2
  exit 1
fi

missing=0
for tag in $(jq -r '.testsuite[].scenario' "$LEDGER" | sort -u); do
  if ! grep -rqw "@$tag" "$ROOT"/NinePSharp.Fog.Wasm.Tests/Features --include=*.feature; then
    echo "ERROR: the ledger names $tag, which no WASI feature holds" >&2
    missing=1
  fi
done
[ "$missing" -eq 0 ]

rm -rf "$OUT"
mkdir -p "$OUT"
dotnet publish "$ROOT/NinePSharp.Fog.Wasi" -c Release -o "$OUT/fog-wasi" -v quiet
cp -r "$SUITE" "$OUT/suite"

{
  echo "version = 1"
  jq -r '.testsuite | group_by(.suite)[] | "\n[[suite]]\nname = \"\(.[0].suite)\"" + (map("\n\n# \(.why)\n[[suite.test]]\nname = \"\(.test)\"\nexpected = \"fail\"") | join(""))' "$LEDGER"
} > "$OUT/expectations.toml"

cd "$OUT/suite"
FOG_WASI="$OUT/fog-wasi/fog-wasi" "$PYTHON" run-tests \
  -r "$ROOT/quality/wasi-testsuite/fog.py" \
  -e "$OUT/expectations.toml" \
  --disable-colors \
  --json-output-location "$OUT/results.json"
