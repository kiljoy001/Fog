# LibTab compute jobs over 9P: BDD contract

Current priority: Plan 9 namespace emulation. Workload execution described here is deferred.

Status: proposed, specification-only. The feature files have no bindings and are
not passing acceptance tests. No compute runtime, service implementation, or new
library dependency is introduced here. This extends the
[all-9P swarm transport contract](../swarm-9p-transport/README.md), not its binary
9P framing or Orleans invocation encoding.

The [universal workload-provider contract](../workload-providers/README.md) defines
how authors add engines through a shared grain adapter. It generalizes runtime
selection to explicit versioned registrations while preserving this file interface.

## Service interface

Job metadata uses LibTab's existing schema-bearing ndb text. Controls are small
Plan 9 text commands. Source code, modules, input, and results
are ordinary file contents, not large cells or a second RPC service.

| Path under the authorized export | Contract |
| --- | --- |
| `/compute/clone` | Opening allocates a staging job; reading returns its server-assigned ID plus LF |
| `/compute/<id>/spec` | Bounded LibTab job description; writable only while staging |
| `/compute/<id>/ctl` | Write exactly `start\n`, `cancel\n`, or `release\n` |
| `/compute/<id>/input` | Bounded raw input; writable only while staging |
| `/compute/<id>/status` | Read-only LibTab status snapshot |
| `/compute/<id>/result` | Read-only raw result, published only on successful completion |

These are proposed names. A clone fid retains one ID across partial reads; it
does not allocate on every read. Clunking it does not release the job. A staging
lease bounds abandoned allocations. IDs are not recycled into another job's
authority while stale references or retry records can remain valid.

One control write contains exactly one complete command including its final LF.
No whitespace variants, arguments, missing LF, command batches, or shell expansion
are accepted in v1. Control offsets are ignored. These commands fit the minimum
supported 9P message size; unlike document uploads, they are not accumulated across
separate Twrites. Rejected commands have no command side effect.
Scenario steps saying "write start", "write cancel", or "write release" mean the
corresponding complete LF-terminated command unless they explicitly test malformed
input. A successful Rwrite reports the accepted command byte count; job identity and
outcome are observed through the existing job files, not an extra response envelope.

`start` acknowledges validation, freezing, and admission, not execution success.
Repeated `start` for the same accepted job returns its existing admission without
creating another run. `cancel` initiates cancellation and is idempotent, including
after a terminal outcome. `release` removes staging or terminal jobs; it rejects
active jobs, which must be cancelled and stopped first. Successful release
invalidates existing job fids, removes retained results, and prevents resurrection
by a late completion. Later access reports absence/bad fid rather than recreating
the job.

## Documents, uploads, and snapshots

- The text encoding is strict UTF-8 with LF-terminated lines. LibTab's schema,
  continuation indentation, entity escaping, nil semantics, and cell tags remain
  unchanged. See `../libtab` and `../libtabdotnet` for the source format; relative
  filesystem references here are from the repository root.
- `spec` is exactly one `fogjob-v1` schema and one explicit job row matching the
  allocated ID. Check row cardinality and duplicate attributes before LibTab's
  row-set deduplication. LibTab row identity is whole-row content, not a unique
  job-key constraint.
- Validate the expected schema and allowed columns, not merely whatever columns
  the sender declares. Allowed columns include only common fields and the selected
  registered provider's declared fields. Reject unknown columns, repeated fields, invalid UTF-8,
  NUL, unterminated lines/quotes, and attribute names longer than ndb's 31-byte
  limit. The existing .NET parser can ignore a final unterminated line and shorten
  long names; the service must reject those inputs rather than accepting a prefix.
- Bound total document bytes, input bytes, line length, columns, and rows before
  expensive parsing/crypto. Enforce both the incoming physical-line limit and
  LibTab's canonical emitted-cell-line limit of 7,168 bytes, including encoding
  expansion and framing. This is not a 7,168-byte decoded-value allowance.
- Opening a staging file for replacement/truncation starts a private bounded
  upload. Permit one writer per job across `spec` and `input`. Require contiguous
  byte offsets; reject holes, overlaps, and writes beyond the upload budget without
  changing accepted bytes. Successful clunk seals the upload; terminal logical
  session loss before sealing discards it and preserves the preceding sealed
  revision, if any. Direct connections terminate on disconnect. A temporary AAN
  carrier break does not clunk or seal the still-live writer; existing staging,
  upload and session deadlines continue to apply.
- A seal means the upload is closed, not that its contents are valid. No uploads
  launch execution. `start` rejects while a writer is open or validation/admission
  fails. Rejection leaves the job staging and editable. Sealed revisions are
validated and atomically frozen when admission succeeds.
- External source/artifact paths resolve only through the job's authorized namespace.
  Freeze verified artifact bytes or immutable content identities before admission;
  later path replacement cannot change execution. Large content stays outside the
  table. Missing, inaccessible, oversized, or incoherently changing artifacts fail
  validation without a run.
- Each open `status` fid pins a complete serialized snapshot. Reads at successive
  offsets cannot combine different states. Reopen to observe a newer revision.
  Completed `spec` and `result` reads likewise preserve byte-offset semantics.

## Version-one schema

All numeric job fields below are plain LibTab text, interpreted by the service as
positive unsigned decimal integers within both the implementation range and the
principal's quota. There are no implicit unlimited defaults or unit conversions.
Unknown runtime names or runtime-inapplicable fields are rejected.
Numeric text consists only of ASCII digits; leading zeroes are allowed, but signs,
whitespace, fractions, and exponents are not. Runtime versions and immutable artifact
identities are resolved under service policy and pinned in the admission record;
they must not change when placement changes.

All jobs require `job`, `runtime`, and an exact `provider_version`. Runtime names
are open to explicitly registered providers, not a closed enum. The following are
the built-in provider profiles; additional providers declare their own `p_` options
and work meters through the provider contract. An unknown or disabled name/version
pair is still rejected; there is no implicit provider loading or version fallback.

| Provider profile | Required fields in addition to `job`, `runtime`, and `provider_version` |
| --- | --- |
| All | `memory_bytes`, `deadline_ms`, `output_bytes` |
| `wasm` namespace profile | `source`, `p_host_calls`, `p_io_bytes` |

Common limits cannot be shadowed or weakened by provider options. Undeclared options
remain errors even if the client includes them in its own LibTab schema.

The [concrete runtime profiles](../fog-v1-profiles/Runtimes.md) now fix each initial
engine's source/input/result formats, ABI and metering. The first process-isolated
profiles conservatively count runtime overhead/artifact residency within memory_bytes;
examples here illustrate documents, not a promise that every runner fits their budgets.

`deadline_ms` runs from successful admission, including queue wait, and remains in
force during host calls. `memory_bytes` bounds job-private working memory; shared
immutable code/artifact caches have separate finite node budgets. `output_bytes`
limits raw result bytes. Account for input loading, compilation, and artifact loading
under finite preparation limits too; metering only the execution loop is not enough.

The selected dotnet-webassembly profile meters host calls and requested IO bytes.
It does not provide instruction fuel: a requested `fuel` contract is rejected. Guest
loops remain bounded by independently supervised deadlines and CPU/memory limits.
Imported calls require explicit file capabilities as well as these budgets.
`memory_bytes`, `deadline_ms`, and `output_bytes` apply independently.
There is no built-in language-model execution profile or token budget.

Examples are in [examples](examples). They assume those job IDs have been allocated,
their referenced files are authorized, and their requested limits fit policy. They
are documents, not an assertion that these workloads can execute in the current repo.

## Lifetime and observable outcomes

The primary states are:

```text
staging --start--> queued --> running --> succeeded
                    |           |
                    +-----------+------> failed
staging, queued, or running --cancel--> cancelled
```

Cancellation may be pending while the visible state is queued/running; status
reports `cancel_requested=true`. Publish `cancelled` only after execution stops and
job-private resources have been reclaimed. Completion and cancellation have one
serialized winner; a terminal result cannot later be overwritten by a stale callback.
An expired queued job fails with `deadline` without starting its runtime.

Status uses schema `fogstatus-v1`, one row, and the declared columns illustrated in
[status-succeeded.tab](examples/status-succeeded.tab): identity, revision, runtime,
provider version, state, cancellation flag, reason/error code, result length, and relevant resource
counters. Counters are nonnegative decimal; unavailable runtime-specific counters
are absent/nil, not invented zeros. `error` is a bounded, escaped, sanitized message.
It must not leak exception stacks, host paths, credentials, source, or private input.
The complete status document, including the error line, remains within its bounds.
Declared provider meters may add host-produced `p_` usage columns; their names,
units, and validation come from the pinned provider options, not arbitrary result data.

`result` is unavailable until success. v1 does not promise live output streaming or publish
failed/cancelled partial output as a successful result. A successful empty result has
length zero; it is distinct from an unavailable result. Terminal publication makes
status and the completed result agree. Each pinned earlier status remains an earlier
snapshot. Reopen status/result to observe completion.

Release execution contexts on success, failure, cancellation, or deadline enforcement;
do not wait for the client to release the job. Retain only bounded status/results for
a configured TTL. Immutable, verified modules may stay in quota-bound
caches. Mutable heaps, private input, input handles, and credentials cannot carry
from one job into the next. A grain coordinates execution; it is not the security
sandbox. Use an execution boundary that can enforce termination, including a worker
process where native code cannot be safely interrupted in-process.
Reclaim private staged/frozen input bytes at termination as well. The retained spec
is metadata, not permission to keep private input alive until result expiry.

Accepted jobs are not cancelled merely because their 9P client disconnects. After
terminal session loss, reconnect, reauthorize, and use the known job ID while it
remains retained. [AAN](../swarm-9p-transport/Aan.md) can preserve a live session
across a temporary physical break; it does not pause execution, gas, deadlines,
leases or retention, and does not retry jobs. Clunk, Tflush, explicit
cancel, and release are distinct actions. In particular, a lost/flushed `start` reply
can leave admission uncertain; inspect the job or repeat `start`, not allocate a new
job automatically. Replay protection here is scoped to the retained job/admission
record; this does not add durable exactly-once effects across total coordinator loss.
An unavailable/lost job is not silently rerun. Side-effecting workloads need explicit
application operation identities and provider-owned idempotency.

## Security and placement

The [single-operator foundation](../fog-foundation/README.md) adds a concrete
authentication/authorization and host-lifecycle profile. It preserves this job
interface while specifying aggregate reservations, explicit policy-epoch revocation
and ephemeral outcomes across total coordinator loss. Those additions remain
specification-only, like this document.

Authenticate peers and authorize every job operation against the principal and its
namespace. A supplied job ID, pathname, `signer` label, or `key` label grants no access.
Choose workers by runtime/version, hardware support, available memory, and verified
artifact availability. Reject or queue within bounded policy; never silently
substitute an engine/artifact or expose native HTTP/RPC as a fallback to 9P.

The proposed optional MCP gateway would translate external tool calls
into this same authorized file interface. It is not a workload provider and does
not add an internal RPC path or host language-model inference.

LibTab `SIGNED`, `HASHED`, and `ENCRYPTED` cells remain available for separate artifact
manifests or credentials; they are not required column types in the v1 job schema.
Verify signed bytes with a trusted key before trusting their meaning. A signature
over one cell does not sign sibling fields: bind all authority-relevant fields and
the artifact digest inside the authenticated manifest, and compare them to the
frozen job. Encryption is not authorization. Apply resource limits before hash/KDF
or signature processing. Runtime sandbox host access must go through explicitly
granted capabilities; uploading WASM must not grant arbitrary CLR, filesystem,
process, socket, or swarm-transport access.

## Features and verification

| Feature | Acceptance evidence |
| --- | --- |
| [TextJobInterface.feature](TextJobInterface.feature) | Real file operations, control grammar, snapshots, and raw results |
| [JobSubmission.feature](JobSubmission.feature) | Upload sealing, strict LibTab validation, freeze/admission, retry identity |
| [RuntimeBudgets.feature](RuntimeBudgets.feature) | WASM limits, placement, and sandbox boundaries |
| [JobLifecycle.feature](JobLifecycle.feature) | Cancellation races, cleanup, reconnect, retention, and uncertain failure |
| [LibTabCompatibility.feature](LibTabCompatibility.feature) | C/.NET agreement, escaping/nil, format limits, and trusted manifests |

Tags `@libtab_jobs`, `@JOB_*`, `@wire`, `@property`, `@fuzz`, `@cluster`, and
`@security` identify scope and required evidence; they do not indicate implemented
tests. Bind these in the existing Reqnroll project when implementing the service.
No no-op assertions or skipped scenarios count as completion.

Keep the existing unit/property/fuzz/mutation regime:

1. Unit/BDD: deterministic workers and controlled barriers; observe accepted job
   identities, exact file bytes, state transitions, execution counts, and reclaimed
   resources. Then run real adapters for each runtime, not only doubles.
2. Property: generate LibTab values/documents, upload fragments and offsets, budget
   boundaries, and start/cancel/release interleavings against a small job-state model.
3. Fuzz: bounded malformed text and 9P request sequences, including UTF-8 splits,
   huge fields/numbers, missing LF, duplicates, oversized encoded lines, crypto
   envelopes, and incomplete uploads. Assert no unauthorized execution, state leak,
   budget bypass, or unintended admission, not merely absence of crashes.
4. Mutation: retain the existing 90% gate; target validation, row cardinality, numeric
   boundaries, budget charging, authorization, admission idempotency, snapshot
   publication, and cleanup. Investigate survivors rather than weakening the gate.
5. Differential/integration: reuse the sibling C/.NET LibTab corpus/harness approach
   and stock 9P clients. Verify complete job workflows on at least two real silos,
   with the all-9P wire audit and native runtime libraries exercised separately.

Fixtures must explicitly set all limits, artifact/runtime versions, deadlines,
staging leases, and retention TTLs. Use controlled time rather than sleeps for
state-machine races; use bounded watchdogs for genuinely non-cooperative workers.
No throughput or completed runtime-conformance claim is made here.
