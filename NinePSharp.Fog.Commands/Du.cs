using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// du(1), as 9front's du.c: each directory's total in units, rounded up from its files' lengths
// rounded up to the block size, or in bytes, qids or times; each line printed as it is found.
public sealed class Du
{
    private const long Vkilo = 1024;

    // SI prefixes for units past 1, and nil, which %s prints as <nil>.
    private static readonly string?[] Pfxes = [string.Empty, "k", "M", "G", "T", "P", "E", "Z", "Y", null];

    private readonly Process process;
    private readonly HashSet<(ulong Path, ushort Type, uint Dev)> cache = [];
    private bool aflag;
    private bool autoscale;
    private bool fflag;
    private bool fltflag;
    private bool qflag;
    private bool readflg;
    private bool sflag;
    private bool tflag;
    private bool uflag;
    private long blocksize = Vkilo;
    private long unit;

    private Du(Process process) => this.process = process;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var du = new Du(process);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'a':
                    du.aflag = true;
                    break;
                case 'b':
                    if (arguments.Value() is { } b)
                    {
                        du.blocksize = Libc.Strtoul(b, 0, out int ss);
                        du.blocksize = ss == 0 ? 1 : du.blocksize;
                        for (; ss < b.Length && b[ss] == 'k'; ss++)
                        {
                            du.blocksize *= 1024;
                        }
                    }

                    break;
                case 'e':
                    du.fltflag = true;
                    break;
                case 'f':
                    du.fflag = true;
                    break;
                case 'h':
                    du.autoscale = true;
                    break;
                case 'n':
                    (du.aflag, du.blocksize, du.unit) = (true, 1, 1);
                    break;
                case 'p':
                    if (arguments.Value() is { } p)
                    {
                        int scale = Array.FindIndex(Pfxes, x => x is null || string.Equals(p, x, StringComparison.OrdinalIgnoreCase));
                        if (Pfxes[scale] is null)
                        {
                            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv[0]}: unknown suffix {p}\n"));
                            process.Exits($"unknown suffix {p}");
                        }

                        du.unit = 1;
                        while (scale-- > 0)
                        {
                            du.unit = unchecked(du.unit * Vkilo);
                        }
                    }

                    break;
                case 'q':
                    du.qflag = true;
                    break;
                case 'r':
                    du.readflg = true;
                    break;
                case 's':
                    du.sflag = true;
                    break;
                case 't':
                    du.tflag = true;
                    break;
                case 'u':
                    du.uflag = true;
                    break;
                default:
                    await process.WriteAsync(2, "usage: du [-aefhnqstu] [-b size] [-p si-pfx] [file ...]\n"u8.ToArray());
                    process.Exits("usage");
                    break;
            }
        }

        if (du.unit == 0)
        {
            du.unit = du.qflag || du.tflag || du.uflag || du.autoscale ? 1 : Vkilo;
        }

        du.blocksize = Math.Max(du.blocksize, 1);
        IReadOnlyList<string> names = arguments.Rest();
        foreach (string name in names.Count == 0 ? ["."] : names)
        {
            await du.PrintamtAsync(await du.DuAsync(name, await du.DirstatAsync(name)), name);
        }

        process.Exits(null);
    }

    private async Task PrintamtAsync(long amt, string name)
    {
        if (readflg)
        {
            return;
        }

        string line;
        if (autoscale)
        {
            int scale = 0;
            double val = (double)amt / unit;

            // Stryker disable once Equality,Arithmetic : a long's bytes are less than 1024 zettabytes
            while (Math.Abs(val) >= 1024 && scale < Pfxes.Length - 1)
            {
                scale++;
                val /= 1024;
            }

            // Stryker disable once String : a long's bytes are less than 1024 zettabytes
            line = $"{Encoding.UTF8.GetString(Libc.Sprint("%.6g", [Libc.FmtArg.Of(val)]))}{Pfxes[scale] ?? "<nil>"}";
        }
        else if (fltflag)
        {
            line = Encoding.UTF8.GetString(Libc.Sprint("%.6g", [Libc.FmtArg.Of((double)amt / unit)]));
        }
        else
        {
            ulong n = (ulong)unchecked((amt + unit - 1) / unit);
            line = qflag ? n.ToString("x16", CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);
        }

        await process.WriteAsync(1, Encoding.UTF8.GetBytes($"{line}\t{Libc.Quote(name)}\n"));
    }

    private async Task<Stat?> DirstatAsync(string name)
    {
        try
        {
            return await process.StatAsync(name);
        }
        catch (SyscallException error)
        {
            Errstr.Set(process, error.Message);
            return null;
        }
    }

    private long Dirval(Stat d, long size)
        => qflag ? (long)d.Qid.Path : tflag ? (uflag ? d.Atime : d.Mtime) : size;

    // The blocks of a file, read through and thrown away with -r.
    private async Task ReadfileAsync(string name)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync(name, NinePConstants.OREAD);
        }
        catch (SyscallException error)
        {
            await WarnAsync(name, error.Message);
            return;
        }

        try
        {
            // read's count is a long, which the block size is cut to.
            while (!(await process.ReadAsync(fd, (int)blocksize)).IsEmpty)
            {
            }
        }
        catch (SyscallException error)
        {
            await WarnAsync(name, error.Message);
        }

        await process.CloseAsync(fd);
    }

    private async Task<long> DufileAsync(string name, Stat d)
    {
        long t = Blkmultiple((long)d.Length);
        if (aflag || readflg)
        {
            string file = $"{name}/{d.Name}";
            if (readflg)
            {
                await ReadfileAsync(file);
            }

            t = Dirval(d, t);
            await PrintamtAsync(t, file);
        }

        return t;
    }

    private async Task<long> DuAsync(string name, Stat? stat)
    {
        if (stat is not { } dir)
        {
            return await WarnAsync(name, Errstr.Get(process));
        }

        if ((dir.Qid.Type & QidType.QTDIR) == 0)
        {
            return Dirval(dir, Blkmultiple((long)dir.Length));
        }

        int fd;
        try
        {
            fd = await process.OpenAsync(name, NinePConstants.OREAD);
        }
        catch (SyscallException error)
        {
            return await WarnAsync(name, error.Message);
        }

        IReadOnlyList<Stat> entries = [];
        try
        {
            entries = await process.DirReadAsync(fd);
        }
        catch (SyscallException error)
        {
            await WarnAsync(name, error.Message);
        }

        long nk = 0;
        foreach (Stat d in entries)
        {
            if ((d.Qid.Type & QidType.QTDIR) == 0)
            {
                nk += await DufileAsync(name, d);
                continue;
            }

            // Stryker disable once String : dirread gives no . or .., which du.c passes over all the same
            if (d.Name is "." or ".." || !cache.Add((d.Qid.Path, d.Type, d.Dev)))
            {
                continue;
            }

            string file = $"{name}/{d.Name}";
            long t = await DuAsync(file, d);
            nk += t;
            if (!sflag)
            {
                await PrintamtAsync(Dirval(d, t), file);
            }
        }

        // Stryker disable once Statement : a descriptor left open changes nothing du prints
        await process.CloseAsync(fd);
        return Dirval(dir, nk);
    }

    private async Task<long> WarnAsync(string s, string error)
    {
        if (!fflag)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"du: {s}: {error}\n"));
        }

        return 0;
    }

    // The length rounded up to a whole number of blocks.
    private long Blkmultiple(long n) => (n + blocksize - 1) / blocksize * blocksize;
}
