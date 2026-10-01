# Single-operator fog foundation

Status: proposed, specification-only. These documents and Gherkin scenarios are
design contracts, not implemented authentication, authorization, membership, or
acceptance tests. They add no production dependency and change no running service.

## Scope and decisions

This is the security and host-control foundation for the existing
[9P transport](../swarm-9p-transport/README.md),
[LibTab jobs](../libtab-compute-jobs/README.md), and
[provider interface](../workload-providers/README.md).
Workloads remain application-neutral. Games, identity federation, and application
economies have no special cases in this layer.

Version one has:

- One operator, one explicitly configured control node, and explicitly enrolled
  worker nodes. The control node can also execute work.
- User-owned factotum processes. The fog stores enrolled public keys, not users'
  passwords or private signing keys. Secstore is optional client infrastructure.
- Server-authenticated TLS for user connections; mutually authenticated TLS and
  separately enrolled node identities for internal connections. All remote service
  RPC is 9P; configured stock AAN framing sits between TLS and logical 9P.
- Standard authentication fids for user proofs; no login HTTP endpoint, bearer
  login token, private 9P opcode, or automatic dp9ik/p9sk1 fallback.
- Operator-controlled LibTab configuration, per-principal exported namespaces,
  resource-side authorization, and aggregate admission limits.
- Disposable execution with bounded retained outcomes. No automatic failover,
  transparent job retry, or durable exactly-once effects.
- [Built-in AAN](../swarm-9p-transport/Aan.md) for bounded continuity of live
  logical sessions across temporary link failures. Stock AAN beneath 9P is selected;
  its [secure session contract](../fog-v1-profiles/AanSession.md) defines bootstrap
  and resumption. AAN does not extend authority or job leases.

No federation, Emercoin, public worker enrollment, arbitrary plug-in downloads,
recursive jobs, remote administration API, or general distributed transaction
service is introduced. Nodes and their host administrators are trusted; uploaded
guest programs and network clients are not. A signature is not remote attestation.

## Ownership

| Owner | Responsibility |
| --- | --- |
| User's factotum | Select an approved key and sign a verified authentication statement |
| Local 9P host | TLS, authentication fids, principal binding, bootstrap node admission, connection limits |
| Client and host AAN transport | Bounded replay state, physical reconnect and same-session continuity, independent of grains |
| Control node | Active policy, membership provider, admission reservations, job records, scheduling |
| Resource host | Enforce effective resource grants, handle lifetime, current execution lease |
| Orleans | Grain identity, placement and lifecycle within the configured cluster |
| Execution worker | Run a pinned provider in an enforceable containment boundary |

The control-node services needed to form the cluster are ordinary host services,
not grains. Once the runtime is available, resource and job coordinators may be
grains. There is no dependency from opening an Orleans transport channel back to
Orleans activation.

## Documents

| Design | Contract |
| --- | --- |
| [Authentication](Authentication.md) | Factotum proof profile, TLS identity, auth-fid byte protocol, enrollment |
| [Namespace policy](NamespacePolicy.md) | LibTab policy tables, roots, groups, rights, revocation and worker authority |
| [Host lifecycle](HostLifecycle.md) | Bootstrap, membership boundary, scheduling, leases, restart and operations |

The normative feature files are `Authentication.feature`,
`NamespaceAuthorization.feature`, `PolicyLifecycle.feature`,
`HostBootstrap.feature`, `AdmissionRecovery.feature`, and
`FoundationIntegration.feature`. Stable scenario IDs start with `@FOG_`.
`ResourceAuthorization.feature` (`@NS_AUTHZ_`) specifies the resource authorization
layer, implemented in `NinePSharp.Namespaces.Authorization` with executable bindings in
its test project. `NamespaceViews.feature` (`@FOG_VIEW_`) specifies per-principal Fog
views; it has no bindings yet.

[Examples](examples) illustrate canonical challenge, node configuration and host
status documents. Their repeated-byte nonces and certificate/key digests are
non-operational fixtures, not deployable credentials, randomness or trust pins.
They are not a complete policy bundle or a successful authentication transcript.

## Plan 9 reuse and deliberate differences

Local source references are relative to the repository root:

| Reuse | Source | Adaptation |
| --- | --- | --- |
| Auth fids and attach matching | `../9front/sys/man/5/attach`, `sys/src/lib9p/auth.c` | Preserve fid lifecycle; replace the p9any/AuthInfo driver with the explicit proof verifier |
| User-side signing | `../factotum-dp9ik/src/cmd/auth/factotum/monocypherproto.c` and `monokey.c` | Reuse `role=sigcell`; do not reinterpret `tpm9p-user-auth-v1` |
| Namespace groups and construction | `../9front/sys/man/6/namespace`, existing `docs/plan9-namespace-semantics.md` | Build authorized views from existing mount/handle machinery, with LibTab configuration |
| User/group and open permissions | `../9front/sys/man/6/users`, `sys/man/5/open` | Implement actual group membership; lib9p's small uid helper is not a complete group database |
| Limited exports | `../9front/sys/man/4/exportfs` | Explicit roots and read-only projections; not a substitute for sandboxing |
| Administrative lookup | `../9front/sys/man/8/ndb`, `sys/man/3/srv` | Static endpoint discovery; neither a membership consensus service nor a transferable remote fd |
| Persistent byte streams | `../9front/sys/man/8/aan`, `sys/src/cmd/aan.c` | Built-in bounded session resumption; authenticated reconnect and source edge cases require explicit design and tests |

Explicit authority revocation is additional to ordinary open-time mode checking.
Virtual namespaces do not sandbox arbitrary native .NET code. Existing fids never
become authorized merely because their path is now visible under a new mount.

## Verification contract

These features must acquire real Reqnroll bindings as implementation proceeds.
Parsing them successfully is syntax validation only. No skipped/no-op scenarios
count as completion. Preserve the existing unit, property, fuzz and mutation gates:

1. Unit/BDD: deterministic clocks and synchronization barriers, exact proof bytes,
   known enrolled public keys, explicit policy generations, observable execution
   counts, permissions and resource ownership.
2. Property: fragmented auth reads/writes; auth/attach/reset races; aliases and
   mount changes; generated policy intersections; concurrent reservations; lease
   renewal, expiry and stale completion sequences against small reference models.
3. Fuzz: bounded LibTab/UTF-8/signed-cell inputs, duplicate fields/rows, algorithm
   confusion, huge lengths, auth-fid misuse, hostile namespace and policy commands.
   Assert no unauthorized attach/effect, privilege widening, quota overrun, leaked
   secret, or unbounded allocation, not just no crash.
4. Mutation: retain the existing 90% threshold. Target principal/export matching,
   signature verification, challenge consumption, permission intersections,
   generations, deadlines, reservation accounting and cleanup. Investigate
   survivors; do not exclude these decisions to obtain a green run.
5. Integration: real custom factotum, C/.NET LibTab agreement, real TLS and two
   silos, the selected membership adapter, and a separately isolated runtime.
   Capture all service traffic and reject undeclared non-9P connections.

Acceptance includes rejection of valid-but-wrong-context signatures. Cryptographic
review of the complete new authentication profile is required before production;
reuse of a signing primitive or passing conformance tests is not that review.

## Implementation sequence and remaining bounded design work

1. Prove actual factotum-to-.NET signed-cell verification and auth-fid integration.
2. Bind principals to resource permissions and private namespace groups.
3. Implement and review AAN resume authentication for the selected stock framing
   beneath 9P, then verify live file-session recovery.
4. Start one node using local configuration; introduce the second over 9P.
5. Bind the selected Orleans version's membership API to the host-level 9P store.
6. After namespace compatibility, prove a bounded WASM job through the same path.

The [concrete v1 profiles](../fog-v1-profiles/README.md) now specify the six previously
open contracts: AAN establishment, Orleans membership records, the three runtime
profiles, optional persistent storage, worker/scope coordination and Linux process
containment. These are selected designs, not capabilities supplied by `ndb` or
implemented services. Exact installed bundles/limits are deployment configuration;
bindings, implementation, security review and real runtime evidence remain required.
