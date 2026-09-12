# First-slice verification — 2026-09-10

This evidence covers the host-local record/transaction/control-command core, not
the remaining network, authentication, persistence or execution services.

## Commands and outcomes

- `dotnet test NinePSharp.Fog.Tests/NinePSharp.Fog.Tests.csproj --no-restore`
  (also run with Coverlet): **104 passed, zero skipped**. Includes unit/security
  boundaries, FsCheck record/fragment/command models, three Reqnroll scenarios and
  six specification example round trips through the production LibTab wrapper.
- `bash scripts/run-quality.sh`: **passed**. Full solution build, all configured
  test projects, merged coverage thresholds, gate self-tests and CRAP checks ran.
  The Release merged report records 100% line/branch coverage for NinePSharp.Fog;
  the separate Debug report records 100% line and 99.51% branch coverage.
- From `NinePSharp.Fog.Tests`, `dotnet stryker --config-file
  ../stryker-config-fog.json --reporter json --reporter progress --output
  ../.artifacts/stryker-fog --skip-version-check --break-on-initial-test-failure
  --verbosity error`: **95.69%**, against the existing 90% gate. 331 killed,
  2 timeout-killed, 15 survived, zero uncovered. JSON is generated at
  `.artifacts/stryker-fog/reports/mutation-report.json`.
- `FUZZ_SECONDS=30 bash scripts/fuzz.sh fog`: **165,084 executions, zero crashes
  and zero hangs**. Earlier 10-second iteration: 53,049 executions, also clean.
  Final findings: `.artifacts/fuzz/findings/fog-20260910041027-2`.
- Targeted `dotnet format ... whitespace`, `git diff --check` and shell syntax
  checks passed. Generated feature C#, binaries, coverage and fuzz findings are
  not source additions.

The repository-wide `--full` mutation/fuzz matrix was **not** rerun: the normal
repository gate and the new module's mutation/fuzz campaigns were run separately.
The full script now includes the new module for subsequent release validation.
NuGet emitted existing vulnerability-feed access warnings; no successful dependency
vulnerability audit is claimed. The optional semgrep stage reported that semgrep
was unavailable. Neither check was disabled to obtain a passing result.

## Survivor review

No per-file/mutation exclusions or reduced thresholds were introduced. Reviewed
survivors are retained in the reported score:

- Ten record-wrapper survivors cover the unused UTF-8 preamble setting, a constructor
  diagnostic, ordering of exhaustive required-field checks, and early validation
  changes subsequently rejected by other strict shape/LibTab canonicality checks.
  The latter are defense-in-depth checks: current public-boundary assertions do not
  independently prove precisely which rejection layer ran first.
- Four transaction-store survivors are an equivalent EOF empty-slice boundary, two
  continuation-context choices, and omission of early clearing of frozen inputs.
  The last retains extra input data until the already-reserved transaction expires;
  the tests prove bounded ownership but do not assert that early internal reclamation.
- One control-command survivor changes continuation-context capture. No claim of
  exhaustive custom SynchronizationContext scheduling verification is made.

Additional boundary tests killed mutations in independent transaction-count limits,
aggregate multi-file snapshot limits, sequence exhaustion, every-column validation,
ASCII boundaries and upload-buffer cleanup. Authentication, expiry, replay and
reservation branches were not exempted.
