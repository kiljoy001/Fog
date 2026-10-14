# Fog System Architecture & Failure Model

This document outlines the fundamental architectural design, cluster model, and failure semantics of Fog.

---

## 1. The Two-Level Architectural Model

Fog operates on two distinct scopes of distribution:

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Level 2: The Plan 9 Grid                        │
│                                                                        │
│   • Inter-cluster communication via pure 9P2000 over TLS / TCP         │
│   • dp9ik authentication against domain authentication servers         │
│   • Global naming under /n/{cluster} or /n/{host}                      │
│   • Outside peers (9front, drawterm, browser runtimes) see one host    │
└────────────────────────────────────┬───────────────────────────────────┘
                                     │ 9P + dp9ik
┌────────────────────────────────────▼───────────────────────────────────┐
│                   Level 1: Single System Image Cluster                 │
│                                                                        │
│   • Microsoft Orleans virtual actor mesh across participating silos    │
│   • Single shared PID space and kernel process model                   │
│   • Process state, descriptors, and namespaces hosted in grains        │
│   • Dynamic workload placement at rfork boundary                       │
│   • Durable storage backed by replicated Gefs Bε-tree file server      │
└────────────────────────────────────────────────────────────────────────┘
```

### Level 1: Intra-Cluster (Single System Image)
Inside a cluster, multiple physical or virtual machines act as one unified computer:
* Each node runs a single .NET process hosting an **Orleans Silo**.
* Silos communicate via high-performance RPC and form a unified virtual actor mesh.
* A single distributed **Fog Kernel** runs across the entire cluster.
* When a program forks or executes, placement across nodes is transparent.

### Level 2: Inter-Cluster (The Plan 9 Grid)
Between separate clusters or when interacting with external systems:
* The cluster presents itself to the outside world as a single Plan 9 host.
* Other hosts, whether native 9front workstations, Halatha browser runtimes, or secondary Fog clusters, attach over standard 9P2000.
* Authentication uses standard Plan 9 `dp9ik` protocol against the cluster's auth authority.
* Resources are imported and exported under `/n/{name}`.

---

## 2. Process Model & Grain Placement

In Fog, **"Everything that is not the kernel is a process."**

The kernel provides the core primitives:
* Process hierarchy and scheduling
* Per-process mutable namespaces
* File descriptor tables
* Devices (`#c`, `#|`, `#d`, `#p`, `ramfs`)
* System calls (`rfork`, `exec`, `open`, `read`, `write`, `close`, `dup`, `pipe`, `mount`, `bind`)

### Process Grains & `rfork` Semantics
Every process is modeled as an Orleans virtual actor grain keyed by its **PID**.

When `rfork` executes, Fog allocates a fresh process grain for the child. This is the **placement boundary**:
* The child process grain may activate on any available silo in the cluster based on load, memory availability, or hardware affinity.
* What processes share is governed by the standard Plan 9 `rfork` flags, partitioned into dedicated grains:

| Plan 9 Flag | Shared State | Backing Grain |
| :--- | :--- | :--- |
| Without `RFNAMEG` | Mount table & namespace | `VProcessGroupGrain` (Shared namespace grain) |
| Without `RFFDG` | Descriptor table | `DescriptorGroupGrain` |
| Without `RFENVG` | Environment variables | `EnvironmentGroupGrain` |
| System call `pipe()` | Inter-process pipe | `PipeGrain` (Per-pipe queue) |

### Memory & Execution Affinity
While process identity and metadata live in grain state, running program execution lives on its local silo:
* **Async Task Frames:** Reside on the silo's .NET thread pool and heap.
* **WASM Linear Memory:** Allocated in unmanaged memory buffers hosted by that silo.
* **Process Grain Invariant:** A process grain remains pinned to its assigned silo from `exec` until `exit`. Orleans does not migrate active running processes mid-flight.

---

## 3. Failure Domains & Recovery Semantics

A distributed cluster must be clear about what survives a node crash and what does not.

```
┌─────────────────────────────────┬─────────────────────────────────────────────────────────┐
│ Component Kind                  │ Behavior When Machine / Silo Dies                       │
├─────────────────────────────────┼─────────────────────────────────────────────────────────┤
│ Grain-Backed State              │ Survived. Grain reactivates on a surviving silo upon    │
│ (Namespaces, Descriptors, etc.) │ next invocation, loading durable state from Gefs.       │
├─────────────────────────────────┼─────────────────────────────────────────────────────────┤
│ Transient In-Memory Compute     │ Terminated. Process terminates with an exit error code; │
│ (WASM Stack, Heap Buffers)      │ parent's wait sees clean exit status, never hangs.     │
├─────────────────────────────────┼─────────────────────────────────────────────────────────┤
│ In-Flight Network Requests      │ Reported with unknown/failed outcome. No silent retries │
│                                 │ that could violate idempotency.                         │
├─────────────────────────────────┼─────────────────────────────────────────────────────────┤
│ Durable File Data (Gefs)        │ Fully durable. 16 KiB blocks replicated across nodes    │
│                                 │ via Tendermint consensus quorum (2f + 1).              │
└─────────────────────────────────┴─────────────────────────────────────────────────────────┘
```

### Application State Survival
Fog does not checkpoint raw running memory stacks across machines. If an application requires fault resilience:
1. It writes checkpoint records or state files to its namespace (e.g., `/mnt/{app}/save` or `/tmp`).
2. That write commits to the underlying replicated Gefs storage server.
3. If the host machine dies, the supervisor restarts the process on another silo, which reads the saved file back at boot.

---

## 4. Machine-Bound Resources & Affinity Rules

Some hardware resources cannot be migrated to other machines:
* A physical listening network port
* A TPM 2.0 cryptographic chip (used by `KeyFs` for storage key sealing)
* Local console devices (`#c`)
* Machine-local raw device files

Fog enforces **Affinity Placement Rules**:
* Processes that open machine-bound resources are pinned to that specific machine's silo.
* If the machine fails, processes dependent on that machine-bound resource terminate cleanly rather than hanging in an unreachable state.
