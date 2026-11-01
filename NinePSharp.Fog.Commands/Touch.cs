using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// touch(1), as 9front's touch.c.
public static class Touch
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        uint now = (uint)await Syslog.TimeAsync(process);
        bool nocreate = false;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 't':
                    string t = await arguments.EargfAsync(() => UsageAsync<string>(process));
                    now = Libc.Strtoul(t, 0, out int s);
                    if (s == 0 || s != t.Length)
                    {
                        await UsageAsync<string>(process);
                    }

                    break;
                case 'c':
                    nocreate = true;
                    break;
                default:
                    await UsageAsync<string>(process);
                    break;
            }
        }

        IReadOnlyList<string> files = arguments.Rest();
        if (files.Count == 0)
        {
            await UsageAsync<string>(process);
        }

        bool failed = false;
        foreach (string name in files)
        {
            failed |= await TouchAsync(process, nocreate, name, now) != 0;
        }

        process.Exits(failed ? "touch" : null);
    }

    private static async Task<int> TouchAsync(Process process, bool nocreate, string name, uint now)
    {
        Stat d = Process.NullDir;
        var stbuff = new Stat(0, d.Type, d.Dev, d.Qid, d.Mode, d.Atime, now, d.Length, d.Name, d.Uid, d.Gid, d.Muid);
        try
        {
            await process.WStatAsync(name, stbuff);
            return 0;
        }
        catch (SyscallException error) when (nocreate)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"touch: {name}: cannot wstat: {error.Message}\n"));
            return 1;
        }
        catch (SyscallException)
        {
        }

        int fd;
        try
        {
            fd = await process.CreateAsync(name, NinePConstants.OREAD | NinePConstants.OEXCL, 0b110_110_110);
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"touch: {name}: cannot create: {error.Message}\n"));
            return 1;
        }

        try
        {
            await process.FWStatAsync(fd, stbuff);
        }
        catch (SyscallException)
        {
        }

        // Stryker disable once Statement : the descriptor is closed as touch exits, if not before
        await process.CloseAsync(fd);
        return 0;
    }

    private static async Task<T> UsageAsync<T>(Process process)
    {
        await process.WriteAsync(2, "usage: touch [-c] [-t time] files\n"u8.ToArray());
        process.Exits("usage");
        return default!;
    }
}
