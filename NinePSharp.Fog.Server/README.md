# Fog control files over 9P

This adapter exposes the [Fog transaction core](../NinePSharp.Fog/README.md) through
ordinary 9P2000 file operations on an explicitly started, mutually authenticated
TLS 1.3 node listener.

`FogTransactionFileTree` registers services under `/control/<service>`. Opening
`clone` allocates a transaction and returns its server-generated ID. Under that
ID, input files accept contiguous writes and seal on successful clunk; `ctl`
accepts exact `commit\n` and `release\n` writes. `status` and output files provide
snapshots taken at open. Session cleanup aborts unfinished uploads while preserving
sealed input and accepted commits.

`FogNinePDispatcher` bounds sessions, fids, outstanding requests and snapshot bytes
and lifetimes. Flush cancels a commit caller's wait while the accepted effect
continues. `FogNodePolicy` checks certificate identity and current enrollment on
attach and subsequent fid operations. The host must supply trusted enrollments,
certificate material, limits and service-specific commit callbacks.

`FogTransactionClient` uses `NinePSequentialClient` for bounded sequential exchanges.
It freezes input bytes, retries IO failures at most twice under one deadline and
preserves a known transaction ID across connections. After collecting all requested
outputs, it treats a lost release reply as cleanup failure and returns the result.
`FogTlsClient` connects to a numeric endpoint with an explicit server SPKI pin,
expected DNS identity and node certificate.

This is the direct enrolled-node TLS profile. Factotum user authentication, AAN
switch/resumption, a durable transaction ledger, automatic commit reconciliation,
membership and storage services, and the full worker/supervisor protocol remain unfinished.
The experimental math-worker implementation is described below.
The original broader wire acceptance specifications are not certified by these tests.

## Validation

See [VERIFICATION.md](VERIFICATION.md) for commands, results and scope limits.

```sh
dotnet test NinePSharp.Fog.Server.Tests/NinePSharp.Fog.Server.Tests.csproj
bash scripts/run-quality.sh
FUZZ_SECONDS=30 bash scripts/fuzz.sh fog-files
# From NinePSharp.Fog.Server.Tests:
dotnet stryker --config-file ../stryker-config-fog-server.json --skip-version-check
dotnet stryker --config-file ../stryker-config-control-client.json --skip-version-check
```

The tests cover dispatcher operations and limits, policy revocation, real TLS
connections, fragmented messages, lost commit/release replies, generated payloads
and two executable control-wire scenarios. The full quality script includes the
server and sequential-client mutation gates; passing the standard gate alone does
not establish that the full mutation and fuzz matrix passes.

## Direct math-worker milestone

`FogJobFileTree`, `FogJobClient` and `LinuxMathRunner` now provide an experimental
`/compute` service with asynchronous execution and retained results. The
[demo guide](../docs/fog-math-demo.md) documents the deployable host, AngouriMath
runner, two-machine workflow, resource limits and remaining profile work.
