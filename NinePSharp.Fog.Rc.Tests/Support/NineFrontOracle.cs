using NinePSharp.Fog.Namespaces.Tests.Support;

namespace NinePSharp.Fog.Rc.Tests.Support;

// One 9front terminal for the test run, booted when a scenario first asks for it. A run without
// a 9front ISO or qemu-system-x86_64 fails rather than leaving the comparison out.
internal static class NineFrontOracle
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static NineFrontTerminal? terminal;

    internal static async Task<NineFrontTerminal> TerminalAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (NineFrontTerminal.Unavailable is string reason)
            {
                throw new InvalidOperationException(reason);
            }

            return terminal ??= await NineFrontTerminal.BootAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task StopAsync()
    {
        if (terminal is not null)
        {
            await terminal.DisposeAsync();
            terminal = null;
        }
    }
}
