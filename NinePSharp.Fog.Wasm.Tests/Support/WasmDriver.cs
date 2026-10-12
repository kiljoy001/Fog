using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Wasm.Tests.Support;

public sealed class WasmDriver
{
    // Compiling a module and starting its thread is slower than a kernel call.
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    public Process Init { get; set; } = null!;
}
