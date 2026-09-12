# Optional durable resource storage

Status: proposed `fog-store-v1`. This is a bounded single-authority storage profile,
not distributed consensus, automatic replication, or durable job execution.

## Selected backend and durability boundary

One control-host service owns an operator-protected local SQLite database on a
local filesystem with working file locks and flush semantics. Select WAL mode,
`synchronous=FULL`, foreign keys enabled and a bounded busy timeout. Verify the
effective settings before publishing readiness; reject network filesystems or
storage without the required lock/flush behavior. Never enable NORMAL/OFF as an
unannounced performance fallback. No client opens the database over SMB/NFS or SQL.

The service exports `/control/storage` through the same authenticated 9P runtime
export and [transaction convention](Records.md). SQLite is local implementation
machinery, not a second swarm protocol. Policy allows only explicitly enrolled
resource hosts and registered state codecs; guests and public users cannot write
arbitrary grain blobs or supply CLR type names.

A successful mutation's Rwrite is sent only after one SQLite transaction commits
both the new value/tombstone and the retry outcome. A crash before commit exposes
neither; a crash after commit but before reply exposes both after recovery. These
guarantees depend on the declared storage hardware/OS flush contract, not on AAN.
Disk full, failed flush or database corruption stops writes and marks storage
unavailable; do not acknowledge success or create an empty replacement database.

SQLite's [WAL](https://sqlite.org/wal.html) and
[synchronous setting](https://www.sqlite.org/pragma.html#pragma_synchronous) supply
the local transaction/flush mechanism. The 9P identity, authority and retry rules
below are this project's additional contract.

## Identity and wire records

At first administrative initialization generate a persistent 256-bit `store_id`.
Do not regenerate it on ordinary restart or reuse operation/object counters. Acquire
an exclusive host ownership lock before opening for service. A lost/corrupt existing
identity blocks startup; reinitialization is an explicit operator action after fencing.

`request` uses `fogstoreop-v1`, one row, columns:

`op,store_id,service_id,cluster_id,provider,state_name,grain_type,grain_key,expected,
codec,payload_bytes,payload_sha256`

Operations are `read`, `put`, `clear`. `grain_type` is canonical padded base64url of
`GrainId.Type.Value.Value` bytes and `grain_key` of `GrainId.Key.Value` bytes from Orleans
10.3.1; preserve the bytes rather than using display names or ambiguous delimiters.
Decode/encode round-trip in adapter tests. Each is bounded by `tx_bytes` and the
7168-byte cell bound. Other identity fields are exact bounded UTF-8 strings, no NUL;
service/cluster retain their configured identifier grammar.

The complete key is that six-part service/cluster/provider/state/type/key tuple.
Hash indexing may use SHA-256 of length-prefixed components (uint32 little-endian
byte lengths), but always compare retained full components on lookup; a hash
collision must not alias resources. No key component is interpreted as a host path.

`expected` is nil only for `read`. For mutations it is either `absent` or the exact
opaque etag returned by a prior read/write. No wildcard/unconditional replacement
in v1. `put` requires sealed raw `payload`, exact byte count and SHA-256, plus an
enabled `codec`; `clear` has nil codec/hash and zero payload bytes, with no payload
file. Read has these payload/codec fields nil. Empty put is distinct from clear.

`reply` uses `fogstorereply-v1`:
`op,store_id,ok,error,record_exists,etag,codec,payload_bytes,payload_sha256`.
Successful read publishes the matching immutable raw `payload` snapshot when a
record exists. Not-found is successful with `record_exists=false`, nil codec/hash,
zero bytes and `etag=absent` for a never-written key, or its tombstone etag.
Put returns the new etag/metadata; clear returns a new tombstone etag and false.
A conflict is `ok=false,error=conflict` and no payload; it changes no value or etag.

Etags are `<store_id>-<global-mutation-sequence>` with checked uint64 counters and
no reuse. Clearing a value leaves a bounded tombstone entry so stale writers cannot
mistake it for a never-written key. `expected=absent` succeeds only if the key was
never written; recreating a cleared key requires its tombstone etag. Keep tombstones
within explicit storage capacity; when full, refuse new mutations rather than
discarding stale-write protection. An administrative whole-store replacement is
not a transparent compaction operation and requires fencing/new identity.

Read, put and clear operate on exactly one record. A record may contain a complete
resource aggregate (tree/data/idempotency ledger), but there is no multi-key atomic
transaction API in this profile. Applications needing cross-aggregate transactions
must not infer one from sequential writes or a successful AAN acknowledgement.

## Durable transaction/retry records

Unlike membership, storage transaction IDs and records survive storage process
restart. Allocate their monotonically increasing sequence durably before acknowledging
clone. Persist sealed request metadata and the frozen payload with the eventual
commit transaction; mutable staging can be discarded after a crash. Staging loss
never commits an effect. A previously committing ID after recovery is either the
durable completed outcome or an uncommitted staging/expired record, not a guessed
success. If staging inputs were lost, report staging-loss and require the owner to
reupload the exact original request under that still-existing ID before commit, or
let it expire; no commit runs with missing bytes. Retrying cannot execute an already
committed effect again.

Retained outcomes consume `storage_operations` and `storage_total_bytes`. Before
commit reserve the new value, worst-case result and ledger cost. Reject `limit`
before any mutation when capacity cannot be reserved. Retention expiration removes
the operation record but never permits that ID to be allocated/recreated again;
a later lookup/commit returns `tx-expired`. A new transaction with a stale expected
etag still conflicts. If the caller discarded its known ID and rereads a new etag,
that is a new application decision, not automatically safe replay of an old effect.

Private pending payloads and snapshot bytes remain bounded. `release`/expiry cannot
remove a live value; clearing values requires `clear` with the correct etag. Output
snapshots cannot keep the entire database's WAL pinned indefinitely: use bounded
materialized copies or a finite snapshot lifetime, and reclaim their reservations.

## Orleans IGrainStorage mapping

Pin `Microsoft.Orleans.Core` 10.3.1 and implement all three generic operations and
their CancellationToken overloads. The token controls waiting, not rollback after
commit. The adapter retains a transaction ID and frozen serialization while resolving
an ambiguous call; it must not silently serialize newer mutable state and retry it
under the old ID.

| Method | Mapping |
| --- | --- |
| `ReadStateAsync<T>` | `read`; decode only an enabled exact codec for T. Set State, ETag and RecordExists together after complete verification. Missing state gets the registered default-state factory and the returned absence/tombstone etag. |
| `WriteStateAsync<T>` | Serialize one immutable snapshot; `put` with the previous ETag, or `absent` only for a never-read/default new state. On success set the new ETag and RecordExists=true. Conflict maps to `InconsistentStateException`; do not alter the caller's fields on failure. |
| `ClearStateAsync<T>` | `clear` with the previous ETag; on success use the default-state factory, returned tombstone ETag and RecordExists=false. Clearing a never-written absent key may create its first tombstone; repeated same transaction returns the original outcome. |

Null/empty ETag is accepted only for an initial `RecordExists=false` state and maps
to `absent`; `*` is rejected. A codec is an operator-registered `(codec_id,state_type,
schema_version,bundle_digest)` mapping with a bounded serializer/deserializer. V1
selects Orleans 10.3.1's serialization engine with generated/registered types only;
the provider assigns a fixed codec ID per state contract. Do not load assemblies
or choose types from stored/client text. Schema upgrades require an explicit new
codec and a tested migration, not best-effort field dropping.

## Mountable resource semantics

Durable resource providers store their aggregate plus mutation-idempotency ledger
in the **same** blob/CAS transaction. `WriteAsync`, `CreateAndOpenAsync` and
`RemoveAsync` cannot persist the data and replay result separately. Allocate stable
object/Qid paths monotonically in that aggregate; deletion never recycles an ID.
Increment Qid versions on visible content/metadata changes without claiming the
32-bit Qid version is an eternal unique storage etag.

An open handle is ephemeral, bound to the resource-host boot and logical session;
durable content does not make it reusable after a host restart. An old mutation's
durable success may be reported through its application operation identity, but
an old open/create result's handle is stale and must not be made usable by replay.
Recover a committed create's stable object identity and explicitly reopen it under
fresh authority. This distinction must be exercised against the existing resource
grain interface; no old fid or lease is restored from a blob.

Ordinary resource-provider operation IDs have a configured bounded validity window
and issuer boot. Retain replay entries through that window; reject old/unknown
issuer epochs and expired operation IDs rather than executing them after ledger
eviction. Pure storage CAS is not enough to guarantee resource-operation idempotency
when a provider deliberately reads a newer etag and applies the mutation again.

## Recovery and scope limits

On restart recover SQLite, verify store identity/settings/schema, and reconcile
staging reservations before serving. Keep policy, store and control-boot identities
separate. AAN sessions, jobs and membership are not loaded from this database.
No remote replica promotion or online live-copy restore is defined. Administrative
backup uses SQLite's consistent backup facility while access is controlled; restore
requires draining/fencing the old writer and new sessions. Restoring an older image
must use a new store identity so old etags/transaction IDs cannot alias rollback state.

Conformance in `Storage.feature` includes lost replies, process termination around
commit, failed flush/disk full, CAS races, tombstones, codec rejection and durable
effects with stale handles. Physical power-loss durability requires fault testing
the deployed storage stack in addition to unit/mock crash tests.
