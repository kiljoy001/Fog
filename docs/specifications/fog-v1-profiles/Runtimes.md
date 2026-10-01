# Initial runtime profiles

Status: proposed version-one behavior behind the existing `IWorkloadProvider` and
LibTab job interface. These choices do not install or certify a runtime.

| runtime | Profile / ABI / meter | Selected engine and useful boundary |
| --- | --- | --- |
| `wasm` | `fog-wasi-namespace-v1` / `wasip1-namespace-v1` / `fog-host-io-v1` | dotnet-webassembly core wasm32 commands; Fog supplies WASIp1 imports and explicit virtual directory capabilities |

The selected primary WASM workload contract is defined in [Wasm.md](Wasm.md) and uses [Isolation.md](Isolation.md). Runtime/provider names remain extensible,
but installing a different ABI or different semantics requires a different explicit
profile/version and its conformance tests. Do not silently substitute engines.

The exact engine build, native dependencies, profile, meter and platform are pinned
by the operator's `fogruntime-lock-v1` entry and verified bundle manifest. Neither
`provider_api=1` nor an upstream version string alone identifies executable bytes.
This engine selection supersedes the unimplemented Wasmtime proposal. Host IO metering
and supervised deadlines do not provide instruction fuel. Reject old fuel contracts;
registration identity includes the engine, ABI and meter.

Source/input remain pinned raw files under the existing job contract.
Preparation includes bounded reading, parsing, validation, compilation and artifact
loading. Limits cover preparation as well as execution; successful preparation
does not authorize an external write or remove the original admitted deadline.
No engine spawns a server, downloads packages, invokes a shell, or inherits
factotum credentials. Staged job output stays private until host-approved terminal success. Explicitly
authorized namespace file effects may already be visible; execution is not a transaction.

For these process-isolated profiles the memory reservation conservatively covers
the whole execution process, including runtime overhead and resident artifact
pages, within `memory_bytes`. This is stricter than accounting only a guest
heap. Host immutable caches retain their separate cap and do not create an unlimited
per-job memory allowance. Reject a job whose budget cannot hold the selected runner.

Result bytes and error semantics are fixed per profile. Common `deadline`,
`memory-limit`, `output-limit`, `cancelled`, `policy-changed`, `worker-lost` and
`cleanup-failed` reasons remain host-owned. A provider cannot turn one of these
into success by catching an exception or returning a completion record.

The proposed WASM profile does not promise a full OS inside WASM, a chat agent/tool runner or GPU support. Those are
explicit extensions, not hidden defaults needed to interpret version one.
LLM interoperability belongs to the optional external MCP gateway,
not a built-in execution profile. The fog does not host language-model inference.
