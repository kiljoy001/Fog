# Initial runtime profiles

Status: proposed version-one behavior behind the existing `IWorkloadProvider` and
LibTab job interface. These choices do not install or certify a runtime.

| runtime | Profile / ABI / meter | Selected engine and useful boundary |
| --- | --- | --- |
| `math` | `fog-math-v1` / `angouri-expr-v1` / `linux-process-cpu-v1` | AngouriMath behind explicit evaluate/simplify/differentiate/solve operations; supervised process CPU allowance, hard memory/output/deadline limits |
| `wasm` | `fog-wasi-v1` / `wasip1-command-v1` / `wasmtime-fuel-v1` | Wasmtime core wasm32 command modules, WASIp1 stdin/stdout/stderr, no inherited directories or sockets |

Normative details are [Math.md](Math.md) and [Wasm.md](Wasm.md).
Both use [Isolation.md](Isolation.md). Runtime/provider names remain extensible,
but installing a different ABI or different semantics requires a different explicit
profile/version and its conformance tests. Do not silently substitute engines.

The exact engine build, native dependencies, profile, meter and platform are pinned
by the operator's `fogruntime-lock-v1` entry and verified bundle manifest. Neither
`provider_api=1` nor an upstream version string alone identifies executable bytes.
Fuel counts may change across Wasmtime releases; registration identity includes
the engine and meter so a job cannot migrate to a differently charged implementation.

Source/input remain pinned raw files under the existing job contract.
Preparation includes bounded reading, parsing, validation, compilation and artifact
loading. Limits cover preparation as well as execution; successful preparation
does not authorize an external write or remove the original admitted deadline.
No engine spawns a server, downloads packages, invokes a shell, or inherits
factotum credentials. Output stays private until host-approved terminal success.

For these process-isolated profiles the memory reservation conservatively covers
the whole execution process, including runtime overhead and resident artifact
pages, within `memory_bytes`. This is stricter than accounting only a guest
heap. Host immutable caches retain their separate cap and do not create an unlimited
per-job memory allowance. Reject a job whose budget cannot hold the selected runner.

Result bytes and error semantics are fixed per profile. Common `deadline`,
`memory-limit`, `output-limit`, `cancelled`, `policy-changed`, `worker-lost` and
`cleanup-failed` reasons remain host-owned. A provider cannot turn one of these
into success by catching an exception or returning a completion record.

The math profile exposes a bounded subset of a computer algebra library. The
initial profiles do not promise a full OS inside WASM, a chat agent/tool runner or GPU support. Those are
explicit extensions, not hidden defaults needed to interpret version one.
LLM interoperability belongs to the optional external MCP gateway,
not a built-in execution profile. The fog does not host language-model inference.
