# Common bounded control transactions

Status: proposed `fog-tx-v1` service convention. Standard 9P2000 framing and the
existing LibTab encoding are unchanged. All schemas below are closed: columns are
listed in canonical order; unknown/duplicate columns, schemas and rows are errors.

## Encoding and limits

Use strict UTF-8 without BOM, LF, canonical LibTab escaping and one final empty
line. Verify physical input before LibTab row deduplication; enforce the existing
7168-byte encoded cell-line bound. One schema per file. An empty result set contains
its schema and zero rows, not a fabricated nil row. A required absent/nil field is
an error; unused/optional fields are semantic nil unless stated otherwise, represented
canonically by omitting that row attribute while retaining its declared schema column.
Literal text `nil` is not semantic nil. Plain control commands are not LibTab.

IDs are case-sensitive ASCII. Random IDs and SHA-256 digests are 64 lowercase hex
characters; counters are canonical unsigned decimal with no leading zero except
`0`. Checked arithmetic rejects overflow before allocation/mutation. Identifiers
inherited from the job/auth contracts retain their existing grammar. Table keys
are explicit tuples, never whole-row set equality. Booleans are `true` or `false`.

New required positive `foglimits-v1` rows when these services are enabled:
`tx_per_owner`, `tx_total`, `tx_bytes`, `tx_bytes_total`, `tx_rows`, `tx_files`,
`tx_staging_ms`, `tx_retention_ms`, `tx_commit_ms`, `snapshot_bytes`, `snapshot_ms`,
`membership_rows`, `scope_rows`, `scope_lease_ms`, `worker_poll_ms`, `artifact_bytes`,
`storage_blob_bytes`, `storage_total_bytes`, `storage_operations`, `storage_busy_ms`,
`worker_pids`, `worker_fds`, `worker_tmp_bytes`, `worker_stderr_bytes`, `worker_cpu_cores`.
Storage rows are required only with storage enabled. No implicit unlimited values.
`tx_bytes` bounds the complete transaction, including auxiliary tables and payload;
it must cover the configured maximum storage blob when storage is enabled. Reserve
both staging and worst-case result capacity before committing; reads need snapshot
capacity too. Bounds may be lowered by owner/service policy, never raised by a request.

## File tree

Each service base, for example `/control/membership`, has:

```text
clone                       open allocates one staging transaction; read ID + LF
<id>/request                one service-specific LibTab request
<id>/<declared-input>        auxiliary LibTab table or raw payload
<id>/ctl                    commit\n or release\n, one command per Twrite
<id>/status                 immutable per-open fogtx-v1 snapshot
<id>/reply                  service-specific immutable reply after completion
<id>/<declared-output>       auxiliary immutable table or raw payload
```

The server allocates IDs, not the caller. Ephemeral services use
`<server-boot-hex>-<monotonic-sequence>`; storage uses its persistent store UUID and
durably allocated sequence. Old IDs are never recreated. Only `clone` allocates;
walking a missing/expired ID returns absence, never creates a transaction. Only the
authenticated owner node/user can access its transactions. The service may impose
additional role checks. User workloads cannot access `/control`.

An open clone pins its one ID across fragmented reads. Losing that allocation's
reply can leak only a bounded staging reservation, not execute a requested action.
Use the same known ID to retry a commit; do not allocate another transaction to
guess the outcome of a mutation. Service-specific idempotency may add a stronger
job/key identity, but transport retry does not manufacture it.

One writer per transaction across all inputs. Sequential offsets start at zero;
holes, overlaps, overflow and excess bytes fail without changing accepted bytes.
Successful clunk seals that file. A terminal logical-session loss discards its
unsealed revision; an AAN suspension does not. Reopening for replacement is allowed
only while staging. All required files must be sealed before commit.

`commit\n` is exactly seven bytes. It freezes all inputs, validates authority and
schema, and invokes one service operation. It is serialized with cancellation,
expiry and competing commits. The service atomically records success or a semantic
failure with its effect, where an effect exists. A valid semantic rejection such
as CAS conflict completes the transaction: Rwrite acknowledges seven bytes and
`reply` describes the rejection. Invalid syntax/incomplete inputs returns bounded
Rerror and leaves staging. No side effect occurs merely on upload or file clunk.

No second commit executes the frozen request again. A completed transaction returns
its retained outcome; changed bytes under that ID are rejected. A running commit
may fail its client wait while the outcome is still unknown; Tflush orders replies,
not rollback. Keep its reservation until the service reconciles completion, or mark
the service unavailable rather than executing a replacement concurrently.

`release\n` removes staging or completed ownership, but rejects a committing
transaction. Staging/retention timeouts reclaim abandoned records. Expired outcomes
return `tx-expired`/absence, never rerun. Releasing or expiring a record removes retry
evidence, not its committed resource effect. Recheck current authority even when
returning an old result; retention does not bypass policy revocation.

`fogtx-v1` has exactly `id,state,error`, with states `staging`, `committing`, `done`.
Error is nil or a bounded machine code; no secrets or stack traces. A process crash
can make an ephemeral transaction unavailable instead of yielding a made-up outcome.

## Snapshot and failure rules

`reply` and all output files represent one atomic snapshot/operation result. They
cannot be read before `done`. Opening each output pins that same transaction result;
reads at offsets cannot combine revisions. A service-specific reply identifies
its table/store boot and version. Large replies remain within reserved snapshot
capacity; do not silently truncate rows. An oversized read fails `snapshot-limit`.

Required common errors: `denied`, `not-ready`, `invalid-request`, `upload-open`,
`limit`, `busy`, `tx-expired`, `stale-boot`, `policy-changed`, `unavailable`.
They are ordinary bounded Rerrors before commit or service reply codes after a
semantic outcome. Services explicitly define success/conflict/not-found behavior.
No error string grants authority or permits a client to infer rollback after a
lost connection. Clients use the retained transaction to resolve ambiguity.

Service host calls are serialized at the relevant atomic boundary, not all on one
blocking Orleans turn. Local storage/crypto/IO waits have finite limits. Cancellation
and health/control processing retain separately bounded capacity. No transaction
may require grain activation merely to bootstrap its own transport.

## Deployment lock

An operator-owned canonical `fogruntime-lock-v1` table has columns
`runtime,provider_version,profile,engine_version,source_revision,bundle_sha256,abi,
meter,platform,syscalls_sha256`. Line wrapping here is documentation only; the file
has that exact schema. Each enabled runtime/version has exactly one row per supported
platform. `source_revision` identifies the immutable source snapshot; the SHA-256
identifies the installed runner/dependency/descriptor bundle, verified before launch.
`syscalls_sha256` identifies its installed seccomp policy. No `latest`, wildcard
version, unverified download or unsigned job-selected executable is accepted.

The bundle is an operator-installed immutable directory, with a canonical manifest
of relative paths, byte lengths and SHA-256 values; no symlinks, absolute paths,
duplicate normalized paths or executable paths outside it. Its identity is the
SHA-256 of that exact manifest. The host verifies every listed file and rejects
extra executable/library files. The operator is the trust root; hashes are identity,
not enrollment or signatures. Different architectures may have different bundles;
placement requires the exact registered variant and meter, not matching display names.

Initial profile/ABI/meter names are fixed by Runtimes.md and Isolation.md. Dependency
release numbers are installation inputs, but a changed bundle requires a new explicit
provider version/variant and its conformance evidence. No rolling upgrade rewrites
an admitted job's identity. A pinned job cannot be placed on a different binary
merely because both registrations say `provider_api=1`.
