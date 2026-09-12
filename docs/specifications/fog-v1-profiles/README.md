# Concrete version-one fog profiles

Status: proposed contracts, with a partial implementation tracked below. These
contracts address the six design gaps identified after selecting stock AAN beneath
9P. Their publication does not establish implemented services, security certification,
or passing acceptance tests. Existing unit/property/fuzz/mutation gates remain mandatory.

Implementation has started in [NinePSharp.Fog](../../../NinePSharp.Fog/README.md):
bounded plain LibTab records and the host-local ephemeral transaction core. This
has a [direct TLS control-file adapter](../../../NinePSharp.Fog.Server/README.md).
AAN authentication/switch and the service-specific integrations remain unfinished;
the full acceptance specifications are not passing tests. See the component
README for implemented boundaries, tests and remaining integration work.

## Decisions

| Gap | Selected contract |
| --- | --- |
| AAN establishment and resumption | [AanSession.md](AanSession.md): authenticated 9P bootstrap, explicit switch barrier, stock AAN data phase, fresh signed resume intent |
| Orleans membership | [Membership.md](Membership.md): Orleans 10.3.1 API mapping to a single control-host 9P transaction service |
| Math and WASM profiles | [Runtimes.md](Runtimes.md): bounded AngouriMath jobs and Wasmtime WASIp1 commands |
| Persistent resources | [Storage.md](Storage.md): optional single-host transactional blob store over 9P, local SQLite WAL/FULL, conditional grain-state writes |
| Worker coordination and authority | [WorkerControl.md](WorkerControl.md): pull assignments, immutable scopes, timed leases, resource-side scope attaches and fenced completion |
| Execution-process boundary | [Isolation.md](Isolation.md): Linux process-per-job supervision, cgroup v2, namespaces, seccomp and two private 9P channels |

[Records.md](Records.md) defines the common bounded LibTab transaction interface
used by membership, storage and worker control. These are file-service operations,
not a replacement binary RPC protocol. Orleans invocation bytes retain their
existing encoding inside `/transport/orleans`; local method calls remain local.

The operator chooses deployment limits and supplies exact installed dependency
digests. That is configuration under these contracts, not permission to change the
wire format, metering rules, ABI, authentication algorithm or state machine. An
unsupported profile or missing required mechanism fails closed before admission.

The six [example documents](examples) are canonical format fixtures, not a deployable
configuration or a successful authentication transcript. Repeated-byte IDs/digests,
the fictitious source revision and boottime deadlines are deliberately non-operational.
Storage-put also requires its sealed empty payload and an explicitly registered
fixture codec. Never copy these examples as keys, trust pins or production lock data.

## Scope retained

One trusted operator and one explicitly configured control node; no federation,
automatic leader election, control-node failover, public worker enrollment or
recursive jobs. Node administrators and installed provider/runner code are trusted;
guest code and input files are not. Linux is the first supported execution host.
Other hosts can act as clients; advertising equivalent execution support requires
a separately specified containment profile, not a silent weakened fallback.

Job execution remains disposable. AAN retains live transport state, not VM heaps.
Membership, jobs, scopes and retained job results are ephemeral across total control
process loss. Policy and enrollment retain their existing durable contract. Optional
storage persists explicitly registered resource/grain data, not the entire fog.

These documents refine the preceding transport, foundation, jobs and provider
specifications. In particular they explicitly add the `aan` bootstrap export and
the internal `scope:<id>` export; neither makes arbitrary attach strings authoritative.

## Evidence and implementation order

1. Bind the common record parser and real custom-factotum AAN bootstrap/switch.
2. Bind membership to the installed Orleans 10.3.1 interfaces; form two real silos
   with no native Orleans sockets and prove fresh versus resumed session behavior.
3. Bind worker control, scope authorization and the Linux supervisor together.
4. Run the AngouriMath profile through the common job interface; then WASM.
5. Enable durable storage only after crash/recovery and conditional-write tests pass.

Stable feature IDs use `@FOG_V1_*`. Feature parsing checks syntax only. Real Reqnroll
bindings must observe bytes, side effects, record versions, authority and resource
ownership; no no-op assertions or silently skipped scenarios count as implementation.

For every new boundary use the existing regime:

- Unit/BDD with controlled time, deterministic faults and exact state/byte assertions.
- FsCheck properties against small independent models: CAS, replay, lease expiry,
  scope attenuation, gas accounting and terminal-state races.
- Bounded SharpFuzz/AFL++ campaigns for record sequences, auth statements, runtime
  readers/modules/models and the worker protocol. Assert invariants, not just no crash.
- The existing 90% mutation gate, including new parsers/adapters and decision code.
  Review surviving mutants; do not exempt authorization, deadlines or persistence.
- Integration with actual factotum, actual silos, actual containment and an independent
  wire decoder. Storage adds process/crash fault injection at transaction boundaries.

`bash scripts/run-quality.sh --full` remains the release gate when code and bindings
exist. Native runner code also requires sanitizer-backed tests in its own build;
passing the .NET gate cannot certify native code which that gate never exercised.

## Sources checked

- Installed Orleans 10.3.1 `Orleans.Core.xml` and `Orleans.Core.Abstractions.xml`,
  plus the existing `GrainContracts.cs`: the actual API baseline, not a guessed API.
- Local `../9front/sys/src/cmd/aan.c`, `../go-aan`, `../factotum-dp9ik`,
  `../libtab` and `../libtabdotnet`: framing, signing and text-format references.
- Primary upstream runtime/OS/storage references are linked beside their use.
  New fog-specific policy and protocol choices are proposals of this project.
