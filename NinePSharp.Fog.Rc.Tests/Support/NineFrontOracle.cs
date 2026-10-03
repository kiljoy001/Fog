using NinePSharp.Fog.Namespaces.Tests.Support;

namespace NinePSharp.Fog.Rc.Tests.Support;

// One 9front terminal for the test run, booted when a scenario first asks for it, when
// FOG_9FRONT_ISO names a 9front ISO and qemu-system-x86_64 is installed.
internal static class NineFrontOracle
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static NineFrontTerminal? terminal;
    private static bool tried;

    internal static async Task<NineFrontTerminal?> TerminalAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!tried)
            {
                tried = true;
                terminal = NineFrontTerminal.Unavailable is null ? await NineFrontTerminal.BootAsync() : null;
            }

            return terminal;
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
