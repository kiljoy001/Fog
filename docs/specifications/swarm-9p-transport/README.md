# Swarm-wide 9P transport: BDD contract

Status: proposed, specification-only. These Gherkin files have no step bindings
yet and are not passing acceptance tests. They live outside the test project so
the existing quality gate cannot accidentally count unimplemented scenarios as
verification. No transport implementation is included in this change.

## Intended outcome

Every swarm-owned network path uses 9P, including Orleans client traffic,
silo-to-silo calls, and the Orleans control plane. A 9P gateway in front of a
separate native Orleans network does not meet this contract. Neither does
negotiating 9P and then switching the socket to an unframed Orleans stream.

Orleans retains grain identity, invocation encoding, placement, scheduling, and
activation management. Its encoded messages are file data carried by ordinary
9P reads and writes. A general 9P client can open and use the channel, but must
understand Orleans payloads to invoke arbitrary Orleans methods through it.
This transport work does not by itself unify application-specific grain APIs.

The [single-operator foundation](../fog-foundation/README.md) specifies bootstrap
identity and user-side factotum authentication separately from transport framing.
Its user-proof listener is a distinct TLS profile; internal node channels retain
explicit mutual authentication and grain-independent setup.

The workload-facing text interface is separately specified in the
[LibTab compute-job contract](../libtab-compute-jobs/README.md).

[Built-in AAN](Aan.md) adds the required resumable-session design: a temporary
physical break may preserve a live logical 9P session, while terminal loss still
requires a fresh session. Its lifecycle and safety requirements are specified;
the selected secured data stack is TCP, TLS, stock AAN framing, then logical 9P.
The [fog-aan-v1 session contract](../fog-v1-profiles/AanSession.md) specifies its
authenticated bootstrap, claim and switch. AAN must be built into both transport
endpoints, independent of grains; implementation and security review are still needed.
This replaces the earlier outer-framing restriction: decode TLS and reassemble
AAN before asserting that the logical stream consists entirely of standard 9P.
Direct non-resumable endpoints remain a separately configured compatibility profile.

TCP/IP remains the network substrate; TLS secures fog links and AAN provides
bounded byte-stream continuity. None is an alternate swarm RPC protocol. This does
not require one socket, one port,
or one listener for the entire swarm. It does require that all advertised swarm
application and control-plane endpoints speak 9P with no native-RPC fallback.

External providers are not silently exempted. The strict 9P-only deployment must
use local providers or providers whose remote traffic uses 9P. An external SQL,
Redis, HTTP, or other non-9P provider makes that deployment nonconforming. OS
infrastructure such as address resolution is outside the swarm protocol claim;
the wire acceptance fixture uses static peer addresses to make its traffic audit
unambiguous. This specification does not require implementing every external
provider: incompatible ones must be rejected in strict mode.

## Proposed channel contract

- The baseline dialect is standard `9P2000`; no private 9P opcodes are required.
- `/transport/orleans` is the proposed channel file in the authorized swarm
  export. Its name is a specification choice, not an existing implementation.
- A connection negotiates, authenticates/attaches, walks, and opens the channel
  read-write before Orleans bytes can pass. `Tattach.Uname` is not proof of identity.
- The channel is a non-seekable, connection-local duplex stream. Offsets do not
  select stored data or trigger replay. Independent channels have independent
  byte streams; there is no cross-channel ordering guarantee.
- The adapter keeps at most one data read and one data write outstanding per
  channel. The protocol loop must still process flush and lifecycle requests
  while either direction is blocked.
- Positive-length reads wait for bytes or EOF; temporary idleness is not EOF.
  Zero-length reads return immediately without consuming data. A successful
  write acknowledges stream acceptance, not grain execution or durable storage.
- Opening the channel, authenticating it, and closing it must not require grain
  activation or a remote grain call. This prevents a circular bootstrap dependency.
- Fids, pending tags, and byte buffers are bounded and session-owned. A new
  logical session or version reset never inherits the preceding session's state.
  AAN resumption retains the same live session; direct connections cannot resume.
- Flush preserves the 9P reply barrier. Cancellation is not rollback. If a write
  has an ambiguous accepted prefix, terminate that byte stream instead of guessing
  which bytes to replay into it.

The flush and stream IO expectations follow the local 9front references
`sys/man/5/flush`, `sys/man/5/read`, and `sys/man/5/version`. In particular, an old
reply may precede `Rflush`, but must never follow it. The channel's non-seekable
semantics are an explicit service contract, not a claim that ordinary files ignore
offsets.

## Features and evidence

| Feature | Observable acceptance evidence |
| --- | --- |
| [SwarmWireProtocol.feature](SwarmWireProtocol.feature) | Captured links decode entirely as standard 9P; no alternate RPC connections |
| [AanWireProtocol.feature](AanWireProtocol.feature) | Stock AAN record framing below 9P, independent size limits and real-peer codec compatibility |
| [TransportChannels.feature](TransportChannels.feature) | Exact bytes, order, isolation, partial transfers, and EOF behavior |
| [TransportLifecycle.feature](TransportLifecycle.feature) | Bootstrap independence, flush barriers, cleanup, reconnect, and ambiguous failures |
| [TransportSafety.feature](TransportSafety.feature) | Authentication, malformed-input rejection, negotiated bounds, and backpressure |
| [SwarmIntegration.feature](SwarmIntegration.feature) | Real cross-silo execution/migration, a stock 9P client, and deployment conformance |
| [AanContinuity.feature](AanContinuity.feature) | Same-session replay suppression, partial records, one-way progress, flush and terminal loss |
| [AanSafety.feature](AanSafety.feature) | Resume ownership, fencing, quotas, expiry and malformed records |
| [AanIntegration.feature](AanIntegration.feature) | Upload continuity, non-pausing budgets, lease expiry and real silo recovery |

The features are tagged `@swarm_transport`; scenario IDs provide stable references
for implementation and regression tests. `@wire`, `@property`, `@fuzz`, `@security`,
and `@cluster` identify required evidence, not implemented test capabilities.

## Binding and verification plan

Promote the features into `NinePSharp.Namespaces.Orleans.Tests/Features/Transport`
with real Reqnroll bindings as implementation starts. Unbound scenarios remain
explicitly unimplemented; do not supply no-op assertions or silently skip them
to claim completion. Existing resource/gateway BDD scenarios remain regression
requirements, not substitutes for these transport scenarios.

Use the existing unit, property, fuzz, and mutation regime:

1. Unit/BDD: deterministic duplex streams and an independent wire recorder. Assert
   responses, accepted bytes, resource counts, and application completion separately.
2. Property: vary binary payloads, fragmentation/coalescing, negotiated sizes, and
   read/write interleavings. Compare delivered bytes against a simple stream model.
3. Fuzz: extend the existing SharpFuzz/AFL++ runner with malformed frames and
   stateful negotiate/attach/walk/open/read/write/flush/reset/close sequences. Check
   bounds, session isolation, and cleanup, not merely absence of exceptions.
4. Mutation: retain the existing 90% gate and cover frame bounds, response tags,
   partial-write accounting, authorization, cancellation barriers, and cleanup.
   Inspect surviving mutants for missing behavioral assertions; do not weaken the
   threshold or exclude transport decisions to make a run green.
5. Integration: use at least two real silos and an external Orleans client, force
   calls onto another silo, and capture every swarm link. Block undeclared outbound
   connections. Independently decode TLS/AAN and check the full logical 9P streams;
   audit bootstrap and resumption too. Exercise a stock 9P client independently of
   the new adapter on its explicitly configured direct endpoint.

Use synchronization barriers and controlled time for races/deadlines. Named limits
in scenarios are fixture configuration, not performance claims. Do not assert
elapsed sleeps or incidental allocation totals as proof of correctness. Memory
oracles measure owned buffers, queued payload bytes, outstanding operations, and
return to the fixture's idle resource baseline.

Implementation is complete only when the behavior is bound and verified, the full
quality gate passes, and the deployment traffic audit finds no non-9P swarm path.
