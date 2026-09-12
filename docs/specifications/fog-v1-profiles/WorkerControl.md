# Worker assignment, scopes and resource-side authority

Status: proposed `fog-control-v1`, single trusted operator. All network exchanges
below use ordinary 9P files, protected by the selected TLS/AAN profile where enabled.
None requires a guest to possess a node credential or invoke a grain API directly.

## Control service and shared records

The control host publishes `/control/info` under the enrolled-node `runtime` export.
Its per-open snapshot is one `fogcontrol-v1` row:
`protocol,service_id,cluster_id,control_boot,policy_epoch,store_id`.
`protocol=fog-control-v1`; store_id is semantic nil when optional storage is disabled.
Boot/epoch must be verified before any transaction; a changed boot invalidates all
old assignments, scopes and local caches. Reading this file does not join membership.

`/control/work` and `/control/scopes` implement [Records.md](Records.md). The host
maintains these registries outside Orleans; workload grains coordinate jobs through
them but cannot bypass their atomic reservation/lease/publication decisions.

Work `request` is one `fogworkop-v1` row:

`op,control_boot,policy_epoch,node,worker_boot,job,scope,lease_seq,reason,
result_bytes,result_sha256`

Operations: `advertise`, `poll`, `acquire`, `prepared`, `renew`, `stopped`, `finish`.
Unused fields are semantic nil; every request includes current boot/epoch and the
authenticated node's exact node/worker_boot. IDs use the existing job and hex-ID
grammars. Lease sequence is checked uint64. Job/scope fields are required except on
advertise/poll. Reason is a bounded code, never an exception or prompt.

Work `reply` is `fogworkreply-v1`:
`op,ok,error,control_boot,policy_epoch,worker_boot,job,scope,lease_seq,ttl_ms,state`.
State is `idle`, `offered`, `leased`, `running`, `stop-requested` or `terminal`.
TTL is positive only for a granted execution lease, otherwise nil. Error is nil
on success. `idle` is a successful immediate poll result, not a blocking infinite read.

`advertise` seals `capacity` (`fogcapacity-v1`):
`slots,memory_bytes,platform`, and `providers` (`fogworkerprovider-v1`):
`runtime,provider_version,bundle_sha256,profile,abi,meter`.
The control host checks installed registrations and its configured per-node caps;
claims cannot raise them. The worker boot must match its enrolled current membership
incarnation; an old boot cannot advertise again. Artifacts need not be cached for
eligibility; cache hints use an optional `cache` table `fogcache-v1` with
`sha256,bytes`, bounded by tx_rows. Treat them as hints and verify actual bytes.
No capacity or cached digest constitutes admission or resource authority.

## Assignment state machine

1. Admission already freezes owner, spec, input/artifacts, policy and aggregate
   reservations. Only the control scheduler selects an eligible worker. Before
   publishing an offer it atomically reserves that worker's slot/memory and records
   `(job,worker_boot,scope)`. One job has at most one live assignment.
2. `poll` returns the oldest unacknowledged offer for that worker, or one newly
   selected offer if capacity permits, or idle. Losing a poll reply cannot allocate
   a second assignment for that job. Offers expire at the earlier of prepare_ms
   and the job deadline; since no acquire occurred, an expired offer cannot start.
3. An offered result includes immutable files `spec`, `scope`, `mounts`, `grants`,
   `artifacts`, and `input` reference metadata. The worker verifies all identities
   and bounds before `acquire`. Bytes are transferred over authorized 9P files;
   no filesystem path supplied by a peer is executed or opened as a host path.
4. `acquire` atomically acknowledges the offer and grants initial lease_seq=1.
   The worker starts its monotonic request timer **before enqueueing the request
   to AAN or any network queue**. It prepares only after receiving a matching timely
   grant. A lost grant does not permit execution; repeat the same transaction to
   recover its original reply, whose TTL still measures from the original request.
5. After preparation, `prepared` requires the current lease and atomically records
   run permission. Its reply is running without extending that lease. The supervisor
   runs at most once for `(control_boot,job,worker_boot,scope)`, and only after a
   timely matching permission. A lost reply is queried/retried under that identity;
   neither coordinator nor supervisor constructs another execution instance.
6. `renew` requests exactly current lease_seq+1 while the old lease remains valid.
   A matching timely response supersedes the interval from that request's original
   local start. At most one renewal is outstanding per scope. Old/duplicate responses
   cannot extend it; a missed expiry permanently closes the execution scope.
7. The control host returns stop-requested instead of a grant after cancellation,
   policy change, drain expiry or job deadline. It never renews revoked authority.
   Worker watchdogs enforce expiry without waiting for another control response.

An acquire/renew response includes the exact transaction ID through the transaction
directory, scope, worker boot, epoch and sequence. TTL is at most lease_ms and the
remaining admitted deadline. The control host records its conservative issuance
barrier including clock-rate error and stop allowance; retries return the original
grant, not a freshly timed lease. The persisted high-water bound from HostLifecycle.md
also covers scope leases issued to resource hosts.

Offers can be withdrawn before acquisition without assuming execution started.
After acquire, uncertain reservations remain until a trustworthy stopped report
or the conservative lease/stop barrier. V1 marks a lost acquired assignment failed
after reconciliation; it never silently assigns the same job to another worker.
Worker membership death can trigger stop/reconciliation, but is not proof of instant
containment or permission to double-spend capacity.

## Frozen scope package

`scope` uses one `fogscope-v1` row:

`scope,job,control_boot,policy_epoch,owner,worker_node,worker_boot,runtime,
provider_version,bundle_sha256,namespace_sha256,artifacts_sha256,memory_bytes,
output_bytes,deadline_ms`

The deadline is the original admitted duration, informational here; grants carry
the usable interval from a request-local monotonic start. Workers cannot restart
the admitted duration on preparation or receipt. Current control job records remain
the authority for its remaining time.

`mounts` uses the existing `fogmounts-v1` ordered mount schema; `grants` uses
`foggrants-v1`, restricted to this owner's frozen effective rights. File metadata
and resource-provider invariants still intersect those grants. The namespace digest
is SHA-256 of uint32-LE length + exact mounts bytes, then length + exact grants bytes.
No user/group resolution on another node may widen this frozen snapshot.

`artifacts` uses `fogartifacts-v1`:
`field,provider,device,object,sha256,bytes`.
Include every frozen provider artifact and reserved field `input`; field names are
unique. Object identities are opaque provider identities, never host filenames.
The table's exact canonical bytes determine artifacts_sha256. Digests verify content,
not permission. The assigned worker reads the pinned immutable objects through its
scope; substitutions, size mismatches and unbounded streams fail preparation.
The initial input file remains raw, not a large LibTab cell.

## Resource-host scope lookup and lease

Scope `request` is one `fogscopeop-v1` row:
`op,control_boot,policy_epoch,scope,worker_node,worker_boot,resource_node`.
Only `lease` is allowed. The authenticated requester must equal resource_node and
be an enrolled resource host with registered providers. The recorded assignment
must match worker_node/worker_boot and remain live. Caller-provided owner/grants
are not accepted. Each request starts a local monotonic timer before network enqueue.

The reply `fogscopelease-v1` has
`ok,error,control_boot,policy_epoch,scope,resource_node,lease_id,ttl_ms` and on success
the same immutable `scope,mounts,grants,artifacts` files. lease_id is fresh random
hex, bound to that resource host and request. TTL is no greater than scope_lease_ms,
the remaining job deadline and the control host's conservative current execution
authority window. Request delay consumes TTL; the host cannot restart it on receipt.
Resource caches expire at their local request-start plus TTL. No network response
arriving after that deadline restores authority.

Scope lookup after stop/revoke returns denied, not the old package with a new lease.
Issuing a resource lease adds its maximum interval/stop allowance to the control
revocation barrier; global revocation is not complete until all such grants have
expired or acknowledged invalidation. Current-generation admissions stay paused
through that barrier as already required by the foundation.

## Scope-bound 9P resource attach

An assigned worker accesses resources through node-authenticated
`Tattach(uname=<worker-node>,afid=NOFID,aname=scope:<64-hex-scope-id>)` on a resource
host. This new internal export is allowed only on the enrolled-node listener.
The resource host acquires/validates a current scope lease, checks that the TLS
peer is the assigned worker, and constructs only the granted namespace root.
Public user proofs, arbitrary attach names and knowing a scope ID do not suffice.

Bind each root/open handle to scope, worker boot, policy epoch and resource lease.
Every operation checks current validity at its acceptance boundary; a handle cannot
outlive its scope just because ordinary mode changes leave opens usable. Already
accepted side effects may finish; revocation is not rollback. Late callbacks cannot
publish a new authoritative result after scope loss. Deny renewal/IO during unresolved
policy changes rather than relying on a stale User string in an Orleans message.

When the resource host dispatches to a resource grain, it creates the trusted
ResourceOperationContext from this root: issuer node, scope reference, owner and
operation ID. That context is accepted only from the trusted adapter on an enrolled
internal channel. Guest/provider input cannot override it. AAN retransmission
does not create another operation ID; a new explicit application retry requires
the provider's idempotency contract. The guest namespace never contains this
scope-bootstrap, runtime transport, control service or user factotum.

### Orleans invocation binding

Some existing resource methods, including ReadAsync and WalkAsync, do not carry a
ResourceOperationContext argument. Their lack of such an argument must not bypass
destination-side scope checks. The trusted adapter places one typed
`JobResourceAuthorityV1` value under the reserved Orleans RequestContext key
`fog.job-authority.v1` for **every job-scoped resource invocation**, including reads,
walks, stat, create and clunk. It is part of the Orleans payload inside 9P, not a
new public RPC or user-supplied text parameter.

Its exact generated-serializer field IDs/types are:

| ID | Field | Type |
| --- | --- | --- |
| 0 | ControlBoot | string, 64 lowercase hex |
| 1 | PolicyEpoch | ulong |
| 2 | ScopeId | string, 64 lowercase hex |
| 3 | WorkerNode | string, enrolled identifier |
| 4 | WorkerBoot | string, 64 lowercase hex |
| 5 | IssuerNode | string, enrolled resource-adapter host |
| 6 | IssuerBoot | string, current registered host boot |
| 7 | OperationId | existing ResourceOperationIdModel |

ResourceOperationIdModel retains serializer IDs 0=SessionId and 1=Sequence. Its
SessionId is `<issuer-boot>-<random-logical-session-id>`, both hex; Sequence is a
checked per-session uint64 counter assigned once at logical request acceptance.
AAN duplicate suppression and an adapter retry preserve that ID and request bytes.
The existing ResourceOperationContextModel fields remain IDs 0=OperationId,
1=ProcessId and 2=User; when present they must match the trusted operation and scope
owner. ProcessId is routing metadata, not authority. No existing IDs are reassigned.

A mandatory destination invocation filter validates the reserved envelope against
the live control registry and its **own** resource-host scope lease before entering
the provider. It checks the issuer's current registered boot and admitted scope,
worker, epoch, target resource/rights and operation identity. It does not trust a
serialized authenticated flag, User string or a source host's local expiry timestamp.
Trusted Orleans forwarding may add node hops; the last TLS peer is not substituted
for the registered original issuer. This relies on the declared trusted-node domain,
not on permitting untrusted users to open the internal Orleans export.

For open handles, retain their bound scope and operation issuer privately at the
destination. A job-bound handle requires the matching valid envelope on every use;
missing context cannot downgrade it to an ordinary user handle. Walk/stat/create
also require the job context even when no handle has been opened. The source adapter
must not accept this reserved metadata from a guest/public 9P payload. Direct-user
operations retain their separately authenticated namespace/handle checks; an absent
job context is not an anonymous fallback. Unsupported filter/context propagation
makes the deployment fail conformance before admitting jobs.

Establish and clear request context in a finally-scoped invocation wrapper. Do not
leak one job's authority into another invocation, timer, continuation or unrelated
grain call. Resource providers must not persist the envelope as reusable authority;
durable blobs contain resource data and replay evidence, not live scope credentials.

## Completion and cleanup

`finish` seals raw `result`, declares exact length/SHA-256, and uses a `completion`
file (`fogcompletion-v1`): `outcome,finish_reason,error,stopped`.
Outcome is `succeeded`, `failed` or `cancelled`. Failed/cancelled completion has no
result and zero result_bytes; success may have zero bytes with the empty digest.
Only the assigned current worker may submit. `stopped=true` is an assertion by the
trusted supervisor that execution was reaped and job-private ownership reconciled,
not a guest's own status flag. Provider meter usage is an optional `fogusage-v1`
table `name,value`; allowed names come from the pinned registration.

The control host atomically chooses the terminal winner, verifies length/digest,
live publication authority, job deadline, cancellation/policy status and reserved
output allowance, then publishes success and releases reservations once. Result
upload before that point is private staging. A late success after cancel/expiry
cannot override the host's failed/cancelled outcome. The supervisor preserves
bounded result bytes for transfer only; no VM/KV heap remains alive for client reads.

`stopped` reports failed/cancelled containment cleanup without a result. It may be
accepted from the matching old assignment after its lease/epoch expires solely to
reconcile stop/capacity; it cannot renew authority or publish success. Duplicate
finish/stopped messages return the existing outcome. Conflicting completion bytes
or a wrong job/worker incarnation are rejected, not last-writer-wins updates.

If control dies, scopes and grants expire and workers stop. If a worker dies,
control waits its conservative barrier; it does not accept a new worker boot's claim
to have completed the old job. The job remains uncertain regarding previously
accepted external effects. See `WorkerControl.feature` for the acceptance matrix.
