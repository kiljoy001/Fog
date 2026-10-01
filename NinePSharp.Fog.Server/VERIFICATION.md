# Dispatcher session model and listener rejection — 2026-09-30

`FogDispatcherFuzz` drives arbitrary 9P request sequences, including wrong and
unenrolled certificates, NoTag, flushes, session and snapshot expiry, close and
the unsupported auth/create/remove/wstat requests, against an independent model
of session readiness, expiry, fid ownership and commit effects. It requires the
request tag on every reply, only declared error codes (never `unavailable`),
`denied` for unsupported requests, at most one effect per transaction, and no fid
use without the certificate that attached it. It runs as an AFL target
(`scripts/fuzz.sh fog-dispatcher`, seeds in `corpus/fog-dispatcher`) and as FsCheck
properties in `ControlDispatcherTests`, one starting from a live session. The
`session.bin` seed must reach two acknowledged commits with exactly one effect.
A deliberately injected clunk defect (a fid retained after a foreign certificate's
failed clunk) was detected by the seed, then removed. `FogFileFuzz` is now also
run as a property.

New unit tests cover denied unsupported requests and their tags, stat lengths for
unopened, write-only and snapshot opens, clunk of an unopened fid, and TLS clients
with no certificate or an unenrolled one being rejected and releasing their slot.

- Fog server tests: 151 passed; Fog core tests: 109 passed.
- Fog server coverage (Release): 99.83% lines, 100% branches; Fog core 100%.
- AFL `fog-dispatcher`, 60 s: 475,603 executions, 0 crashes, 0 hangs.
- Fog core mutation: 100% (344 killed). Fog server mutation: 100% (757 killed,
  0 timeouts, 0 survivors). Control client mutation: 100% (73 killed). No
  exclusions were added.

## Follow-up fixes — 2026-10-01

- `Payload` is one switch again. The split `SessionPayload ?? FilePayload` matched
  disjoint message types, so swapping its operands was an equivalent mutant.
- Listener disposal: a stopped `TcpListener` rejects the next accept with
  `InvalidOperationException` before observing the token (measured; no ordering
  produced `ObjectDisposedException`). Disposal between accepts therefore faulted
  the unobserved accept loop. The loop now ends on that exception during disposal,
  and disposal awaits the loop before snapshotting connections and disposing its
  token, so a client accepted during disposal is also awaited. Regression tests
  drive the loop after cancel-then-stop, require a non-disposal failure to
  surface, and hold disposal on an incomplete loop and connection.
- The post-handshake certificate recheck is covered by a clock that lapses after
  the handshake callback's reads.
- `FogTlsClient.AcceptServerCertificate` rejects a missing server certificate. A
  test requires disposing the returned stream to close its socket; previously no
  test detected `leaveInnerStreamOpen: true`.
- The saturated-flush test now bounds its second flush; without the reserved-slot
  counter it hung (a timeout) instead of failing.
- `FogTransactionClient.cs:60` remains uncovered: the compiler's fall-through exit
  of a try that always returns or throws. Restructuring only moved the artifact.

# Direct Fog control adapter verification — 2026-09-10

This evidence covers the direct enrolled-node TLS control adapter and sequential
transaction client. It does not certify AAN, factotum bootstrap, membership/storage
services, workload execution or process containment.

## Standard gate and contract examples

- `bash scripts/run-quality.sh`: passed, **745 tests**, zero failed/skipped. This
  includes 119 Fog server tests and 107 Fog core tests, plus the other solution
  suites, analyzers, coverage checks, gate self-tests and CRAP checks.
- Merged coverage: **81.30% lines, 69.50% branches**. Maximum CRAP is **30.00**,
  meeting the unchanged 30 gate. The client exchange and transaction methods were
  split into frame IO/validation and transaction completion/cleanup operations.
- Fog tests cover exact message and snapshot bounds, directory record offsets,
  pending-request/flush admission, reset and terminal cleanup, policy revocation,
  certificate identity/validity, listener deadlines, hostile peer replies,
  fragmented IO, lost commit/release replies and asynchronous caller contexts.
- All **30 specification feature files** parse with Gherkin 29.0.0. This is a
  syntax check, not execution of their proposed acceptance scenarios.
The symbolic-math runtime and examples referenced by the former demo have been
removed. These dated results are historical; current validation uses the namespace
and generic transaction tests. The current mutation target is 100%, so the older
90% results below do not satisfy the current gate.

## Mutation evidence

From `NinePSharp.Fog.Server.Tests`:

```sh
dotnet stryker --config-file ../stryker-config-control-client.json \
  --reporter json --reporter progress --output ../.artifacts/stryker-control-client \
  --skip-version-check --break-on-initial-test-failure --verbosity error
```

Sequential client: **91.78%**, meeting the unchanged 90% threshold. 66 killed,
one timeout-killed, six survived, zero uncovered. Stryker also reports 21
compile-error mutants, which are not evidence of a test killing those mutations.
The surviving minimum-header boundary and continuation-context changes remain
in the reported score; no per-file or mutation exclusions were added.

The server command uses the same options with
`--config-file ../stryker-config-fog-server.json` and
`--output ../.artifacts/stryker-fog-server`. It passed at **91.11%**: 702 killed,
15 timeout-killed, 66 survived and four uncovered. The 108 compile-error mutants
are excluded by Stryker, not counted as kills. The threshold remains 90% and the
scope remains every server C# source file.

The four uncovered mutants concern Qid counter exhaustion and allocation-failure
rollback. Reviewed survivors remain in the score: redundant rejection layers,
directory allocation checks backed by the final snapshot check, alternate fid
sequences, maintenance/disposal paths, continuation-context choices and TLS/socket
settings. The tests do not independently prove all admission-versus-handshake
timing distinctions, concurrent close/reset interleavings, TLS resumption settings
or exact wall-clock certificate-expiry equality. These are remaining test gaps,
not exceptions to the implementation contract or newly excluded mutation scopes.

The existing `.artifacts/stryker-fog-atomic` report gives **95.48%** for the core
including `CommitAtomicAsync`. Its mutated source files were compared with the
current files and match. This core mutation run was not repeated during this
continuation. Its report contains 359 killed/timeout-killed, 17 survived and zero
uncovered mutants.

## Bounded fuzz campaign

`FUZZ_SECONDS=30 bash scripts/fuzz.sh fog-files`: passed, **60,802 executions**,
83 corpus entries, **zero crashes and zero hangs**. The instrumented assemblies
were the Fog server and core. The model checks fragmented input, rejected offsets,
upload abort/seal behavior, retained results, single commit effects and release.
This is control-fid model fuzzing, not a TLS handshake fuzzer or full runtime test.

Findings are in
`.artifacts/fuzz/findings/fog-files-20260910165035-358966`; its matching log is in
`.artifacts/fuzz/logs`. Standard-gate output is archived as
`.artifacts/quality-fog-final.log`, and the scratch specification checker is
`.artifacts/validate-fog-specs.fsx` (`dotnet fsi` after the Release build).
Targeted `dotnet format ... whitespace` and `git diff --check` passed.

## Limits of the evidence

The repository-wide `--full` release matrix is separate from the targeted Fog
checks. A passing standard gate does not claim all release campaigns were rerun.
NuGet emitted vulnerability-feed access warnings, so no successful dependency
vulnerability audit is claimed. Semgrep was unavailable and the script reported
that its optional rules were skipped. Neither check was disabled for a pass.
