# fog-wasi-namespace-v1: dotnet-webassembly applications

Status: selected primary workload target; production adapter and containment are
pending. The [compatibility tests](../../../tools/WasmCompatibility.Tests) exercise
the actual engine with namespace file IO, not a complete WASI implementation.
This supersedes the unimplemented Wasmtime/stdio-only `fog-wasi-v1` proposal, with
new ABI and meter identities. Old runtime locks are incompatible.

## Engine and entry point

Use [RyanLamansky/dotnet-webassembly](https://github.com/RyanLamansky/dotnet-webassembly)
at reviewed revision `70c46c0ee78abbdca92490a1881fe509605c5c3d` for the integration
baseline. Pin the built bundle, .NET runtime, platform and adapter too. The engine
provides compilation, numeric host imports and linear memory; Fog supplies WASI.

Initially accept binary wasm32 core command modules with one defined memory exported
as `memory` and `_start: () -> ()`. Require a finite memory maximum within the admitted
limit. Reject core start sections: imports cannot execute during instantiation before
the bridge owns the exported memory. Invoke the ordinary `_start` export after memory
and capabilities are installed. Preparation compiles without executing guest code.
Reject imported/shared/multiple memories, memory64, threads, components and unapproved
imports. Initially admit MVP numeric instructions; additional instruction sets require
pinned compiler fixtures. Bound table growth, module size and compilation too.

Only exactly typed, named `wasi_snapshot_preview1` functions are linkable. The import
manifest/hash identifies implemented functions and explicit unsupported stubs. Unknown
names/signatures fail linking; known functions outside the profile return NOSYS.
Supported operations absent from a provider return NOTSUP. Guests receive no arbitrary
CLR delegates, reflection, grain references or ambient filesystem adapter.

## Async execution boundary

```text
WASM command on a dedicated execution worker
    -> synchronous WASI import (guest waits here)
    -> bounded private 9P request
    -> process authority: admit + retain operation/channel ownership
    -> async namespace/provider IO
    -> process authority: validate completion + publish result
    -> WASI errno and output bytes; guest continues
```

The inspected `Runtime/FunctionImport.cs` rejects Task/ValueTask results. Ordinary
imports cannot suspend and resume the compiled WASM stack as a managed async method.
Run one invocation at a time in an independently supervised worker process. Blocking
on its IO bridge occupies its guest thread, not an Orleans scheduler thread. Bound
worker count and queued work. Asyncify or another stack transformation requires a
separate verified execution contract; a Task alone does not provide it.

Serialize admission and completion, not provider IO inside the same grain turn that
must accept exit. Use split requests/completions or explicitly safe interleaving;
default non-reentrant behavior cannot process kill while awaiting that call. No
ownership lock spans provider IO. Each request carries process incarnation, operation
ID and descriptor allocation generation, and pins its channel, rights and namespace view.

Exit stops admission and detaches ownership. Late open/create results cannot install
into an exited process even if another process still owns the shared descriptor group.
Close returned handles and record cleanup instead. Late results cannot touch replacement
guest memory or reused fds. An admitted read/write retains its lease until completion
or acknowledged abort. Cancellation does not prove an external write failed. Reconcile
uncertain effects rather than blindly replaying with a new operation ID. Durable rules
are in [ResourceLifetimes.md](../plan9-namespace/ResourceLifetimes.md).

## Namespace file capabilities

Target [WASI Preview 1](https://wasi.dev/releases/wasi-p1); use its
[pinned WITX](https://github.com/WebAssembly/WASI/tree/a2b96e81c0586125cc4dc79a5be0b78d9a059925/legacy/preview1/witx)
for layouts, flags and rights. Keep the Plan 9 syscall API separate: adapt WASI instead
of changing native create, dup, seek or namespace traversal semantics.

Guest descriptors map to retained virtual descriptors and allocation identities, never
Linux fds or bare paths. Fds 0/1/2 are explicit input/result/diagnostic resources; jobs
retain their bounded private stream behavior. Additional preopens come from authorized
virtual directories, e.g. `/app` read-only and `/data` read/write. Each carries base and
inheriting rights, flags and a retained directory channel. No host root/home/tmp or
factotum credentials are inherited. Guest fd numbers need not equal native virtual fds.

The host freezes each preopen grant as guest name, retained directory identity,
base/inheriting rights and provider scope in the admitted execution context. Provider
registration declares this capability manifest; unrecognized or unauthorized grants
fail admission. The existing job text fields do not themselves grant directory access.
Persist resource identities and authority versions, never native handles or pointers.

Resolve paths relative to the supplied directory capability. Reject absolute paths and
traversal above its root; authorize every mount/provider crossing. Use channel identity
and bounded walking, not lexical prefix checks. Symlinks, renames and mount races must
not permit escape. An opened subdirectory grants no authority above that directory.
Rights may only shrink; flags and strings cannot create authority.

| WASI surface | Required namespace support/adaptation |
| --- | --- |
| path_open | Capability-relative lookup/open; separate create-if-missing, exclusive create, truncate and directory-only. CREATE without TRUNC preserves existing contents. Exclusive creation needs atomic provider support. |
| fd_read/write | Validated vectors, access rights, short transfers and shared open-channel offsets. Positioned provider IO exists; shared offset syscalls remain pending. |
| fd_pread/pwrite | Explicit positions on seekable files, no shared-position change. Reject unrepresentable offsets, including all-ones; never reinterpret them as Plan 9's implicit-offset sentinel. |
| fd_seek/tell | Checked delta/position, explicit stream/directory restrictions. A 9P resource need not be seekable. |
| fd_close/renumber | Close detaches one slot. Renumber atomically moves ownership and replaces the target, retaining flags/rights/position. Plan 9 dup clears OCEXEC; dup then close is not atomic renumber. |
| fd_prestat_get/dir_name, fd_fdstat_get/set_rights/set_flags | Preopen metadata, file type, capability attenuation and supported per-open flags. Unsupported append/nonblocking/sync flags fail explicitly. |
| fd_readdir | WASI dirents and opaque continuation cookies over retained directory state, preserving union order. No raw 9P stat bytes or byte-offset cookies. Handle short dirent fragments per WASI. |
| fd_filestat_get, path_filestat_get | Explicit metadata type, identity and timestamp/size conversions; do not fabricate provider durability or POSIX identity guarantees. |
| create/remove, metadata mutation | Typed provider operations for directories, unlink, truncate and times. Retain the calling directory fd; 9P remove consumes a temporary fid. |
| fd_sync/datasync/allocate, append | Provider guarantees for durability, reservation and atomic append. Successful write/stat alone is insufficient. |
| rename, links, symlinks | Explicit provider extensions, atomicity and escape-safe walking. Cross-provider copy/delete is not atomic rename. |

`IResourceDataOperations` lacks several distinctions; its byte mode cannot express
native OEXCL (0x1000). Add typed open/create requests and capability negotiation while
preserving existing 9P mode-byte compatibility. First complete open/read/write/position/
close, preopens, rights, stat and directory enumeration. Broader provider features
remain explicit capabilities with defined errors, not silent successful no-ops.

## Memory, metering and cleanup

Treat i32 pointers as unsigned offsets. Use widened checked arithmetic for vector
arrays, lengths and result pointers; validate all destinations before provider effects.
Bound vectors and total copy sizes before allocating. Copy paths/write buffers into
host-owned memory before async IO; never send guest pointers to grains. Reacquire the
current memory base for result copying: `Runtime/UnmanagedMemory.cs` reallocates on
memory growth. No pointer/Span may survive IO or concurrent grow/dispose; serialize
all guest calls on an instance.

The inspected API supplies no built-in WASI context, fuel counter or interruption
hook this profile can rely on. Select `meter=fog-host-io-v1`: each host call consumes
one p_host_calls unit (including invalid calls), and validated calls charge their full
requested input/output buffer capacity against p_io_bytes before IO. Counters never
reset on retry/reconnect/status. Latch exhaustion as host-call-limit or io-limit;
exit zero cannot hide it. These meters do not count guest instructions.
Host-produced usage columns are p_host_calls_used and p_io_bytes_requested.

Mandatory deadline_ms and whole-process memory/CPU/stack containment follow
[Isolation.md](Isolation.md), including preparation and infinite loops without imports.
Reject requests for a fuel contract rather than silently substituting wall time.
Bound host waits; the independent supervisor kills/reaps wedged workers. A cancellation
token or disposing a live instance cannot safely interrupt running JIT code.

Provide bounded args/env, clocks, random, polling and proc_exit for admitted fixtures.
They grant no ambient filesystem. proc_exit unwinds into runner lifecycle handling;
it never exits the Orleans host. Normal return/exit zero is candidate success, nonzero
exit is wasm-exit and other traps are wasm-trap. Stop guest execution before disposing
memory; detach all guest descriptors, drain/reconcile operations and finish cleanup.
No Task, stack or native pointer is durable state.

Only staged job-result bytes depend on successful publication. Authorized writes to
/data or another application's ctl file may already be visible when execution fails;
there is no rollback or automatic transactional restart. Installation, upgrades,
long-lived services and durable restart build on this command milestone and need
separate lifecycle acceptance tests.

## Evidence and gates

[WasiNamespace.feature](WasiNamespace.feature) and
[ProcessFileSyscalls.feature](../plan9-namespace/ProcessFileSyscalls.feature) are design
specifications pending executable bindings. The engine fixture proves numeric imports,
compiled guest memory, async IO bridging and descriptor leases, not full WASI, Orleans
responsiveness or Linux containment. Run:

`bash scripts/check-wasm-compatibility.sh /path/to/pinned/dotnet-webassembly`

Production slices must enter the standard BDD/property/fuzz/mutation pipeline with
zero surviving or uncovered mutations. Compatibility tests are additional evidence.
