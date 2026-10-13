#!/usr/bin/env bash
# Installs the WASI reference runtimes, wasi-testsuite and the fuzzing tools under $HOME, as
# quality/wasi-controls.json pins them, checking each downloaded archive against its hash. Needs no
# root. Run on the gate; a second run leaves what is already installed.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PINS="$ROOT/quality/wasi-controls.json"
CONTROLS="${CONTROLS:-$HOME/controls}"
SOURCES="${SOURCES:-$HOME/src}"

pin() { python3 -c "import json,sys; d=json.load(open('$PINS')); print(d['$1']['$2'])"; }

fetch() { # name: download the archive the pins name and check its hash
  local url sum file
  url="$(pin "$1" url)"
  sum="$(pin "$1" sha256)"
  file="$CONTROLS/archives/$(basename "$url")"
  mkdir -p "$CONTROLS/archives"
  [ -f "$file" ] || curl -sSfL -o "$file" "$url"
  echo "$sum  $file" | sha256sum -c --quiet
  echo "$file"
}

archive="$(fetch wasmtime)"
[ -d "$CONTROLS/wasmtime-v$(pin wasmtime version)-x86_64-linux" ] || tar -xJf "$archive" -C "$CONTROLS"

archive="$(fetch wazero)"
mkdir -p "$CONTROLS/wazero-$(pin wazero version)"
tar -xzf "$archive" -C "$CONTROLS/wazero-$(pin wazero version)"

archive="$(fetch uv)"
tar -xzf "$archive" -C "$CONTROLS"
UV="$CONTROLS/uv-x86_64-unknown-linux-gnu/uv"
UV_PYTHON_INSTALL_DIR="$CONTROLS/python" "$UV" python install "$(pin uv python)"

mkdir -p "$SOURCES"
SUITE="$SOURCES/wasi-testsuite"
[ -d "$SUITE" ] || git clone -q "$(pin wasi-testsuite repository)" "$SUITE"
git -C "$SUITE" fetch -q --all
git -C "$SUITE" checkout -q "$(pin wasi-testsuite prebuilt)"
[ -d "$SUITE/.venv" ] || UV_PYTHON_INSTALL_DIR="$CONTROLS/python" "$UV" venv -q -p "$(pin uv python)" "$SUITE/.venv"
"$UV" pip install -q -p "$SUITE/.venv/bin/python" -r "$SUITE/test-runner/requirements.txt"

AFL="$SOURCES/AFLplusplus"
[ -d "$AFL" ] || git clone -q --depth 1 --branch "$(pin aflplusplus tag)" "$(pin aflplusplus repository)" "$AFL"
[ -x "$AFL/afl-fuzz" ] || make -s -C "$AFL" afl-fuzz

command -v sharpfuzz >/dev/null 2>&1 || dotnet tool install --global "$(pin sharpfuzz tool)" --version "$(pin sharpfuzz version)"

echo "installed under $CONTROLS and $SOURCES; put $AFL and ~/.dotnet/tools on PATH for scripts/fuzz.sh"
