# Bootstrap, admission and failure boundaries

Status: proposed v1 host behavior. This is not a claim that the referenced runtime
adapters, membership provider or storage service exist today.

## Single-operator topology

One configured control node owns membership, active policy, admission accounting
and observable job records. It may also run workers. Additional nodes are explicitly
enrolled execution/silo hosts. User-facing jobs are admitted only through the control
node in v1, avoiding independently oversubscribed per-user budgets across gateways.
Worker advertisement is compatibility information, not admission authority.

Each host has an OS-protected local configuration and node-specific TLS private key.
A bounded LibTab `fognodes-v1` table contains `node`, `endpoint`, `tls_name`,
`spki_sha256`, `role` columns. Endpoints are explicit `tcp!host!port` addresses;
the secured application protocol is still 9P. `role` is `control` or `worker`;
exactly one row is control. Duplicate IDs, endpoints or node keys, invalid pins,
ports outside 1..65535, or an unknown role fail startup. Configuration validation
performs no peer dial, DNS-driven enrollment, or trust-on-first-use.

The mapping is ndb-like configuration, not live membership. `/srv`-style local
publication similarly does not make an endpoint alive or authorize a remote node.
No automatic control-node election or promotion is supported.

Each host also loads a finite `foglimits-v1` LibTab table with columns `name`,
`value`, one explicitly declared row per required limit. Reject unknown/duplicate
names, overflow, invalid units, and missing entries. Counts, bytes and milliseconds
are positive unsigned decimal; `clock_error_ppm` may be zero only when the operator
can justify that bound. Required names are:

| Category | Limit names |
| --- | --- |
| Pre-authentication | `connections`, `auth_per_connection`, `auth_per_source`, `auth_total`, `auth_bytes_total`, `verify_concurrency`, `attempts_per_minute`, `handshake_ms`, `challenge_ms`, `session_ms` |
| Execution and transport | `node_running_jobs`, `node_memory_bytes`, `node_queue_jobs`, `cache_bytes`, `transfer_bytes`, `transfer_ms`, `prepare_ms`, `stop_ms`, `lease_ms`, `clock_error_ppm` |
| Administration | `drain_ms`, `shutdown_ms`, `policy_bytes`, `policy_rows`, `audit_bytes` |

Fixed authentication document limits remain 2048/4096 bytes; node settings cannot
raise those protocol maxima. Job limits intersect host and principal allowances.
Configuration consistency checks require enough bounded storage for admitted
control exchanges, and `shutdown_ms` must include the configured drain and stop
allowances. TCP source identity for rate limits is not a user identity; deployments
behind shared addresses still need the global and per-connection limits.

The proposed [AAN extension](../swarm-9p-transport/Aan.md) defines additional finite
session, replay-buffer and reconnect limits for its eventual enabled profile.
Suspended sessions remain charged to their owner and the node. The direct-9P
configuration above does not implicitly enable AAN. Its bootstrap is defined by
[fog-aan-v1](../fog-v1-profiles/AanSession.md). The closed set of enabled-profile
limits also includes the rows in [Records.md](../fog-v1-profiles/Records.md).

## Ordered startup

1. Validate configuration, finite safety limits, exact provider registrations,
   containment support, and local credential access. Acquire an exclusive lock
   on the configured control-state directory on the control host.
2. Start host-local TLS/9P listeners, bootstrap node authorization and local health
   files. Start the control node's membership service independently of grains.
3. Authenticate internal peers using mutual TLS, matching the enrolled node key
   and TLS name. `Tattach.aname=runtime` with `NOFID` is permitted only for that
   verified node profile; `uname` must match its enrolled node ID. This exception
   never applies to the user listener's factotum-authenticated `aname=fog` profile.
4. Open `/transport/orleans` without grain calls. Form membership with the selected
   9P provider, register node incarnations, install active policy and worker leases.
5. Start Orleans-backed namespaces/job services; admit user work only when their
   prerequisites are ready. Authentication can succeed before the job service
   is ready, but a data attach requiring that service returns `not-ready`.

Node and user listeners may use separate ports. A user proof does not confer node
membership; a node certificate does not automatically create a user account. No
guest can access either the host's private credentials or internal-control export.

Bootstrap state is local even if optional resources later use remote storage. A
deployment that requires a non-9P remote provider fails before joining. Reminders,
stream backplanes and persistent-resource providers are disabled unless explicitly
configured with a conforming implementation. In-memory fixtures do not establish
durability claims. User factotum and client secstore remain outside the host.

## Membership service boundary

The [Orleans 10.3.1 mapping](../fog-v1-profiles/Membership.md) specifies the concrete
adapter to the host-level 9P membership service. Its acceptance covers atomic version-checked membership
changes, unique node incarnations, coherent snapshots, duplicate request identity,
heartbeats, dead-node updates and bounded request storage. All of those execute
without activating a grain. Reading a node table is not an implementation of them.

That mapping must be implemented and tested against the actual pinned Orleans API
before the second-node milestone passes. The adapter fails closed if a required
method/atomic operation is unsupported. No ordinary-file last-writer-wins updates
may masquerade as a compare-and-swap membership service. Its transaction-file rules
also support [worker/scope coordination](../fog-v1-profiles/WorkerControl.md) and the
optional [durable resource store](../fog-v1-profiles/Storage.md).

## Scheduling and aggregate reservations

At start, the control node atomically validates authority, pins inputs/artifacts,
and reserves principal and control-node queue/result capacity before reporting
admission. Charge staging
jobs/bytes at allocation/upload, queued slots at admission, running slots and private
memory before preparation, and retained output allowance for each admitted job.
Reservations are released exactly once at the matching lifecycle boundary. Queue
capacity, running capacity and artifact cache capacity are separate budgets.

Choose only enabled, ready, leased workers with the exact provider implementation,
runtime and artifact requirements, supported hardware/containment, and sufficient
unreserved capacity. Prefer the control host when compatible capacity is available;
otherwise prefer an eligible worker with verified cached artifacts, then the least
reserved memory fraction, breaking ties by node ID. This is a deterministic basic
policy, not a general optimal locality scheduler.

Use FIFO per principal with round-robin turns between runnable principals; skip a
principal whose head job cannot fit without blocking others. Expired heads fail
before selection. An unavailable compatible worker may keep an admitted job queued
only within its existing deadline/queue allowance. There is no unlimited wait or
silent runtime/artifact downgrade. Enforce a finite artifact-transfer byte allowance
and timeout before preparation. Any failed pin/transfer frees its reservations.

Artifact identity is an explicit cryptographic digest of verified immutable bytes,
not a Qid version or pathname. Copy into bounded temporary storage, verify size and
digest, then atomically publish a cache entry. Incomplete/corrupt copies cannot be
used or advertised as present. V1 restarts interrupted copies from zero; resumable
distribution across a lost logical transfer is not required. AAN may preserve a
still-live transfer across a physical break within its original timeout; that is
byte continuity, not persistent partial-download recovery. Pin an entry while a
job uses it; evict only unpinned
entries under the node's finite cache budget. Cache hits still require authority.

## Leases and independent stopping

Each worker process runs under a finite host-enforced execution lease associated
with control boot ID, worker incarnation, policy epoch and job scope. Use local
monotonic time, not wall-clock timestamps supplied by peers. Lease renewal is an
authenticated request/response with a fresh request ID; a worker starts the renewal
timer when it sends the request. A response must match the current request and
boot/epoch, and arrives before that timer's deadline. Receiving a response late
cannot restart an expired lease. Duplicate replies never extend it.

AAN transport probes and successful resumption are not membership heartbeats or
lease renewals. Suspension never pauses the timer or refreshes a queued renewal's
original send time. A buffered reply delivered after expiry remains stale even
when every byte was transported correctly. RPC and membership failure detectors
may terminate a logical connection before AAN's retention allowance expires.

The worker's authority expires no later than send time plus the granted interval.
For the Linux execution profile, send time is captured with CLOCK_BOOTTIME before
enqueueing to AAN; absolute boottime deadlines travel only to the local supervisor.
This makes delayed replies shorten available time instead of extending stale
authority. The control node's conservative barrier starts no earlier than issuance
and includes the maximum configured clock-rate error and containment-stop allowance.
Implementations must establish their monotonic-clock/suspend behavior and configure
that bound. If they cannot, the worker is ineligible; a suspended process must check
lease validity before resuming guest work or publishing IO.

Expiry revokes host-call authority immediately and triggers independent containment
termination. The watchdog must remain effective when guest/native code or an Orleans
turn is hung. Publish a failed terminal state only after execution has stopped; use
the existing pending-cancellation visibility while stopping. Deadline, policy epoch
and lease constraints intersect: none can extend another. Lease recovery alone never
resumes an expired job. A new admitted job is required.

An unavailable control node stops admissions. Disconnected workers may finish valid
leased work, but cannot publish new authoritative results after their scope expires.
After expiry they stop; they cannot continue on the strength of cached membership.
The control node never releases uncertain running capacity early and schedules a
replacement as if the previous worker were known stopped.

## Restart and delivery

Control boot IDs and worker incarnations are fresh 256-bit CSPRNG identifiers.
Job IDs include the control boot ID and a monotonically increasing sequence, with
the opaque combined encoding restricted to the job-ID grammar. Old IDs cannot refer
to newly allocated work. Admission records, user sessions, retained results and
membership are ephemeral across total control-process loss in this v1 profile.
Policy and enrollment are durable; they are separate from execution/job metadata.

After control restart, acquire the same exclusive state-directory lock, reload the
committed policy, then wait the full maximum prior lease interval plus its safety
allowance before admitting any new job. Do not infer old workers stopped because
their TCP connections disappeared. Worker restarts kill/reap owned old execution
processes before advertising a new incarnation. Refuse startup if prior containment
ownership cannot be reconciled. Moving/restoring a control-state directory to another
machine requires operator-confirmed fencing of the old host; no online restore or
automatic disaster failover is specified.

Persist a conservative lease/stop/clock-error high-water bound before issuing any
lease that would raise it. Restart and revocation barriers use that persisted bound,
not merely newly configured limits: lowering `lease_ms` must not shorten leases
already issued. Only after a full reconciliation barrier may the high-water bound
be reduced. A missing or corrupt bound in an existing state directory blocks startup;
an operator must fence preceding execution before reinitializing it.

An old-boot job lookup reports `job-unavailable`; it does not fabricate a terminal
result or silently recreate the job. The client must treat effects as potentially
unknown. A current coordinator losing a worker retains the existing `worker-lost`
semantics. Application operation IDs and provider idempotency remain necessary for
any explicit retry that can cause effects. Execution leases are not transactions
and cannot recall already-issued remote writes or make those writes exactly once.
Remote resource services must reject stale scopes at their own acceptance boundary.

## Operator surface and draining

The OS-protected local administration socket exports `/admin/ctl`, `/admin/status`,
and `/admin/audit`. Only local operator access may mutate configuration or drain
hosts. It is not mounted into user or job namespaces. Read access to sanitized
health may be separately granted; health never reveals user keys, prompts, proofs,
private paths or artifact contents.

`ctl` accepts one complete LF-terminated command per Twrite:
`check N\n`, `apply N\n`, `drain\n`, `resume\n`, `shutdown\n`. Policy commands apply
on the control node only. Commands are idempotent where applicable; offsets do not
identify replay slots. No shell expansion, additional arguments or command batches.
A reply acknowledges the control transition, not completion of a drain or global
revocation. Observe status until the requested terminal host condition is reached.

Status is a per-open immutable one-row `foghost-v1` LibTab document with columns
`node`, `boot`, `role`, `state`, `policy_epoch`, `revocation_pending`, `staging_jobs`,
`queued_jobs`, `running_jobs`, `reserved_bytes`, `retained_bytes`, `error`.
States are `starting`, `ready`, `draining`, `drained`, `stopping`, `stopped`, `failed`.
Counts are nonnegative; error is bounded and sanitized. A remote connection loss
is not itself a successful `stopped` observation.

Draining rejects new clone allocations/admissions. On a worker it stops new
assignments; accepted work may finish within its original deadline and lease.
On the control node, admitted queued/running work may still be scheduled/completed.
At the configured drain deadline, cancel remaining work and enforce containment;
`drained` requires zero live execution and reconciled reservations. A resume is
allowed only with current policy, membership, leases and containment; it cannot
resurrect failed jobs. Shutdown drains, closes sessions and reaps execution before
exit, with a finite hard-stop deadline and explicit failure if cleanup is incomplete.

Audit uses bounded records/ring storage with monotonically increasing sequence
numbers; readers can detect overwritten gaps. Record principal/node identifiers,
operation IDs, policy/boot generations and sanitized reasons, not authentication
statements, keys, raw RPC payloads or guest input/output. An audit sink cannot block
authentication or control progress indefinitely; expose dropped-record counts in
the audit stream's bounded metadata. External non-9P telemetry is not a fallback.
