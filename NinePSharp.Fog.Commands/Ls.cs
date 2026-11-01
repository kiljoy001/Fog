using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// ls(1), as 9front's ls.c.
public sealed class Ls
{
    private const uint DMDIR = 0x80000000;
    private const uint DMAPPEND = 0x40000000;
    private const uint DMEXCL = 0x20000000;
    private const uint DMAUTH = 0x08000000;
    private const uint DMTMP = 0x04000000;

    private static readonly string[] Modes = ["---", "--x", "-w-", "-wx", "r--", "r-x", "rw-", "rwx"];

    private readonly Process process;
    private readonly HashSet<char> flags = [];
    private readonly List<(Stat D, string? Prefix)> dirbuf = [];

    // Biobuf on standard output: what ls has printed since it last flushed.
    private readonly StringBuilder bin = new();
    private uint clk;
    private int swidth;
    private int qwidth;
    private int vwidth;
    private int uwidth;
    private int mwidth;
    private int lwidth;
    private int gwidth;

    private Ls(Process process) => this.process = process;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var ls = new Ls(process);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            if (!"FdlmnpqQrstTu".Contains(c, StringComparison.Ordinal))
            {
                await process.WriteAsync(2, "usage: ls [-dlmnpqrstuFQT] [file ...]\n"u8.ToArray());
                process.Exits("usage");
            }

            ls.flags.Add(c);
        }

        if (ls.flags.Contains('l'))
        {
            ls.clk = (uint)await Syslog.TimeAsync(process);
        }

        IReadOnlyList<string> files = arguments.Rest();
        bool errs = files.Count == 0 && await ls.LsAsync(".", false);
        foreach (string file in files)
        {
            errs |= await ls.LsAsync(file, true);
        }

        await ls.OutputAsync();
        process.Exits(errs ? "errors" : null);
    }

    // xcleanname: slashes compressed and a trailing one removed, . and .. left as they are.
    internal static string Xcleanname(string name)
    {
        var w = new StringBuilder();
        for (int r = 0; r < name.Length; r++)
        {
            if (name[r] != '/' || r == 0 || name[r - 1] != '/')
            {
                w.Append(name[r]);
            }
        }

        while (w.Length > 1 && w[^1] == '/')
        {
            w.Length--;
        }

        return w.ToString();
    }

    // %M, as libc's dirmodefmt. No file ls can stat is an auth file, which only an afid names.
    // Stryker disable once String,Conditional : an auth file's A cannot be shown
    // Stryker disable once Bitwise : a uint shifts the same either way
    internal static string Dirmode(uint m)
        => ((m & DMDIR) != 0 ? "d" : (m & DMAPPEND) != 0 ? "a" : (m & DMAUTH) != 0 ? "A" : "-")
            + ((m & DMEXCL) != 0 ? "l" : "-")
            + Modes[(m >> 6) & 7] + Modes[(m >> 3) & 7] + Modes[m & 7];

    private static string Number(ulong n) => n.ToString(CultureInfo.InvariantCulture);

    // Whether s could not be listed.
    private async Task<bool> LsAsync(string s, bool multi)
    {
        Stat db;
        try
        {
            db = await process.StatAsync(s);
            if ((db.Qid.Type & QidType.QTDIR) != 0 && !flags.Contains('d'))
            {
                await OutputAsync();
                int fd = await process.OpenAsync(s, NinePConstants.OREAD);
                IReadOnlyList<Stat> n = await process.DirReadAsync(fd);
                string prefix = Xcleanname(s);
                dirbuf.AddRange(n.Select(d => (d, multi ? prefix : null)));

                // Stryker disable once Statement : the descriptor is closed as ls exits, if not before
                await process.CloseAsync(fd);
                await OutputAsync();
                return false;
            }
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"ls: {s}: {error.Message}\n"));
            return true;
        }

        s = Xcleanname(s);
        int p = s.LastIndexOf('/');
        dirbuf.Add((db, p >= 0 ? s[..p] : null));
        return false;
    }

    private async Task OutputAsync()
    {
        List<(Stat D, string? Prefix)> sorted = flags.Contains('n')
            ? dirbuf
            : dirbuf.Select((d, i) => (d.D, d.Prefix, I: i)).Order(Comparer<(Stat D, string? Prefix, int I)>.Create(Compar)).Select(d => (d.D, d.Prefix)).ToList();
        foreach ((Stat d, _) in sorted)
        {
            Dowidths(d);
        }

        foreach ((Stat d, string? prefix) in sorted)
        {
            Format(d, !flags.Contains('p') && prefix is { } s ? $"{(s == "/" ? string.Empty : s)}/{d.Name}" : d.Name);
        }

        dirbuf.Clear();
        if (bin.Length != 0)
        {
            await process.WriteAsync(1, Encoding.UTF8.GetBytes(bin.ToString()));
            bin.Clear();
        }
    }

    private void Dowidths(Stat db)
    {
        if (flags.Contains('s'))
        {
            swidth = Math.Max(swidth, Number((db.Length + 1023) / 1024).Length);
        }

        if (flags.Contains('q'))
        {
            qwidth = Math.Max(qwidth, Number(db.Qid.Version).Length);
        }

        if (flags.Contains('m'))
        {
            mwidth = Math.Max(mwidth, Libc.Quote(db.Muid).Length + 2);
        }

        if (flags.Contains('l'))
        {
            vwidth = Math.Max(vwidth, Number(db.Dev).Length);
            uwidth = Math.Max(uwidth, Libc.Quote(db.Uid).Length);
            gwidth = Math.Max(gwidth, Libc.Quote(db.Gid).Length);
            lwidth = Math.Max(lwidth, Number(db.Length).Length);
        }
    }

    private void Format(Stat db, string name)
    {
        if (flags.Contains('s'))
        {
            bin.Append(Number((db.Length + 1023) / 1024).PadLeft(swidth)).Append(' ');
        }

        if (flags.Contains('m'))
        {
            bin.Append('[').Append(Libc.Quote(db.Muid)).Append("] ");
            bin.Append(' ', Math.Max(0, mwidth - 2 - Encoding.UTF8.GetByteCount(db.Muid)));
        }

        if (flags.Contains('q'))
        {
            bin.Append(CultureInfo.InvariantCulture, $"({db.Qid.Path:x16} {Number(db.Qid.Version).PadLeft(qwidth)} {(byte)db.Qid.Type:x2}) ");
        }

        if (flags.Contains('T'))
        {
            bin.Append((db.Mode & DMTMP) != 0 ? 't' : '-').Append(' ');
        }

        if (flags.Contains('l'))
        {
            bin.Append(CultureInfo.InvariantCulture, $"{Dirmode(db.Mode)} {(char)db.Type} {Number(db.Dev).PadLeft(vwidth)} {Libc.Quote(db.Uid).PadRight(uwidth)} {Libc.Quote(db.Gid).PadRight(gwidth)} {Number(db.Length).PadLeft(lwidth)} {Asciitime(flags.Contains('u') ? db.Atime : db.Mtime)} ");
        }

        bin.Append(flags.Contains('Q') ? name : Libc.Quote(name)).Append(Fileflag(db)).Append('\n');
    }

    private string Fileflag(Stat db)
        => !flags.Contains('F') ? string.Empty
            : (db.Qid.Type & QidType.QTDIR) != 0 ? "/"
            : (db.Mode & 0b001_001_001) != 0 ? "*"
            : string.Empty;

    // compar: by time, newest first, with -t, or by name, a directory's entries after the
    // directory's own name; ties in the order ls found them, all reversed with -r.
    private int Compar((Stat D, string? Prefix, int I) a, (Stat D, string? Prefix, int I) b)
    {
        Stat ad = a.D, bd = b.D;
        int i;
        if (flags.Contains('t'))
        {
            i = unchecked((int)(flags.Contains('u') ? bd.Atime - ad.Atime : bd.Mtime - ad.Mtime));
        }
        else if (a.Prefix is not null && b.Prefix is not null)
        {
            i = string.CompareOrdinal(a.Prefix, b.Prefix);
            i = i == 0 ? string.CompareOrdinal(ad.Name, bd.Name) : i;
        }
        else if (a.Prefix is not null)
        {
            // A directory's listing and a name given alone that is the directory's own: one name
            // cannot be both, so compar's tie here does not come.
            // Stryker disable once all : no name is both a directory listed and a file given
            i = string.CompareOrdinal(a.Prefix, bd.Name);
            i = i == 0 ? 1 : i;
        }
        else if (b.Prefix is not null)
        {
            // Stryker disable once all : no name is both a directory listed and a file given
            i = string.CompareOrdinal(ad.Name, b.Prefix);
            i = i == 0 ? -1 : i;
        }
        else
        {
            i = string.CompareOrdinal(ad.Name, bd.Name);
        }

        i = i == 0 ? a.I.CompareTo(b.I) : i;
        return flags.Contains('r') ? -i : i;
    }

    // The month, day and time, or the month, day and year for a time more than six months past or
    // a day ahead.
    private string Asciitime(uint l)
    {
        string t = Libc.Ctime(l);
        return unchecked(l < clk - (180u * 24 * 60 * 60) || clk + (24u * 60 * 60) < l) ? t[4..11] + t[23..28] : t[4..16];
    }
}
