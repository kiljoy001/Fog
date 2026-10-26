using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// mntgen, as 9front's mntgen.c: a directory in which any name walked to is an empty directory, for
// as long as a fid is on it, mounted after the mount point, /n by default.
public sealed class Mntgen
{
    private readonly Process process;
    private readonly string argv0;
    private readonly List<Tab> tab = [];
    private readonly Srv fs;
    private uint time0;
    private uint now;

    public Mntgen(Process process, string argv0)
    {
        this.process = process;
        this.argv0 = argv0;
        fs = new Srv
        {
            Argv0 = argv0,
            Attach = FsattachAsync,
            Open = FsopenAsync,
            Read = FsreadAsync,
            Stat = FsstatAsync,
            Walk = FswalkAsync,
            DestroyFid = Fsclunk,
        };
    }

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Mntgen(process, argv[0]).RunAsync(argv);

    // hash: the low 48 bits of the first word of the name's MD5.
    private static ulong Hash(string name)
        => BinaryPrimitives.ReadUInt64LittleEndian(MD5.HashData(Encoding.UTF8.GetBytes(name))) & ((1UL << 48) - 1);

    private async Task RunAsync(IReadOnlyList<string> argv)
    {
        Srv.Enter(process);
        time0 = (uint)await Syslog.TimeAsync(process);
        string? service = null;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'D':
                    break;
                case 's':
                    service = await arguments.EargfAsync(UsageAsync);
                    break;
                default:
                    await UsageAsync();
                    break;
            }
        }

        IReadOnlyList<string> rest = arguments.Rest();
        if (rest.Count > 1)
        {
            await UsageAsync();
        }

        try
        {
            await fs.PostMountAsync(service, rest.Count != 0 ? rest[0] : "/n", MountFlags.After);
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv0}: {error.Message}\n"));
            process.Exits(error.Message);
        }

        // Stryker disable once Statement : a program of Fog's that returns exits with no status, as this does
        process.Exits(null);
    }

    private async Task<string> UsageAsync()
    {
        await process.WriteAsync(2, "mntgen [-s srvname] [mtpt]\n"u8.ToArray());
        process.Exits("usage");
        return default!;
    }

    private Tab? Findtab(ulong path) => tab.Find(t => t.Qid == path);

    // dirgen: the root as i -1, and each name made, as directories of sys.
    private Stat Dirgen(int i)
    {
        (string name, ulong path, uint time) = i == -1 ? ("/", 0UL, time0) : (tab[i].Name, tab[i].Qid, tab[i].Time);
        return new Stat(0, 0, 0, new Qid(QidType.QTDIR, 0, path), (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101, time, time, 0, name, "sys", "sys", string.Empty);
    }

    private Task FsopenAsync(Req r) => fs.RespondAsync(r, ((Topen)r.Ifcall).Mode != NinePConstants.OREAD ? "permission denied" : null);

    private Task FsreadAsync(Req r)
    {
        if (r.Fid!.Qid.Path == 0)
        {
            Srv.Dirread(r, i => i < tab.Count ? Dirgen(i) : null);
        }

        return fs.RespondAsync(r, null);
    }

    private Task FsstatAsync(Req r)
    {
        ulong qid = r.Fid!.Qid.Path;

        // Stryker disable once Statement,String,Block : a fid is on a name for as long as the name is, so no stat finds none
        if (qid != 0 && Findtab(qid) is null)
        {
            return fs.RespondAsync(r, "path not found (???)");
        }

        // Stryker disable once Conditional : the root's path is no name's, so looking it up finds nothing, whose index is -1 too
        r.D = Dirgen(qid == 0 ? -1 : tab.IndexOf(Findtab(qid)!));
        return fs.RespondAsync(r, null);
    }

    // fswalk1: nothing in a name's directory but its dotdot; in the root, any name, made a
    // directory if it is new, unless its hash is another's.
    private string? Fswalk1(Fid fid, string name)
    {
        if (fid.Qid.Path != 0)
        {
            if (name != "..")
            {
                return "path not found";
            }

            if (Findtab(fid.Qid.Path) is { } t)
            {
                t.Ref--;
            }

            fid.Qid = new Qid(QidType.QTDIR, 0, 0);
            return null;
        }

        if (name == "..")
        {
            return null;
        }

        if (tab.Find(t => t.Name == name) is { } found)
        {
            found.Ref++;
            fid.Qid = new Qid(QidType.QTDIR, 0, found.Qid);
            return null;
        }

        ulong h = Hash(name);

        // Stryker disable once Statement,String,Block : two names whose MD5s agree in 48 bits are not to be found for a test
        if (Findtab(h) is not null)
        {
            return "hash collision";
        }

        tab.Add(new Tab(name, h, now) { Ref = 1 });
        fid.Qid = new Qid(QidType.QTDIR, 0, h);
        return null;
    }

    // fswalk: the time a name made now is made at, then walkandclone.
    private async Task FswalkAsync(Req r)
    {
        now = (uint)await Syslog.TimeAsync(Srv.Up);
        await fs.WalkAndCloneAsync(r, Fswalk1, Fsclone);
    }

    private string? Fsclone(Fid fid, Fid newfid)
    {
        if (Findtab(fid.Qid.Path) is { } t)
        {
            t.Ref++;
        }

        return null;
    }

    // fsclunk: a name goes once no fid is on it.
    private void Fsclunk(Fid fid)
    {
        ulong qid = fid.Qid.Path;
        if (qid == 0)
        {
            return;
        }

        // Stryker disable once Statement,String,Block : a name goes only once no fid is on it, so a clunked fid's is always found
        if (Findtab(qid) is not { } t)
        {
            _ = Srv.Up.WriteAsync(2, Encoding.UTF8.GetBytes($"warning: cannot find {qid:x}\n"));
            return;
        }

        if (--t.Ref == 0)
        {
            tab.Remove(t);

            // Stryker disable once Statement : a count just come to none is not below it, so nothing more is said
            return;
        }

        // Stryker disable once Statement,String,Equality,Block : each fid on a name counts once, so the count is never below none
        if (t.Ref < 0)
        {
            _ = Srv.Up.WriteAsync(2, Encoding.UTF8.GetBytes($"warning: negative ref count for {t.Name}\n"));
        }
    }

    private Task FsattachAsync(Req r)
    {
        if (((Tattach)r.Ifcall).Aname.Length != 0)
        {
            return fs.RespondAsync(r, "invalid attach specifier");
        }

        r.Qid = new Qid(QidType.QTDIR, 0, 0);
        r.Fid!.Qid = r.Qid;
        return fs.RespondAsync(r, null);
    }

    // A name made, its qid, when it was made, and how many fids are on it.
    private sealed class Tab(string name, ulong qid, uint time)
    {
        public string Name { get; } = name;

        public ulong Qid { get; } = qid;

        public uint Time { get; } = time;

        public int Ref { get; set; }
    }
}
