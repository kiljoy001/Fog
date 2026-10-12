#!/usr/bin/env bash
# Compiles the WASM test fixtures: the WAT sources with WABT's wat2wasm, and the Preview 1 trace
# interpreter (Fixtures/trace) with Rust through rustup. The compiled modules are committed, so the
# tests need neither; rerun this after changing a source.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURES="$ROOT/NinePSharp.Fog.Wasm.Tests/Fixtures"
WABT_VERSION="1.0.34"
RUST_VERSION="rustc 1.90.0 (1159e78c4 2025-09-14)"
RUSTUP_BIN="${RUSTUP_BIN:-$HOME/.cargo/bin}"

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

# rustup's toolchain, which has the wasm32 targets, ahead of any other rustc.
if [[ "$(PATH="$RUSTUP_BIN:$PATH" rustc --version)" != "$RUST_VERSION" ]]; then
  echo "ERROR: expected $RUST_VERSION from $RUSTUP_BIN" >&2
  exit 1
fi
(cd "$FIXTURES/trace" && PATH="$RUSTUP_BIN:$PATH" cargo build --release --locked --quiet)
cp "$FIXTURES/trace/target/wasm32-unknown-unknown/release/wasi_trace.wasm" "$FIXTURES/trace.wasm"
