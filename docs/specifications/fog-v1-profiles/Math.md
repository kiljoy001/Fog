# fog-math-v1: bounded symbolic jobs with AngouriMath

Status: proposed full profile. The [direct-worker milestone](../../fog-math-demo.md)
now runs pinned AngouriMath 2.4.0 in an isolated process under the distinct
`angouri-2.4.0-demo1` contract. It has actual engine vectors and a two-machine
reconnect demonstration. It does not yet implement this complete supervisor and
capability contract. No Lisp evaluator was implemented. WASM remains the planned
general-program execution profile.

Use the published [AngouriMath](https://github.com/asc-community/AngouriMath)
library through a C# `IWorkloadProvider`. The initial dependency candidate is
[AngouriMath 2.4.0](https://www.nuget.org/packages/AngouriMath/2.4.0), checked on
2026-09-10. Pin the package, transitive dependencies, .NET runner and settings in
the operator-approved bundle. An upstream version alone is not bundle identity.
Reuse its parser and algebra operations; do not build a second symbolic engine.

## Job and operation contract

Register `runtime=math`, profile `fog-math-v1`, ABI `angouri-expr-v1`, meter
`linux-process-cpu-v1`. In addition to common job limits, require `source`,
`p_operation` and positive decimal `p_cpu_ms`. The only operation names are:

| Operation | Meaning | Extra fields |
| --- | --- | --- |
| `evaluate` | Evaluate a closed numeric expression using exact arithmetic | None |
| `simplify` | Simplify a symbolic polynomial expression | None |
| `differentiate` | Formal first derivative of a polynomial expression | Required `p_variable` |
| `solve` | Find the finite real roots of a univariate polynomial equal to zero | Required `p_variable` |

Reject missing, unknown and inapplicable options. `steps`, `fuel`, scripts, CLR
code and runtime-selected package references are not math-job fields. The removed
`runtime=lisp` name is not an alias for `math`; existing Lisp text must be rewritten.

`source` identifies immutable expression bytes resolved through the authorized job
namespace. It may refer to the job's sealed `input` file; that file then contains
the expression itself. There is no second evaluated input program or mutable
environment. Parameters are substituted by the submitting client into a bounded
expression before admission. A later batch/substitution ABI needs its own declared
options and conformance tests.

## Initial expression surface

The first profile deliberately chooses a small, testable subset of AngouriMath:
decimal integers, rational arithmetic, ASCII variables, parentheses, unary minus,
`+`, `-`, `*`, `/`, and `^`. Require explicit multiplication. Division denominators
must be nonzero numeric rational constants; exponents must be literal nonnegative
integers. Numeric-only expressions are valid for `evaluate`; the other operations
accept polynomials with exact rational coefficients. `solve` accepts only one free
variable and degree at most four. The zero polynomial has infinitely many roots
and returns `math-nonfinite`; a nonzero constant has an empty finite root set.

Variables match `[A-Za-z][A-Za-z0-9_]{0,63}` and cannot name engine constants or
built-ins. Reject implicit engine symbols, functions, matrices, complex numbers,
assignments, lambdas, sequences and host objects in this initial profile. The
wrapper must validate both bounded input tokens and the parsed engine AST; a
successful upstream parse does not grant a larger expression language. Arithmetic
and polynomial manipulation are performed by AngouriMath, not a replacement
evaluator hidden in the validation layer.

Broader algebra, integration, matrices and approximate numerical evaluation are
natural later operations. They need explicit domain, precision, result and resource
semantics before registration. This first profile does not certify every operation
available in the library.

Input is strict UTF-8 without BOM or NUL, at most min(artifact_bytes, 1 MiB).
At most 65536 AST nodes, nesting depth 256, polynomial degree 1024, and 4096 bits
in normalized rational numerators and denominators are admitted. Check lexical
length and structure before invoking the engine. These are input/result admission
bounds; internal CAS intermediates are bounded by the isolated process allowance,
not by a claimed per-arithmetic-operation step counter.

## Resources and cancellation

There is no `lisp-steps-v1` replacement disguised as a CAS step count. The provider
does not promise deterministic instruction fuel or equal CPU use across machines.
`p_cpu_ms` caps cumulative user plus system CPU time for the complete job cgroup,
including process startup, parsing, algebra and result serialization. A fresh
per-job cgroup starts this accounting; cached work cannot reset it. Report observed
CPU usage as `p_cpu_ms_used`, rounded up from microseconds.

The Linux supervisor samples cumulative cgroup CPU use at least every 10 ms and
terminates the entire job cgroup when usage reaches the allowance. Sampling and
scheduling permit overshoot; `p_cpu_ms` is a supervised CPU allowance, not exact
precharged gas. The independent CPU-rate quota, memory limit and admission-based
wall deadline remain mandatory. Latch `cpu-limit` on budget exhaustion and reject
candidate output if final measured usage has reached the allowance. Missing CPU
accounting or supervision makes this profile unavailable.

Set AngouriMath's cancellation token for the job before engine calls and clear it
when the execution scope ends. [Upstream cancellation](https://github.com/asc-community/AngouriMath/wiki/Multithreading)
is cooperative; the supervisor must kill/reap an unresponsive process. It must also
enforce whole-process memory, output and deadline limits while parsing or printing.
No engine exception can turn a latched host failure into successful output.

Follow [Isolation.md](Isolation.md): fresh process per job, no inherited network,
filesystem or credentials, no engine compilation to arbitrary executable code,
and private bounded output until successful cleanup. Installed engine code is
trusted host code; submitted expressions are untrusted data.

## Results and failures

Return one closed plain-cell LibTab document with schema `fogmath-result-v1` and
columns `kind,value` in that order. Both fields are required; the semantic key is
`(kind,value)`. `evaluate`, `simplify` and `differentiate` return one `expression`
row. `solve` returns distinct `root` rows, sorted by ordinal UTF-8 value bytes;
no real roots returns the schema and zero rows. All normal LibTab escaping, cell,
row and total-byte bounds apply. No partial document becomes a successful result.

Values use the pinned engine's invariant expression printer with fixed settings.
Exact rationals normalize signs/gcd and denominator one prints as an integer.
Root expressions must be finite, real and resolved; an unevaluated solver object
is `math-unresolved`, not an empty result. Printer and simplifier output is pinned
to the bundle and golden vectors, not claimed canonical across all algebraically
equivalent expressions or engine releases. No floating-point fallback is permitted.

Expected mathematical vectors include `1/2 + 1/3` -> `5/6`, evaluating
`1 + 2*2 + 3*2^2` -> `17`, differentiating `1 + 2*x + 3*x^2` -> an expression
equivalent to `2 + 6*x`, and solving `x^2 - 4` -> roots `-2`, `2`.
Before registration, capture exact serialized output from the actual pinned
engine, check these mathematical expectations independently, and commit the byte
vectors. These examples are requirements, not evidence of an engine run.

Provider failures are bounded codes: `math-syntax`, `math-size`, `math-domain`,
`math-nonfinite`, `math-unresolved`, `math-result`. Host-owned failures include
`cpu-limit`, `deadline`, `memory-limit`, `output-limit`, `cancelled`,
`policy-changed`, `worker-lost` and `cleanup-failed`. Do not publish stack traces.

`Runtimes.feature` requires actual-engine vectors, reader/AST fuzzing, preparation
and algebra timeout faults, CPU-accounting boundaries with a controlled clock,
memory/output containment, fresh job state and process-reaping evidence.
