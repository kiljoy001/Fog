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
| A server that keeps its durable state in grain-backed files and resources | Requests in flight fail with an error or an unknown outcome; the process comes back on another silo and carries on |
| A WASM application with snapshots | It resumes from its last checkpoint on another silo, replaying its journal |
| A program that keeps everything in memory | Its run is lost, as when a process crashes; its parent's wait sees it end with an error status, never a hang |

Requests in flight follow the rule the dispatcher already follows: work that cannot be
shown to have finished is reported with an unknown outcome, never silently retried.

## Applications and snapshots

A WASM application is a process execed from `/bin/{app}`, using WASI over its own
descriptors and namespace ([Wasm.md](../fog-v1-profiles/Wasm.md)). Its whole state is
linear memory, globals, tables and its call stack. dotnet-webassembly compiles the guest to
CLR code, so the stack is a thread stack and cannot be saved as it is. Binaryen's Asyncify
transform makes the module able to unwind its stack into linear memory at a WASI call and
rewind it later. With that, a snapshot is plain data a grain can store:

1. Snapshot at a WASI call boundary, with no kernel call in flight.
2. Journal the result of every import after it: bytes read, counts written, clock values,
   random bytes.
3. To restore, rewind the snapshot and answer the guest's imports from the journal until it
   runs out; effects already journalled happen exactly once. A call issued but never
   journalled has an unknown outcome.

Snapshots give WASM applications Orleans's resilience whatever they keep in memory. They
also allow moving a running application to another silo, and deactivating an idle one
until it has work. Managed C# programs cannot be snapshot, since their frames hold
delegates and object references; they keep durable state in grain storage instead.

The first WASI bridge does not take snapshots, but is built for them: every import passes
through one place that can journal its result, and kernel calls are made only at import
boundaries.

## Getting in

A user reaches the cluster as they would a 9front machine: rc through stock `rcpu` or
`drawterm`, authenticated by the cluster's auth server, or 9P attaches with dp9ik from any
Plan 9 client. The shell is Fog's port of 9front rc, running as a process like any other.

## Order of work

1. The grain-backed kernel: process, descriptor group, environment group and pipe grains;
   fork onto a fresh grain, wait and exit across silos; the existing kernel scenarios
   passing on a multi-silo test cluster.
2. Devices behind resource grains, with placement rules for machine-bound resources.
3. The services as programs a process execs: control-export sessions, keyfs, authsrv, the
   listeners and the transaction service, each keeping its behaviour tests.
4. The WASM exec format and WASI bridge, built for snapshots.
5. Snapshots and the import journal; restore on another silo; migration and idle
   deactivation.

Each step follows the usual gate: features, then failing tests, then code, with mutation,
fuzz and Coyote checks.

## Where older specifications disagree

| Specification | Disagreement |
| --- | --- |
| [Membership.md](../fog-v1-profiles/Membership.md) | One configured control host owns the membership table, recreated empty at each control boot. That host is a single point of failure, so the cluster cannot outlive it; membership has to survive the loss of any one machine. |
| [Isolation.md](../fog-v1-profiles/Isolation.md) | A Linux-only supervisor runs one OS process per job. WASM processes run inside the silo here, and Fog targets any contemporary machine. Containment of runaway guests (a loop without imports cannot be stopped from inside the process) still needs a design: fuel instrumented into the module, or a supervisor per machine. |
| [fog-v1-profiles README](../fog-v1-profiles/README.md), [LibTab jobs](../libtab-compute-jobs/README.md), [WorkerControl.md](../fog-v1-profiles/WorkerControl.md) | Written around disposable jobs, worker leases and a control node. Fog runs long-lived applications as processes, and work is placed by fork, not pulled by workers. |
| [fog-foundation README](../fog-foundation/README.md) | One explicitly configured control node. Control-node duties become processes and grains that can run on any machine. |

Until each is rewritten, these specifications describe what exists or was proposed, not
the direction.
