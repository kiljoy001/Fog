# Experiments for an exploratory fog

The fog is an architectural idea being made concrete. These are small experiments
to discover useful behavior, not claims of an existing customer problem or a
commitment to build every application. The proposed execution pair is AngouriMath
for mathematical operations and WASM for general programs. An [experimental direct math worker](fog-math-demo.md) now implements the first
two-machine calculation/reconnect experiment. The full workload-provider protocols
and WASM runner remain future work.

## A shared mathematical workbench

Submit a polynomial from a laptop, ask a worker to simplify, differentiate or solve
it, and read the result through the same files used by any other job. Start with
`x^2 - 4` and the two real roots. Add a notebook or small browser UI as a client.

The experiment tests whether ordinary files make remote computation pleasant to
inspect and compose. Compare it with the same AngouriMath call locally: tiny
expressions will not justify distribution on performance alone. Sharing a service,
keeping the client lightweight and inspecting jobs may be the interesting benefits.

## Many independent what-if calculations

Explore a spring model, a projectile curve, a pricing formula or a game balance
function over many parameter choices. Derive a symbolic expression once, then use
WASM workers for batches of numerical evaluations. The client coordinates the
batches; workers do not recursively submit jobs under the current scope.

The useful questions are when batching amortizes scheduling and transport, how
results identify their inputs and engine versions, and whether cancellation feels
predictable. Start with fixed client-side substitutions; the first math ABI does
not include an implicit batch language.

## Procedural worlds on spare machines

Use WASM jobs to generate independent terrain tiles, noise fields, pathfinding
samples or simulation seeds. Treat a tile as an immutable input/result pair and
inspect both through a mounted namespace. A game/editor client chooses which
results to use; no worker receives the editor's whole filesystem.

This explores coarse-grained parallel work, repeatable artifacts and revocation
when a machine leaves. GPU execution and distributed mutable game state are
separate extensions, not prerequisites for the first demo.

## A personal computation shelf

Give trusted machines named mathematical and WASM services in one namespace.
A terminal script, notebook and an optional external assistant can discover and
submit to the same authorized resources. New capabilities appear as registered
providers, rather than requiring a different network API for each client.

This experiment asks whether the common interface actually reduces client work.
Try two deliberately different clients before expanding the provider catalogue.
The proposed MCP gateway is optional and is not yet an implemented interface.

## A visible fault laboratory

Run a job, disconnect its client, reconnect with the known ID and inspect the
retained result. Then revoke access or kill a worker and observe which state is
preserved and which becomes unavailable. Show the timeline in a small UI or log.

This may be the best early demonstration of the project itself: what a namespace
means when computation and connections have different lifetimes. The current
direct TLS control adapter already supports narrower transaction experiments;
AAN resumption and actual job/worker failure scenarios remain future integration.

## First end-to-end experiment

Use two operator-controlled machines, one control host and one worker. Submit one
AngouriMath job, read its result, then repeat with the reply deliberately lost.
The second attempt must recover the same retained operation or report it
unavailable; it must never guess by silently starting another effect.

Measure total latency, job startup cost, peak process memory, transferred bytes
and cleanup time, beside a local AngouriMath baseline. Keep the UI simple enough
that ordinary 9P commands can reproduce the demonstration. Those observations
should guide the next feature more than a large speculative application roadmap.
