using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// auth/factotum, as 9front's fs.c, rpc.c, util.c, confirm.c and log.c: a file server of ctl, rpc,
// proto, needkey, confirm and log, holding keys and running authentication protocols for those
// who open rpc. Its protocols are those Fog's logins speak: p9any, dp9ik and pass. Its nvram is the
// one the host keeps in its sealed key store.
public sealed class Factotum
{
    public const string Easproto = "auth server protocol botch";
    public const string Ebadarg = "invalid argument";
    public const string Ebadkey = "bad key";
    public const string Enegotiation = "negotiation failed, no common protocols or keys";
    private const string Etoolarge = "rpc too large";
    private const string Etoosmall = "rpc too small";
    private const int Maxrpc = 4096;
    private const int Qroot = 0;
    private const int Qfactotum = 1;
    private const int Qrpc = 2;
    private const int Qkeylist = 3;
    private const int Qprotolist = 4;
    private const int Qconfirm = 5;
    private const int Qlog = 6;
    private const int Qctl = 7;
    private const int Qneedkey = 8;
    private const uint Dmexcl = (uint)NinePConstants.FileMode9P.DMEXCL;
    private const uint Dmdir = (uint)NinePConstants.FileMode9P.DMDIR;

    // rpc.c's verbs, and ctl's.
    private const int Vunknown = -1;
    private const int Vauthinfo = 0;
    private const int Vread = 1;
    private const int Vstart = 2;
    private const int Vwrite = 3;
    private const int Vattr = 4;

    // dirtab: confirm and needkey are its first two, as inuse counts them.
    private static readonly (string Name, int Qidpath, uint Perm)[] Dirtab =
    [
        ("confirm", Qconfirm, 0b110_000_000 | Dmexcl),
        ("needkey", Qneedkey, 0b110_000_000 | Dmexcl),
        ("ctl", Qctl, 0b110_100_100),
        ("rpc", Qrpc, 0b110_110_110),
        ("proto", Qprotolist, 0b100_100_100),
        ("log", Qlog, 0b100_000_000 | Dmexcl),
    ];

    private static readonly string[] Rpctab = ["authinfo", "read", "start", "write", "attr"];
    private static readonly string[] Ignored = ["role", "disabled"];

    private readonly Process process;
    private readonly string argv0;
    private readonly List<Key> ring = [];
    private readonly int[] inuse = new int[Dirtab.Length];
    private readonly Logbuf logbuf = new();
    private readonly Logbuf confbuf = new();
    private readonly Logbuf needkeybuf = new();
    private readonly List<Req> cusewait = [];
    private readonly List<Req> needwait = [];
    private readonly List<string> debugged = [];
    private readonly Srv fs;
    private int seqnum;
    private ulong conftaggen;
    private int caphashfd = -1;

    public Factotum(Process process, string argv0)
    {
        this.process = process;
        this.argv0 = argv0;
        var dp9ik = new P9sk();
        Prototab = [new P9any(dp9ik), dp9ik, new Pass()];
        fs = new Srv
        {
            Argv0 = argv0,
            Attach = FsattachAsync,
            Walk1 = Fswalk1,
            Open = FsopenAsync,
            Read = r => LoggedAsync(FsreadAsync(r)),
            Write = r => LoggedAsync(FswriteAsync(r)),
            Stat = FsstatAsync,
            Flush = r => LoggedAsync(FsflushAsync(r)),
            DestroyFid = Fsdestroyfid,
        };
    }

    // %r: the errstr of the process this runs in.
    public static string Errstr
    {
        get => global::NinePSharp.Fog.Commands.Errstr.Get(Srv.Up);
        set => global::NinePSharp.Fog.Commands.Errstr.Set(Srv.Up, value);
    }

    public bool Debug { get; private set; }

    public bool Askforkeys { get; private set; } = true;

    public bool Sflag { get; private set; }

    public string Owner { get; private set; } = string.Empty;

    public Proto[] Prototab { get; }

    // authaddr: the auth servers -a gives, for bootstrapping.
    public List<string> Authaddr { get; } = [];

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Factotum(process, argv[0]).RunAsync(argv);

    // matchattr: each of the pattern's attributes, as its type asks, in either list; role and
    // disabled match as defaults whatever their type.
    public static bool Matchattr(Attr? pat, Attr? a0, Attr? a1)
    {
        for (; pat is not null; pat = pat.Next)
        {
            AttrType type = Ignored.Contains(pat.Name) ? AttrType.Default : pat.Type;
            bool has = Attr.Find(a0, pat.Name) is not null || Attr.Find(a1, pat.Name) is not null;
            if (type == AttrType.Query ? !has : (type == AttrType.Nameval || has) && !Hasnameval(a0, a1, pat.Name, pat.Val))
            {
                return false;
            }
        }

        return true;
    }

    // setattrs: each of b's attributes put in a, a value in place of the first of its name, which
    // takes it, and of any others, which go; a query of a name a has changes nothing.
    public static Attr? Setattrs(Attr? a, Attr? b)
    {
        for (; b is not null; b = b.Next)
        {
            if (b.Type == AttrType.Query && a?.All.Any(x => x.Name == b.Name) == true)
            {
                continue;
            }

            bool found = false;
            Attr? last = null;
            for (Attr? l = a; l is not null; l = l.Next)
            {
                if (l.Name != b.Name)
                {
                    last = l;
                }
                else if (!found)
                {
                    found = true;
                    (l.Val, l.Type) = (b.Val, AttrType.Nameval);
                    last = l;
                }
                else
                {
                    last!.Next = l.Next;
                }
            }

            if (!found)
            {
                var added = new Attr(b.Type, b.Name, b.Val, null);
                if (last is null)
                {
                    a = added;
                }
                else
                {
                    last.Next = added;
                }
            }
        }

        return a;
    }

    // setattr: the attributes fmt gives put in a.
    public static Attr? Setattr(Attr? a, string fmt) => Setattrs(a, Attr.Parse(fmt));

    // sortattr: by name, as its merge sort leaves equal names.
    public static Attr? Sortattr(Attr? a)
    {
        if (a?.Next is null)
        {
            return a;
        }

        Attr? a0 = null, a1 = null;
        int i = 0;
        for (Attr? anext; a is not null; a = anext)
        {
            anext = a.Next;

            // Stryker disable once Update : counting down alternates odd and even as counting up does
            if (i++ % 2 != 0)
            {
                (a.Next, a0) = (a0, a);
            }
            else
            {
                (a.Next, a1) = (a1, a);
            }
        }

        a0 = Sortattr(a0);
        a1 = Sortattr(a1);
        Attr? first = null, last = null;
        while (a0 is not null || a1 is not null)
        {
            Attr next;
            if (a1 is null || (a0 is not null && string.CompareOrdinal(a0.Name, a1.Name) < 0))
            {
                (next, a0) = (a0!, a0!.Next);
            }
            else
            {
                (next, a1) = (a1, a1.Next);
            }

            (first, last) = last is null ? (next, next) : (first, last.Next = next);
        }

        last!.Next = null;
        return first;
    }

    // %N: the names of the attributes, each as a query.
    public static string Attrnamefmt(Attr? a) => string.Join(' ', (a?.All ?? []).Select(x => $"{Libc.Quote(x.Name)}?"));

    // isclient: 1 for a client, 0 for a server, -1 for no or another role.
    public static int Isclient(string? role)
    {
        if (role is null)
        {
            Errstr = "role not specified";
            return -1;
        }

        if (role is "server" or "client")
        {
            return role == "client" ? 1 : 0;
        }

        Errstr = $"unknown role {Libc.Quote(role)}";
        return -1;
    }

    public static Rpc Toosmall(Fsstate fss, int n)
    {
        fss.Nwant = n;
        return Rpc.Toosmall;
    }

    // failure: the error given, or the errstr, kept as the conversation's and logged.
    public Rpc Failure(Fsstate s, string? fmt)
    {
        if (fmt is null)
        {
            s.Err = Errstr;
        }
        else
        {
            s.Err = fmt;
            Errstr = fmt;
        }

        Flog($"{s.Seqnum}: failure {s.Err}");
        return Rpc.Failure;
    }

    public Rpc Phaseerror(Fsstate s, string op)
    {
        Errstr = $"protocol phase error: {op} in state {Phasename(s, s.Phase)}";
        return Rpc.Phase;
    }

    public Proto? Findproto(string name) => Array.Find(Prototab, p => p.Name == name);

    // findkey: the skip-th key matching the request's attributes and fmt's, the user's own or one
    // for anyone, confirmed if it asks to be; or the attributes to ask for one by, when there were
    // queries in what was asked and a key may be asked for.
    public Rpc Findkey(out Key? ret, Keyinfo ki, string? fmt)
    {
        ret = null;
        string? who = ki.User;
        Attr? attr0 = ki.Attr;
        Attr? attr1 = fmt is null ? null : Attr.Parse(fmt);
        who = who == Owner ? null : who;
        Attr? attr2 = who is null ? null : Attr.Parse($"owner={Libc.Quote(who)}");
        Attr? attr3 = who is null ? null : Attr.Parse("owner=*");
        string? p = Attr.StrFind(attr0, "proto") ?? Attr.StrFind(attr1, "proto");
        if (p is not null && Findproto(p) is null)
        {
            Errstr = $"unknown protocol {p}";
            return Failure(ki.Fss, null);
        }

        if (Search(out ret, ki, attr0, attr1, attr2, attr3, out int nmatch) is { } found)
        {
            return found;
        }

        Flog($"{ki.Fss.Seqnum}: no key matches {Attr.Format(attr0)} {Attr.Format(attr1)} {Attr.Format(attr2)} {Attr.Format(attr3)}");
        Errstr = $"no key matches {Attr.Format(attr0)} {Attr.Format(attr1)}";
        if (!Askforkeys || who is not null || !(Hasqueries(attr0) || Hasqueries(attr1)))
        {
            return Failure(ki.Fss, null);
        }

        ki.Fss.Keyinfo = nmatch == 0 ? Question(attr0, attr1) : string.Empty;
        return Rpc.Needkey;
    }

    // replacekey: the key in place of one whose public attributes are the same, or added, first
    // when before is set.
    public int Replacekey(Key kn, bool before)
    {
        lock (ring)
        {
            int i = ring.FindIndex(k => Matchattr(kn.Attr, k.Attr, null) && Matchattr(k.Attr, kn.Attr, null));
            if (i >= 0)
            {
                Key old = ring[i];
                ring[i] = kn;
                Closekey(old);
            }
            else if (before)
            {
                ring.Insert(0, kn);
            }
            else
            {
                ring.Add(kn);
            }
        }

        return 0;
    }

    // disablekey: a key that has failed marked disabled, unless on a server.
    public void Disablekey(Key k)
    {
        if (!Sflag && !k.Attr!.All.Any(a => a.Type == AttrType.Nameval && a.Name == "disabled"))
        {
            k.Attr = Append(k.Attr, new Attr(AttrType.Nameval, "disabled", "by.factotum", null));
        }
    }

    public void Closekey(Key k) => k.Proto.Closekey(k);

    // _authdial: the auth server of the domain, through cs, or, while cs is not yet there, the one
    // given by -a or /net/ndb's auth=, within 30 seconds.
    public async Task<int> AuthdialAsync(string authdom)
    {
        Process up = Srv.Up;
        await up.AlarmAsync(30 * 1000);
        int fd = -1;
        if (await BindnetcsAsync(up) >= 0)
        {
            fd = await Authsrv.AuthdialAsync(up, null, authdom);
        }
        else
        {
            if (Authaddr.Count == 0)
            {
                await NetndbauthaddrAsync(up);
            }

            // Stryker disable once Equality : factotum's 0 is its own, so a call is never given it
            for (int i = 0; fd < 0 && i < Authaddr.Count; i++)
            {
                fd = await DialAsync(up, Net.Netmkaddr(Authaddr[i], "tcp", "567"));

                // Stryker disable once all : Fog has no il, so the call by it fails whatever its address
                if (fd < 0)
                {
                    fd = await DialAsync(up, Net.Netmkaddr(Authaddr[i], "il", "566"));
                }
            }
        }

        // Stryker disable once Statement : getastickets sets its own alarm as soon as this returns
        await up.AlarmAsync(0);
        return fd;
    }

    // mkcap: a capability for from to become to, its hash given to the kernel, or null.
    public async Task<string?> MkcapAsync(string from, string to)
    {
        // Stryker disable once Equality,Statement,Block : a write to no descriptor fails as no caphash does
        if (caphashfd < 0)
        {
            return null;
        }

        string cap = $"{from}@{to}";
        byte[] rand = new byte[20];
        Authsrv.Genrandom(rand);
        string key = Convert.ToBase64String(rand);
        byte[] hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(cap));
        try
        {
            await Srv.Up.WriteAsync(caphashfd, hash);
        }

        // Stryker disable once all : caphash, opened by the host owner, takes each whole hash that its factotum, the host owner still, writes
        catch (SyscallException)
        {
            return null;
        }

        return $"{cap}@{key}";
    }

    // flog: a line in the log.
    public void Flog(string buf) => Logbufappend(logbuf, buf);

    // rpcrdwrlog and rpcstartlog: the outcome of a read, write or start, when debugging.
    public void Rpcrdwrlog(Fsstate fss, string rdwr, int n, int ophase, Rpc ret)
    {
        if (Debug)
        {
            Logret($"{fss.Seqnum}: {rdwr} {n} in phase {Phasename(fss, ophase)} yields phase {Phasename(fss, fss.Phase)}", fss, ret);
        }
    }

    public void Rpcstartlog(Attr? attr, Fsstate fss, Rpc ret)
    {
        if (Debug)
        {
            Logret($"{fss.Seqnum}: start {Attr.Format(attr)} yields phase {Phasename(fss, fss.Phase)}", fss, ret);
        }
    }

    // ctlwrite: key adds a key for each of its protocols, delkey deletes those matching, debug turns
    // debugging on or off; a comment or nothing does nothing.
    public int Ctlwrite(string a, bool atzero)
    {
        if (a.Length == 0 || a[0] == '#')
        {
            return 0;
        }

        int nl = a.IndexOf('\n', StringComparison.Ordinal);
        if (nl >= 0)
        {
            if (nl + 1 != a.Length)
            {
                Errstr = "multiline write not allowed";
                return -1;
            }

            a = a[..nl];
        }

        int sp = a.IndexOf(' ', StringComparison.Ordinal);

        // Stryker disable once Equality : at a leading space the verb is empty, and so unknown, either way
        string verb = sp < 0 ? a : a[..sp], p = sp < 0 ? string.Empty : a[(sp + 1)..];
        switch (verb)
        {
            case "debug":
                Debug = !Debug;
                return 0;
            case "delkey":
                return Delkey(p);
            case "key":
                return Addkey(p, atzero);
            default:
                Errstr = "unknown verb";
                return -1;
        }
    }

    // What a needkey asks for: the attributes asked with and those of fmt, without role and
    // disabled, sorted.
    private static string Question(Attr? attr0, Attr? attr1)
    {
        Attr? all = Attr.Copy(attr0);
        for (Attr? b = attr1; b is not null; b = b.Next)
        {
            all = Append(all, new Attr(b.Type, b.Name, b.Val, null));
        }

        foreach (string ignored in Ignored)
        {
            all = Attr.Delete(all, ignored);
        }

        return Attr.Format(Sortattr(all));
    }

    private static Attr? Append(Attr? a, Attr b)
    {
        if (a is null)
        {
            return b;
        }

        a.All.Last().Next = b;
        return a;
    }

    private static bool Hasnameval(Attr? a0, Attr? a1, string name, string val)
        => (a0?.All ?? []).Concat(a1?.All ?? []).Any(a => a.Name == name && a.Type != AttrType.Query && a.Val == val);

    private static bool Hasqueries(Attr? a) => a?.All.Any(x => x.Type == AttrType.Query) == true;

    // phasename: the phase's name, or its number when the protocol gives it none.
    private static string Phasename(Fsstate fss, int phase)
        => fss.Phase == Fsstate.Broken ? "Broken"
        : phase == Fsstate.Established ? "Established"
        : phase == Fsstate.Notstarted ? "Notstarted"
        : phase >= 0 && phase < fss.Maxphase && phase < fss.Phasename.Length ? fss.Phasename[phase]
        : phase.ToString(CultureInfo.InvariantCulture);

    // convAI2M: the users, capability and secret, each preceded by its length.
    private static byte[]? ConvAI2M(AuthInfo ai, int n)
    {
        var p = new List<byte>();
        foreach (byte[] s in new[] { Encoding.UTF8.GetBytes(ai.Cuid), Encoding.UTF8.GetBytes(ai.Suid), Encoding.UTF8.GetBytes(ai.Cap ?? string.Empty), ai.Secret })
        {
            if (p.Count + s.Length + 2 >= n)
            {
                return null;
            }

            p.Add((byte)s.Length);

            // Stryker disable once Bitwise : a length is never less than none
            p.Add((byte)(s.Length >> 8));
            p.AddRange(s);
        }

        return [.. p];
    }

    private static Qid Mkqid(QidType type, int path) => new(type, 0, (ulong)path);

    // bindnetcs: #I on /net and cs on /net/cs, unless they are there already; -1 when cs is not.
    private static async Task<int> BindnetcsAsync(Process up)
    {
        // Stryker disable once String : #I bound where it is already is bound there again, to the same effect
        if (!await AccessAsync(up, "/net/tcp"))
        {
            try
            {
                await up.BindAsync("#I", "/net", MountFlags.Before);
            }
            catch (SyscallException)
            {
                // bind's failure is not looked at.
            }
        }

        if (await AccessAsync(up, "/net/cs"))
        {
            return 0;
        }

        int srvfd;
        try
        {
            srvfd = await up.OpenAsync("/srv/cs", NinePConstants.ORDWR);
        }
        catch (SyscallException)
        {
            return -1;
        }

        try
        {
            // Stryker disable once String : cs attaches whatever is asked to be attached
            await up.MountAsync(srvfd, -1, "/net", MountFlags.Before, string.Empty);
            return 0;
        }
        catch (SyscallException)
        {
            await up.CloseAsync(srvfd);
            return -1;
        }
    }

    private static async Task<bool> AccessAsync(Process up, string name)
    {
        try
        {
            await up.StatAsync(name);
            return true;
        }
        catch (SyscallException)
        {
            return false;
        }
    }

    private static async Task<int> DialAsync(Process up, string addr)
    {
        try
        {
            return await Net.DialAsync(up, addr);
        }
        catch (SyscallException error)
        {
            global::NinePSharp.Fog.Commands.Errstr.Set(up, error.Message);
            return -1;
        }
    }

    // secstore -G factotum run with its output on ctl, and waited for.
    private async Task SecstoreAsync(string ctl)
    {
        process.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            int cfd;
            try
            {
                cfd = await child.OpenAsync(ctl, NinePConstants.OWRITE);
            }
            catch (SyscallException error)
            {
                await child.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv0}: can't open factotum: {error.Message}\n"));
                child.Exits($"can't open factotum: {error.Message}");
                return;
            }

            await child.DupAsync(cfd, 1);
            try
            {
                await child.ExecAsync("/bin/auth/secstore", ["secstore", "-G", "factotum"]);
            }
            catch (SyscallException)
            {
                // execl's failure is not looked at; the child exits.
            }

            child.Exits(null);
        });
        try
        {
            await process.WaitAsync();
        }
        catch (SyscallException)
        {
            // waitpid with nothing to wait for.
        }
    }

    // The files posted as the service, mounted on /mnt/factotum or before the mount point given,
    // and the service made writable by all; the mount point.
    private async Task<string> ServeAsync(string? mtpt, string? service)
    {
        int fd = await fs.PostAsync(service);
        if (mtpt is null)
        {
            mtpt = "/mnt";
            await MountAsync(fd, "/mnt/factotum", MountFlags.Replace, "factotum");
        }
        else
        {
            await MountAsync(fd, mtpt, MountFlags.Before, string.Empty);
        }

        if (service is not null)
        {
            try
            {
                Stat d = Process.NullDir;
                await process.WStatAsync($"/srv/{service}", new Stat(0, d.Type, d.Dev, d.Qid, 0b110_110_110, d.Atime, d.Mtime, d.Length, d.Name, d.Uid, d.Gid, d.Muid));
            }
            catch (SyscallException error)
            {
                await PrintAsync($"factotum warning: cannot chmod 666 /srv/{service}: {error.Message}\n");
            }
        }

        return mtpt;
    }

    // -S's keys from the nvram, or -u's host owner.
    private async Task HostownerAsync(Flags flags)
    {
        if (Sflag)
        {
            if (await GetnvramkeyAsync(flags.K ? Authsrv.NVwrite : Authsrv.NVwriteonerr) < 0)
            {
                await PrintAsync($"factotum warning: cannot read nvram: {Errstr}\n");
            }
        }
        else if (flags.U)
        {
            await PromptforhostownerAsync();
        }
    }

    // -a, at most seven of them, the last taking the place of the seventh; -m and -s.
    private void Value(Flags flags, char c, string value)
    {
        if (c == 'm')
        {
            flags.Mtpt = value;
        }
        else if (c == 's')
        {
            flags.Service = value;
        }
        else if (Authaddr.Count < 7)
        {
            Authaddr.Add(value);
        }
        else
        {
            Authaddr[6] = value;
        }
    }

    // A flag without a value; false for one factotum does not take.
    private bool Flag(Flags flags, char c)
    {
        switch (c)
        {
            case 'D':
                break;
            case 'S':
                (Askforkeys, flags.Secstore, Sflag) = (false, false, true);
                break;
            case 'd':
                (Debug, flags.Private) = (true, false);
                break;
            case 'g':
                flags.G = true;
                break;
            case 'k':
                flags.K = true;
                break;
            case 'n':
                flags.Secstore = false;
                break;
            case 'p':
                flags.Private = false;
                break;
            case 'u':
                flags.U = true;
                break;
            default:
                return false;
        }

        return true;
    }

    // The flags, each as fs.c's ARGBEGIN takes it; one it does not know is a usage error.
    private async Task<Flags> FlagsAsync(Arguments arguments)
    {
        var flags = new Flags();
        foreach (char c in arguments.Flags())
        {
            if (c is 'a' or 'm' or 's')
            {
                Value(flags, c, await arguments.EargfAsync(UsageAsync));
            }
            else if (!Flag(flags, c))
            {
                await UsageAsync();
            }
        }

        return flags;
    }

    // The skip-th key on the ring that matches, is the user's or anyone's, and may be used; Confirm
    // for one still to be confirmed; or null, with how many matched, for none.
    private Rpc? Search(out Key? ret, Keyinfo ki, Attr? attr0, Attr? attr1, Attr? attr2, Attr? attr3, out int nmatch)
    {
        ret = null;
        nmatch = 0;
        lock (ring)
        {
            foreach (Key k in ring)
            {
                if (Attr.StrFind(k.Attr, "disabled") is not null && !ki.Usedisabled)
                {
                    continue;
                }

                if (!Matchattr(attr0, k.Attr, k.Privattr) || !Matchattr(attr1, k.Attr, k.Privattr) || (!Matchattr(attr2, k.Attr, null) && !Matchattr(attr3, k.Attr, null)))
                {
                    continue;
                }

                if (nmatch++ < ki.Skip)
                {
                    continue;
                }

                int canuse = ki.Noconf ? 1 : Canusekey(ki.Fss, k);
                if (canuse < 0)
                {
                    return Rpc.Confirm;
                }

                if (canuse != 0)
                {
                    ret = k;
                    return Rpc.Ok;
                }
            }
        }

        return null;
    }

    // netndbauthaddr: the values of each auth= in /net/ndb that a blank ends, at most seven.
    private async Task NetndbauthaddrAsync(Process up)
    {
        string b;
        try
        {
            int fd = await up.OpenAsync("/net/ndb", NinePConstants.OREAD);
            var all = new List<byte>();
            for (ReadOnlyMemory<byte> chunk; !(chunk = await up.ReadAsync(fd, 1024)).IsEmpty;)
            {
                all.AddRange(chunk.Span);
            }

            await up.CloseAsync(fd);
            b = Encoding.UTF8.GetString([.. all]);
        }
        catch (SyscallException)
        {
            return;
        }

        const string Blanks = "\n\t ";
        for (int e = 0, p; (p = b.IndexOf("auth=", e, StringComparison.Ordinal)) >= 0;)
        {
            if (p > e && !Blanks.Contains(b[p - 1], StringComparison.Ordinal))
            {
                e = p + 5;
                continue;
            }

            p += 5;
            for (e = p; e < b.Length && !Blanks.Contains(b[e], StringComparison.Ordinal); e++)
            {
            }

            if (e == b.Length)
            {
                break;
            }

            string addr = b[p..e++];
            if (addr.Length != 0)
            {
                Authaddr.Add(addr);
                if (Authaddr.Count >= 7)
                {
                    break;
                }
            }
        }
    }

    private void Logret(string pre, Fsstate fss, Rpc ret) => Flog(ret switch
    {
        // Stryker disable once String : no protocol answers with its errstr, which only needkey's error gives an rpc
        Rpc.Errstr => $"{pre}: error {Errstr}",
        Rpc.Failure => $"{pre}: failure {fss.Err}",
        Rpc.Needkey => $"{pre}: needkey {fss.Keyinfo}",
        Rpc.Ok => $"{pre}: ok",
        Rpc.Toosmall => $"{pre}: toosmall {fss.Nwant}",
        Rpc.Phase => $"{pre}: phase: {Errstr}",
        _ => $"{pre}: waiting for confirm",
    });

    // canusekey: 1 for a key that needs no confirming, or whose confirming said yes, 0 for one it
    // said no to, and -1 for one still to be asked about, which is then asked about.
    private int Canusekey(Fsstate fss, Key k)
    {
        if (Attr.StrFind(k.Attr, "confirm") is null)
        {
            return 1;
        }

        if (fss.Conf.Find(c => c.Key == k) is { } conf)
        {
            return conf.Canuse;
        }

        fss.Conf.Add(new Conf(k, conftaggen++));
        return -1;
    }

    private int Delkey(string p)
    {
        Attr? attr = Attr.Parse(p);
        if (attr?.All.Any(pa => pa.Type != AttrType.Query && pa.Name.StartsWith('!')) == true)
        {
            Errstr = "only !private? patterns are allowed for private fields";
            return -1;
        }

        List<Key> deleted;
        lock (ring)
        {
            deleted = ring.FindAll(k => Matchattr(attr, k.Attr, k.Privattr));
            ring.RemoveAll(deleted.Contains);
        }

        deleted.ForEach(Closekey);
        int nmatch = deleted.Count;

        if (nmatch == 0)
        {
            Errstr = "found no keys to delete";
            return -1;
        }

        return 0;
    }

    // key: the proto= attributes taken out, then the private ones, and a key made of the rest
    // for each protocol.
    private int Addkey(string p, bool atzero)
    {
        Attr? attr = Attr.Parse(p);
        Attr? protos = null, priv = null;
        foreach (Attr a in (attr?.All ?? []).ToList())
        {
            if (a.Name == "proto")
            {
                protos = Append(protos, new Attr(a.Type, a.Name, a.Val, null));
                attr = Attr.Delete(attr, "proto");
            }
        }

        if (protos is null)
        {
            Errstr = "key without protos";
            return -1;
        }

        foreach (Attr a in (attr?.All ?? []).ToList())
        {
            if (a.Name.StartsWith('!'))
            {
                priv = Append(priv, new Attr(a.Type, a.Name, a.Val, null));
                attr = Attr.Delete(attr, a.Name);
            }
        }

        int ret = 0;
        for (Attr? pa = protos; pa is not null; pa = pa.Next)
        {
            if (Findproto(pa.Val) is not { } proto)
            {
                Errstr = $"unknown proto {pa.Val}";
                ret = -1;
                continue;
            }

            if (!proto.TakesKeys)
            {
                Errstr = $"proto {proto.Name} doesn't take keys";
                ret = -1;
                continue;
            }

            var k = new Key { Attr = new Attr(AttrType.Nameval, "proto", proto.Name, Attr.Copy(attr)), Privattr = Attr.Copy(priv), Proto = proto };
            if (proto.Addkey(this, k, atzero) < 0)
            {
                ret = -1;
            }
        }

        return ret;
    }

    private async Task RunAsync(IReadOnlyList<string> argv)
    {
        Srv.Enter(process);
        var arguments = new Arguments(argv);
        Flags flags = await FlagsAsync(arguments);
        IReadOnlyList<string> rest = arguments.Rest();
        if (rest.Count != 0 && !flags.G)
        {
            await UsageAsync();
        }

        if (flags.Private)
        {
            await PrivateAsync();
        }

        await InitcapAsync();

        // Stryker disable once String : catching more notes changes nothing seen: a note to the server interrupts the read of its next message, which ends it caught or not, as lib9p's getreq ends it, and nothing waits for it
        process.Notify(note => note.StartsWith("interrupt", StringComparison.Ordinal) || note.StartsWith("alarm", StringComparison.Ordinal));

        if (flags.G)
        {
            if (rest.Count != 1)
            {
                await UsageAsync();
            }

            await AskuserAsync(rest[0]);
            process.Exits(null);
        }

        await HostownerAsync(flags);
        Owner = process.User;
        string mtpt = await ServeAsync(flags.Mtpt, flags.Service);
        if (flags.Secstore)
        {
            await SecstoreAsync($"{mtpt}/factotum/ctl");
        }

        process.Exits(null);
    }

    private async Task MountAsync(int fd, string old, MountFlags flags, string aname)
    {
        try
        {
            await process.MountAsync(fd, -1, old, flags, aname);
        }
        catch (SyscallException)
        {
            // fs.c does not look at what mount returns.
        }
    }

    private Task PrintAsync(string text) => process.WriteAsync(2, Encoding.UTF8.GetBytes(text)).AsTask();

    // sysfatal.
    private async Task SysfatalAsync(string message)
    {
        await PrintAsync($"{argv0}: {message}\n");
        process.Exits(message);
    }

    private async Task<string> UsageAsync()
    {
        await PrintAsync($"usage: {argv0} [-DSdknpu] [-a authaddr] [-m mtpt] [-s service]\nor    {argv0} -g 'params'\n");
        process.Exits("usage");
        return default!;
    }

    // private: no other process may debug this one and steal its keys.
    private async Task PrivateAsync()
    {
        int fd;
        try
        {
            fd = await process.OpenAsync($"/proc/{process.Pid}/ctl", NinePConstants.OWRITE | NinePConstants.OCEXEC);
        }
        catch (SyscallException error)
        {
            await PrintAsync($"Warning! {argv0} can't protect itself from debugging: {error.Message}\n");
            return;
        }

        foreach ((string msg, string warning) in new[] { ("private", "can't protect itself from debugging"), ("noswap", "can't turn off swapping") })
        {
            try
            {
                await process.WriteAsync(fd, Encoding.UTF8.GetBytes(msg));
            }
            catch (SyscallException error)
            {
                await PrintAsync($"Warning! {argv0} {warning}: {error.Message}\n");
            }
        }

        await process.CloseAsync(fd);
    }

    // initcap: caphash kept open, as opens of it may be disabled later.
    private async Task InitcapAsync()
    {
        try
        {
            caphashfd = await process.OpenAsync("/dev/caphash", NinePConstants.OCEXEC | NinePConstants.OWRITE);
        }
        catch (SyscallException)
        {
            caphashfd = -1;
        }
    }

    // askuser: a key asked for on the console and given to the mounted factotum: promptforkey
    // asks for each query, the secret ones last and without echo, user's default the user's own.
    private async Task AskuserAsync(string parameters)
    {
        int cons;
        try
        {
            cons = await process.OpenAsync("/dev/cons", NinePConstants.ORDWR);
        }
        catch (SyscallException error)
        {
            await SysfatalAsync($"opening /dev/cons: {error.Message}");
            return;
        }

        Attr? attr = Attr.Parse(parameters);
        string adding = string.Concat((attr?.All ?? []).Where(a => a.Type != AttrType.Query && !a.Name.StartsWith('!')).Select(a => $" {Libc.Quote(a.Name)}={Libc.Quote(a.Val)}"));
        await process.WriteAsync(cons, Encoding.UTF8.GetBytes($"\n!Adding key:{adding}\n"));
        foreach (bool secret in new[] { false, true })
        {
            foreach (Attr a in (attr?.All ?? []).Where(a => a.Type == AttrType.Query && a.Name.StartsWith('!') == secret))
            {
                string name = secret ? a.Name[1..] : a.Name;
                if (await Authsrv.ReadconsAsync(process, name, name == "user" ? process.User : null, secret) is not { } val)
                {
                    await SysfatalAsync("user terminated key input");
                    return;
                }

                (a.Val, a.Type) = (val, AttrType.Nameval);
            }
        }

        await process.WriteAsync(cons, "!\n"u8.ToArray());

        // Stryker disable once Statement : -g exits as soon as askuser returns, which closes what it left open
        await process.CloseAsync(cons);
        int fd;
        try
        {
            fd = await process.OpenAsync("/mnt/factotum/ctl", NinePConstants.ORDWR);
        }
        catch (SyscallException error)
        {
            await SysfatalAsync($"opening /mnt/factotum/ctl: {error.Message}");
            return;
        }

        try
        {
            await process.WriteAsync(fd, Encoding.UTF8.GetBytes($"key {Attr.Format(attr)}\n"));
        }
        catch (SyscallException error)
        {
            await SysfatalAsync($"sending key to factotum: {error.Message}");
        }

        // Stryker disable once Statement : -g exits as soon as askuser returns, which closes what it left open
        await process.CloseAsync(fd);
    }

    // promptforhostowner: the user, from $user or the console, written to /dev/hostowner.
    private async Task PromptforhostownerAsync()
    {
        string? p = await Cs.GetenvAsync(process, "user");
        while (string.IsNullOrEmpty(p))
        {
            if ((p = await Authsrv.ReadconsAsync(process, "user", "glenda", false)) is null)
            {
                return;
            }
        }

        await WritehostownerAsync(p);
    }

    // getnvramkey: the host owner's dp9ik key from the nvram, as ctl takes it, and the host owner named
    // from it. Its DES key, for p9sk1, is left, as p9sk1 is.
    private async Task<int> GetnvramkeyAsync(int flag)
    {
        var safe = new Nvrsafe();

        // Stryker disable once Equality : readnvram gives 0 or -1
        if (await Authsrv.ReadnvramAsync(process, safe, flag) < 0 && safe.Authid[0] == 0)
        {
            return -1;
        }

        string authid = Libc.Quote(Authsrv.Str(safe.Authid)), authdom = Libc.Quote(Authsrv.Str(safe.Authdom));
        if (safe.Aesmachkey.Any(b => b != 0))
        {
            // Stryker disable once Boolean : the ring is empty when the nvram's key is added, so first and last are one
            Ctlwrite($"key proto=dp9ik user={authid} dom={authdom} !hex={Convert.ToHexString(safe.Aesmachkey)} !password=______", false);
        }

        await WritehostownerAsync(Authsrv.Str(safe.Authid));
        return 0;
    }

    private async Task WritehostownerAsync(string owner)
    {
        int at = owner.IndexOf('@', StringComparison.Ordinal);
        owner = at < 0 ? owner : owner[..at];
        int fd;
        try
        {
            fd = await process.OpenAsync("/dev/hostowner", NinePConstants.OWRITE | NinePConstants.OCEXEC);
        }
        catch (SyscallException)
        {
            return;
        }

        try
        {
            await process.WriteAsync(fd, Encoding.UTF8.GetBytes(owner));
        }
        catch (SyscallException error)
        {
            await PrintAsync($"factotum: setting /dev/hostowner to {Libc.Quote(owner)}: {error.Message}\n");
        }

        await process.CloseAsync(fd);
    }

    private async Task FsattachAsync(Req r)
    {
        string aname = ((Tattach)r.Ifcall).Aname;
        if (aname is not ("factotum" or ""))
        {
            await fs.RespondAsync(r, "unknown mount spec");
            return;
        }

        r.Fid!.Qid = Mkqid(QidType.QTDIR, aname.Length == 0 ? Qroot : Qfactotum);
        r.Qid = r.Fid.Qid;
        await fs.RespondAsync(r, null);
    }

    private string? Fswalk1(Fid fid, string name)
    {
        switch ((int)fid.Qid.Path)
        {
            case Qroot when name == "factotum":
                fid.Qid = Mkqid(QidType.QTDIR, Qfactotum);
                return null;
            case Qroot when name == "..":
                return null;
            case Qroot:
                return "not found";
            case Qfactotum when Array.FindIndex(Dirtab, d => d.Name == name) is int i and >= 0:
                fid.Qid = Mkqid(QidType.QTFILE, Dirtab[i].Qidpath);
                return null;
            case Qfactotum when name == "..":
                fid.Qid = Mkqid(QidType.QTDIR, Qroot);
                return null;
            case Qfactotum:
                return "not found";

            // Stryker disable once Statement,String : lib9p walks only from a directory, and factotum has two
            default:
                return "cannot happen";
        }
    }

    private async Task<Stat> FillstatAsync(string name, QidType type, int path, uint perm)
    {
        uint now = (uint)(await Syslog.TimeAsync(Srv.Up));
        return new Stat(0, 0, 0, Mkqid(type, path), perm, now, now, 0, name, Owner, Owner, string.Empty);
    }

    private async Task FsstatAsync(Req r)
    {
        int path = (int)r.Fid!.Qid.Path;
        int i = Array.FindIndex(Dirtab, d => d.Qidpath == path);
        if (path == Qroot)
        {
            r.D = await FillstatAsync("/", QidType.QTDIR, Qroot, 0b101_101_101 | Dmdir);
        }
        else if (path == Qfactotum)
        {
            r.D = await FillstatAsync("factotum", QidType.QTDIR, Qfactotum, 0b101_101_101 | Dmdir);
        }
        else
        {
            r.D = await FillstatAsync(Dirtab[i].Name, QidType.QTFILE, Dirtab[i].Qidpath, Dirtab[i].Perm);
        }

        await fs.RespondAsync(r, null);
    }

    // fsopen: the owner's bits for factotum's owner and the others' for anyone else; a file of
    // DMEXCL open once at a time.
    private async Task FsopenAsync(Req r)
    {
        int[] need = [4, 2, 6, 1];
        byte mode = ((Topen)r.Ifcall).Mode;
        int i = Array.FindIndex(Dirtab, d => d.Qidpath == (int)r.Fid!.Qid.Path);

        // Stryker disable once Bitwise : a uint's bits shift alike either way
        uint perm = i < 0 ? 5 : r.Fid!.Uid == Owner ? Dirtab[i].Perm >> 6 : Dirtab[i].Perm;
        int n = need[mode & 3];
        if ((mode & ~(3 | NinePConstants.OTRUNC)) != 0 || (perm & n) != n)
        {
            await fs.RespondAsync(r, "permission denied");
            return;
        }

        if (i >= 0 && (Dirtab[i].Perm & Dmexcl) != 0)
        {
            lock (inuse)
            {
                if (inuse[i] != 0)
                {
                    i = -2;
                }
                else
                {
                    // Stryker disable once Update : one opening counted down is as far from none as one counted up
                    inuse[i]++;
                }
            }

            if (i == -2)
            {
                await fs.RespondAsync(r, "file in use");
                return;
            }
        }

        r.Fid!.Aux = new Fsstate { Sysuser = r.Fid.Uid };
        await fs.RespondAsync(r, null);
    }

    private void Fsdestroyfid(Fid fid)
    {
        // Stryker disable once Bitwise : only a DMEXCL file's count is ever looked at, so any other's set to none is unseen
        if (fid.Omode != -1 && Array.FindIndex(Dirtab, d => d.Qidpath == (int)fid.Qid.Path && (d.Perm & Dmexcl) != 0) is int i and >= 0)
        {
            lock (inuse)
            {
                inuse[i] = 0;
            }
        }

        // Stryker disable once Statement,Block : a protocol's close frees only what its conversation held, which nothing else sees
        if (fid.Aux is Fsstate { Ps: not null } fss)
        {
            fss.Proto!.Close(fss);
        }
    }

    // readlist: the next line of a listing, or none at its end; one too long to fit is an error.
    private string? Readlist(Fsstate s, Func<int, int, (int N, byte[] Line)> gen, Req r)
    {
        (int n, byte[] line) = gen(s.Listoff, (int)r.Count);
        if (n == -1)
        {
            return Etoosmall;
        }

        if (n != 0)
        {
            s.Listoff++;
        }

        r.Data = line;
        return null;
    }

    // keylist: the i-th key, as ctl lists it, its secrets shown only by name.
    private (int N, byte[] Line) Keylist(Fsstate fss, int i, int n)
    {
        var ki = new Keyinfo(fss, null) { Attr = null, Noconf = true, Skip = i, Usedisabled = true };
        if (Findkey(out Key? k, ki, null) != Rpc.Ok)
        {
            return (0, []);
        }

        byte[] all = Encoding.UTF8.GetBytes($"key {Attr.Format(k!.Attr)} {Attrnamefmt(k.Privattr)}\n");
        byte[] a = all[..Math.Min(all.Length, n - 1)];

        // Stryker disable once Equality : a line cut to n - 1 is always more than n - 4 long
        return a.Length + 4 >= n && !a.Contains((byte)'\n') ? (-1, []) : (a.Length, a);
    }

    private (int N, byte[] Line) Protolist(int i, int n)
    {
        if (i >= Prototab.Length)
        {
            return (0, []);
        }

        byte[] name = Encoding.UTF8.GetBytes(Prototab[i].Name + "\n");
        return name.Length > n ? (-1, []) : (name.Length, name);
    }

    // fsrpcio: an rpc's read or write, the server reading other requests while it runs.
    private async Task FsrpcioAsync(Req r)
    {
        var fss = (Fsstate)r.Fid!.Aux!;
        await fs.ReleaseAsync();
        await fss.Lock.WaitAsync();
        try
        {
            if (r.Ifcall is Tread)
            {
                await RpcreadAsync(r);
            }
            else
            {
                await RpcwriteAsync(r);
            }
        }
        finally
        {
            fss.Lock.Release();
        }

        await fs.AcquireAsync();
    }

    private async Task FsreadAsync(Req r)
    {
        var s = (Fsstate?)r.Fid!.Aux;
        switch ((int)r.Fid.Qid.Path)
        {
            case Qroot:
                Stat factotum = await FillstatAsync("factotum", QidType.QTDIR, Qfactotum, Dmdir | 0b101_101_101);
                Srv.Dirread(r, n => n > 0 ? null : factotum);
                await fs.RespondAsync(r, null);
                break;
            case Qfactotum:
                var entries = new List<Stat>();
                foreach (var d in Dirtab)
                {
                    entries.Add(await FillstatAsync(d.Name, QidType.QTFILE, d.Qidpath, d.Perm));
                }

                Srv.Dirread(r, n => n < entries.Count ? entries[n] : null);
                await fs.RespondAsync(r, null);
                break;
            case Qrpc:
                await FsrpcioAsync(r);
                break;
            case Qneedkey:
                Logbufread(needkeybuf, r);
                break;
            case Qconfirm:
                Logbufread(confbuf, r);
                break;
            case Qlog:
                Logbufread(logbuf, r);
                break;
            case Qctl:
                await fs.RespondAsync(r, Readlist(s!, (i, n) => Keylist(s!, i, n), r));
                break;
            default:
                await fs.RespondAsync(r, Readlist(s!, Protolist, r));
                break;
        }
    }

    private async Task FswriteAsync(Req r)
    {
        int path = (int)r.Fid!.Qid.Path;
        if (path == Qrpc)
        {
            await FsrpcioAsync(r);
            return;
        }

        string s = Encoding.UTF8.GetString(r.Data);
        int ret = path switch
        {
            Qneedkey => await NeedkeywriteAsync(s),
            Qconfirm => await ConfirmwriteAsync(s),
            _ => Ctlwrite(s, false),
        };
        if (ret < 0)
        {
            await fs.RespondAsync(r, Errstr);
            return;
        }

        r.Rcount = r.Count;
        await fs.RespondAsync(r, null);
    }

    private async Task FsflushAsync(Req r)
    {
        await FlushwaitAsync(cusewait, confbuf, r.Oldreq!);
        await FlushwaitAsync(needwait, needkeybuf, r.Oldreq!);
        await LogbufflushAsync(logbuf, r.Oldreq!);
        await fs.RespondAsync(r, null);
    }

    // confirmflush and needkeyflush: a waiting rpc or read flushed is interrupted.
    private async Task FlushwaitAsync(List<Req> waiting, Logbuf lb, Req r)
    {
        bool found;
        lock (lb.Lock)
        {
            found = waiting.Remove(r);
        }

        // Stryker disable once Statement : a request waiting on the list waits in no log, so the log's flush finds nothing
        if (found)
        {
            await fs.RespondAsync(r, "interrupted");
            return;
        }

        await LogbufflushAsync(lb, r);
    }

    private async Task LogbufflushAsync(Logbuf lb, Req r)
    {
        bool found;
        lock (lb.Lock)
        {
            Req[] kept = [.. lb.Wait.Where(w => w != r)];
            found = kept.Length != lb.Wait.Count;
            lb.Wait.Clear();
            foreach (Req w in kept)
            {
                lb.Wait.Enqueue(w);
            }
        }

        if (found)
        {
            await fs.RespondAsync(r, "interrupted");
        }
    }

    private void Logbufread(Logbuf lb, Req r)
    {
        lock (lb.Lock)
        {
            lb.Wait.Enqueue(r);
        }
    }

    // logbufproc: each waiting read given the oldest message, cut with ... to fit it.
    private async Task LogbufprocAsync(Logbuf lb)
    {
        while (true)
        {
            Req r;
            string s;
            lock (lb.Lock)
            {
                if (lb.Wait.Count == 0 || lb.Rp == lb.Wp)
                {
                    return;
                }

                r = lb.Wait.Dequeue();
                if (r.Count < 5)
                {
                    // Stryker disable once String : a read too short is refused without the message, which is left for the next
                    s = string.Empty;
                }
                else
                {
                    s = lb.Msg[lb.Rp]!;
                    lb.Msg[lb.Rp] = null;
                    lb.Rp = (lb.Rp + 1) % lb.Msg.Length;
                }
            }

            if (r.Count < 5)
            {
                await fs.RespondAsync(r, "factotum: read request count too short");
                continue;
            }

            byte[] msg = Encoding.UTF8.GetBytes(s);
            int n = (int)r.Count;
            if (n < msg.Length + 1 + 1)
            {
                // Back past the last byte, and then any rune it ends, to the first byte of one.
                n -= 5;
                while (n > 0 && (msg[--n] & 0xC0) == 0x80)
                {
                }

                r.Data = [.. msg[..n], .. "...\n"u8];
            }
            else
            {
                r.Data = [.. msg, (byte)'\n'];
            }

            await fs.RespondAsync(r, null);
        }
    }

    // logbufappend: the message kept, the oldest let go when there are 128; the reads waiting for
    // one are answered as the request that logged it ends.
    private void Logbufappend(Logbuf lb, string buf)
    {
        if (Debug)
        {
            lock (debugged)
            {
                debugged.Add(buf);
            }
        }

        lock (lb.Lock)
        {
            lb.Msg[lb.Wp] = buf;
            lb.Wp = (lb.Wp + 1) % lb.Msg.Length;
        }
    }

    // A request handled, then what it logged while debugging printed on the error output, and the
    // reads waiting on what it logged answered.
    private async Task LoggedAsync(Task handled)
    {
        // Stryker disable once Statement : each handler logs before it first waits, as Fog's pipe writes never wait, so the log is read alike whether or not the handler is awaited first
        await handled;
        string[] lines;
        lock (debugged)
        {
            lines = [.. debugged];
            debugged.Clear();
        }

        foreach (string line in lines)
        {
            await Srv.Up.WriteAsync(2, Encoding.UTF8.GetBytes(line + "\n"));
        }

        await LogbufprocAsync(logbuf);
        await LogbufprocAsync(confbuf);
        await LogbufprocAsync(needkeybuf);
    }

    // rpcwrite: the verb and its argument kept for the read that runs them.
    private async Task RpcwriteAsync(Req r)
    {
        if (r.Count >= Maxrpc)
        {
            await fs.RespondAsync(r, Etoolarge);
            return;
        }

        var fss = (Fsstate)r.Fid!.Aux!;
        if (fss.Pending)
        {
            await fs.RespondAsync(r, "rpc already pending; read to clear");
            return;
        }

        byte[] buf = r.Data;
        int nul = Array.IndexOf(buf, (byte)0);

        // Stryker disable once Equality : a verb of nothing, as a NUL or space first gives, is unknown however it is cut
        int sp = Array.IndexOf(buf, (byte)' ', 0, nul < 0 ? buf.Length : nul);

        // Stryker disable once Equality : a verb of nothing, as a NUL or space first gives, is unknown however it is cut
        fss.Verb = Encoding.UTF8.GetString(buf, 0, sp >= 0 ? sp : nul >= 0 ? nul : buf.Length);

        // Stryker disable once Equality : a verb of nothing, as a NUL or space first gives, is unknown however it is cut
        fss.Arg = sp >= 0 ? buf[(sp + 1)..] : [];
        fss.Iverb = Array.IndexOf(Rpctab, fss.Verb);
        r.Rcount = r.Count;
        fss.Pending = true;
        await fs.RespondAsync(r, null);
    }

    private async Task RetstringAsync(Req r, Fsstate fss, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        r.Data = bytes[..Math.Min(bytes.Length, (int)r.Count)];
        fss.Pending = false;
        await fs.RespondAsync(r, null);
    }

    // retrpc: the rpc's outcome as its read returns it; one waiting for a key or a confirmation
    // is answered when that comes.
    private async Task RetrpcAsync(Req r, Rpc ret, Fsstate fss)
    {
        switch (ret)
        {
            case Rpc.Errstr:
                await RetstringAsync(r, fss, $"error {Errstr}");
                break;
            case Rpc.Failure:
                await RetstringAsync(r, fss, $"error {fss.Err}");
                break;
            case Rpc.Needkey:
                if (!Needkeyqueue(r, fss))
                {
                    await RetstringAsync(r, fss, $"needkey {fss.Keyinfo}");
                }

                break;
            case Rpc.Ok:
                await RetstringAsync(r, fss, "ok");
                break;
            case Rpc.Toosmall:
                await RetstringAsync(r, fss, $"toosmall {fss.Nwant}");
                break;
            case Rpc.Phase:
                await RetstringAsync(r, fss, $"phase {Errstr}");
                break;
            default:
                await ConfirmqueueAsync(r, fss);
                break;
        }
    }

    private async Task<bool> RdwrcheckAsync(Req r, Fsstate fss)
    {
        string? why = fss.Ps is null ? "error no current protocol" : null;

        // Stryker disable once Conditional,Equality,String : each protocol's init leaves it past Notstarted
        why ??= fss.Phase == Fsstate.Notstarted ? "protocol not started" : null;

        // Stryker disable once Conditional,Equality,String : no protocol of factotum's breaks its conversation
        why ??= fss.Phase == Fsstate.Broken ? $"error {fss.Err}" : null;

        // Stryker disable once Conditional,String : each protocol done has its authinfo
        string done = fss.Haveai ? "done haveai" : "done";
        why ??= fss.Phase == Fsstate.Established ? done : null;
        if (why is not null)
        {
            await RetstringAsync(r, fss, why);
        }

        return why is null;
    }

    // rpcread: the pending rpc run, and its answer read.
    private async Task RpcreadAsync(Req r)
    {
        if (r.Count < 64)
        {
            await fs.RespondAsync(r, "rpc read too small");
            return;
        }

        var fss = (Fsstate)r.Fid!.Aux!;
        if (!fss.Pending)
        {
            await fs.RespondAsync(r, "no rpc pending");
            return;
        }

        switch (fss.Iverb)
        {
            case Vstart:
                await StartAsync(r, fss);
                break;
            case Vread:
                if (fss.Arg.Length != 0 && fss.Arg[0] != 0)
                {
                    await RetstringAsync(r, fss, "error read needs no parameters");
                }
                else if (await RdwrcheckAsync(r, fss))
                {
                    int ophase = fss.Phase;
                    (Rpc ret, byte[] a) = await fss.Proto!.ReadAsync(this, fss, (int)r.Count - 3);
                    Rpcrdwrlog(fss, "read", a.Length, ophase, ret);
                    if (ret == Rpc.Ok)
                    {
                        // Stryker disable once Conditional,Equality : each protocol's read gives something
                        r.Data = a.Length == 0 ? "ok"u8.ToArray() : [.. "ok "u8, .. a];
                        fss.Pending = false;
                        await fs.RespondAsync(r, null);
                    }
                    else
                    {
                        await RetrpcAsync(r, ret, fss);
                    }
                }

                break;
            case Vwrite:
                if (await RdwrcheckAsync(r, fss))
                {
                    int ophase = fss.Phase;
                    Rpc ret = await fss.Proto!.WriteAsync(this, fss, fss.Arg);
                    Rpcrdwrlog(fss, "write", fss.Arg.Length, ophase, ret);
                    await RetrpcAsync(r, ret, fss);
                }

                break;
            case Vauthinfo:
                await AuthinfoAsync(r, fss);
                break;
            case Vattr:
                await RetstringAsync(r, fss, $"ok {Attr.Format(fss.Attr)}");
                break;
            default:
                await RetstringAsync(r, fss, "error unknown verb");
                break;
        }
    }

    private async Task StartAsync(Req r, Fsstate fss)
    {
        if (fss.Phase != Fsstate.Notstarted)
        {
            Flog($"{fss.Seqnum}: implicit close due to second start; old attr '{Attr.Format(fss.Attr)}'");
            if (fss.Proto is not null && fss.Ps is not null)
            {
                fss.Proto.Close(fss);
            }

            (fss.Ps, fss.Proto, fss.Attr, fss.Phase) = (null, null, null, Fsstate.Notstarted);
        }

        int nul = Array.IndexOf(fss.Arg, (byte)0);
        Attr? attr = Attr.Parse(Encoding.UTF8.GetString(fss.Arg, 0, nul < 0 ? fss.Arg.Length : nul));
        if (Attr.StrFind(attr, "proto") is not { } p)
        {
            await RetstringAsync(r, fss, "error did not specify proto");
            return;
        }

        if (Findproto(p) is not { } proto)
        {
            await RetstringAsync(r, fss, $"error unknown protocol {Libc.Quote(p)}");
            return;
        }

        (fss.Attr, fss.Proto, fss.Seqnum) = (attr, proto, ++seqnum);
        Rpc ret = await proto.InitAsync(this, fss);
        Rpcstartlog(attr, fss, ret);
        if (ret != Rpc.Ok)
        {
            (fss.Attr, fss.Phase) = (null, Fsstate.Notstarted);
        }

        await RetrpcAsync(r, ret, fss);
    }

    private async Task AuthinfoAsync(Req r, Fsstate fss)
    {
        if (fss.Phase != Fsstate.Established)
        {
            await RetstringAsync(r, fss, "error authentication unfinished");
            return;
        }

        // Stryker disable once all : each protocol done has its authinfo
        if (!fss.Haveai)
        {
            await RetstringAsync(r, fss, "error no authinfo available");
            return;
        }

        fss.Ai.Cap = await MkcapAsync(r.Fid!.Uid, fss.Ai.Suid);
        byte[]? e = ConvAI2M(fss.Ai, (int)r.Count - 3);
        fss.Ai.Cap = null;
        if (e is null)
        {
            await RetstringAsync(r, fss, "error read too small");
            return;
        }

        r.Data = [.. "ok "u8, .. e];
        fss.Pending = false;
        await fs.RespondAsync(r, null);
    }

    // confirmqueue: each key still to be confirmed asked about on confirm, and the rpc kept until
    // an answer comes.
    private async Task ConfirmqueueAsync(Req r, Fsstate fss)
    {
        if (inuse[0] == 0)
        {
            await fs.RespondAsync(r, "confirm is closed");
            return;
        }

        Conf[] asked = [.. fss.Conf.Where(c => c.Canuse == -1)];

        // Stryker disable once Statement,String,Block : findkey asks for a confirmation only once it has added one still to ask about
        if (asked.Length == 0)
        {
            await fs.RespondAsync(r, "no confirmations to wait for (bug)");
            return;
        }

        foreach (Conf c in asked)
        {
            Logbufappend(confbuf, $"confirm tag={c.Tag} {Attr.Format(c.Key.Attr)}");
        }

        lock (confbuf.Lock)
        {
            cusewait.Add(r);
        }
    }

    // confirmwrite: tag=... answer=yes or no for a key, and the rpc that waits for it run again.
    private async Task<int> ConfirmwriteAsync(string s)
    {
        Attr? a = Attr.Parse(s);
        if (a is null)
        {
            Errstr = "empty write";
            return -1;
        }

        if (Attr.StrFind(a, "tag") is not { } t)
        {
            Errstr = "no tag";
            return -1;
        }

        ulong tag = Libc.Strtoul(t, 0, out _);
        string? ans = Attr.StrFind(a, "answer");
        if (ans is null || ans is not ("yes" or "no"))
        {
            Errstr = ans is null ? "no answer" : "bad answer";
            return -1;
        }

        await fs.ReleaseAsync();
        Req? r;
        Conf? conf = null;
        lock (confbuf.Lock)
        {
            r = cusewait.Find(w => (conf = ((Fsstate)w.Fid!.Aux!).Conf.Find(c => c.Tag == tag)) is not null);
            if (r is not null)
            {
                cusewait.Remove(r);
            }
        }

        if (r is null)
        {
            await fs.AcquireAsync();
            Errstr = "tag not found";
            return -1;
        }

        var fss = (Fsstate)r.Fid!.Aux!;
        await fss.Lock.WaitAsync();
        conf!.Canuse = ans == "yes" ? 1 : 0;
        await RpcreadAsync(r);
        fss.Lock.Release();
        await fs.AcquireAsync();
        return 0;
    }

    // needkeyqueue: the rpc kept, and needkey told what key it waits for, when needkey is open.
    private bool Needkeyqueue(Req r, Fsstate fss)
    {
        if (inuse[1] == 0)
        {
            return false;
        }

        Logbufappend(needkeybuf, $"needkey tag={r.Tag} {fss.Keyinfo}");
        lock (needkeybuf.Lock)
        {
            needwait.Add(r);
        }

        return true;
    }

    // needkeywrite: tag=... once the key is added, or with error=..., and the rpc run again or failed.
    private async Task<int> NeedkeywriteAsync(string s)
    {
        Attr? a = Attr.Parse(s);
        if (a is null)
        {
            Errstr = "empty write";
            return -1;
        }

        if (Attr.StrFind(a, "tag") is not { } t)
        {
            Errstr = "no tag";
            return -1;
        }

        ulong tag = Libc.Strtoul(t, 0, out _);
        Req? r;
        lock (needkeybuf.Lock)
        {
            r = needwait.Find(w => w.Tag == tag);
            if (r is not null)
            {
                needwait.Remove(r);
            }
        }

        if (r is null)
        {
            Errstr = "tag not found";
            return -1;
        }

        var fss = (Fsstate)r.Fid!.Aux!;
        await fs.ReleaseAsync();
        await fss.Lock.WaitAsync();
        if (Attr.StrFind(a, "error") is { } error)
        {
            Errstr = error;
            await RetrpcAsync(r, Rpc.Errstr, fss);
        }
        else
        {
            await RpcreadAsync(r);
        }

        fss.Lock.Release();
        await fs.AcquireAsync();
        return 0;
    }

    // fs.c's flags that the program's start looks at.
    private sealed class Flags
    {
        public bool Secstore { get; set; } = true;

        public bool Private { get; set; } = true;

        public bool G { get; set; }

        public bool K { get; set; }

        public bool U { get; set; }

        public string? Mtpt { get; set; }

        public string? Service { get; set; }
    }
}
