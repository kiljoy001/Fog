using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// cat(1), as 9front's cat.c.
public static class Cat
{
    private const int IoUnit = 32768;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        if (argv.Count == 1)
        {
            await CopyAsync(process, 0, "<stdin>");
        }

        foreach (string name in argv.Skip(1))
        {
            int fd = await OpenAsync(process, name);
            await CopyAsync(process, fd, name);
            await process.CloseAsync(fd);
        }

        process.Exits(null);
    }

    private static async Task<int> OpenAsync(Process process, string name)
    {
        try
        {
            return await process.OpenAsync(name, NinePConstants.OREAD);
        }
        catch (SyscallException error)
        {
            return await FatalAsync<int>(process, $"can't open {name}: {error.Message}");
        }
    }

    private static async Task CopyAsync(Process process, int fd, string name)
    {
        ReadOnlyMemory<byte> block;
        while (!(block = await ReadAsync(process, fd, name)).IsEmpty)
        {
            try
            {
                await process.WriteAsync(1, block);
            }
            catch (SyscallException error)
            {
                await FatalAsync<int>(process, $"write error copying {name}: {error.Message}");
            }
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadAsync(Process process, int fd, string name)
    {
        try
        {
            return await process.ReadAsync(fd, IoUnit);
        }
        catch (SyscallException error)
        {
            return await FatalAsync<ReadOnlyMemory<byte>>(process, $"error reading {name}: {error.Message}");
        }
    }

    // sysfatal, which exits, so it never returns a T.
    private static async Task<T> FatalAsync<T>(Process process, string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"cat: {message}\n"));
        process.Exits(message);
        return default!;
    }
}
