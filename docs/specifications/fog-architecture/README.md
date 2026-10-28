# Fog: Plan 9 on a resilient cluster

Status: agreed direction; most of it is not built yet. This document sets the shape the
other specifications refine. Where an older specification disagrees, this one decides,
and [the conflicts are listed](#where-older-specifications-disagree) below.

## Goal

Install Fog on any contemporary machine and run Plan 9, complete with a shell. Add more
machines and they become one system through the namespace: every machine reaches the other
machines' files, devices and WASM applications by name, users log in and keep their secrets
on any machine, and when one machine dies the applications start again on another. Between
such systems, and with 9front machines, the ordinary Plan 9 grid applies.

## No shared kernel

Each machine runs its own Fog kernel, with its own process table, descriptors, notes, pipes
and devices, as each machine in a Plan 9 network does. No kernel state is shared between
machines. A kernel spread across machines would have to keep copies of processes,
descriptors and pipes in agreement, and after a partition the copies diverge in ways a
kernel cannot merge: a wait status delivered twice, a pipe with two ends of different ages.
Keeping each kernel to one machine removes that failure rather than repairing it.

Three things replicate or move between machines, each chosen so it cannot diverge:

| What | How |
| --- | --- |
| Accounts and cluster state | secstore, the auth server's keys and Orleans's membership table, in a store whose operations every machine applies in an order agreed by consensus ([below](#the-replicated-store-accounts-and-the-cluster)) |
| WASM application services | Orleans grains, one activation at a time, reaching their saved state on the machine that holds it ([below](#wasm-applications)) |
| Execution on another machine | rcpu's model: the child runs on the other kernel against the caller's exported namespace ([below](#running-on-another-machine)) |

The cluster appears to the outside as one host. Other Fog clusters and 9front machines are
named under `/n/{name}`, by DNS name or EmerDNS name, authenticate with dp9ik against the auth
server of their domain, and share namespaces over 9P. Inside the cluster the same rules hold
between machines: a machine is a Plan 9 host, and the cluster is joined by its namespace.

This follows Plan 9's grid, where work moves between machines by `cpu` and `import`, and the
systems that grew from it at Lsub. Plan B ("Plan B: an operating system for ubiquitous
computing environments", PerCom 2006; its manual's intro(2) and import(2)) names resources
rather than holding descriptors to them, imports "any box advertised under name sel with
constraint matching selc", and picks among them by constraints such as architecture and
network. Clive (github.com/fjballest/clive) keeps a name space as a prefix table over local
and remote ZX trees, and streams requests for high-latency links.

## Everything that is not the kernel is a process

The kernel is the process model, namespaces, descriptors, devices and the system calls.
Everything else runs as a kernel process: rc and the commands, WASM programs, and the
servers that are .NET hosts today (keyfs, authsrv, the control export and each of its
sessions, the listeners, the transaction service). A server is a program a process execs,
reaching the world through its descriptors and its namespace, as a Plan 9 file server does.

## Running on another machine

A process runs on another machine as `rcpu` runs it on a 9front cpu server (`/rc/bin/rcpu`):

- One connection, authenticated with dp9ik over TLS, carries everything.
- The caller exports its namespace over that connection (`exportfs -r /`); the remote side
  mounts it at `/mnt/term`, binds its console, and runs the command with standard input,
  output and error taken from the caller's descriptors and in the caller's directory.
- Notes travel as data: the caller writes `interrupt`, `hangup` or `kill` into a pipe it
  exports at `/mnt/cpunote`, and the remote side writes them to the process's `notepg`.
- The exit status travels back as a file, `/mnt/term/env/rstatus`. A connection lost before
  it arrives makes the caller exit `disconnected`.

Nothing here knows about a cluster: the remote child belongs to the remote kernel, and what
it shares with its caller is files. If either machine dies, the other sees the connection
hang up.

Which machine runs it is chosen by the caller, as `$cpu` and `rcpu -h` choose. Machines are
found by name in ndb; choosing among several by constraints, as Plan B does, is still to
design.

## Devices and machine-bound resources

Devices belong to the kernel of the machine they are on. Another machine reaches them by
importing that machine's tree: `/n/{machine}/dev`, `/n/{machine}/net`, `/n/{machine}/proc`,
as in Plan 9. A listening port, a TPM, a console and a local disk therefore need no
placement rules; they are where they are, and are reached by name.

## WASM applications

A WASM program runs in one of two ways.

**As a command.** A program execed from `/bin/{app}`, using WASI over its own descriptors and
namespace ([Wasm.md](../fog-v1-profiles/Wasm.md)), is an ordinary process on the kernel that
execs it, or on another machine by [rcpu's model](#running-on-another-machine). Fog does not
save a running command: if its machine dies its run is lost, as when a process crashes.

**As a service.** A long-running application that serves a file tree is an Orleans grain,
mounted at `/mnt/{app}` in any machine's namespace; the shared root lays `/mnt` out for
applications by namespace(4) convention. Each machine runs one Orleans silo, and the silos
form the cluster that hosts these grains and nothing else. A grain is a logical identity that
always exists; an activation represents it on one silo at a time. A walk, open or read of
`/mnt/{app}` reaches the activation wherever it is, and Orleans starts one on some silo if
there is none.

Grains suit applications where they do not suit a kernel. A WASM instance's state is its
linear memory and what crosses its import boundary: it holds no descriptors into a kernel, no
notes and no parent waiting on it. Between requests it can be saved and started again
elsewhere. A service's saved state stays on the file server of the machine that holds its
data, and the activation reaches it through the namespace (`/n/{machine}/…`), as an rcpu
child reaches its caller's files through `/mnt/term`; where the activation runs does not
change where the data is. When a silo dies, Orleans activates its grains on the remaining
silos the next time they are called, and each mounts its data again and loads its state.
What lived only in the dead silo's memory, and requests running there, are lost. When the
machine holding a service's data dies, the service is unavailable until it returns.

Still to design:

- A request runs in one grain turn and must finish within Orleans's response timeout, so
  work that blocks (a read that waits for input, as `/dev/cons` does) is answered by a later
  turn rather than holding one open.
- After a partition two activations of one grain can briefly coexist. Saving state with a
  conditional write by qid version ([below](#storage-is-a-9p-file-server-on-each-machine)) means only one of
  them can commit.
- Containment of a runaway guest (see [Isolation.md](../fog-v1-profiles/Isolation.md) below).

## What survives the loss of a machine

| Kind of program | When its machine dies |
| --- | --- |
| A WASM application service | Requests in flight fail with an error or an unknown outcome; the grain starts again on another silo and reads its saved state back from the machine that holds it |
| secstored and the auth server | Clients redial another machine, which holds the same replicated store |
| A program that keeps everything in memory | Its run is lost, as when a process crashes; a caller on another machine sees the connection hang up, never a hang |
| Files on the machine's own file server | Unavailable until the machine returns; lost with its disk unless copied elsewhere |

Requests in flight follow the rule the dispatcher already follows: work that cannot be
shown to have finished is reported with an unknown outcome, never silently retried.

## Storage is a 9P file server on each machine

Everything Fog keeps is kept in files on a 9P file server, as in Plan 9. There is no
database. Each machine runs its own file server over its own disk, at local speed, and other
machines reach it by name as `/n/{machine}`, as a Plan 9 terminal reaches its file server.
Nothing is copied between machines on every write: when a machine is down its files are
unavailable until it returns, and if its disk is lost they are lost, unless they were kept
elsewhere. Copying snapshots to other machines for that case is still to design.

The file server is a port of 9front's gefs (gefs(4), `sys/src/cmd/gefs` and the paper
`sys/doc/gefs.ms`), as rc and libthread are ports of 9front's. Gefs builds the 9P file
system on copy-on-write Bε trees. A commit becomes current only when the superblocks are
written, so a crash loses at most unsynced data and never corrupts the file system. Every
block pointer carries a hash of the block, so corruption is detected rather than returned,
and a birth generation, which gives snapshots and reclaims their space. Changes are messages
upserted into the tree, so a qid version or time can change without a read-modify-write and
several changes commit atomically together. Its manual still calls it experimental.

Each machine keeps its store in one ordinary file used as gefs's disk, as gefs's `-f`
option already allows. Fog reads and writes it at block offsets with .NET's random-access file
API, which works the same on Linux, macOS and Windows. The file must be on a local disk, never
NFS or SMB; it is held under an exclusive lock and preallocated, so a full disk is known before
a commit rather than during one. A commit counts as durable only once the platform has truly
flushed it: `fsync` on Linux, `FlushFileBuffers` on Windows and `F_FULLFSYNC` on macOS, verified
on each platform at startup, as [Storage.md](../fog-v1-profiles/Storage.md) already demands.

Grain state is files too. A WASM application service keeps its state on the file server of
the machine that holds its data, and reaches it through the namespace, as an rcpu child
reaches its caller's files through `/mnt/term`. Fog's Orleans storage provider is a 9P client
that keeps each grain's state in a file named by its grain type and key. Orleans needs a
conditional write, so a stale activation cannot overwrite a newer one; the file server
provides it through the file's qid version: a write names the version it read, and fails if
the file has changed since. A whole new state replaces the old in one step.

## The replicated store: accounts and the cluster

A few things must be the same on every machine and outlive any one of them: secstore's
verifiers and files, the auth server's keys (keyfs), and Orleans's membership table. They are
small and rarely written, and a wrong copy is a security failure, so they are kept in a
replicated store rather than on one machine's file server.

- Every storage machine holds the whole store, served over 9P as a file tree. secstored and
  the auth server on any machine read it locally and answer the same way, so a client whose
  machine dies redials another, and a user can log in while any 2f+1 of them run.
- A write is an operation: a create, write, remove or wstat from an authenticated session.
  Any machine accepts one and submits it to consensus, which puts the operations in one
  order. Every machine applies them in that order to its own copy; none copies another's
  result.
- Applying the same operations must give the same tree everywhere: block allocation, qids,
  versions and times depend only on the operations, with times taken from the block's agreed
  time, never a local clock. The tree's root hash is the consensus application hash, so a
  machine whose copy differs is seen at the block where it went wrong.
- A conditional write by qid version is checked when the operation is applied, in consensus
  order, which is what Orleans's membership protocol needs. The storage machines agree among
  themselves without Orleans, so the table never depends on what it serves.
- A write waits for one consensus round. That is acceptable here because these writes are a
  password change, a key file saved, a silo joining or leaving; bulk data never goes through
  the store.

The order is decided by Tendermint consensus, ported to C# from Tendermint Core v0.34
(Apache-2.0), as gefs, rc and libthread are ported from 9front.

- The port takes Tendermint's consensus state machine: rounds of proposal, prevote and
  precommit with its locking rules, the vote sets and validator set with proposer rotation,
  the write-ahead log that lets a restarted validator rejoin safely, and the guard that stops
  a validator signing two different votes for one height and round.
- Consensus messages travel over 9P like everything else: each storage machine serves its
  consensus endpoint as files. Tendermint's own peer-to-peer layer and gossip are not ported.
- The application is called directly in C#. The mempool is ported, since any machine accepts
  operations; ABCI, block and state sync, RPC and the light client are not needed.
- A committed block is final: there are no forks to roll back.
- It tolerates f faulty machines out of 3f+1, Byzantine ones included, so a cluster that must
  survive one failed machine needs four storage machines. A lying or compromised machine
  cannot change a user's verifier or key, because the others apply the operations themselves.
- The validators are the storage machines; adding or removing one is a validator update
  decided by consensus.

The port is checked against Tendermint's protocol specification and its tests, and Coyote
explores its interleavings like libthread's.

Still to design: how a machine that was down catches up (replaying the blocks it missed, or
fetching a recent tree), and how the store's history is trimmed.

### Testing storage and replication

Each layer is tested on its own and then on top of the one below, on the gate like the
interop and Coyote stages.

- **The tree on a device file.** The tree's scenarios run against a block store on a real
  device file as well as in memory. A crash injected at each write boundary, then a reopen,
  must leave the file system exactly at its last committed root, with nothing half-written
  visible. Bytes flipped on disk must make a read refuse the block. A port of gefs's
  `check.c` ends every such test: no leaked, unreachable or doubly freed blocks.
- **The file system through 9P.** The 9front interop stage mounts the file server and runs rc
  workloads: copies, `mk` builds, removes, appends, renames and wstats. The same script run on
  a fresh 9front gefs and on Fog must leave the same listings, modes, sizes and contents,
  except where Fog departs from gefs on purpose and the script says so.
- **The replicated store.**
  - A test cluster of storage machines with the consensus port takes operations through 9P
    on every machine. Every machine must reach the same root hash at every height.
  - Fault tests:
    - Kill a machine mid-round, and check the others commit without it and it catches up.
    - Partition the cluster, and check it never commits two blocks for one height.
    - Let one machine apply an operation differently, and check its hash is refused at that
      block.
    - Check that secstored and the auth server on every machine answer a login the same way
      before and after a machine is lost.
- **Interleavings.** Coyote explores the consensus port's interleavings, Byzantine and crashed
  validators included, as it does libthread's and the dispatcher's.
- **Load and soak.** A load stage drives 9P traffic at a machine's file server for hours, then
  checks it with `check.c`; slow leaks like gefs's own unfreed siblings appear only over time.

The block store therefore needs a way to inject a crash at each write boundary. A commit also
needs a single point where the root changes, so a test can stop just before and just after it.

## Getting in

A user reaches the cluster as they would a 9front machine: rc through stock `rcpu` or
`drawterm`, authenticated by the cluster's auth server, or 9P attaches with dp9ik from any
Plan 9 client. The shell is Fog's port of 9front rc, running as a process on the machine the
user dialled.

## Order of work

1. The file server, a gefs port, on each machine, and the Orleans storage provider over 9P
   with conditional writes by qid version; then the replicated store for secstore, keyfs and
   Orleans's membership table, with a C# port of Tendermint consensus ordering its operations.
2. Machines joined by their namespaces: `exportfs`, and running on another machine by
   rcpu's model, with notes and exit status carried as files; the 9front interop stage
   running `rcpu` and `import` between Fog machines and 9front.
3. The services as programs a process execs: control-export sessions, keyfs, authsrv, the
   listeners and the transaction service, each keeping its behaviour tests; secstored and
   authsrv reading the replicated store.
4. The WASM exec formats and the WASI bridges: Preview 1 core modules, then Preview 2
   components with the component layer and canonical ABI.
5. WASM application services as grains mounted at `/mnt/{app}`: the Orleans membership
   table in the replicated store, state saved by conditional write on the machine holding the
   service's data, and a silo's loss tested on a multi-silo test cluster.

Each step follows the usual gate: features, then failing tests, then code, with mutation,
fuzz and Coyote checks.

## Where older specifications disagree

| Specification | Disagreement |
| --- | --- |
| [Membership.md](../fog-v1-profiles/Membership.md) | One configured control host owns the membership table, recreated empty at each control boot. That host is a single point of failure, so the cluster cannot outlive it; the table belongs in the replicated store. |
| [Storage.md](../fog-v1-profiles/Storage.md) | Grain state is served over 9P but kept in SQLite on one control host: a database behind a file interface, and a single point of failure. Storage is a 9P file server on each machine whose contents are files, with accounts and cluster state in a replicated store. |
| [Isolation.md](../fog-v1-profiles/Isolation.md) | A Linux-only supervisor runs one OS process per job. WASM programs run inside the Fog process here, and Fog targets any contemporary machine. Containment of runaway guests (a loop without imports cannot be stopped from inside the process) still needs a design: fuel instrumented into the module, or a supervisor per machine. |
| [fog-v1-profiles README](../fog-v1-profiles/README.md), [WorkerControl.md](../fog-v1-profiles/WorkerControl.md) | Written around disposable jobs, worker leases and a control node. Fog has no jobs: it runs long-lived applications as processes or grains, and work is placed by the caller, as `rcpu -h` places it, not pulled by workers. |
| [fog-foundation README](../fog-foundation/README.md) | One explicitly configured control node. Control-node duties become processes that can run on any machine, with secstore, keys and membership in the replicated store. |

Until each is rewritten, these specifications describe what exists or was proposed, not
the direction.
