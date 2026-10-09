# Verification, Testing & Formal Quality Engineering

Fog maintains an exceptionally rigorous software verification process. Every component is subjected to a four-tier testing gauntlet designed to detect logic errors, race conditions, and edge-case failures.

---

## 1. The Quality Gauntlet

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Tier 1: BDD Specifications                      │
│        Human-readable Gherkin feature scenarios (.feature files)       │
│                  Executable via Reqnroll and xUnit                     │
├────────────────────────────────────────────────────────────────────────┤
│                Tier 2: Concurrency Exploration (Coyote)                │
│    Systematic exploration of async interleavings and scheduler state   │
│             Zero race conditions, deadlocks, or task leaks             │
├────────────────────────────────────────────────────────────────────────┤
│                 Tier 3: Mutation Testing (Stryker .NET)                │
│          Synthesizes mutants (boundary inversions, statement drops)   │
│           Strict mutation score threshold: 90% - 100% kill rate        │
├────────────────────────────────────────────────────────────────────────┤
│                      Tier 4: Coverage & CRAP Score                     │
│               Line & branch coverage thresholds enforced               │
│          Change Risk Anti-Patterns (CRAP) score capped at <= 30        │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Executable Specifications (BDD with Reqnroll)

Instead of opaque test cases, Fog's behavior is specified in Gherkin feature files across each test project:

* **Kernel Behavior:** [`NinePSharp.Fog.Kernel.Tests/Features/`](../NinePSharp.Fog.Kernel.Tests/Features/) covers `Processes.feature`, `Descriptors.feature`, `Pipes.feature`, `Files.feature`, and `Renumber.feature`.
* **Shell Behavior:** [`NinePSharp.Fog.Rc.Tests/Features/`](../NinePSharp.Fog.Rc.Tests/Features/) covers `RcSyntax.feature`, `RcExecution.feature`, and `RcInteractive.feature`.
* **Gefs Storage:** [`NinePSharp.Fog.Gefs.Tests/Features/`](../NinePSharp.Fog.Gefs.Tests/Features/) covers tree balancing, superblocks, arena compaction, and crash recovery.
* **Authentication:** [`NinePSharp.Fog.Auth.Tests/Features/`](../NinePSharp.Fog.Auth.Tests/Features/) covers `KeyFs.feature`, `AuthSrv.feature`, and `AuthFid.feature`.

Every scenario is checked in CI and can be executed with standard test runners:
```sh
dotnet test NinePSharp.Fog.Kernel.Tests/NinePSharp.Fog.Kernel.Tests.csproj
```

---

## 3. Concurrency Verification with Microsoft Coyote

Asynchronous and distributed systems are notoriously difficult to debug using conventional testing due to non-deterministic thread scheduling. Fog uses **Microsoft Coyote** in [`NinePSharp.Fog.Coyote.Tests`](../NinePSharp.Fog.Coyote.Tests/):

* **Controlled Scheduling:** Coyote takes control of the .NET task scheduler and timer providers.
* **Systematic Interleaving Exploration:** Iterates through thousands of possible thread interleavings to uncover:
  * Deadlocks between concurrent 9P requests
  * Race conditions during tree splits and merges in Gefs
  * Channel buffer corruption in `NinePSharp.Fog.Thread`
  * Desynchronization during transaction aborts

Run the Coyote suite via:
```sh
bash scripts/run-quality.sh --coyote
```

---

## 4. Mutation Testing (Stryker .NET)

Code coverage alone is insufficient: code can be executed without assertions truly validating correctness. Fog enforces **mutation testing** using Stryker .NET:

* **Mutation Rules:** Stryker injects defects into Fog source code (e.g., changing `<=` to `<`, dropping method calls, mutating string literals).
* **Thresholds:** A build fails if the test suite does not detect and kill **at least 90% to 100%** of injected mutants.
* **Configurations:** Each subsystem has a dedicated configuration:
  * `stryker-config-fog-kernel.json`
  * `stryker-config-fog-rc.json`
  * `stryker-config-fog-gefs.json`
  * `stryker-config-fog-thread.json`
  * `stryker-config-fog-server.json`

To execute a targeted mutation run:
```sh
bash scripts/run-quality.sh --mutate fog-kernel
```

---

## 5. Automated Quality Script (`scripts/run-quality.sh`)

The repository provides a unified script for executing the entire quality verification pipeline:

```sh
# Run standard quality gate (build, test, code coverage, CRAP score analysis)
bash scripts/run-quality.sh

# Run full comprehensive gate (includes mutation testing, Coyote, and fuzzing)
bash scripts/run-quality.sh --full

# Run fuzz testing campaign
bash scripts/run-quality.sh --fuzz
```
