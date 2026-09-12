# fog-wasi-v1: bounded WASIp1 commands

Status: proposed Wasmtime execution profile. Pin the exact engine/build/config in
the runtime lock; do not use its changing default feature set as this ABI.

## Module and entry point

`source` is a binary WebAssembly core module, not WAT, a serialized native artifact
or a component. Select wasm32, MVP numeric instructions and the WASIp1 command
convention. Disable threads/shared memory, memory64, multi-memory, SIMD/relaxed SIMD,
GC, exceptions, tail-call and component-model extensions. Reject a module requiring
an unsupported feature before execution. One defined/exported memory named `memory`
and an exported `_start: () -> ()` are required; imported memory is rejected.

Preparation validates and compiles only; it must not instantiate or execute a start
section. Run instantiates and invokes `_start` exactly once under the original fuel,
memory and deadline limits. A module start section consumes the same run budget.
Reject custom imports: only standard `wasi_snapshot_preview1` functions are linkable.
No arbitrary host method, CLR object, grain factory or direct network import exists.

Use Wasmtime's WASIp1 implementation with an explicitly constructed context, as
described by its [WASIp1 embedding guide](https://docs.wasmtime.dev/examples-wasip1.html).
Do not inherit host stdin/stdout/environment or add preopened host directories.
The adapter must supply the bounded streams below rather than a default host console.

## WASI surface

- argv is exactly one string `fog-wasm`; environment is empty.
- fd 0 reads the frozen job input and reaches EOF at its end. fd 1 writes private
  result staging under output_bytes. fd 2 writes bounded job diagnostics under
  worker_stderr_bytes; those diagnostics are never the successful result or audit log.
- No preopened directories or sockets; path operations cannot reach a host path.
  Invalid descriptor/path access returns the corresponding WASI error. Socket
  operations and process signals return NOSYS. No ambient filesystem/process access
  is added merely because the WASI function exists in the imported module.
- Args/environment queries, descriptor operations on the three streams, clocks,
  random_get, poll_oneoff, sched_yield and proc_exit use WASIp1 semantics. Clocks
  are actual host clocks and random_get uses the host CSPRNG; this profile does
  not promise deterministic output for programs using them. Reads/waits are bounded
  by the admitted deadline; empty input is EOF, not an indefinite console read.
- proc_exit(0) or normal return from `_start` is candidate success; nonzero exit is
  `wasm-exit`, any other trap `wasm-trap`, fuel exhaustion `fuel-limit`.

Unknown WASI function names fail linking. All WASI buffer/count pointers are validated
against current linear memory with checked arithmetic. Bound vector lengths, total
copy bytes and host-call concurrency before allocation. Descriptor close/renumber
cannot acquire another capability; there are only the three supplied streams.
No read/write namespace facade is exposed to the guest in this initial profile.
Applications requiring additional files must use a separately specified ABI/version,
not silent directory inheritance.

## Budgets and publication

Enable Wasmtime fuel consumption; set the store allowance once to job fuel, before
instantiation. Do not replenish on yields, calls, AAN reconnect or status reads.
Guest instruction fuel follows the pinned engine. Before every WASI call, deduct
an additional `1 + ceil(B/64)` from remaining fuel, where B is the checked total
requested input/output buffer capacity; zero-buffer calls cost 1. A call cannot
evade cost with a large requested count that later transfers zero bytes. For
vector calls, sum capacities with checked arithmetic before entering host code.

Also enable epoch interruption driven by the supervisor's independent monotonic
deadline/cancel path, with traps rather than indefinite yield. Fuel and epochs
serve different purposes in Wasmtime's
[interruption model](https://docs.wasmtime.dev/examples-interrupting-wasm.html).
Neither interrupts a wedged native host function by itself; the process supervisor
is the final bound. Cap linear memory/table growth with resource limiters and enforce
the whole-process memory cap. A denied allocation/growth that makes the job exceed
its admitted memory allowance latches memory-limit, even if guest code catches a
failure return and exits zero.

Output is exact stdout bytes, with no added newline, prompt or exit-status trailer.
Stderr overflow is `diagnostic-limit`, not silently unlimited logging. Either output
failure prevents success. Success finish reason is `completed`; no partial output
is published after trap/cancel/limit failure. All contexts/memories/fds are per-job.
Compiled immutable module caches may be host-owned and quota-bound, keyed by module
digest, engine bundle, configuration and meter; never load guest-supplied native cache
bytes. Test runtime vectors and actual isolated execution, not only an echo double.
