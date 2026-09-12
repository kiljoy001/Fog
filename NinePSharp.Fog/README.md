# Fog control core: first implementation slice

This library starts [fog-tx-v1](../docs/specifications/fog-v1-profiles/Records.md).
It is a host-local component, **not a mounted 9P service or a completed fog runtime**.
No listener is enabled and no authentication bypass is registered.

Implemented:

- Closed plain-cell records using the actual `LibTab` 0.1.0 NuGet package. Strict
  UTF-8, exact schema/order, canonical escaping, nil omission, physical duplicate
  detection, semantic tuple keys, finite byte/row limits and the 7168-byte cell guard.
- Ephemeral server-generated transaction identities, owner checks on every access,
  conservative count/byte reservations, one writer, contiguous uploads, explicit
  seal, replacement and terminal-session abort. Aborting does not erase a preceding
  sealed revision. AAN suspension must not call `CloseSession`.
- Frozen inputs/outputs, commit-once/replay, semantic rejection, cancellation of a
  caller's wait without cancelling the effect, immutable offset reads, expiry and
  cleanup. Exceptions during apply leave the transaction committing/unavailable;
  they never authorize a retry or silently release its reservation.
- Exact `commit\n` and `release\n` per-write control commands. Three executable
  Reqnroll scenarios join the real record codec and transaction core; they do not
  substitute for the original wire-level BDD acceptance suite.

## Embedding contract

Create one `FogTransactionStore` per bounded service. Its owner/session arguments
must come from a trusted authenticated adapter, not unverified `Tattach.uname`.
The authorization callback performs the service's current policy check; it must be
bounded, non-reentrant and fail closed. It does not authenticate a connection.

Preparation is bounded, synchronous, side-effect-free and non-reentrant. It validates
all required auxiliary files and service-specific fields and produces every output
before `ApplyAsync` can execute. The store checks total output capacity and copies
the bytes. The service must implement its own atomic CAS/effect boundary; this generic
primitive cannot make external IO transactional. In particular it is **not** the
SQLite durable transaction/outcome ledger specified in Storage.md.

Apply is invoked outside the store lock, once, without the caller's wait token.
The service must provide its own finite execution watchdog and reconciliation.
If apply hangs or throws after possibly causing an effect, its transaction stays
reserved and cannot be released or retried. This initial core intentionally offers
no administrative "assume rollback" escape hatch. A production binding must mark
affected service operations unavailable until reconciliation/fencing is complete.

Reservations cover payload bytes, not exact CLR heap size. Sealed plus replacement
input is bounded together; temporary copies, task/buffer/record object overhead are
also bounded by these counts but require additional host memory headroom. Parsed
records have explicit independent `maxBytes` and `maxRows` limits. A host supplies
the appropriate monotonic `TimeProvider`; this default is not a claim to implement
the Linux `CLOCK_BOOTTIME` execution-lease profile.

The separate [Fog server adapter](../NinePSharp.Fog.Server/README.md) now provides
9P clone/fid/control/status routing, per-open snapshot lifetime and separate snapshot
reservations for the direct enrolled-node TLS profile. Its client preserves known
transaction IDs across reconnects, and stream-processor tests cover flush ordering.

Not implemented yet: automatic commit watchdog/reconciliation,
service-specific schemas/roles, real
factotum bootstrap, TLS/AAN switch/resumption, membership/storage adapters, workers
and runtime containment. Keep the original wire BDD scenarios unclaimed until those
paths run through actual connections. Typed cryptographic LibTab cells also remain
with the authentication implementation, not this plain control-record wrapper.

## Validation

See [VERIFICATION.md](VERIFICATION.md) for the initial results, survivor review and
the distinction between the normal repository gate and targeted mutation/fuzz runs.

```sh
dotnet test NinePSharp.Fog.Tests/NinePSharp.Fog.Tests.csproj
bash scripts/fuzz.sh fog
# Run from NinePSharp.Fog.Tests:
dotnet stryker --config-file ../stryker-config-fog.json --skip-version-check
```

The solution and `scripts/run-quality.sh --full` include this project, its coverage,
the existing 90% mutation threshold (including string mutations), and its bounded
SharpFuzz/AFL campaign. The fuzzer checks a separate byte-list/transaction model,
canonical round trips, rejected transitions, immutable reads and effect counts;
only expected protocol errors are caught. The same model runs under FsCheck.

Use the published LibTab dependency normally. For offline development with the
existing sibling checkout, restore once using its packaged feed in addition to
your normal NuGet source; do not add a machine-specific project reference or copy
another implementation of its escaping/crypto into this repository.
