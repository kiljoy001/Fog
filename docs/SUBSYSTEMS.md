# Fog Subsystems Deep Dive

This document details each major subsystem in the Fog repository, explaining their design, key classes, responsibilities, and interactions.

---

## 1. `NinePSharp.Fog` — Core Transaction Engine

The core transaction engine implements the transactional control plane (`fog-tx-v1`) based on **LibTab closed records**.

### Key Responsibilities
* **Closed Plain-Cell Records:** Strict UTF-8 serialization, canonical escaping, nil omission, duplicate detection, and a 7,168-byte cell guard using the `LibTab` engine.
* **Transaction Lifecycle:** Ephemeral server-generated transaction IDs, owner enforcement, count and byte capacity reservations, single-writer constraint, and replacement semantics.
* **Atomic Plans & Commits:** Execution of prepared plans via `FogAtomicPlan` and `FogCommitPlan`. Two-phase commit preparation guarantees that if an effect throws an exception, the transaction is marked non-retryable and reserved to prevent double execution.

### Key Types
* [`FogTransactionStore`](../NinePSharp.Fog/FogTransactionStore.cs): Manages active transactions, reservations, quotas, and lifecycle states.
* [`FogControlFile`](../NinePSharp.Fog/FogControlFile.cs): Encapsulates per-transaction control file operations (`clone`, `ctl`, `status`, inputs, outputs).
* [`FogAtomicPlan`](../NinePSharp.Fog/FogAtomicPlan.cs) & [`FogCommitPlan`](../NinePSharp.Fog/FogCommitPlan.cs): Prepare and validate atomic state transitions.
* [`FogRecordSchema`](../NinePSharp.Fog/FogRecordSchema.cs): Enforces record schemas, byte limits, and row limits.

---

## 2. `NinePSharp.Fog.Kernel` — Plan 9 Distributed Kernel Emulation

Provides complete Plan 9 kernel emulation in managed C# with true system call semantics and per-process isolation.

### Key Responsibilities
* **Process Lifecycle:** Implements Plan 9 `Process` hierarchy, state tracking, PID management, parent-child signaling, and exit wait queues (`Waitmsg`).
* **`rfork` Subsystem:** Implements all Plan 9 fork flags (`RFNAMEG`, `RFENVG`, `RFFDG`, `RFPROC`, `RFMEM`, `RFNOWAIT`). Controls whether the child process shares or copies namespaces, descriptors, and environment tables.
* **Kernel Devices:**
  * `#c` ([`ConsDevice`](../NinePSharp.Fog.Kernel/ConsDevice.cs)): Console device managing stdin, stdout, stderr, and raw terminal I/O.
  * `#|` ([`PipeDevice`](../NinePSharp.Fog.Kernel/PipeDevice.cs)): Bidirectional stream pipes with queue limits and EOF detection.
  * `#d` ([`DupDevice`](../NinePSharp.Fog.Kernel/DupDevice.cs)): File descriptor manipulation and renumbering.
  * `#p` ([`ProcessDevices`](../NinePSharp.Fog.Kernel/ProcessDevices.cs)): Process status filesystem (`/proc`).
  * `RamFs` ([`RamFs`](../NinePSharp.Fog.Kernel/RamFs.cs)): Fast, in-memory 9P filesystem for scratch space and temporary files.

### Key Types
* [`FogKernel`](../NinePSharp.Fog.Kernel/FogKernel.cs): Central kernel instance managing device registration and syscall dispatching.
* [`Process`](../NinePSharp.Fog.Kernel/Process.cs): The running process instance containing descriptor tables, current working directory, and namespace roots.
* [`RforkFlags`](../NinePSharp.Fog.Kernel/RforkFlags.cs): Bitfield enum mirroring Plan 9's `rfork(2)` flags.

---

## 3. `NinePSharp.Fog.Gefs` — Native Port of 9front `gefs(4)`

A managed implementation of 9front’s copy-on-write $B^\varepsilon$-tree filesystem, providing fault-tolerant, content-addressed storage.

### Key Responsibilities
* **$B^\varepsilon$-Tree Storage:** Log-structured copy-on-write tree that buffers upserts into interior nodes, flushing lazily down to leaves.
* **Self-Healing Blocks:** All blocks are fixed-size 16 KiB chunks identified by SHA-256 hashes. Corrupted blocks are detected on read and repaired from cluster peers.
* **Atomic Superblock Commits:** Changes become durable only when twin superblocks are written. Free block lists (`Deadlist`) ensure no storage leaks across snapshot deletions.
* **9P Server Interface:** Exposes the tree as a standard 9P2000 file server (`GefsServer`).

### Key Types
* [`Tree`](../NinePSharp.Fog.Gefs/Tree.cs): Core $B^\varepsilon$-tree engine handling insertions, deletions, node splits, and rotations.
* [`Store`](../NinePSharp.Fog.Gefs/Store.cs): Manages the on-disk storage layout, superblock transitions, and generation counters.
* [`Allocator`](../NinePSharp.Fog.Gefs/Allocator.cs) & [`Arena`](../NinePSharp.Fog.Gefs/Arena.cs): Allocates block ranges, logs allocations, and performs free-space compaction.
* [`GefsServer`](../NinePSharp.Fog.Gefs/GefsServer.cs): 9P server adapter translating 9P messages (`Twalk`, `Topen`, `Tread`, `Twrite`) into tree operations.

---

## 4. `NinePSharp.Fog.Namespaces` — Distributed Orleans Namespaces

Bridges Plan 9's dynamic namespace trees into Microsoft Orleans virtual actor grains.

### Key Responsibilities
* **Grain-Backed Roots:** The root of every shared namespace is backed by an Orleans grain (`IFogRootGrain`), reachable from any silo in the cluster.
* **Fine-Grained Authorization:** Policy enforcement (`FogAuthorizationAuthority`) checks access tokens and group memberships before granting namespace attach rights.
* **Bounded Exports:** Limits the depth, file count, and byte volume exposed to remote clients.

### Key Types
* [`FogRootGrain`](../NinePSharp.Fog.Namespaces/FogRootGrain.cs): Orleans actor managing the directory tree and attach points of a shared root.
* [`FogSharedRoot`](../NinePSharp.Fog.Namespaces/FogSharedRoot.cs): In-memory representation of a namespace tree shared across multiple processes.
* [`FogAuthorizationAuthority`](../NinePSharp.Fog.Namespaces/FogAuthorizationAuthority.cs): Evaluates capabilities and access rules for namespace binding.

---

## 5. `NinePSharp.Fog.Rc` — Plan 9 `rc` Shell Compiler & Interpreter

A complete, faithful port of Plan 9’s iconic command shell (`rc(1)`), built from scratch in C#.

### Key Responsibilities
* **Lexing & Parsing:** Tokenizes and constructs Abstract Syntax Trees (`RcTree`) matching the exact grammar of Plan 9 `rc`.
* **Bytecode Compilation:** Compiles AST nodes into efficient instruction opcodes (`RcOp`, `RcInstruction`).
* **Interpreter & Execution:** Executes compiled bytecode with full support for:
  * Variable scoping and list values (`RcList`)
  * Wildcard pattern globbing (`RcGlob`)
  * Input/output redirections (`<`, `>`, `>>`, `>[2]`)
  * Inter-process pipelines (`|`)
  * Control structures (`if`, `for`, `while`, `switch`)

### Key Types
* [`RcCompiler`](../NinePSharp.Fog.Rc/RcCompiler.cs): Translates AST trees into bytecode streams.
* [`RcParser`](../NinePSharp.Fog.Rc/RcParser.cs) & [`RcLexer`](../NinePSharp.Fog.Rc/RcLexer.cs): Syntactic and lexical analysis.
* [`RcShell`](../NinePSharp.Fog.Rc/RcShell.cs): Runtime engine executing commands against the `FogKernel`.

---

## 6. `NinePSharp.Fog.Thread` — Plan 9 `libthread(2)` Concurrency

Implements Plan 9's classic Communicating Sequential Processes (CSP) concurrency model in C#.

### Key Responsibilities
* **Typed Channels:** Asynchronous and unbuffered message channels (`Channel<T>`) for process communication.
* **Multiplexed Selection (`Alt<T>`):** Implements Plan 9’s `alt` construct, allowing a thread to simultaneously wait on multiple channel send and receive operations.
* **Rendezvous:** Barrier synchronization and coordination between concurrent tasks.

### Key Types
* [`Channel<T>`](../NinePSharp.Fog.Thread/Channel%7BT%7D.cs): CSP communication channel supporting non-blocking and asynchronous exchange.
* [`Alt<T>`](../NinePSharp.Fog.Thread/Alt%7BT%7D.cs): Multiplexed channel selector.
* [`Rendezvous`](../NinePSharp.Fog.Thread/Rendezvous.cs): Thread meeting and synchronization primitive.

---

## 7. `NinePSharp.Fog.Auth` — Plan 9 Authentication & KeyFs

Provides the complete Plan 9 security architecture, including Factotum and KeyFs.

### Key Responsibilities
* **Authentication Server (`P9anyServer`):** Implements Plan 9's `p9any` and `dp9ik` challenge-response authentication protocols.
* **Key Database (`KeyDatabase`):** Serves the two-level `/mnt/keys/{user}/` tree (`key`, `aeskey`, `pakhash`, `secret`, `log`, `status`, `expire`).
* **Hardware Storage Key Sealing:** Integrates with physical or software TPM 2.0 chips (`StorageKeySeal`) to seal secret database keys at rest.
* **Speaks-For Logic:** Evaluates delegation and principal authority rules (`SpeaksForRule`).

### Key Types
* [`P9anyServer`](../NinePSharp.Fog.Auth/P9anyServer.cs): Handles the initial authentication negotiation.
* [`KeyFsHost`](../NinePSharp.Fog.Auth/KeyFsHost.cs): Hosts the `/mnt/keys` filesystem.
* [`StorageKeySeal`](../NinePSharp.Fog.Auth/StorageKeySeal.cs): Encrypts and seals credentials against the host's TPM.

---

## 8. `NinePSharp.Fog.Server` — 9P Dispatcher & Node Services

Exposes Fog's services over secure, mutually authenticated network transports.

### Key Responsibilities
* **9P Message Dispatcher:** [`FogNinePDispatcher`](../NinePSharp.Fog.Server/FogNinePDispatcher.cs) manages client sessions, fids, open files, and message routing.
* **TLS 1.3 Transport:** Mutual TLS encryption with pinned public keys (SPKI) and certificate authorization.
* **Control File Tree:** Dynamically routes control file operations under `/control/<service>`.
* **Enrollment & Policy:** Enforces node join policies and cryptographic node identity.

### Key Types
* [`FogNinePDispatcher`](../NinePSharp.Fog.Server/FogNinePDispatcher.cs): Core message processing loop for incoming 9P packets.
* [`FogTransactionFileTree`](../NinePSharp.Fog.Server/FogTransactionFileTree.cs): Exposes active transactions as a 9P filesystem.
* [`FogNodePolicy`](../NinePSharp.Fog.Server/FogNodePolicy.cs): Verifies certificate fingerprints and enrollment state.
