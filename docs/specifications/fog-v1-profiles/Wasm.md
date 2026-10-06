# fog-wasi-namespace-v1: WASM applications as Fog processes

Status: selected primary workload target; the exec format and WASI bridge are pending.
The [compatibility tests](../../../tools/WasmCompatibility.Tests) exercise the actual
engine with namespace file IO, not a complete WASI implementation. This supersedes the
unimplemented Wasmtime/stdio-only `fog-wasi-v1` proposal and the earlier one-shot
command model of this profile. Old runtime locks are incompatible.

## An application is a process

Each WASM application runs as one Fog kernel process (`NinePSharp.Fog.Kernel.Process`).
Installing an application drops its module at `/bin/{app}`; running it is an ordinary
rfork followed by exec of that path. Exec recognises a module by its `\0asm` header, as
it recognises `#!` scripts and `\0fog` managed programs, and replaces the process image
with the instantiated module. Nothing else starts, owns or addresses a WASM instance.

The process is the application's whole authority. Its namespace decides what names
exist, its descriptor table decides what it holds open, its environment group supplies
its environment, and rfork decides what it shares with its parent. WASI adds no second
capability system beside these; it adapts them to the Preview 1 interface.

An application is long-lived. `_start` is the application's main and may run for as long
as the application does; returning from it, or `proc_exit`, ends the process. A parent
that waits receives a Waitmsg like any other: an empty status for exit code zero, and a
status naming the exit code or the trap otherwise. Serving the application's file tree at
`/mnt/{app}` builds on this and needs its own lifecycle acceptance tests.

## Engine and module admission

Use [RyanLamansky/dotnet-webassembly](https://github.com/RyanLamansky/dotnet-webassembly)
at reviewed revision `70c46c0ee78abbdca92490a1881fe509605c5c3d` for the integration
baseline. Pin the built bundle, .NET runtime, platform and adapter too. The engine
provides compilation, numeric host imports and linear memory; Fog supplies WASI.

Exec accepts binary wasm32 core modules with one defined memory exported as `memory` and
`_start: () -> ()`. Require a finite memory maximum within the admitted limit. Reject
core start sections: imports cannot execute during instantiation before the bridge owns
the exported memory. Reject imported, shared or multiple memories, memory64, threads,
components and unapproved imports. Initially admit MVP numeric instructions; additional
instruction sets require pinned compiler fixtures. Bound table growth, module size and
compilation too. A module exec refuses fails exec with "exec header invalid" and leaves
the process image unchanged, as for any other invalid executable.

Only exactly typed, named `wasi_snapshot_preview1` functions are linkable. The import
manifest/hash identifies implemented functions and explicit unsupported stubs. Unknown
names or signatures fail linking; known functions outside the profile return NOSYS, and
supported operations a provider lacks return NOTSUP. Guests receive no CLR delegates,
reflection, grain references or ambient filesystem adapter.

## Execution

```text
the process's WASM code, on a thread of its own
    -> synchronous WASI import (the guest waits here)
    -> the same process's kernel call: pread, pwrite, open, fstat, wstat, renumber, ...
    -> async namespace and provider IO
    -> WASI errno and result bytes; the guest continues
```

The inspected `Runtime/FunctionImport.cs` rejects Task and ValueTask results, so an import
cannot suspend the compiled WASM stack as a managed async method. The guest therefore runs
on a dedicated thread, and each import blocks that thread, and only that thread, until its
kernel call completes. Kernel processes otherwise run as tasks; the WASM thread is the one
place a process holds a thread. Bound how many WASM processes may run at once. Asyncify or
another stack transformation would need a separate verified execution contract.

A kernel call made for the guest is the process's own call, with its descriptors, offsets,
user and namespace, exactly as if the process had made it natively. The kernel has no notes
yet; once it does, a note interrupts a pending call made for the guest as it would any
other, and the import returns EINTR or the process ends. Late completions cannot write into
guest memory that has been disposed or into a descriptor that has since been closed or
reused.

## Descriptors and preopens

WASI descriptors are the process's descriptors, by number. Descriptors 0, 1 and 2 are
whatever the parent left open there: a pipe, a console, a file. Preopens are the
directories the parent opened before exec: every open descriptor naming a directory is a
preopen, and `fd_prestat_dir_name` reports the name it was opened by. An application that
needs `/data` is given it by its parent opening `/data`, as an rc script would arrange,
not by a manifest. Nothing is inherited that the parent did not leave open.

Rights come from the open mode and only shrink: a directory opened OREAD gives read rights
to what is opened beneath it, and no flag or string can widen them. `fd_fdstat_set_rights`
may narrow a descriptor's rights; nothing restores them.

Paths resolve relative to the directory descriptor passed to the import, never to the
process's root or current directory. Absolute paths are refused, and so is any walk that
would leave the directory: `..` at its top, or a mount crossing it does not authorize. Use
channel identity and bounded walking, not lexical prefix checks, so renames and mount
races cannot open an escape. A subdirectory opened beneath a preopen grants nothing above it.

## Kernel calls behind WASI

Target [WASI Preview 1](https://wasi.dev/releases/wasi-p1) and its
[pinned WITX](https://github.com/WebAssembly/WASI/tree/a2b96e81c0586125cc4dc79a5be0b78d9a059925/legacy/preview1/witx)
for layouts, flags and rights. The Plan 9 calls stay as they are; WASI is adapted to them.

| WASI surface | Kernel calls and adaptation |
| --- | --- |
| path_open | Walk from the directory descriptor, then open or create. Separate create-if-missing, exclusive create, truncate and directory-only. CREATE without TRUNC preserves existing contents. Exclusive creation needs atomic provider support. |
| fd_read/write | read and write on the descriptor's shared offset; validated vectors, access rights and short transfers. |
| fd_pread/pwrite | pread and pwrite. WASI offsets are unsigned; refuse any offset above the largest signed one, so none can reach Plan 9's -1 implicit-offset sentinel. |
| fd_seek/tell | seek, with checked deltas. A directory seeks only back to its start; a pipe does not seek. |
| fd_close/renumber | close detaches one descriptor. renumber moves a descriptor in one step and closes what the target named; dup then close is not atomic. |
| fd_prestat_get/dir_name, fd_fdstat_get/set_rights/set_flags | The open directory descriptors and the names they were opened by; file type from the qid; rights from the open mode. Unsupported append, nonblocking or sync flags fail explicitly. |
| fd_readdir | dirread from the directory's start, skipping to the cookie, which counts entries; a cookie of zero seeks the directory back to its start. Emit WASI dirents in union order, including repeated names; never raw 9P stat records. Handle a short final record per Preview 1. |
| fd_filestat_get, path_filestat_get | fstat and stat, converting type, qid path, size and times; no fabricated POSIX identity or durability guarantees. |
| fd_filestat_set_size/times | fwstat of the length or modification time, with every other field as nulldir leaves it. |
| path_create_directory, path_remove_directory, path_unlink_file | create with DMDIR, and remove, walking from the directory descriptor. |
| path_rename | wstat of the name, within one directory only; a rename across directories is NOTSUP rather than copy and delete. |
| fd_sync/datasync/allocate, append | Provider guarantees for durability, reservation and atomic append; a successful write or stat alone is insufficient. |
| links, symlinks | NOTSUP: Plan 9 has neither. |
| args_get, environ_get | The argv exec was given, and the process's environment. |
| proc_exit | exits: the process ends with the code as its status. |
| clocks, random, poll_oneoff | Bounded host clocks and randomness; poll over the process's descriptors. They grant no filesystem access. |

## Memory, metering and containment

Treat i32 pointers as unsigned offsets. Use widened checked arithmetic for vector arrays,
lengths and result pointers, and validate every destination before any kernel call. Bound
vectors and total copy sizes before allocating. Copy paths and write buffers into host
memory before the call; never pass guest pointers to the kernel. Reacquire the current
memory base when copying results back: `Runtime/UnmanagedMemory.cs` reallocates as memory
grows, so no pointer or Span may survive a kernel call.

The inspected API supplies no fuel counter or interruption hook this profile can rely on.
Select `meter=fog-host-io-v1`: each host call consumes one p_host_calls unit, invalid calls
included, and validated calls charge their full requested buffer capacity against
p_io_bytes before IO. Counters never reset. Exhausting either ends the process with status
host-call-limit or io-limit; exit zero cannot hide it. These meters do not count guest
instructions.

A guest that loops without calling the host cannot be interrupted by a token or by
disposing its instance: running JIT code does not observe either. Deadlines and memory,
CPU and stack containment of the whole host process follow [Isolation.md](Isolation.md),
including infinite loops without imports.

## Exit and cleanup

Returning from `_start` or `proc_exit(0)` ends the process with an empty status. A nonzero
exit code ends it with status `wasm-exit N`, and a trap with `wasm-trap: ` and the trap's
description. In every case the guest stops before its memory is disposed, and then the
process exits as any process does: its descriptors are closed, files opened ORCLOSE are
removed, and its parent's wait returns the Waitmsg.

Nothing is rolled back. Writes the application made through its descriptors are visible
whether it exits cleanly or traps, as they are for any process.

## Evidence and gates

[WasiNamespace.feature](WasiNamespace.feature) and
[ProcessFileSyscalls.feature](../plan9-namespace/ProcessFileSyscalls.feature) are design
specifications pending executable bindings. The engine fixture proves numeric imports,
compiled guest memory, async IO bridging and descriptor leases, not full WASI or
containment. Run:

`bash scripts/check-wasm-compatibility.sh /path/to/pinned/dotnet-webassembly`

Production slices enter the standard BDD, property, fuzz, mutation and Coyote pipeline with
no surviving or uncovered mutations. Compatibility tests are additional evidence.
