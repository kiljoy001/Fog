# Gefs Storage Engine & Distributed Consensus

Fog does not use an external relational or document database. All durable state—including Orleans actor state, namespace graphs, process metadata, and user files—is stored on a 9P file server backed by a C# port of **9front's `gefs(4)`**.

---

## 1. Why Gefs?

Traditional distributed filesystems often rely on heavyweight metadata servers or complex log-structured merge trees. In contrast, 9front's **gefs** (General File Server) was designed specifically around:
1. **Copy-on-Write (CoW) $B^\varepsilon$-Trees:** High write throughput with atomic commits.
2. **Crash Resilience:** An uncommitted crash loses only unsynced writes; it can never corrupt the filesystem tree.
3. **Built-in Snapshots:** Reclaiming space from deleted historical snapshots without scanning the entire disk.
4. **Content-Addressed Blocks:** Every block pointer carries a cryptographic hash of its target.

---

## 2. On-Disk Layout & Device Model

In Fog, each storage node holds its data in a single preallocated file on a local disk:

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Fog Device File Layout                          │
├───────────────────┬───────────────────┬────────────────────────────────┤
│   Superblock A    │   Superblock B    │       Arenas 0 .. N            │
│     (16 KiB)      │     (16 KiB)      │    (Array of 16 KiB Blocks)    │
└───────────────────┴───────────────────┴────────────────────────────────┘
```

* **Device File:** Opened with exclusive locks and preallocated to prevent out-of-space panics mid-commit.
* **Block Size:** Standardized to **16 KiB** ($16,384$ bytes).
* **Superblock Twins:** Two superblocks alternate as the active anchor. A commit swaps the active superblock atomically by incrementing the generation counter.
* **Flushing:** Commits must execute platform-level durable barriers (`fsync` on Linux, `FlushFileBuffers` on Windows, `F_FULLFSYNC` on macOS) before returning.

---

## 3. The $B^\varepsilon$-Tree Structure

```
                  ┌──────────────────────┐
                  │      Root Node       │
                  │ [Buffer: Upsert Ops] │
                  └──────────┬───────────┘
                             │
              ┌──────────────┴──────────────┐
              ▼                             ▼
   ┌──────────────────────┐      ┌──────────────────────┐
   │    Internal Node     │      │    Internal Node     │
   │ [Buffer: Upsert Ops] │      │ [Buffer: Upsert Ops] │
   └──────────┬───────────┘      └──────────┬───────────┘
              │                             │
       ┌──────┴──────┐               ┌──────┴──────┐
       ▼             ▼               ▼             ▼
  ┌─────────┐   ┌─────────┐     ┌─────────┐   ┌─────────┐
  │ Leaf A  │   │ Leaf B  │     │ Leaf C  │   │ Leaf D  │
  │ [Data]  │   │ [Data]  │     │ [Data]  │   │ [Data]  │
  └─────────┘   └─────────┘     └─────────┘   └─────────┘
```

* **Upsert Messages:** Insertions, deletions, and modifications are treated as messages placed in node buffers.
* **Lazy Flushing:** When an interior node's buffer fills up, messages are pushed down toward the leaves in batches, minimizing random write I/O.
* **Balancing:** Nodes split when they exceed capacity and merge or rotate when deletions cause them to shrink below thresholds.

---

## 4. Content Hashes & Self-Healing Blocks

9front's native gefs uses a 64-bit MetroHash. Fog replaces this with **SHA-256**:

* **Block Pointers (`Bptr`):** Every block pointer contains:
  1. Block offset on the storage device
  2. Birth generation of the block
  3. **SHA-256 hash** of the block's complete contents

### Self-Repair Protocol
Because every block is content-verifiable from the root of the tree:
1. **Corruption Detection:** If a block read fails its SHA-256 hash check, it is immediately recognized as damaged.
2. **Peer Fetch:** The node queries other storage nodes in the cluster for the block at that specific generation and SHA-256 hash over 9P.
3. **Rewrite & Repair:** Once received and verified, the block is rewritten locally, repairing the disk without administrator intervention.

---

## 5. Distributed Replication via Tendermint Consensus

To achieve cluster-wide fault tolerance:

* **Mirrored Offsets:** Every storage node maintains a byte-for-byte identical image of the device file.
* **Consensus Boundary:** The nodes run a C# port of **Tendermint BFT Consensus** (from Tendermint Core v0.34).
* **What Consensus Agrees On:** Only the **committed root hash** and **generation counter** pass through consensus:
  * A proposed root is accepted only if $2f + 1$ validator nodes sign that they already hold all the underlying blocks.
  * Blocks travel directly between storage nodes over 9P; payload data never clogs the consensus state machine.
  * Byzantine Fault Tolerance: Tolerates $f$ faulty or malicious nodes in a $3f + 1$ cluster.
