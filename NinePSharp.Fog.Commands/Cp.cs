using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// cp(1), as 9front's cp.c.
public sealed class Cp
{
    private const int IoUnit = 32768;
    private const uint DMDIR = 0x80000000;

    private readonly Process process;
    private bool failed;
    private bool gflag;
    private bool uflag;
    private bool xflag;

    private Cp(Process process) => this.process = process;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var cp = new Cp(process);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'g':
                    cp.gflag = true;
                    break;
                case 'u':
                    cp.uflag = cp.gflag = true;
                    break;
                case 'x':
                    cp.xflag = true;
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count < 2)
        {
            await UsageAsync(process);
        }

        bool todir = await cp.DirstatAsync(args[^1]) is { } dirb && (dirb.Mode & DMDIR) != 0;
        if (args.Count > 2 && !todir)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"cp: {args[^1]} not a directory\n"));
            process.Exits("bad usage");
        }

        foreach (string from in args.Take(args.Count - 1))
        {
            await cp.CopyAsync(from, args[^1], todir);
        }

        process.Exits(cp.failed ? "errors" : null);
    }

    private static async Task UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage:\tcp [-gux] fromfile tofile\n\tcp [-x] fromfile ... todir\n"u8.ToArray());
        process.Exits("usage");
    }

    private async Task<Stat?> DirstatAsync(string name)
    {
        try
        {
            return await process.StatAsync(name);
        }
        catch (SyscallException)
        {
            return null;
        }
    }

    private async Task FailAsync(string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"cp: {message}\n"));
        failed = true;
    }

    private async Task<bool> SamefileAsync(Stat a, string an, string bn)
    {
        if (await DirstatAsync(bn) is { } b && b.Qid.Type == a.Qid.Type && b.Qid.Path == a.Qid.Path && b.Qid.Version == a.Qid.Version && b.Dev == a.Dev && b.Type == a.Type)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"cp: {an} and {bn} are the same file\n"));
            return true;
        }

        return false;
    }

    private async Task CopyAsync(string from, string to, bool todir)
    {
        if (todir)
        {
            to = $"{to}/{from[(from.LastIndexOf('/') + 1)..]}";
        }

        Stat dirb;
        try
        {
            dirb = await process.StatAsync(from);
        }
        catch (SyscallException error)
        {
            await FailAsync($"can't stat {from}: {error.Message}");
            return;
        }

        if ((dirb.Mode & DMDIR) != 0)
        {
            await FailAsync($"{from} is a directory");
            return;
        }

        if (await SamefileAsync(dirb, from, to))
        {
            failed = true;
            return;
        }

        int fdf;
        try
        {
            fdf = await process.OpenAsync(from, NinePConstants.OREAD);
        }
        catch (SyscallException error)
        {
            await FailAsync($"can't open {from}: {error.Message}");
            return;
        }

        int fdt;
        try
        {
            fdt = await process.CreateAsync(to, NinePConstants.OWRITE, dirb.Mode & 0b111_111_111);
        }
        catch (SyscallException error)
        {
            await FailAsync($"can't create {to}: {error.Message}");
            await process.CloseAsync(fdf);
            return;
        }

        if (await Copy1Async(fdf, fdt, from, to) && (xflag || gflag))
        {
            Stat d = Process.NullDir;
            var dirt = new Stat(0, d.Type, d.Dev, d.Qid, xflag ? dirb.Mode : d.Mode, d.Atime, xflag ? dirb.Mtime : d.Mtime, d.Length, d.Name, uflag ? dirb.Uid : d.Uid, gflag ? dirb.Gid : d.Gid, d.Muid);
            try
            {
                await process.FWStatAsync(fdt, dirt);
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"cp: warning: can't wstat {to}: {error.Message}\n"));
            }
        }

        await process.CloseAsync(fdf);
        await process.CloseAsync(fdt);
    }

    // Whether the whole file was copied.
    private async Task<bool> Copy1Async(int fdf, int fdt, string from, string to)
    {
        while (true)
        {
            ReadOnlyMemory<byte> buf;
            try
            {
                buf = await process.ReadAsync(fdf, IoUnit);
            }
            catch (SyscallException error)
            {
                await FailAsync($"error reading {from}: {error.Message}");
                return false;
            }

            if (buf.IsEmpty)
            {
                return true;
            }

            try
            {
                await process.WriteAsync(fdt, buf);
            }
            catch (SyscallException error)
            {
                await FailAsync($"error writing {to}: {error.Message}");
                return false;
            }
        }
    }
}
