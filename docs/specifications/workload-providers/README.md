# Universal workload providers: grain adapter and authoring contract

Status: proposed, specification-only, like the transport and compute-job specs.
The C# contracts below are an authoring API proposal, not a published SDK or an
implemented workload grain. No runtime, provider loader, or production dependency
is added by these files.

## Two extension points, one public protocol

`IMountableResourceGrain` is the existing universal resource-grain interface in
`NinePSharp.Namespaces.Orleans.Abstractions/GrainContracts.cs`. It exposes traversal,
file IO, metadata, and handle lifetime. Authors implementing arbitrary resource
trees can continue to implement that interface directly.

Compute authors should not have to implement a filesystem merely to run work.
The proposed shared workload-grain adapter implements `IMountableResourceGrain`
and owns the [job file contract](../libtab-compute-jobs/README.md). An author supplies
an `IWorkloadProvider` to that adapter. AngouriMath, WASM, pathfinding, rendering,
and other engines are registrations of the same provider interface, not branches
in a closed runtime enum.

The initial exploratory scope is two providers: AngouriMath for explicit symbolic
math operations and WASM applications with a defined host ABI.
The math provider exposes a bounded subset of the library, and a WASM sandbox does
not supply every OS/application API. Pathfinding below is only an extensibility/conformance
example, not a third initial engine. The shared adapter should be proven with the
two initial providers before expanding the runtime catalogue.

External LLMs can use the proposed MCP gateway to invoke
authorized fog jobs. MCP is a client-facing adapter, not a provider, hosted language
model, or alternate inter-node protocol.

The selected initial [runtime profiles](../fog-v1-profiles/Runtimes.md) define the
AngouriMath expression/operation contract and Wasmtime WASIp1 ABI.
[linux-process-v1](../fog-v1-profiles/Isolation.md) defines their first concrete
containment and private worker-9P contract. These additions remain specification-only.

```text
9P clients -- LibTab spec + input + ctl --> shared workload-grain adapter
                                            |
                           frozen job + bounded execution context
                                            |
                              registered IWorkloadProvider
                                            |
                               per-job IWorkloadExecution
```

This does not introduce an `Execute(object)` RPC or a second network API. Orleans
continues to route resource operations; all remote links follow the
[9P-only transport contract](../swarm-9p-transport/README.md). The execution API is
local to the worker. Streams, cancellation tokens, execution objects, and provider
instances are never serialized as grain arguments.

## Proposed authoring API

The full illustrative declarations are in [Contracts.cs](Contracts.cs). They are
kept outside production projects until the hosting implementation exists.

```csharp
public interface IWorkloadProvider
{
    WorkloadDescriptor Describe();

    ValueTask<IWorkloadExecution> PrepareAsync(
        IWorkloadContext context,
        CancellationToken cancellationToken);
}

public interface IWorkloadExecution : IAsyncDisposable
{
    ValueTask<WorkloadCompletion> RunAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}
```

The responsibilities are:

| Member | Author's responsibility | Host's responsibility |
| --- | --- | --- |
| `Describe` | Return stable LibTab manifest and option declarations without job work | Validate and freeze registration metadata; publish read-only discovery files |
| `PrepareAsync` | Allocate one job's execution context and prepare its engine | Supply frozen inputs, capabilities, limits, and a bounded execution boundary |
| `RunAsync` | Perform the workload; write result bytes through the supplied output | Enforce IO/budget policy and decide the terminal job outcome |
| `StopAsync` | Request/perform engine-specific termination, concurrently with a blocked run | Keep control requests responsive; enforce stop deadline and containment |
| `DisposeAsync` | Reclaim all private state owned by the returned execution | Await/reconcile cleanup before publishing termination or reusing the worker |

`Describe` is registration metadata, not a remote request handler. The host freezes
its result; changing an object/string later cannot change registered policy. A
provider may be reused across jobs, but `PrepareAsync` returns a fresh execution
object each time. Shared immutable caches are permitted under node quotas. Mutable
execution state belongs only to that returned object. A provider that is not safe
for concurrent preparation must be hosted with an explicit concurrency limit.

`PrepareAsync` runs after admission when a compatible worker begins preparation.
The host has already validated the job's core fields, declared options, namespace
references, and artifact identities. Provider-specific input decoding can still
fail the job during preparation. Preparation is bounded by remaining admitted time
and preparation quotas and cannot perform externally visible workload mutations.
Partially allocated state on a preparation exception is the provider's cleanup
responsibility; containment supplies the last-resort cleanup boundary.

The host calls `RunAsync` at most once for a prepared execution. It may call
`StopAsync` before or concurrently with the run. Stop is idempotent. After the run
has stopped, the host calls `DisposeAsync` once. If preparation completes after
cancellation, its execution is stopped/disposed without being run. A hung prepare,
stop, or dispose cannot hold the whole silo indefinitely: the host must terminate
the affected isolated worker within policy.

Returning `WorkloadCompletion` is a candidate success, not authority to set job
state. Host cancellation, budget exhaustion, output failure, or cleanup failure
can prevent success publication. `FinishReason` is bounded provider metadata, not
an override for the host's state machine. Exceptions become sanitized job failures.
Providers do not receive a state setter, raw fid table, `IGrainFactory`, or unrestricted
service container through the execution context.

## Execution context

The context contains:

- The immutable job ID and a host-owned, immutable snapshot of decoded LibTab
  specification values. No caller-owned mutable `TabTable` is shared with a run.
- A read-only input stream and a bounded write-only output stream. Output is
  private staging data until the host publishes a successful result.
- Read-only access to artifacts by declared field name, such as `source`.
  These accesses refer to the already pinned bytes, not a fresh pathname
  lookup that can select different content midway through execution.
- An authorized namespace facade for separately granted read/write capabilities.
  Workload output permission is not permission to write arbitrary namespace files.
  Preparation cannot use this facade for externally visible writes.
- Read-only limits, remaining deadline, and a host-owned `Charge` operation for
  declared cumulative work meters. Providers cannot refund, reset, rename, or
  increase those limits. Memory and time enforcement are not cumulative work meters.

Meter charges happen before the corresponding work. Exceeding a meter latches a
host failure, revokes further job IO as appropriate, and initiates cancellation;
catching a charge exception and returning success cannot clear that failure. Native
engine meters such as WASM fuel may be enforced by their runtime adapter and reconciled
with the host's accounting. Provider-reported usage is not proof of enforcement.

Streams are host-owned; a provider must not retain them after execution disposal.
The host invalidates the context and any opened handles at termination/release.
Resource access preserves 9P namespace authorization and operation identities.
External side effects still require provider/application idempotency when retries
are explicitly requested; this API does not create exactly-once execution.

## Registration and discovery

Providers are explicitly installed/enabled by the host administrator. A job cannot
select a CLR type, assembly path, arbitrary executable, or download location to
load a provider. A trusted registration maps an exact `(runtime, provider_version)`
pair to an implementation. Names and versions are bounded, case-sensitive path
components; empty values, separators, and traversal components are rejected. Versions
are exact opaque identifiers, not floating `latest` or implicit semantic-version ranges.

Duplicate keys fail registration rather than last-writer-wins replacement. Different
versions may coexist. At admission, pin the registration's implementation identity,
descriptor, artifacts, and metering policy. The same key with different implementation
bytes or descriptor semantics on another worker is incompatible; placement cannot
silently switch to it. Upgrades affect only jobs explicitly selecting the new version.

The proposed discovery tree is:

```text
/compute/providers/<runtime>/<provider_version>/manifest
/compute/providers/<runtime>/<provider_version>/options
```

Both files are read-only LibTab snapshots. Discovery declares compatibility, not a
reservation or promise of current capacity. Node scheduling still checks actual
resources, available artifacts, and isolation capability when admitting/placing work.

The manifest uses `fogprovider-v1` with one row describing `runtime`,
`provider_version`, `provider_api`, `job_schema`, and `isolation`. The host adds and
pins an independently verified implementation identity in its registration record.
`provider_api=1` names this proposed provider ABI. `isolation=process` requires a
host-enforced worker process; it is not satisfied merely by a provider claiming to
launch one. Other supported isolation profiles must specify equivalent capability,
memory, and termination guarantees before accepting untrusted code.

## Extensible LibTab options and meters

`fogjob-v1` now requires `provider_version` alongside `job` and `runtime`. This
generalizes the still-proposed job schema; there is no deployed format migration.
AngouriMath/WASM retain their documented required fields as built-in provider
profiles. New providers use the common memory/deadline/output limits plus their
own declared fields.

Provider-defined job fields must begin `p_`, fit the existing 31-byte attribute-name
limit, and be declared in that version's `fogoptions-v1` options table. They may not
override common fields, weaken common limits, or silently accept unknown options.
The host performs option validation before provider code sees the job. Unknown
runtime/version pairs, undeclared `p_` fields, and mismatched declared cell types fail
closed. An absent optional value and semantic nil use only an explicitly declared
default; required fields reject both. Literal `nil` remains ordinary text and must
pass the declared option kind's validation.

The v1 option table has columns `name`, `kind`, `required`, `default`, `minimum`,
`maximum`, `unit`, `meter`, and `usage_field`. Its supported kinds are `text`,
`uint`, `bool`, and `artifact`; these are service validation metadata, not new LibTab typed-cell
tags. `uint` uses the job contract's bounded decimal rules; `bool` is exactly
`true` or `false`. Text has a finite UTF-8 byte bound (`maximum`), and numeric options
have declared finite bounds. There are no implicit unbounded defaults.
Numeric options may allow zero only when their declared bounds permit it; work-meter
limits must be positive. An `artifact` option is a namespace file reference with a
finite byte maximum: the host authorizes and pins it before admission, and the
provider accesses it through `Artifacts.OpenReadAsync` using that option's name.
A plain text option is not automatically an artifact or an access capability.

A positive uint option may declare a cumulative `meter` and a corresponding
`p_`-prefixed `usage_field` in status. The meter's limit is the validated option
value; its unit is explicit. The host produces the usage value from its meter,
not from an arbitrary status row supplied by a provider. Meter names and usage
fields must be unique and cannot shadow common counters. A meter does not replace
memory limits, CPU containment, or deadlines. Built-in native metering fields remain
profile-owned rather than redefined through provider options.

See [the pathfinding example](examples/job-pathfinding.tab),
[its manifest](examples/provider-pathfinding.tab), and
[its option declarations](examples/options-pathfinding.tab). This is a third-party
provider example, not an implemented pathfinding engine. It demonstrates adding
`p_max_nodes`, charging a `nodes` meter, and exposing `p_nodes_used` while keeping
the same clone/spec/input/ctl/status/result workflow.

## Trust boundary

The [single-operator foundation](../fog-foundation/README.md) defines the proposed
principal, namespace and internal job-scope boundary used by these capabilities.
User factotum remains local to the user; providers never inherit a user signing key
or receive an unrestricted credential agent through the execution context.

An interface cannot sandbox arbitrary native .NET code. An in-process plug-in is
trusted host code and must be installed as such; withholding `IGrainFactory` from
the context does not stop malicious plug-ins from using ambient OS/.NET APIs.
Untrusted guest programs run inside a verified sandbox or isolated worker with
OS-level enforcement as needed. Hard termination is enforced by the host, not by
assuming every provider honors `CancellationToken` or every `StopAsync` succeeds.

Containment and peer/namespace authorization apply to provider-specific host calls
too. All inter-node communication stays 9P. A provider may not add an HTTP execution
endpoint or native RPC backdoor to make its conformance tests pass. Local in-process
method calls are not another network protocol; an out-of-process execution adapter
must preserve the agreed 9P communication boundary for its service traffic.

## Features and conformance

| Feature | Required observable behavior |
| --- | --- |
| [ProviderRegistration.feature](ProviderRegistration.feature) | Explicit versioned registration, discovery, options, and admission compatibility |
| [ExecutionContract.feature](ExecutionContract.feature) | Fresh bounded execution, stop/dispose behavior, meters, and failure containment |
| [GrainAdapter.feature](GrainAdapter.feature) | One file protocol, responsive grain control, state ownership, and provider substitution |

These are specification-only `@workload_providers` scenarios with stable `@PROV_*`
IDs. Existing transport and compute-job scenarios remain requirements. The proposed
SDK should ship reusable provider conformance fixtures: a new provider must run the
same lifecycle/IO/limit cases, not copy and weaken a private test suite.

Preserve the repository's unit, property, fuzz, and mutation regime:

1. Unit/BDD: a third-party fixture provider added without editing the adapter,
   plus adversarial providers that fail prepare/run/stop/dispose and complete late.
2. Property: option-schema validation, immutable-context isolation, exact meter
   boundaries, and generated prepare/run/cancel/dispose interleavings.
3. Fuzz: bounded LibTab manifests/options and job submissions, duplicate identities,
   mismatched versions, huge values, invalid meter declarations, and hostile streams.
4. Mutation: retain the 90% gate; cover registration conflicts, option/limit checks,
   identity pinning, failure latches, completion publication, and cleanup ownership.
5. Integration: register a separately built provider assembly on two real workers;
   execute through the normal 9P job files, verify placement/identity pinning, and
   audit all remote links. Exercise actual containment, not only cooperative doubles.

Passing the contract tests is compatibility evidence, not proof that an arbitrary
provider is secure or that its own algorithm is correct. Providers also need their
own algorithmic tests and an explicit deployment trust/isolation policy.
