using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// unmount(1), as 9front's unmount.c: its arguments in mount's order, the mounted then the mount point.
public static class Unmount
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        if (argv.Count is not (2 or 3))
        {
            await process.WriteAsync(2, "usage: unmount mountpoint\n       unmount mounted mountpoint\n"u8.ToArray());
            process.Exits("usage");
        }

        string? mounted = argv.Count == 3 ? argv[1] : null;
        string mountpoint = argv[^1];
        try
        {
            await process.UnmountAsync(mounted, mountpoint);
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"unmount: {mountpoint}: {error.Message}\n"));
            process.Exits($"{mountpoint}: {error.Message}");
        }

        process.Exits(null);
    }
}
