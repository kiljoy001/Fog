#!/usr/bin/env bash
# Compiles the WASM test fixtures from their WAT sources with WABT's wat2wasm.
# The compiled modules are committed, so the tests need no WABT; rerun this after changing a .wat.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURES="$ROOT/NinePSharp.Fog.Wasm.Tests/Fixtures"
WABT_VERSION="1.0.34"

if ! command -v wat2wasm >/dev/null 2>&1; then
  echo "ERROR: wat2wasm is not on PATH" >&2
  exit 1
fi
if [[ "$(wat2wasm --version)" != "$WABT_VERSION" ]]; then
  echo "ERROR: expected wat2wasm $WABT_VERSION, found $(wat2wasm --version)" >&2
  exit 1
fi

for wat in "$FIXTURES"/*.wat; do
  # Shared and 64-bit memories belong to proposals; they are only there to be refused.
  wat2wasm --enable-threads --enable-memory64 "$wat" -o "${wat%.wat}.wasm"
done
