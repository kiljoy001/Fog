namespace NinePSharp.Fog.Kernel.Tests.Support;

public sealed class KernelDriver
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    public FogKernel Kernel { get; set; } = null!;

    public Process Init { get; set; } = null!;

    public int[] Pipe { get; set; } = [];

    public Task<T> Bounded<T>(ValueTask<T> operation) => operation.AsTask().WaitAsync(Bound);
}
