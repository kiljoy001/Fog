# Built-in AAN: resumable transport sessions

Status: specification-only, not an implementation. Stock 9front AAN framing beneath
9P is the selected transport design. The authenticated bootstrap, claim and switch
contract is [fog-aan-v1](../fog-v1-profiles/AanSession.md); implementation and security
review are still required before production use.

## Purpose and placement

AAN (always available network) belongs in the client and host transport libraries,
below the logical 9P connection processor and Orleans connection adapter. It is
not a grain, workload, job retry service, or dependency on an installed `aan`
subprocess. Temporary loss of the physical carrier suspends a live logical byte
stream. Both endpoints retain its bounded sequencing state and resume that same
stream when a permitted replacement carrier becomes available.

This is particularly useful for intermittent fog links and roaming clients. It
does not create connectivity during a partition, migrate sessions to another host,
survive either endpoint losing its session state, or make external effects durable
or exactly-once. It stores transport state, not a workload execution process or WASM instance
context after work finishes. Ordinary job cleanup remains unchanged.

Decision: use stock AAN record framing as a transport layer beneath 9P. Do not
build an outer-9P resumable-stream service or wrap AAN records in 9P file data.
All application and Orleans RPC remains 9P; AAN, like TLS and TCP, is not an
alternative RPC protocol. This explicitly amends the earlier requirement that
the bytes immediately inside TLS must always decode directly as 9P.

The secured data path, from the physical carrier toward the application, is:

```text
TCP -> TLS 1.3 -> stock AAN records -> logical 9P2000 -> files / Orleans payloads
```

TLS protects AAN headers, acknowledgements and payloads. Each replacement physical
carrier has a fresh validated TLS connection; TLS resumption, if used, still does
not authorize an AAN session claim. Disable early application data. Logical 9P
state survives a permitted carrier replacement, not the old TLS connection.
Server-authenticated user TLS and mutually authenticated node TLS retain their
separate trust rules. The direct, non-resumable profile remains TLS then 9P.

The [session profile](../fog-v1-profiles/AanSession.md) defines initial eligibility,
owner-bound claim bytes and profile selection. The diagram above is the data phase:
ordinary authenticated bootstrap 9P precedes an explicit switch/Rwrite barrier.
Bootstrap service calls stay independent of grains and are included in the wire audit. No
HTTP resume endpoint, raw Orleans RPC, private 9P opcode, unauthenticated reconnect,
or implicit protocol sniffing/downgrade is permitted.

Wire assertions apply to the reconstructed logical stream after TLS decoding and,
on AAN endpoints, validated AAN reassembly and duplicate suppression. The first
request of each new logical session is Tversion; a resumed carrier continues that
session and does not send another Tversion. AAN records need not align with 9P
messages. All bytes and framing at both layers must be accounted for independently.

Stock record framing does not imply compatibility with an unmodified 9front
secure-session setup. Verify the record codec against a real 9front peer in a
controlled fixture, and report its source revision and any interoperability
limitations. Production authentication and behavioral conformance are additional
gates; do not weaken the one-way-progress or security requirements to claim reuse.

## Physical carrier versus logical session

The logical session owns the 9P parser, negotiated dialect and msize, afids, fids,
pending tags, namespace roots, open handles, Orleans handshake state, ordered byte
queues and authentication lifetime. A physical socket is replaceable; these objects
are not reconstructed by replaying 9P attach/open/write operations.

```text
connected -- carrier lost --> suspended -- authorized resume --> connected
    |                            |
    +--- close/reset/revoke ------+--- expiry/state loss ---> terminated
```

Only an explicitly AAN-enabled, resumable session can take the resume transition.
A direct connection still terminates on carrier loss. A pre-authentication attempt
cannot hold resumable resources indefinitely: the original handshake deadline
continues; the session profile enables resumption only after an authorized inner
attach has committed.

- Suspension is not EOF, Tflush, Tclunk, Tversion, job cancellation or worker loss.
  It does not discard an unsealed upload or deliver a synthetic protocol reply.
- Resume preserves the same logical session, including a partially assembled 9P
  message. No new inner Tversion, attach, open or Orleans handshake is injected.
- Terminal loss destroys that session. Any replacement starts fresh negotiation
  and authentication; no retained bytes, tags, afids or fids enter the new session.
- Explicit version reset retains standard 9P semantics: invalidate the preceding
  session's handles and pending operations. AAN cannot resurrect the old epoch.
- Closing the logical connection is terminal even while suspended. Cancel timers,
  redial tasks and queued IO, and free ownership within the cleanup deadline.
- A single successful carrier claim fences preceding carriers. Late reads, writes,
  ACKs and completion callbacks from them cannot change the live state. Contending
  claims use an atomic session-local generation, never last-writer-wins routing.

The client library must expose connected/suspended/terminated distinctly to its
caller; an accepted local buffer write is not a remote 9P Rwrite. An operation can
still time out while the stream is suspended. If the caller cannot complete its
flush/tag-retirement barrier, fail the logical session before reusing those tags;
do not discard an arbitrary queued byte range and splice the stream back together.

## Byte continuity and source reuse

The local reference is `../9front/sys/src/cmd/aan.c`, with the behavior described
by [aan(8)](https://9p.io/magic/man2html/8/aan). Its record header has three
little-endian 32-bit fields: payload length, message sequence and cumulative ACK.
Payloads are at most 8192 bytes. A zero-length record with sequence `0xffffffff`
is synchronization; a zero-length ordinary record denotes stream termination.
These are the selected data-record format, not new 9P message definitions. Do not
add private fields or encode headers as LibTab. Human-facing configuration and
controls remain text; binary AAN and 9P framing stays at the transport layer.
The 8192-byte record payload limit is independent of negotiated 9P msize: fragment
larger valid 9P messages across records without changing either layer's bounds.

Reuse the sequencing/retransmission model, with explicit safety requirements:

1. Retain each accepted outgoing record until a valid cumulative ACK releases it.
   ACK means acceptance into the peer's retained bounded stream state, not grain
   execution, stable storage or successful job admission. The ACK value names the
   next expected sequence: ACK 1 acknowledges record 0, not record 1. It cannot
   exceed one past the highest sequence offered to the physical carrier. Publish
   that send watermark before a racing peer can acknowledge the offered record.
2. Accept only a complete, bounded record before exposing its payload. Discard an
   incomplete physical record on carrier loss; retain the preceding logical bytes.
   Retransmit whole unacknowledged records with their original sequence and bytes.
3. Deliver the next expected sequence once. Previously accepted sequence numbers
   are duplicates, not another write to the 9P parser. Future gaps and impossible
   ACKs are errors, not permission to skip bytes or release unsent data.
4. Process valid cumulative ACKs on synchronization and duplicate records too.
   Stale ACKs do not move the release watermark backward. Keep synchronization
   distinct from EOF, and allow a one-way application stream to release its window
   without requiring application data in the opposite direction.
5. Preserve ordering in both directions, including Rflush barriers. Never insert
   a retransmission into a newly negotiated logical session. A repeated transport
   record is not a repeated application invocation.
6. Reserve the synchronization sequence; do not silently wrap counters or alias
   old records. End the session before exhaustion if the selected profile has no
   independently reviewed wrap algorithm. Test the boundary with injected counters.
7. A physical EOF or partial physical write triggers suspension, not logical EOF.
   An explicit logical close has a bounded drain followed by terminal cleanup;
   do not promise an acknowledged graceful close after the peer becomes unreachable.

The local source currently bypasses ACK processing for zero-length synchronization
records. Copying that control flow would undermine one-way progress after filling
the replay window. Also review incomplete-header handling, invalid ACKs, concurrent
reconnect ownership, zero-progress writes, sequence exhaustion and EOF behavior.
Do not inherit its one-day timeout, memory defaults or unauthenticated TCP accept
as fog policy. Record framing compatibility is not proof of these safety properties.

## Resume authority

A session identifier, known fid, TCP source address, sequence number or ACK is not
resume authority. Before accepting replay data, authenticate the replacement
carrier and bind its claim to the exact live session, intended server, server boot,
owner, original authorization epoch and a fresh anti-replay exchange. Session IDs
must be unpredictable and non-reused. Resume cannot change the owner or export.

User sessions remain authorized through user-side factotum; nodes retain the
separate enrolled-node trust profile. A user proof never resumes a node channel.
V1 AAN sessions bind one enrolled user or one enrolled node as their resume
owner. Additional user principals require separate resumable sessions; otherwise
resuming one principal could recover another principal's fids. Ordinary direct 9P
multi-principal attach semantics are not removed.

The current `fog-auth-v1` statement authenticates an afid, not a resume request.
Do not reinterpret or replay it to authorize a new carrier. The session profile's
fogaan-v1 statement binds the fresh bootstrap carrier, original logical session
and server identity; its node proof is bound to the enrolled TLS peer. TLS alone
does not select or transfer ownership of a session. These requirements need real
implementation and review, not authorization implicitly supplied by AAN.
Resume credentials/proofs must never appear in status,
logs, public namespace entries or guest capabilities.

Expired authentication, explicit revocation, changed policy epoch, missing replay
state or a changed endpoint incarnation rejects resumption. Reauthentication
cannot extend the original session's maximum lifetime, widen its rights, or revive
old handles under a new policy. Failed or malicious claims must not evict a healthy
owner or renew a suspended session's timeout.

## Finite resources and failure detectors

For the eventual AAN-enabled profile, extend the operator's `foglimits-v1` table
with positive unsigned limits, validated before listening:

| Names | Meaning |
| --- | --- |
| `aan_sessions`, `aan_sessions_per_owner` | Global and authenticated-owner retained sessions, including suspended sessions |
| `aan_buffer_bytes`, `aan_total_buffer_bytes` | Per-session and global owned send, replay and receive bytes, including partial records |
| `aan_resume_ms`, `aan_lifetime_ms` | Maximum uninterrupted suspension and absolute session lifetime |
| `aan_retry_min_ms`, `aan_retry_max_ms`, `aan_probe_ms` | Bounded jittered redial backoff and liveness probe interval |

Existing connection, pre-authentication, request/fid, handshake and cleanup limits
still apply. Record metadata, active attempts and tasks also need fixed bounds;
small or zero-length records cannot bypass the byte budget with unlimited objects.
Reject inconsistent limits, including retry minimum above maximum or storage too
small for the chosen profile's bounded data and control reserves. Configure AAN
explicitly as required or disabled per endpoint; a required peer that does not
support the pinned profile fails closed, not a silent non-resumable fallback.

Backpressure stops accepting more bytes when capacity is exhausted. Reserve
bounded capacity for ACK/probe and local abort processing; never block cleanup on
an application reader or grain. Per-owner quotas cannot replace a node-wide cap.
Failure to reserve a new session must not evict another owner's live session.

Monotonic timers continue while suspended. Failed dials, partial handshakes and
unauthorized claims do not restart the outage timer; reconnect success does not
restart the absolute lifetime. The effective deadline intersects authentication,
operation, policy, drain and shutdown deadlines. AAN cannot delay a hard shutdown.

Most importantly, AAN probes and resumes are not Orleans membership heartbeats or
execution-lease renewals. Grain-call deadlines, worker CPU allowances, WASM fuel, staging expiry, job deadlines
and result retention continue to run. Renewals
buffered by AAN retain their original request time and incarnation/epoch: delayed
or duplicate replies cannot resurrect expired authority. Use separate logical
channels for bulk traffic and control progress; AAN cannot remove head-of-line
blocking within an ordered stream or overtake queued bytes with a cancellation.

## Acceptance and implementation order

[AanWireProtocol.feature](AanWireProtocol.feature),
[AanContinuity.feature](AanContinuity.feature), [AanSafety.feature](AanSafety.feature)
and [AanIntegration.feature](AanIntegration.feature) define the added obligations.
They are unbound BDD with `@SW9P_A*` identifiers, not completed acceptance tests.

1. Implement/review fog-aan-v1 for the selected TLS/AAN/9P stack and bind its complete
   bootstrap/switch/resume wire audit, not just data IO.
2. Build the bounded record engine and single-owner session state machine with
   controlled clocks and a fault-injected duplex carrier, independently of Orleans.
3. Integrate both client and host beneath the existing logical 9P processor; test
   a real file session before adding the Orleans connection adapter.
4. Verify real two-silo and job behavior, including partitions that outlast leases.

Use the existing unit, property, fuzz and mutation regime, not a new weaker gate:

- Unit/BDD: assert exact delivered bytes, invocation counts, ACK watermarks, pending
  tags, open-handle ownership, deadlines and return to the idle resource baseline.
- Property: generate fragmentation, cuts in every record/header position, lost
  ACKs, replay, duplex interleavings and competing resumes against a small ordered
  stream model. Exercise saturation and sequence boundaries, not just short echoes.
- Fuzz: add bounded stateful record/resume campaigns to the SharpFuzz/AFL++ harness;
  target hostile lengths, ACKs, truncated records and expired/foreign claims. Assert
  no unauthorized delivery, duplicated bytes, unbounded work or leaked ownership.
- Mutation: retain the existing 90% threshold; cover duplicate suppression, ACK
  bounds, single-owner fencing, expiry, quota accounting and terminal cleanup.
  Investigate survivors rather than excluding these decisions.
- Integration: an independent wire recorder, two actual silos, real factotum/TLS
  where applicable, and a real 9front peer for any advertised compatibility profile.
  Exercise direct 9P clients separately; built-in AAN is not required of every client.

Run `bash scripts/run-quality.sh --full` when implementation and bindings exist.
Parsing these feature files alone verifies syntax, not recovery or security.
