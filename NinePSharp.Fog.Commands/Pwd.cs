using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// pwd(1), as 9front's pwd.c: the working directory's name.
public static class Pwd
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        string pathname;
        try
        {
            pathname = await Libc.GetwdAsync(process);
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"pwd: {error.Message}\n"));
            process.Exits("getwd");
            return;
        }

        await process.WriteAsync(1, Encoding.UTF8.GetBytes(pathname + "\n"));
        process.Exits(null);
    }
}
