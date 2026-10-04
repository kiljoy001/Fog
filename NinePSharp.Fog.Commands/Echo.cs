using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// echo(1), as 9front's echo.c.
public static class Echo
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        bool noNewline = argv.Count > 1 && argv[1] == "-n";
        string text = string.Join(' ', argv.Skip(noNewline ? 2 : 1)) + (noNewline ? string.Empty : "\n");
        try
        {
            await process.WriteAsync(1, Encoding.UTF8.GetBytes(text));
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"echo: write error: {error.Message}\n"));
            process.Exits("write error");
        }

        process.Exits(null);
    }
}
