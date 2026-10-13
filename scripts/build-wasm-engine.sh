#!/usr/bin/env bash
# Builds the WASM engine Fog runs: dotnet-webassembly at the commit Wasm.md pins, with the patches in
# third_party/dotnet-webassembly applied, packed as the WebAssembly version NinePSharp.Fog.Wasm
# references. The package is committed in packages/, so builds need neither; rerun this after
# changing a patch.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
commit=70c46c0ee78abbdca92490a1881fe509605c5c3d
version=2.1.0-fog.1
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git clone --quiet https://github.com/RyanLamansky/dotnet-webassembly.git "$work/engine"
git -C "$work/engine" checkout --quiet "$commit"
git -C "$work/engine" -c user.name=fog -c user.email=fog@localhost am --quiet "$root"/third_party/dotnet-webassembly/*.patch
dotnet pack "$work/engine/WebAssembly/WebAssembly.csproj" -c Release \
    -p:Version="$version" -p:RepositoryCommit="$(git -C "$work/engine" rev-parse HEAD)" \
    -o "$root/packages"
