# fog-wasi-namespace-v1: WASM applications as Fog processes

Status: selected primary workload target; the exec formats and both WASI bridges are pending.
The [compatibility tests](../../../tools/WasmCompatibility.Tests) exercise the actual
engine with namespace file IO, not a complete WASI implementation. This supersedes the
unimplemented Wasmtime/stdio-only `fog-wasi-v1` proposal and the earlier one-shot
command model of this profile. Old runtime locks are incompatible.

Fog runs both WASI previews: Preview 1 core modules and Preview 2 components. Both are
adapted to the same kernel calls, through one layer that resolves paths, keeps rights,
converts stats and maps errors, so each rule that keeps an application inside its
authority is written once.

## Sources

| What | Pinned at |
| --- | --- |
| WASI Preview 1 | [WITX](https://github.com/WebAssembly/WASI/tree/a2b96e81c0586125cc4dc79a5be0b78d9a059925/legacy/preview1/witx) at `a2b96e81c0586125cc4dc79a5be0b78d9a059925`: `wasi_snapshot_preview1.witx` and `typenames.witx` |
| WASI Preview 2 | [WASI 0.2.12](https://github.com/WebAssembly/WASI/tree/v0.2.12/proposals) (tag `v0.2.12`, `281ba75`, released 2026-06-02): the WIT of `wasi:io`, `wasi:clocks`, `wasi:random`, `wasi:filesystem`, `wasi:sockets` and `wasi:cli`, and [path-resolution.md](https://github.com/WebAssembly/WASI/blob/v0.2.12/proposals/filesystem/path-resolution.md) |
| Component model | [design/mvp](https://github.com/WebAssembly/component-model/tree/6c48079/design/mvp) at `6c48079`: Explainer.md, Binary.md, CanonicalABI.md and WIT.md, without any emoji-gated feature |
| Conformance | [wasi-testsuite](https://github.com/WebAssembly/wasi-testsuite) at `5ea11d8`, which tests Preview 1 and Preview 3 but not Preview 2 |
| Engine | [dotnet-webassembly](https://github.com/RyanLamansky/dotnet-webassembly) at `70c46c0ee78abbdca92490a1881fe509605c5c3d` |

The component model text at `6c48079` already describes Preview 3's async features. Everything
added since WASI 0.2 is marked there with an emoji ("gated"); Fog's Preview 2 profile is the
ungated text only, which is what WASI 0.2 shipped.

## An application is a process

Each WASM application runs as one Fog kernel process (`NinePSharp.Fog.Kernel.Process`).
Installing an application drops its module or component at `/bin/{app}`; running it is an
ordinary rfork followed by exec of that path. Exec recognises WASM by its preamble, as it
recognises `#!` scripts and `\0fog` managed programs, and replaces the process image with
the instantiated application. Nothing else starts, owns or addresses a WASM instance.

| Preamble | Form | Interface | Entry |
| --- | --- | --- | --- |
| `00 61 73 6d 01 00 00 00` | core module | WASI Preview 1: `wasi_snapshot_preview1` imports | exported `_start: () -> ()` |
| `00 61 73 6d 0d 00 01 00` | component | WASI Preview 2: `wasi:*@0.2.N` imports | exported `wasi:cli/run@0.2.N`'s `run: func() -> result` |

Any other version or layer after the magic fails exec with "exec header invalid".

The process is the application's whole authority. Its namespace decides what names
exist, its descriptor table decides what it holds open, its environment group supplies
its environment, and rfork decides what it shares with its parent. WASI adds no second
capability system beside these; it adapts them to each preview's interface.

An application is long-lived. Its entry is the application's main and may run for as
long as the application does; returning from it, or exiting, ends the process. A parent
that waits receives a Waitmsg like any other: an empty status for success, and a status
naming the exit code or the trap otherwise. Serving the application's file tree at
`/mnt/{app}` builds on this and needs its own lifecycle acceptance tests.

## Engine and admission

dotnet-webassembly compiles and runs core modules: numeric host imports and linear
memory. Fog supplies WASI, and for Preview 2 the component layer as well: the engine has
no component support, so Fog parses and validates the component, instantiates its core
modules with the engine, links them as the component says, and lifts and lowers values
across its imports and exports by the canonical ABI.

Every core module, alone or inside a component, meets the same limits: binary wasm32
with one defined memory, a finite memory maximum within the admitted limit, no core start
section (imports cannot run during instantiation, before the bridge owns the memory), no
imported, shared or multiple memories, no memory64 or threads, and only MVP numeric
instructions until additional instruction sets have pinned compiler fixtures. Table growth,
module size and compilation are bounded. A Preview 1 module exports its memory as
`memory`; a component names the memory its canonical options use.

A Preview 1 module links only exactly typed, named `wasi_snapshot_preview1` functions.
The import manifest/hash identifies implemented functions and explicit unsupported
stubs. Unknown names or signatures fail exec; known functions outside the profile return
NOSYS, and supported operations a provider lacks return NOTSUP.

A component links only these interfaces, imported at any version from 0.2.0 to 0.2.12,
with each function typed as that version defines it:

- `wasi:io/{error, poll, streams}`
- `wasi:clocks/{monotonic-clock, wall-clock}`
- `wasi:random/{random, insecure, insecure-seed}`
- `wasi:filesystem/{types, preopens}`
- `wasi:sockets/{network, instance-network, tcp, tcp-create-socket, udp, udp-create-socket, ip-name-lookup}`
- `wasi:cli/{environment, exit, stdin, stdout, stderr, terminal-input, terminal-output, terminal-stdin, terminal-stdout, terminal-stderr}`

Exec refuses a component that imports anything else, including `wasi:http`,
`wasi:clocks/timezone` (unstable in 0.2.12), any 0.3 interface, or a function newer than the
version it imports (such as `exit-with-code`, added in 0.2.12, imported at 0.2.11). It
refuses any gated feature: `async`, `stream`, `future`, `map`, `error-context`, value
imports or exports, a component start function, fixed-length lists, threads and the rest.
It also refuses a component that does not export `wasi:cli/run` as a `func() -> result`.

Guests receive no CLR delegates, reflection, grain references or ambient filesystem
adapter. A refused application fails exec with "exec header invalid" and leaves the
process image unchanged, as for any other invalid executable.

## Execution

```text
the process's WASM code, on a thread of its own
    -> synchronous WASI import (the guest waits here)
    -> the same process's kernel call: pread, pwrite, open, fstat, wstat, renumber, ...
    -> async namespace and provider IO
    -> WASI result in guest memory; the guest continues
```

The inspected `Runtime/FunctionImport.cs` rejects Task and ValueTask results, so an import
cannot suspend the compiled WASM stack as a managed async method. The guest therefore runs
on a dedicated thread, and each import blocks that thread, and only that thread, until its
kernel call completes. Kernel processes otherwise run as tasks; the WASM thread is the one
place a process holds a thread. Bound how many WASM processes may run at once. For a
component, the import is the canonical ABI's lowered function; lifting its arguments,
the kernel call and lowering its results happen on that same thread.

Preview 2 streams do not block: `read` returns what is ready, possibly nothing, and the
guest waits through a pollable. Fog keeps that promise with read-ahead: a stream holds a
buffer that a kernel read fills in the background, `read` returns what the buffer holds,
and the stream's pollable is ready once the buffer holds bytes, end of file or an error.
For a descriptor without positions, such as a pipe or the console, the bytes read ahead
belong to the descriptor, not to the stream, so a stream dropped and taken again loses
none. A file's streams read at their own offsets and do not disturb each other. A blocking
read, `pollable.block` and `poll` block the guest's thread only. Preview 1's `poll_oneoff`
waits on the same readiness.

A kernel call made for the guest is the process's own call, with its descriptors, offsets,
user and namespace, exactly as if the process had made it natively. The kernel has no notes
yet; once it does, a note interrupts a pending call made for the guest as it would any
other, and the import returns EINTR (`interrupted`) or the process ends. Late completions,
including read-ahead, cannot write into guest memory that has been disposed or into a
descriptor that has since been closed or reused.

Preview 3 (WASI 0.3, released 2026-08-11) makes imports async in the component model
itself, which would let a guest wait without holding a thread. It needs the gated async
features above and is outside this profile.

## Descriptors and preopens

WASI descriptors are the process's descriptors. In Preview 1 they are the same numbers.
In Preview 2 each `descriptor` resource holds a process descriptor of its own, and
dropping the resource closes that descriptor; streams taken from a descriptor keep its
channel open until they are dropped too.

Descriptors 0, 1 and 2 are whatever the parent left open there: a pipe, a console, a
file. Preview 2's stdin, stdout and stderr are streams on them, and the `terminal-*`
interfaces return a terminal only for a descriptor on the console. Preopens are the
directories the parent opened before exec: every open descriptor naming a directory is a
preopen, under the name it was opened by, which the kernel's fd2path gives. Preview 1 reports them through
`fd_prestat_get` and `fd_prestat_dir_name`; Preview 2's `get-directories` returns a
descriptor for each, a duplicate of the preopen, so dropping it closes only the
duplicate. An application that needs `/data` is given it by its parent opening `/data`,
as an rc script would arrange, not by a manifest. Nothing is inherited that the parent
did not leave open. `initial-cwd` returns none, so relative paths resolve from the
preopens.

## Rights

Plan 9 opens a directory only for reading, so a directory's open mode cannot carry the
right to change what is in it. A preopen therefore starts with every right; the file
server still checks each call against the process's user and the file's permissions, as
it does for any process, and the namespace still decides what names exist. A parent
grants less the Plan 9 way: through the namespace it builds and the user it runs as.

WASI's rights only narrow from there. In Preview 1, `fd_fdstat_set_rights` narrows a
descriptor's rights, and what is opened beneath a directory gets no right the
directory lacks; nothing restores a right. In Preview 2, a descriptor's flags are fixed
when it is opened: `open-at` asking for `write` or `mutate-directory`, or with `create`
or `truncate`, fails with `read-only` beneath a directory without `mutate-directory`.

## Paths

Paths resolve relative to the directory descriptor passed to the call, never to the
process's root or current directory. Plan 9 has no open relative to a descriptor, so the
bridge keeps a map from each directory the application holds, by the path the application
knows it by, to two things: its true path in the process's namespace, and its identity, the
file server and qid path it had when the application got it. A preopen enters the map under
the name it was opened by; a directory opened beneath one enters it under that name and what
followed.

A call first cleans the application's path. An absolute path is an escape, and so is a `..`
that climbs above the directory the call names; as path-resolution.md requires, a path that
steps outside and comes back, such as `sub/../../data/f` beneath `/data`, is an escape too,
so nothing is learned about what lies outside. An escape fails with NOTCAPABLE in Preview 1
and `not-permitted` in Preview 2, and nothing is resolved for it. The call then resolves the
directory's true path by name through the namespace as it is now, checks that what it finds
has the identity the map holds, and walks the rest of the path from it.

The namespace is the process's authority, as for any Plan 9 process: a bind beneath a
directory the application holds applies to the application's next call. A directory it
holds, though, stays the one it was given, as a Plan 9 descriptor keeps what it opened: if
what the true path now names has another identity, because something was bound over it,
replaced it or moved it, the call fails with NOENT (`no-entry`) and nothing is walked from it.
The application's own path_rename and path_remove_directory update the map, so a directory it
renames stays usable under its new name. A directory moved by another process is not
followed; reference runtimes, whose descriptors follow it, differ there, and the divergence
ledger names it.

Only processes that share the application's namespace can change it. A parent that does not
want that runs the application in a namespace of its own (rfork with RFNAMEG or RFCNAMEG).
Between the identity check and the open, such a process could still bind over the directory;
it holds that authority already, and nothing outside its namespace is exposed.

## Kernel calls behind WASI

The Plan 9 calls stay as they are; WASI is adapted to them.

| Preview 1 | Preview 2 | Kernel calls and adaptation |
| --- | --- | --- |
| path_open | open-at | Resolve beneath the directory descriptor, then open or create. Separate create-if-missing, exclusive create, truncate and directory-only. CREATE without TRUNC preserves existing contents: what exists is opened, since a Plan 9 create truncates. Exclusive creation needs atomic provider support. A directory opens only for reading, whatever its rights, as devopen and lib9p allow; truncating one is ISDIR. |
| fd_read, fd_write | read-via-stream, write-via-stream, input-stream, output-stream | Preview 1: read and write on the descriptor's shared offset; validated vectors, access rights and short transfers. Preview 2: streams at their own offsets, by pread and pwrite, or by read and write for a descriptor without positions. |
| fd_pread, fd_pwrite | read, write | pread and pwrite. WASI offsets are unsigned; refuse any offset above the largest signed one, so none can reach Plan 9's -1 implicit-offset sentinel. |
| fd_seek, fd_tell | (streams have their own offsets) | seek, with checked deltas. A directory seeks only back to its start; a pipe does not seek. |
| fd_close, fd_renumber | resource drop | close detaches one descriptor. renumber moves a descriptor in one step and closes what the target named; dup then close is not atomic. |
| fd_prestat_get, fd_prestat_dir_name, fd_fdstat_get, fd_fdstat_set_rights, fd_fdstat_set_flags | get-directories, get-flags, get-type | The open directory descriptors and the names they were opened by; file type from the qid; rights and flags as above. Unsupported append, nonblocking or sync flags fail explicitly. |
| fd_readdir | read-directory, directory-entry-stream | dirread from the directory's start. Preview 1 counts entries with its cookie and lists `.` and `..` first, as its WITX says; `..` at a preopen's top reports nothing about what lies above. Preview 2 omits `.` and `..`, and each `read-directory` is a new stream from the start. Entries come in union order, repeated names included; never raw 9P stat records. |
| fd_filestat_get, path_filestat_get | stat, stat-at | fstat and stat: type from the qid, size from the length, modification and access times from mtime and atime; one link; no status-change time (Preview 2 none, Preview 1 the modification time); no fabricated POSIX identity. Preview 1's inode is the qid path. |
| (none) | is-same-object, metadata-hash, metadata-hash-at | Same object: the same qid path on the same file server. The metadata hash covers the qid path and version, the length and mtime, with a secret of the process, so it changes whenever the file does. |
| fd_filestat_set_size, fd_filestat_set_times, path_filestat_set_times | set-size, set-times, set-times-at | fwstat or wstat of the length or modification time, with every other field as nulldir leaves it. Access time cannot be set and fails NOTSUP (`unsupported`) unless left unchanged. |
| path_create_directory, path_remove_directory, path_unlink_file | create-directory-at, remove-directory-at, unlink-file-at | create with DMDIR, and remove, walking from the directory descriptor. Remove checks the file's type first, so unlinking a directory fails ISDIR (`is-directory`) and removing a file as a directory fails NOTDIR (`not-directory`). |
| path_rename | rename-at | wstat of the name, within one directory only; a rename across directories fails NOTSUP in Preview 1 and `cross-device` in Preview 2, rather than copy and delete. A Plan 9 wstat does not replace, so a rename onto an existing name is EXIST (`exist`). |
| fd_sync, fd_datasync | sync, sync-data | The provider's durability: on gefs a null wstat, which commits. A provider without a durability guarantee fails NOTSUP (`unsupported`). Neither has effect on a descriptor not open for writing. |
| fd_allocate, fd_advise | advise | fd_allocate fails NOTSUP. Advice is accepted and ignored. |
| (append flag) | append-via-stream | An append-only (DMAPPEND) file appends at its end on every write. Any other file fails NOTSUP (`unsupported`): a seek to the end and a write are not atomic. |
| path_link, path_symlink, path_readlink | link-at, symlink-at, readlink-at | NOTSUP (`unsupported`): Plan 9 has neither links nor symlinks. |
| args_get, environ_get | get-arguments, get-environment | The argv exec was given, and the process's environment. |
| proc_exit | exit, exit-with-code, run's result | exits: the process ends with the status below. |
| clock_time_get, clock_res_get | wall-clock, monotonic-clock | Host clocks, bounded in resolution. Preview 1's process and thread CPU-time clocks fail NOTSUP. |
| random_get | random, insecure, insecure-seed | The host's cryptographic random source, fresh on every call. |
| poll_oneoff | poll, pollable, subscribe-* | Readiness of the process's descriptors and of clock instants. Preview 1 with no subscriptions fails INVAL; Preview 2's `poll` with an empty list traps. |
| sock_accept, sock_recv, sock_send, sock_shutdown | wasi:sockets | No descriptor is a socket, so Preview 1's calls fail NOTSOCK. Preview 2's socket creation and name lookup fail `not-supported` until Fog has a network device. |
| proc_raise | (none) | NOSYS. sched_yield yields the guest's thread. |

Errors are the kernel's error strings, mapped once for both previews:

| Kernel error | Preview 1 | Preview 2 |
| --- | --- | --- |
| file does not exist | NOENT | no-entry |
| file already exists | EXIST | exist |
| permission denied | ACCES | access |
| file is a directory | ISDIR | is-directory |
| not a directory | NOTDIR | not-directory |
| directory is not empty (gefs), has children (lib9p, so ramfs) | NOTEMPTY | not-empty |
| file system full | NOSPC | insufficient-space |
| fd out of range or not open | BADF | bad-descriptor |
| mounted directory forbids creation | ROFS | read-only |
| i/o timeout, or any other | IO | io |

## Canonical ABI

Every value crossing a Preview 2 import is lifted from or lowered into guest memory by the
canonical ABI, in the host, before any kernel call. Its traps are the canonical ABI's:

- Strings are checked for their encoding (UTF-8, UTF-16 or Latin-1+UTF-16, as the
  component's options say); an invalid sequence or a surrogate code point traps. A string
  longer than `MAX_STRING_BYTE_LENGTH` and a list longer than `MAX_LIST_BYTE_LENGTH` trap.
- A pointer that is misaligned for its type, or a range that runs past the end of memory,
  traps. Arithmetic is widened, so no range wraps.
- Results that need memory are placed by the component's `realloc`; its result is checked
  like any other pointer. `realloc` and `post-return` may not call an import; one that
  does traps.
- A handle index that names nothing, or names a resource of another type, traps. An owned
  handle that is lent out cannot be dropped or passed on; a borrowed one is the guest's
  only for the call. A pollable is a child of its stream, and dropping a stream or
  descriptor before the pollables and streams taken from it traps.
- Each handle table is bounded; the handle past its bound traps.

Lists of bytes are copied between guest memory and host memory; no Span on guest memory
survives a kernel call, since `Runtime/UnmanagedMemory.cs` reallocates as memory grows.

## Memory, metering and containment

Treat i32 pointers as unsigned offsets. Use widened checked arithmetic for vector arrays,
lengths and result pointers, and validate every destination before any kernel call. Bound
vectors and total copy sizes before allocating. Copy paths and write buffers into host
memory before the call; never pass guest pointers to the kernel. Reacquire the current
memory base when copying results back.

The inspected API supplies no fuel counter or interruption hook this profile can rely on.
Select `meter=fog-host-io-v1`: each host call consumes one p_host_calls unit, invalid calls
included, and validated calls charge their full requested buffer capacity against
p_io_bytes before IO. For a component, a host call is one call of a lowered import, and
the byte capacity is every list's and string's length. Counters never reset. Exhausting
either ends the process with status host-call-limit or io-limit; exit zero cannot hide
it. These meters do not count guest instructions.

A guest that loops without calling the host cannot be interrupted by a token or by
disposing its instance: running JIT code does not observe either. Deadlines and memory,
CPU and stack containment of the whole host process follow [Isolation.md](Isolation.md),
including infinite loops without imports.

## Exit and cleanup

| How it ends | Status |
| --- | --- |
| `_start` returns, `proc_exit(0)`, `run` returns ok, `exit(ok)`, `exit-with-code(0)` | empty |
| `proc_exit(N)` or `exit-with-code(N)`, N not zero | `wasm-exit N` |
| `run` returns err, `exit(err)` | `wasm-exit 1` |
| a trap | `wasm-trap: ` and the trap's description |

In every case the guest stops before its memory is disposed, and then the process exits
as any process does: its descriptors are closed, files opened ORCLOSE are removed, and its
parent's wait returns the Waitmsg.

Nothing is rolled back. Writes the application made through its descriptors are visible
whether it exits cleanly or traps, as they are for any process. Bytes a Preview 2 output
stream had not flushed when the process ended are lost, as the stream's WIT warns.

## Differential testing

Fog's WASI is compared against reference runtimes as a control
([WasiDifferential.feature](WasiDifferential.feature)). The controls are pinned by version
and release hash and installed under the gate's home directory:

| Control | Previews | Why |
| --- | --- | --- |
| Wasmtime 49.0.2 | 1 and 2 | The reference host for both previews. |
| wazero 1.12.0 | 1 | Independent of Wasmtime, whose Preview 1 is built on its Preview 2. |
| jco 1.37.0, on its own pinned Node | 2 | Independent of Wasmtime's component and WASI 0.2 code. |

The trace interpreter is one small program built twice: a Preview 1 core module that calls
`wasi_snapshot_preview1` directly, and a Preview 2 component that calls the 0.2.12
interfaces directly, neither through libc. It reads a script of calls on stdin and writes a
trace on stdout, one line for each call: the call, its arguments, and either `ok` with its
results or the errno or error code. Every host starts it on the same starting tree,
preopened as `/data`: a temporary directory for the controls, a Fog directory with the same
contents for Fog. After it ends, the final trees are compared by names, types, lengths and
contents.

Traces are normalised before they are compared: inode numbers, times, readdir cookies,
metadata hashes, random bytes and clock values are replaced by what a program may rely on
about them, plain directories are compared sorted by name, and errors, traps and refusals
by their kind only. Union directories and permissions are left out: no control has union
directories, and a control's permission errors depend on the gate's own user. Fog's own
scenarios cover both.

Where both controls agree, Fog must agree with them, unless the divergence ledger
(`quality/wasi-divergences.json`) names the difference and the scenario that states Fog's
behaviour. The run fails on a difference the ledger does not name, on a ledger entry that
no longer differs, on one naming a scenario that does not exist, and on one for a call
where the controls disagree with each other. Where the controls disagree, the call is
recorded as left to the implementation and Fog is held to its own scenarios. Generated
scripts are shrunk to the shortest that still differs. A script whose trace changes
between two runs under one host fails the run as nondeterministic, and the generator is
fixed rather than the script skipped.

## Evidence and gates

[WasiNamespace.feature](WasiNamespace.feature) (Preview 1, and what both previews share),
[WasiPreview2.feature](WasiPreview2.feature), [WasmComponents.feature](WasmComponents.feature),
[WasiDifferential.feature](WasiDifferential.feature)
and [ProcessFileSyscalls.feature](../plan9-namespace/ProcessFileSyscalls.feature) are design
specifications pending executable bindings. The engine fixture proves numeric imports,
compiled guest memory, async IO bridging and descriptor leases, not full WASI or
containment. Run:

`bash scripts/check-wasm-compatibility.sh /path/to/pinned/dotnet-webassembly`

Fixture applications are built with pinned toolchains: wasi-sdk for C and Rust's
`wasm32-wasip1` and `wasm32-wasip2` targets, each pinned by version and hash. The Preview 1
tests of wasi-testsuite at `5ea11d8` run against Fog as conformance evidence, with any
expected failure listed and explained. No shared suite tests Preview 2, so its evidence is
these scenarios and their fixtures.

Production slices enter the standard BDD, property, fuzz, mutation and Coyote pipeline with
no surviving or uncovered mutations. The component parser and the canonical ABI are fuzzed;
read-ahead against stream drop, descriptor close and process exit are Coyote scenarios.
Compatibility tests are additional evidence.
