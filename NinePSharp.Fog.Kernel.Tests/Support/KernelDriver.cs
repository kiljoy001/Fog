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

    // A directory read to its end, as dirreadall reads it, within the bound: on the pool, so a read
    // that never ends, though each block it reads comes at once, fails its scenario, not hangs it.
    public static Task<IReadOnlyList<NinePSharp.Messages.Stat>> ListAsync(Process process, int fd)
        => Task.Run(() => process.DirReadAsync(fd).AsTask()).WaitAsync(Bound);

    public Task<T> Bounded<T>(ValueTask<T> operation) => operation.AsTask().WaitAsync(Bound);
}
