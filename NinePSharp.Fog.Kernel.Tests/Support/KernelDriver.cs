namespace NinePSharp.Fog.Kernel.Tests.Support;

public sealed class KernelDriver
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(3);

    public FogKernel Kernel { get; set; } = null!;

    public Process Init { get; set; } = null!;

    public int[] Pipe { get; set; } = [];

    public ManualClock Clock { get; } = new();

    public TypingConsole Console { get; } = new();

    // What the host's nvram holds, which the kernel's nvram reads and writes in place.
    public byte[] Nvram { get; set; } = new byte[8];

    public Task<T> Bounded<T>(ValueTask<T> operation) => operation.AsTask().WaitAsync(Bound);
}
