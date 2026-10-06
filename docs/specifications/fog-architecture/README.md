# Fog: Plan 9 on a resilient cluster

Status: agreed direction; most of it is not built yet. This document sets the shape the
other specifications refine. Where an older specification disagrees, this one decides,
and [the conflicts are listed](#where-older-specifications-disagree) below.

## Goal

Install Fog on any contemporary machine and run Plan 9, complete with a shell. Add more
machines and they become one system: processes spread across them, and when one machine
dies its work carries on elsewhere. Between such systems, and with 9front machines, the
ordinary Plan 9 grid applies.

## Two levels

**Inside a cluster: one system image.** Each machine runs one Fog silo, and Orleans
membership joins the silos into a cluster. The cluster runs one Fog kernel spread across
its machines: one pid space, namespaces and processes that live in grains, placement
decided at fork, and recovery from grain state when a machine is lost. To a program, the
cluster is one Plan 9 machine.

**Between clusters: a Plan 9 grid.** A cluster appears to the outside as one host. Other
Fog clusters and 9front machines are named under `/n/{name}`, by DNS name or EmerDNS name,
authenticate with dp9ik against the auth server of their domain, and share namespaces over
9P. Nothing at this level knows that a host is a cluster.

A classic Plan 9 grid has a kernel and a process table per machine, and work moves between
machines by `cpu` and `import`. Fog keeps that between hosts and replaces the single machine
with a cluster.

## Silos, grains and the cluster

A silo is one Orleans server: one .NET process on one machine, hosting many grain
activations. A grain is a logical identity that always exists; an activation is the object
that represents it on exactly one silo at a time. When a silo dies, Orleans activates its
grains on the remaining silos the next time they are called, loading their persisted state.
What lived only in the dead silo's memory, and calls that were running there, are lost.

Fog's resilience is therefore exactly as good as what is in grain state. The design puts
the things that must survive there, and says plainly what does not.

## Everything that is not the kernel is a process

The kernel is the process model, namespaces, descriptors, devices and the system calls.
Everything else runs as a kernel process: rc and the commands, WASM applications, and the
servers that are .NET hosts today (keyfs, authsrv, the control export and each of its
sessions, the listeners, the transaction service). A server is a program a process execs,
reaching the world through its descriptors and its namespace, as a Plan 9 file server does.

Every process is its own grain, keyed by its pid. rfork activates a fresh process grain for
the child, and that is where placement happens: the child may start on any silo. A process
grain holds the process's descriptors, environment, program and exit status. What processes
share lives in grains of its own, so it is reachable from any silo:

| Shared by rfork | Lives in |
| --- | --- |
| namespace (no RFNAMEG) | a process group grain holding the mount table (`VProcessGroupGrain` in NinePSharp today) |
| descriptor table (no RFFDG) | a descriptor group grain |
| environment (no RFENVG) | an environment group grain |
| pipes | a pipe grain per pipe |

A running program lives in its silo: its async frames on that silo's heap, a WASM
application's linear memory in an unmanaged buffer of its activation, and the WASM guest's
native stack on a thread of that silo. A process grain therefore stays active, and on its
silo, from exec until exit; Orleans must not deactivate or migrate it in between.

## Devices and machine-bound resources

Devices become reachable from every silo: ramfs, `/env` and the other kernel devices sit
behind resource grains, as namespace resources already do. A descriptor names a channel,
and a channel names a grain, so a child on another machine can use what its parent opened.

Some resources belong to one machine: a listening port, a TPM, a console, a local disk.
A device or server that uses one is placed on that machine's silo, and moves only if the
resource exists elsewhere too (another machine listening on the same address, a key also
sealed to another TPM). Placement rules name these resources explicitly.

## What survives the loss of a machine

| Kind of process | When its machine dies |
| --- | --- |
| An application or server that saves its state to grain-backed files | Requests in flight fail with an error or an unknown outcome; the process starts again on another silo and reads its saved state back |
| A program that keeps everything in memory | Its run is lost, as when a process crashes; its parent's wait sees it end with an error status, never a hang |

Requests in flight follow the rule the dispatcher already follows: work that cannot be
shown to have finished is reported with an unknown outcome, never silently retried.

## Applications save their own state

A WASM application is a process execed from `/bin/{app}`, using WASI over its own
descriptors and namespace ([Wasm.md](../fog-v1-profiles/Wasm.md)). Fog does not save a
running program: its stack and memory live in its silo and go with it. An application that
must survive the loss of its machine writes what it needs to a save file and reads it back
when it starts, as a Plan 9 program would.

The save file must live on the cluster's storage, such as a data directory its parent gives
it or its own tree under `/mnt/{app}`, so it is there on whichever machine the application
starts again. A file on a machine's own disk is a machine-bound resource and is lost with
the machine.

## Storage is a 9P file server

Everything Fog keeps is kept in files on a 9P file server, as in Plan 9. There is no
database. Grain state is files too: Fog's Orleans storage provider is a 9P client that keeps
each grain's state in a file named by its grain type and key, so the file server holds
namespaces, process state, application save files and Orleans's own records alike.

Orleans needs a conditional write, so a stale activation cannot overwrite a newer one. The
file server provides it through the file's qid version: a write names the version it read,
and fails if the file has changed since. A whole new state replaces the old in one step.

The file server is the one thing that cannot keep its state in grains, because grains keep
their state in it. It runs on machines with disks, as processes bound to those machines,
and must survive the loss of any one of them by keeping its data on several.

It is a port of 9front's gefs (gefs(4), `sys/src/cmd/gefs` and the paper `sys/doc/gefs.ms`),
as rc and libthread are ports of 9front's. Gefs builds the 9P file
system on copy-on-write Bε trees. A commit becomes current only when the superblocks are
written, so a crash loses at most unsynced data and never corrupts the file system. Every
block pointer carries a hash of the block, so corruption is detected rather than returned,
and a birth generation, which gives snapshots and reclaims their space. Changes are messages
upserted into the tree, so a qid version or time can change without a read-modify-write and
several changes commit atomically together. Its manual still calls it experimental.

### One device file per machine, mirrored and repaired by content

Each storage machine keeps its store in one ordinary file used as gefs's disk, as gefs's `-f`
option already allows. Fog reads and writes it at block offsets with .NET's random-access file
API, which works the same on Linux, macOS and Windows. The file must be on a local disk, never
NFS or SMB; it is held under an exclusive lock and preallocated, so a full disk is known before
a commit rather than during one. A commit counts as durable only once the platform has truly
flushed it: `fsync` on Linux, `FlushFileBuffers` on Windows and `F_FULLFSYNC` on macOS, verified
on each platform at startup, as [Storage.md](../fog-v1-profiles/Storage.md) already demands.

Every replica's file is a block-for-block mirror. One machine writes a given file system at a
time and decides where each block goes, and every replica writes the block at the same offset.
Offsets in block pointers therefore mean the same thing on every machine, and the same tree has
the same root hash everywhere, which is what consensus agrees on.

Block pointers carry a cryptographic hash in place of gefs's 64-bit MetroHash, which a
dishonest replica could forge; the hash function is chosen when the format is designed. That
lengthens a pointer from 24 bytes, so Fog's on-disk format is not 9front's.

The hashes make every 16 KiB block verifiable on its own, so missing or damaged blocks are
fetched peer to peer from any replica, over 9P like all machine-to-machine traffic:

- Catching up, a replica walks the tree from the newly agreed root, descending only into
  subtrees born after the generation it last synced, and fetches the blocks it lacks from all
  its peers in parallel, as BitTorrent v2 verifies pieces against a hash tree.
- Every block received is checked against the hash in the pointer that names it, all the way
  from the agreed root, so it does not matter which peer sent it.
- A block that fails its hash when read is fetched again from a peer by offset and expected
  hash and rewritten, so gefs's corruption detection becomes repair.

A new root is committed only after 2f+1 validators sign that they hold all of its blocks, so at
least f+1 honest machines can serve it. Scaling comes from many small file systems, for example
one per application or kind of grain state, each with its own writer chosen by consensus and
handed over when that machine fails; gefs already keeps a forest of trees in one store.

Still to design: the hash function; how writes are acknowledged, since a write is safe only
once the root holding it is agreed, so writes are grouped per consensus round; and how dropping
an old root or snapshot is agreed, after which each replica frees its blocks with gefs's
deadlists.

### Agreeing on the current commit

Which commit of each file system is current is decided by Tendermint consensus, ported to C#
from Tendermint Core v0.34 (Apache-2.0), as gefs, rc and libthread are ported from 9front.
Only that small fact is agreed: the replicated state is the current root hash and generation
of each file system. Blocks travel directly between the storage machines and are checked
against their hashes; they never pass through consensus.

- The port takes Tendermint's consensus state machine: rounds of proposal, prevote and
  precommit with its locking rules, the vote sets and validator set with proposer rotation,
  the write-ahead log that lets a restarted validator rejoin safely, and the guard that stops
  a validator signing two different votes for one height and round.
- Consensus messages travel over 9P like everything else: each storage machine serves its
  consensus endpoint as files. Tendermint's own peer-to-peer layer and gossip are not ported.
- The application is called directly in C#: a proposed root is applied only if its generation
  follows the current one and enough replicas hold its blocks. ABCI, the mempool, block and
  state sync, RPC and the light client are not needed.
- A committed decision is final: there are no forks to roll back.
- It tolerates f faulty machines out of 3f+1, Byzantine ones included, so a cluster that must
  survive one failed machine needs four storage machines. A lying or compromised machine
  cannot make the others adopt a forged root.
- The validators are the storage machines; adding or removing one is a validator update
  decided by consensus.

The port is checked against Tendermint's protocol specification and its tests, and Coyote
explores its interleavings like libthread's.

Orleans's membership table belongs on the same file server, or directly in the consensus
state; which suits Orleans's membership protocol better is decided when it is designed. Either
way it outlives any one machine, which a table owned by a single control host cannot.

## Getting in

A user reaches the cluster as they would a 9front machine: rc through stock `rcpu` or
`drawterm`, authenticated by the cluster's auth server, or 9P attaches with dp9ik from any
Plan 9 client. The shell is Fog's port of 9front rc, running as a process like any other.

## Order of work

1. The storage file server, a gefs port, and the Orleans storage provider over 9P, on one
   machine with conditional writes by qid version; then replication of commits, with a C#
   port of Tendermint consensus agreeing on the current one.
2. The grain-backed kernel: process, descriptor group, environment group and pipe grains;
   fork onto a fresh grain, wait and exit across silos; the existing kernel scenarios
   passing on a multi-silo test cluster.
3. Devices behind resource grains, with placement rules for machine-bound resources.
4. The services as programs a process execs: control-export sessions, keyfs, authsrv, the
   listeners and the transaction service, each keeping its behaviour tests.
5. The WASM exec format and WASI bridge.

Each step follows the usual gate: features, then failing tests, then code, with mutation,
fuzz and Coyote checks.

## Where older specifications disagree

| Specification | Disagreement |
| --- | --- |
| [Membership.md](../fog-v1-profiles/Membership.md) | One configured control host owns the membership table, recreated empty at each control boot. That host is a single point of failure, so the cluster cannot outlive it; the table belongs on the replicated file server. |
| [Storage.md](../fog-v1-profiles/Storage.md) | Grain state is served over 9P but kept in SQLite on one control host: a database behind a file interface, and a single point of failure. Storage is a 9P file server whose contents are files, replicated across machines. |
| [Isolation.md](../fog-v1-profiles/Isolation.md) | A Linux-only supervisor runs one OS process per job. WASM processes run inside the silo here, and Fog targets any contemporary machine. Containment of runaway guests (a loop without imports cannot be stopped from inside the process) still needs a design: fuel instrumented into the module, or a supervisor per machine. |
| [fog-v1-profiles README](../fog-v1-profiles/README.md), [LibTab jobs](../libtab-compute-jobs/README.md), [WorkerControl.md](../fog-v1-profiles/WorkerControl.md) | Written around disposable jobs, worker leases and a control node. Fog runs long-lived applications as processes, and work is placed by fork, not pulled by workers. |
| [fog-foundation README](../fog-foundation/README.md) | One explicitly configured control node. Control-node duties become processes and grains that can run on any machine. |

Until each is rewritten, these specifications describe what exists or was proposed, not
the direction.
