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
- The revised `job-math`, `math-result`, `status-succeeded` and `runtime-lock`
  examples parse and reserialize byte-for-byte through `FogRecordSchema` and the
  actual LibTab dependency. The status fixture's missing final record separator
  was corrected.

The AngouriMath substitution is a specification/example change. There was no
implemented Lisp evaluator to migrate. No installed AngouriMath runner or real
math-job execution is claimed; the provider and containment integrations remain
future work under `fog-math-v1`.

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
