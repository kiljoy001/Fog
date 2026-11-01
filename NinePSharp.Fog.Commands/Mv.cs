using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// mv(1), as 9front's mv.c.
public sealed class Mv
{
    private const int IoUnit = 32768;
    private const int MaxName = 4096;
    private const uint DMDIR = 0x80000000;
    private const uint DMAPPEND = 0x40000000;

    private readonly Process process;

    private Mv(Process process) => this.process = process;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        if (argv.Count < 3)
        {
            await process.WriteAsync(2, "usage: mv fromfile tofile\n\t  mv fromfile ... todir\n"u8.ToArray());
            process.Exits("bad usage");
        }

        var mv = new Mv(process);
        string[] args = argv.Skip(1).Select(Cleanname.Clean).ToArray();
        string todir;
        string? toelem;
        if (await mv.DirstatAsync(args[^1]) is { } dirto && (dirto.Mode & DMDIR) != 0
            && !(args.Length == 2 && await mv.DirstatAsync(args[0]) is { } dirfrom && (dirfrom.Mode & DMDIR) != 0))
        {
            (todir, toelem) = (args[^1], null);
        }
        else
        {
            (todir, toelem) = Split(args[^1]);
        }

        if (args.Length > 2 && toelem is not null)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"mv: {args[^1]} not a directory\n"));
            process.Exits("bad usage");
        }

        int failed = 0;
        foreach (string from in args.Take(args.Length - 1))
        {
            failed += await mv.MvAsync(from, todir, toelem) ? 0 : 1;
        }

        process.Exits(failed != 0 ? "failure" : null);
    }

    // split: the directory and last element of a name, . for a name without one.
    private static (string Dir, string Elem) Split(string name)
        => name.LastIndexOf('/') is int s and >= 0 ? (name[..s], name[(s + 1)..])
            : name == ".." ? ("..", ".")
            : (".", name);

    private async Task<Stat?> DirstatAsync(string name)
    {
        try
        {
            return await process.StatAsync(name);
        }
        catch (SyscallException)
        {
        }

        return null;
    }

    private async Task<bool> FailAsync(string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"mv: {message}\n"));
        return false;
    }

    // Whether it moved the file.
    private async Task<bool> MvAsync(string from, string todir, string? toelem)
    {
        Stat dirb;
        try
        {
            dirb = await process.StatAsync(from);
        }
        catch (SyscallException error)
        {
            return await FailAsync($"can't stat {from}: {error.Message}");
        }

        return await Mv1Async(from, dirb, todir, toelem);
    }

    private async Task<bool> Mv1Async(string fromname, Stat dirb, string todir, string? toelem)
    {
        (string fromdir, string fromelem) = Split(fromname);
        toelem ??= fromelem;
        if (toelem.Length == 0)
        {
            return await FailAsync($"null last name element moving {fromname}");
        }

        if (Encoding.UTF8.GetByteCount(toelem) + Encoding.UTF8.GetByteCount(todir) + 2 > MaxName)
        {
            return await FailAsync($"path too big (max {MaxName}): {todir}/{toelem}");
        }

        string toname = $"{todir}/{toelem}";
        if (await SamefileAsync(fromdir, todir))
        {
            if (await SamefileAsync(fromname, toname))
            {
                return await FailAsync($"{fromname} and {toname} are the same");
            }

            if (await DirstatAsync(toname) is not null)
            {
                await HardremoveAsync(toname);
            }

            Stat d = Process.NullDir;
            try
            {
                await process.WStatAsync(fromname, new Stat(0, d.Type, d.Dev, d.Qid, d.Mode, d.Atime, d.Mtime, d.Length, toelem, d.Uid, d.Gid, d.Muid));
                return true;
            }
            catch (SyscallException error) when ((dirb.Mode & DMDIR) != 0)
            {
                return await FailAsync($"can't rename directory {fromname}: {error.Message}");
            }
            catch (SyscallException)
            {
            }
        }

        if ((dirb.Mode & DMDIR) != 0)
        {
            return await FailAsync($"{fromname} is a directory, not copied to {toname}");
        }

        return await CopyAsync(fromname, dirb, toname);
    }

    // Renaming won't work, so the file is copied and the original removed.
    private async Task<bool> CopyAsync(string fromname, Stat dirb, string toname)
    {
        int fdf;
        try
        {
            fdf = await process.OpenAsync(fromname, NinePConstants.OREAD);
        }
        catch (SyscallException error)
        {
            return await FailAsync($"can't open {fromname}: {error.Message}");
        }

        // create won't truncate an append-only file.
        if (await DirstatAsync(toname) is { } dirt && (dirt.Mode & DMAPPEND) != 0)
        {
            await HardremoveAsync(toname);
        }

        int fdt;
        try
        {
            fdt = await process.CreateAsync(toname, NinePConstants.OWRITE, dirb.Mode);
        }
        catch (SyscallException error)
        {
            await process.CloseAsync(fdf);
            return await FailAsync($"can't create {toname}: {error.Message}");
        }

        bool stat = await Copy1Async(fdf, fdt, fromname, toname);

        // Stryker disable once Statement : the descriptor is closed as mv exits, if not before
        await process.CloseAsync(fdf);
        if (stat)
        {
            Stat d = Process.NullDir;
            try
            {
                // Errors are ignored; user none, for one, always fails.
                await process.FWStatAsync(fdt, new Stat(0, d.Type, d.Dev, d.Qid, dirb.Mode, d.Atime, dirb.Mtime, d.Length, d.Name, d.Uid, d.Gid, d.Muid));
            }
            catch (SyscallException)
            {
            }

            try
            {
                await process.RemoveAsync(fromname);
            }
            catch (SyscallException error)
            {
                stat = await FailAsync($"can't remove {fromname}: {error.Message}");
            }
        }

        // Stryker disable once Statement : the descriptor is closed as mv exits, if not before
        await process.CloseAsync(fdt);
        return stat;
    }

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
                return await FailAsync($"error reading {from}: {error.Message}");
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
                return await FailAsync($"error writing {to}: {error.Message}");
            }
        }
    }

    private async Task<bool> SamefileAsync(string a, string b)
        => a == b || (await DirstatAsync(a) is { } da && await DirstatAsync(b) is { } db
            && da.Qid.Type == db.Qid.Type && da.Qid.Path == db.Qid.Path && da.Qid.Version == db.Qid.Version && da.Dev == db.Dev && da.Type == db.Type);

    // hardremove: every file by the name, as a union can hold more than one; mv ends if there is none.
    private async Task HardremoveAsync(string a)
    {
        try
        {
            await process.RemoveAsync(a);
        }
        catch (SyscallException error)
        {
            await FailAsync($"can't remove {a}: {error.Message}");
            process.Exits("mv");
        }

        while (true)
        {
            try
            {
                await process.RemoveAsync(a);
            }
            catch (SyscallException)
            {
                return;
            }
        }
    }
}
