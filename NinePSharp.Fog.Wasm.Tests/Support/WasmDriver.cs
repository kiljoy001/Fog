using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Wasm.Tests.Support;

public sealed class WasmDriver
{
    // Compiling a module and starting its thread is slower than a kernel call.
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    public Process Init { get; set; } = null!;

    // The kernel's and the applications' time: 2026-10-01 12:00 UTC.
    public FixedClock Clock { get; } = new(DateTimeOffset.FromUnixTimeSeconds(1790856000));
}
