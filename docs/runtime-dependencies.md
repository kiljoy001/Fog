# Runtime dependency notes

Current development focuses on Plan 9 namespace emulation and Orleans resource
adapters. The symbolic-math execution project and its package dependencies have
been removed. No guest execution runtime is required for namespace operations.

The following Wasmtime notes are historical development evidence for a deferred
workload-provider proposal, not evidence of an implemented or registered provider.

Current primary target: [dotnet-webassembly with a Fog WASI bridge](specifications/fog-v1-profiles/Wasm.md).
Run `bash scripts/check-wasm-compatibility.sh /path/to/pinned/dotnet-webassembly`
for the selected source baseline. No production engine bundle is installed by that test.

## Wasmtime (superseded development evidence)

No existing Wasmtime build was found in the sibling repositories. With permission,
downloaded the official [Wasmtime 44.0.0 Linux x86_64 release](https://github.com/bytecodealliance/wasmtime/releases/tag/v44.0.0)
and verified the archive against the SHA-256 published in GitHub's release metadata
before extraction. No global installation or unpinned install script was used.

- Archive: `wasmtime-v44.0.0-x86_64-linux.tar.xz`, 12,391,896 bytes.
- Published/observed SHA-256:
  `52eba06fe9f4364aa6164a4a3eafb2ca692ba9a756cbe8137b5574871f8cbfc8`.
- Local executable: `.artifacts/tools/wasmtime-v44.0.0-x86_64-linux/wasmtime`.
- Executable SHA-256:
  `b5a5d5f09c47b71caf5f6111c23c3ccf7b4e3c706a057fe2dcfd849b0c3d40e2`.
- Reported version: `wasmtime 44.0.0 (af382d7d9 2026-04-20)`.

Using `run -W fuel=1000` on the checked-in `tools/runtime-vectors` WAT fixtures:
normal return succeeds, WASIp1 output is exactly `fog-wasi-ok\n`, and the infinite
loop fails with `all fuel consumed by WebAssembly`. These trusted development WAT
fixtures do not authorize WAT as job input; `fog-wasi-v1` accepts binary modules only.
The CLI uses a host compilation cache in this diagnostic invocation, not the final
fog sandbox. Its fuel test does **not** prove the required extra per-WASI-call gas
charge, deadline enforcement, private bounded output or whole-process isolation.

Downloaded binaries remain ignored generated artifacts. The .NET embedding package
and a complete pinned runner bundle have not yet been installed.
