using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// ndb/cs, as 9front's cs.c: a file server on a pipe, mounted after the network's directory, whose
// cs file translates a network address into the clone file to open and the address to give it. A
// lookup that asks dns goes on in a slave process, so the master reads the next request; Fog has
// no ndb/dns, so dnsquery asks the host's resolver, and the interfaces are the host's. -d logs
// what cs does, but not each 9P message, as Fog has no fcallfmt to print them.
public sealed class Cs(string argv0)
{
    private const int Nreply = 20;
    private const int Maxreply = 256;
    private const int Maxrequest = 128;
    private const int Maxpath = 128;
    private const int Maxfdata = 8192;
    private const int Maxhost = 64;
    private const int Maxservice = 64;
    private const int Maxactive = 200;
    private const int Maxattr = 32;
    private const int Errmax = 128;
    private const int Iohdrsz = 24;
    private const int Maxwelem = 16;
    private const ulong Qdir = 0;
    private const ulong Qcs = 1;
    private const int V4 = 1;
    private const int V6 = 2;
    private const int Ntcp = 1;
    private const string Logfile = "cs";

    private readonly Dictionary<uint, Mfile> fids = [];
    private readonly List<Job> joblist = [];
    private readonly SemaphoreSlim joblock = new(1, 1);
    private readonly SemaphoreSlim dblock = new(1, 1);
    private readonly SemaphoreSlim netlock = new(1, 1);
    private readonly SemaphoreSlim ipifclock = new(1, 1);

    // Networks, ordered by preference; net doesn't apply to (r)udp, icmp(v6) or telco.
    private readonly Network[] network =
    [
        new("il", false, false, true, V4 | V6),
        new("tcp", false, false, false, V4 | V6),
        new("il", false, false, false, V4 | V6),
        new("udp", false, true, false, V4 | V6),
        new("icmp", false, true, false, V4),
        new("icmpv6", false, true, false, V6),
        new("rudp", false, true, false, V4),
        new("ssh", false, true, false, V4 | V6),
        new("telco", true, true, false, 0),
    ];

    private uint vers;
    private int mfd;
    private bool debug;
    private long active;
    private string? dbfile;
    private Ndb? db;
    private string? csuser;
    private string mntpt = "/net";
    private IReadOnlyList<Ipifc> ipifcs = [];
    private int confipvers;
    private int dnsipvers;
    private int lookipvers = V4 | V6;
    private string? mysysname;
    private Network? netlist;
    private Network? last;

    // The network's own database.
    private string Netndb => $"{mntpt}/ndb";

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Cs(argv[0]).RunAsync(process, argv);

    // %N: the tuples of t's line.
    internal static string NdbLine(NdbTuple? t)
    {
        var f = new StringBuilder();
        string sep = string.Empty;
        for (; t is not null; t = t.Entry)
        {
            f.Append(CultureInfo.InvariantCulture, $"{sep}{t.Attr}={Ndb.ValFmt(t.Val)}");
            if (t.Line != t.Entry)
            {
                break;
            }

            sep = " ";
        }

        return f.ToString();
    }

    // getfields with mflag: the runs of characters not in the set; the last of max runs to the end.
    internal static List<string> Getfields(string str, int max, string set)
    {
        var fields = new List<string>();
        int start = 0;
        bool intok = false;
        for (int i = 0; i < str.Length; i++)
        {
            if (set.Contains(str[i], StringComparison.Ordinal))
            {
                if (fields.Count + (intok ? 1 : 0) >= max)
                {
                    break;
                }

                if (intok)
                {
                    fields.Add(str[start..i]);
                }

                intok = false;
            }
            else if (!intok)
            {
                start = i;
                intok = true;
            }
        }

        if (intok)
        {
            fields.Add(str[start..]);
        }

        return fields;
    }

    // setext: the mount point with '_'s for '/'s.
    private static string Setext(string p) => p.Replace('/', '_');

    private static string Nstrcpy(string from, int len) => from.Length < len ? from : from[..(len - 1)];

    // ndbline(ndbnew(attr, val)): a tuple that is its own line.
    private static NdbTuple Ndbnew(string attr, string val)
    {
        var t = new NdbTuple(attr, val);
        t.Line = t;
        return t;
    }

    private static bool Lookforproto(NdbTuple? t, string proto)
    {
        for (; t is not null; t = t.Entry)
        {
            if (t.Attr == "proto" && t.Val == proto)
            {
                return true;
            }
        }

        return false;
    }

    // qmatch: whether the entry holds each pair after the first, any value for a *.
    private static bool Qmatch(NdbTuple t, List<string> attr, List<string> val, int n)
        => Enumerable.Range(1, n - 1).All(i => t.Entries().Any(nt => attr[i] == nt.Attr && (val[i] == "*" || val[i] == nt.Val)));

    private static void Qreply(Mfile mf, NdbTuple? t)
    {
        while (mf.Reply.Count < Nreply && t is not null)
        {
            mf.Reply.Add(Encoding.UTF8.GetBytes(NdbLine(t)));

            // Skip to the next line.
            do
            {
                NdbTuple? l = t.Line;
                t = t.Entry;
                if (t != l)
                {
                    break;
                }
            }
            while (t is not null);
        }
    }

    private static async Task<long> TimeAsync(Process process)
    {
        int fd = await process.OpenAsync("/dev/bintime", NinePConstants.OREAD);
        try
        {
            return BinaryPrimitives.ReadInt64BigEndian((await process.PReadAsync(fd, 8, 0)).Span) / 1_000_000_000;
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    private static async Task ProcsetnameAsync(Process process, string name)
    {
        try
        {
            int fd = await process.OpenAsync($"/proc/{process.Pid}/args", NinePConstants.OWRITE);
            try
            {
                await process.WriteAsync(fd, Encoding.UTF8.GetBytes(name));
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException)
        {
            // procsetname's failure is not cs's.
        }
    }

    private static async Task<string?> GetenvAsync(Process process, string name)
    {
        string? value = null;
        try
        {
            int fd = await process.OpenAsync($"/env/{name}", NinePConstants.OREAD);
            try
            {
                var all = new List<byte>();
                for (ReadOnlyMemory<byte> block; !(block = await process.ReadAsync(fd, 8192)).IsEmpty;)
                {
                    all.AddRange(block.Span);
                }

                value = Encoding.UTF8.GetString([.. all]).TrimEnd('\0');
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException)
        {
            // getenv gives nil for a variable that is not there.
        }

        return value;
    }

    // readn: n bytes, or fewer at the end.
    private static async Task<byte[]> ReadnAsync(Process process, int fd, int n)
    {
        var all = new List<byte>();
        for (ReadOnlyMemory<byte> more; all.Count < n && !(more = await process.ReadAsync(fd, n - all.Count)).IsEmpty;)
        {
            all.AddRange(more.Span);
        }

        return [.. all];
    }

    // read9pmsg: one whole message, or none at the end; a length that cannot be a message's is an error.
    private static async Task<byte[]> Read9pmsgAsync(Process process, int fd, int max)
    {
        byte[] count = await ReadnAsync(process, fd, 4);
        if (!BinaryPrimitives.TryReadUInt32LittleEndian(count, out uint len))
        {
            return [];
        }

        if (len <= 4 || len > max)
        {
            throw new SyscallException("bad length in 9P2000 message header");
        }

        byte[] rest = await ReadnAsync(process, fd, (int)len - 4);
        return rest.Length < len - 4 ? [] : [.. count, .. rest];
    }

    // convM2S: the request, or null for one cs cannot read.
    private static ISerializable? ConvM2S(byte[] m)
    {
        try
        {
            return (MessageTypes)m[4] switch
            {
                MessageTypes.Tversion => new Tversion(m),
                MessageTypes.Tauth => new Tauth(m),
                MessageTypes.Tflush => new Tflush(m),
                MessageTypes.Tattach => new Tattach(m),
                MessageTypes.Twalk => new Twalk(m),
                MessageTypes.Topen => new Topen(m),
                MessageTypes.Tcreate => new Tcreate(m),
                MessageTypes.Tread => new Tread(m),
                MessageTypes.Twrite => new Twrite(m),
                MessageTypes.Tclunk => new Tclunk(m),
                MessageTypes.Tremove => new Tremove(m),
                MessageTypes.Tstat => new Tstat(m),
                MessageTypes.Twstat => new Twstat(m),
                _ => null,
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A message too short for its type.
        }

        return null;
    }

    private static uint FidOf(ISerializable request) => request switch
    {
        Tattach r => r.Fid,
        Twalk r => r.Fid,
        Topen r => r.Fid,
        Tcreate r => r.Fid,
        Tread r => r.Fid,
        Twrite r => r.Fid,
        Tclunk r => r.Fid,
        Tremove r => r.Fid,
        Tstat r => r.Fid,
        Twstat r => r.Fid,
        _ => 0,
    };

    // The reply holding the offset, and the offset it starts at; -1 when the replies end before it.
    private static int FindReply(Mfile mf, long off, out long toff)
    {
        toff = 0;
        for (int i = 0; i < mf.Reply.Count; i++)
        {
            if (off < toff + mf.Reply[i].Length)
            {
                return i;
            }

            toff += mf.Reply[i].Length;
        }

        return -1;
    }

    private static byte[] ConvS2M(ISerializable message)
    {
        byte[] bytes = new byte[message.Size];
        message.WriteTo(bytes);
        return bytes;
    }

    // setnetmtpt: the mount point, /net by default; one not rooted is a suffix of /net.
    private static string Setnetmtpt(string x) => Nstrcpy(x.StartsWith('/') ? x : $"/net{x}", Maxpath);

    private static long Atoi(string s) => Libc.Strtol(s, 10, out _);

    // The v4 and then the v6 addresses dns gives, and the error of the last query that gave none.
    private static async Task<(NdbTuple? Answer, string? Error)> DnsAddressesAsync(Process process, string name, int ipvers)
    {
        (NdbTuple? t, string? error) = (ipvers & V4) != 0 ? await Ndb.DnsQueryAsync(process, name, "ip") : (null, null);
        if ((ipvers & V6) != 0)
        {
            (NdbTuple? t6, string? error6) = await Ndb.DnsQueryAsync(process, name, "ipv6");
            (t, error) = t6 is null ? (t, error6) : (Ndb.Concatenate(t, t6), error);
        }

        return (t, error);
    }

    private async Task RunAsync(Process process, IReadOnlyList<string> argv)
    {
        bool justsetname = false;
        string ext = string.Empty;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case '4':
                    lookipvers = V4;
                    break;
                case '6':
                    lookipvers = V6;
                    break;
                case 'd':
                    debug = true;
                    break;
                case 'f':
                    dbfile = await arguments.EargfAsync(() => UsageAsync(process));
                    break;
                case 'n':
                    justsetname = true;
                    break;
                case 'x':
                    mntpt = Setnetmtpt(await arguments.EargfAsync(() => UsageAsync(process)));
                    ext = Setext(mntpt);
                    break;

                    // cs.c takes no other option, and ignores one it does not know.
            }
        }

        var master = new Proc(process);
        await NdbinitAsync(master);
        await NetinitAsync(master);

        if (!justsetname)
        {
            string servefile = $"/srv/cs{ext}";
            try
            {
                await process.UnmountAsync(servefile, mntpt);
            }
            catch (SyscallException)
            {
                // Nothing of an earlier cs was mounted.
            }

            try
            {
                await process.RemoveAsync(servefile);
            }
            catch (SyscallException)
            {
                // Nor posted.
            }

            await process.RforkAsync(RforkFlags.Rend | RforkFlags.Noteg);
            csuser = process.User;
            await MountinitAsync(master, servefile);
        }

        process.Exits(null);
    }

    private async Task<string> UsageAsync(Process process)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"usage: {argv0} [-46dn] [-f ndb-file] [-x netmtpt]\n"));
        process.Exits("usage");
        return default!;
    }

    // mountinit: /srv/cs posted, and the pipe mounted after the mount point by the parent, which
    // then exits; cs serves the other end in a child.
    private async Task MountinitAsync(Proc ctx, string service)
    {
        Process process = ctx.Process;
        (int p0, int p1) = await TryAsync(ctx, () => process.PipeAsync().AsTask(), "pipe failed");
        int f = await TryAsync(ctx, () => process.CreateAsync(service, NinePConstants.OWRITE | NinePConstants.ORCLOSE, 0b110_110_110).AsTask(), service);

        // Stryker disable once String : a file just created in /srv takes the number of any open descriptor, so this write does not fail
        await TryAsync(ctx, () => process.WriteAsync(f, Encoding.UTF8.GetBytes(p1.ToString(CultureInfo.InvariantCulture))).AsTask(), "write /srv/cs");

        mfd = p0;
        process.Fork(RforkFlags.Fdg | RforkFlags.Proc | RforkFlags.Nameg, async child =>
        {
            await child.CloseAsync(p1);
            await ProcsetnameAsync(child, mntpt);
            for (Ndb? d = db; d is not null; d = d.Next)
            {
                d.Process = child;
            }

            await IoAsync(new Proc(child));
        });

        // Put ourselves into the file system.
        await process.CloseAsync(p0);
        await TryAsync(
            ctx,
            async () =>
            {
                // Stryker disable once String : cs's attach, as cs.c's, does not look at the aname
                await process.MountAsync(p1, -1, mntpt, MountFlags.After, string.Empty);
                return 0;
            },
            "mount failed");
    }

    private async Task<T> TryAsync<T>(Proc ctx, Func<Task<T>> call, string what)
    {
        try
        {
            return await call();
        }
        catch (SyscallException error)
        {
            ctx.Errstr = error.Message;
            return await ErrorAsync<T>(ctx, what);
        }
    }

    private async Task NdbinitAsync(Proc ctx)
    {
        (db, string? error) = await Ndb.OpenAsync(ctx.Process, dbfile);
        if (db is null)
        {
            ctx.Errstr = error!;
            await ErrorAsync<bool>(ctx, "can't open network database");
        }

        for (Ndb? netdb = db; netdb is not null; netdb = netdb.Next)
        {
            if (netdb.File == Netndb)
            {
                return;
            }
        }

        if ((await Ndb.OpenAsync(ctx.Process, Netndb)).Db is { } nd)
        {
            db = Ndb.Cat(nd, db);
        }
    }

    // newfid: the fid in use, or a new one; cs.c reuses the memory of one no longer in use.
    private Mfile Newfid(uint fid)
    {
        if (!fids.TryGetValue(fid, out Mfile? mf) || !mf.Busy)
        {
            fids[fid] = mf = new Mfile();
        }

        return mf;
    }

    private async Task<Job> NewjobAsync(ISerializable request)
    {
        var job = new Job(request);
        await joblock.WaitAsync();
        joblist.Insert(0, job);
        joblock.Release();
        return job;
    }

    private async Task FreejobAsync(Job job)
    {
        await joblock.WaitAsync();
        joblist.Remove(job);
        joblock.Release();
    }

    private async Task FlushjobAsync(ushort tag)
    {
        await joblock.WaitAsync();
        if (joblist.FirstOrDefault(job => job.Request.Tag == tag && job.Request is not Tflush) is { } j)
        {
            j.Flushed = true;
        }

        joblock.Release();
    }

    private async Task IoAsync(Proc master)
    {
        while (true)
        {
            byte[] mdata;
            try
            {
                mdata = await Read9pmsgAsync(master.Process, mfd, Iohdrsz + Maxfdata);
            }
            catch (SyscallException error)
            {
                master.Errstr = error.Message;
                mdata = await ErrorAsync<byte[]>(master, "mount read");
            }

            if (mdata.Length == 0)
            {
                break;
            }

            if (ConvM2S(mdata) is not { } request)
            {
                await Syslog.WriteAsync(master.Process, true, Logfile, string.Create(CultureInfo.InvariantCulture, $"format error {mdata[0]:x} {mdata[1]:x} {mdata[2]:x} {mdata[3]:x} {mdata[4]:x}"));
                break;
            }

            Job job = await NewjobAsync(request);
            await dblock.WaitAsync();
            var ctx = new Proc(master.Process);
            Task handling = HandleAsync(ctx, job);
            await Task.WhenAny(handling, ctx.Slaved.Task);

            // A slave unlocks, frees the job and dies when it has replied.
            if (!ctx.IsSlave)
            {
                await handling;
                dblock.Release();
                await FreejobAsync(job);
            }
        }
    }

    private async Task HandleAsync(Proc ctx, Job job)
    {
        try
        {
            Mfile mf = Newfid(FidOf(job.Request));

            switch (job.Request)
            {
                case Tversion r:
                    await RversionAsync(ctx, job, r);
                    break;
                case Tauth:
                    await SendmsgAsync(ctx, job, "authentication not required");
                    break;
                case Tflush r:
                    await FlushjobAsync(r.OldTag);
                    await SendmsgAsync(ctx, job, null, new Rflush(r.Tag));
                    break;
                case Tattach r:
                    await RattachAsync(ctx, job, mf, r);
                    break;
                case Twalk r:
                    await RwalkAsync(ctx, job, mf, r);
                    break;
                case Topen r:
                    await SendmsgAsync(ctx, job, (mf.Qid.Type & QidType.QTDIR) != 0 && r.Mode != 0 ? "permission denied" : null, new Ropen(r.Tag, mf.Qid, 0));
                    break;
                case Tcreate:
                    await SendmsgAsync(ctx, job, "creation permission denied");
                    break;
                case Tread r:
                    await RreadAsync(ctx, job, mf, r);
                    break;
                case Twrite r:
                    await RwriteAsync(ctx, job, mf, r);
                    break;
                case Tclunk r:
                    mf.Busy = false;
                    await SendmsgAsync(ctx, job, null, new Rclunk(r.Tag));
                    break;
                case Tremove:
                    await SendmsgAsync(ctx, job, "remove permission denied");
                    break;
                case Tstat r:
                    await RstatAsync(ctx, job, mf, r);
                    break;
                case Twstat:
                    await SendmsgAsync(ctx, job, "wstat permission denied");
                    break;
            }

            // Slave processes die after replying.
            if (ctx.IsSlave)
            {
                dblock.Release();
                await FreejobAsync(job);
                if (debug)
                {
                    await Syslog.WriteAsync(ctx.Process, false, Logfile, $"slave death {ctx.Process.Pid}");
                }

                Interlocked.Decrement(ref active);
                ctx.Done!.SetResult();
            }
        }
        catch (Exception error) when (ctx.IsSlave)
        {
            // A slave killed by a note dies of it, as cs.c's does, with nothing more done.
            ctx.Done!.SetException(error);
        }
    }

    private Task RversionAsync(Proc ctx, Job job, Tversion r)
    {
        uint msize = Math.Min(r.MSize, Iohdrsz + Maxfdata);
        return SendmsgAsync(ctx, job, null, new Rversion(r.Tag, msize, r.Version.StartsWith("9P", StringComparison.Ordinal) ? "9P2000" : "unknown"));
    }

    private Task RattachAsync(Proc ctx, Job job, Mfile mf, Tattach r)
    {
        if (!mf.Busy)
        {
            mf.Busy = true;
            mf.User = r.Uname;
        }

        mf.Qid = new Qid(QidType.QTDIR, vers++, Qdir);
        return SendmsgAsync(ctx, job, null, new Rattach(r.Tag, mf.Qid));
    }

    private async Task RwalkAsync(Proc ctx, Job job, Mfile mf, Twalk r)
    {
        string? err = null;
        Mfile? nmf = null;
        string[] elems = r.Wname;
        var wqid = new List<Qid>();
        if (r.NewFid != r.Fid)
        {
            // Clone fid.
            nmf = Newfid(r.NewFid);
            if (nmf.Busy)
            {
                await SendmsgAsync(ctx, job, "clone to used channel");
                return;
            }

            nmf.CopyFrom(mf);
            nmf.Qid = new Qid(nmf.Qid.Type, vers++, nmf.Qid.Path);
            mf = nmf;
        }

        Qid qid = mf.Qid;
        for (int i = 0; i < elems.Length && i < Maxwelem; i++)
        {
            if ((qid.Type & QidType.QTDIR) == 0)
            {
                err = "not a directory";
                break;
            }

            // .. and . are the directory, which qid already is.
            if (elems[i] == "cs")
            {
                qid = new Qid(QidType.QTFILE, qid.Version, Qcs);
            }
            else if (elems[i] is not (".." or "."))
            {
                err = "file does not exist";
                break;
            }

            wqid.Add(qid);
        }

        if (nmf is not null && (err is not null || wqid.Count < elems.Length))
        {
            nmf.Busy = false;
        }

        if (err is null)
        {
            mf.Qid = qid;
        }

        await SendmsgAsync(ctx, job, err, new Rwalk(r.Tag, [.. wqid]));
    }

    private async Task RreadAsync(Proc ctx, Job job, Mfile mf, Tread r)
    {
        string? err = null;
        ReadOnlyMemory<byte> data = ReadOnlyMemory<byte>.Empty;
        long off = (long)r.Offset;
        int cnt = (int)Math.Min(r.Count, int.MaxValue);
        mf.Ref++;
        if (off < 0)
        {
            err = "bad read offset";
        }
        else if ((mf.Qid.Type & QidType.QTDIR) != 0)
        {
            if (off == 0)
            {
                uint clock = (uint)await TimeAsync(ctx.Process);
                var dir = new Stat(0, 0, 0, new Qid(QidType.QTFILE, vers, Qcs), 0b110_110_110, clock, clock, 0, "cs", mf.User, mf.User, mf.User);
                byte[] buf = new byte[dir.Size];
                int at = 0;
                dir.WriteTo(buf, ref at);
                data = buf;
            }
        }
        else
        {
            // Look for an answer at the right offset, looking up more answers until there is one or no more.
            int i;
            long toff;
            while ((i = FindReply(mf, off, out toff)) == -1 && await LookupAsync(ctx, mf) && !job.Flushed)
            {
            }

            if (i >= 0)
            {
                // Give back a single reply, or part of one.
                data = mf.Reply[i].AsMemory((int)(off - toff), (int)Math.Min(cnt, toff + mf.Reply[i].Length - off));
            }
        }

        await SendmsgAsync(ctx, job, err, new Rread(r.Tag, data));
        mf.Ref--;
    }

    private async Task RwriteAsync(Proc ctx, Job job, Mfile mf, Twrite r)
    {
        string? err = null;
        int cnt = (int)r.Count;
        if ((mf.Qid.Type & QidType.QTDIR) != 0)
        {
            err = "can't write directory";
        }
        else if (cnt >= Maxrequest)
        {
            err = "request too long";
        }
        else
        {
            string data = Encoding.UTF8.GetString(r.Data.Span);
            if (data.IndexOf('\0', StringComparison.Ordinal) is int nul and >= 0)
            {
                data = data[..nul];
            }

            bool newline = cnt > 0 && r.Data.Span[cnt - 1] == '\n';
            err = mf.User != "none" && mf.User == csuser && await ControlAsync(ctx, data, newline) ? null : await QueryAsync(ctx, mf, data);
        }

        await SendmsgAsync(ctx, job, err, new Rwrite(r.Tag, (uint)cnt));
    }

    // The special commands of cs's owner; false for a write that is none of them.
    private async Task<bool> ControlAsync(Proc ctx, string data, bool newline)
    {
        if (data.StartsWith("debug", StringComparison.Ordinal))
        {
            // Toggle debugging.
            debug = !debug;
            await Syslog.WriteAsync(ctx.Process, true, Logfile, $"debug {(debug ? 1 : 0)}");
        }
        else if (data.StartsWith("ipv4", StringComparison.Ordinal))
        {
            // Toggle ipv4 lookups.
            lookipvers ^= V4;
            await Syslog.WriteAsync(ctx.Process, true, Logfile, $"ipv4lookups {((lookipvers & V4) != 0 ? 1 : 0)}");
        }
        else if (data.StartsWith("ipv6", StringComparison.Ordinal))
        {
            // Toggle ipv6 lookups.
            lookipvers ^= V6;
            await Syslog.WriteAsync(ctx.Process, true, Logfile, $"ipv6lookups {((lookipvers & V6) != 0 ? 1 : 0)}");
        }
        else if (data.StartsWith("add ", StringComparison.Ordinal))
        {
            // Add networks to the default list.
            if (newline && data.EndsWith('\n'))
            {
                data = data[..^1];
            }

            Netadd(data[4..]);
            await ReadipinterfacesAsync(ctx);
        }
        else if (data.StartsWith("refresh", StringComparison.Ordinal))
        {
            // Refresh all state.
            Refresh(ctx);
        }
        else
        {
            return false;
        }

        return true;
    }

    private async Task<string?> QueryAsync(Proc ctx, Mfile mf, string data)
    {
        if (mf.Ref != 0)
        {
            return "query already in progress";
        }

        mf.Ref++;

        // Start the transaction with a clean slate.
        mf.Clean(netlist);
        string? err = null;
        if (data.StartsWith('!'))
        {
            // A general query.
            err = await GenqueryAsync(ctx, mf, data[1..]);
        }
        else
        {
            if (debug)
            {
                await Syslog.WriteAsync(ctx.Process, false, Logfile, $"write {data}");
            }

            // Break up the name.
            List<string> field = Getfields(data, 4, "!");
            switch (field.Count)
            {
                case 1:
                    mf.Net = "net";
                    mf.Host = field[0];
                    break;
                case 4:
                    (mf.Rem, mf.Serv, mf.Host, mf.Net) = (field[3], field[2], field[1], field[0]);
                    break;
                case 3:
                    (mf.Serv, mf.Host, mf.Net) = (field[2], field[1], field[0]);
                    break;
                case 2:
                    (mf.Host, mf.Net) = (field[1], field[0]);
                    break;
            }

            // Do the first net worth of lookup.
            if (!await LookupAsync(ctx, mf))
            {
                err = Nstrcpy(ctx.Errstr, 64);
            }
        }

        mf.Ref--;
        return err;
    }

    private async Task RstatAsync(Proc ctx, Job job, Mfile mf, Tstat r)
    {
        bool isdir = (mf.Qid.Type & QidType.QTDIR) != 0;
        uint clock = (uint)await TimeAsync(ctx.Process);
        var dir = new Stat(0, 0, 0, mf.Qid, isdir ? (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101 : 0b110_110_110, clock, clock, 0, isdir ? "." : "cs", mf.User, mf.User, mf.User);
        await SendmsgAsync(ctx, job, null, new Rstat(r.Tag, dir));
    }

    private async Task SendmsgAsync(Proc ctx, Job job, string? err, ISerializable? reply = null)
    {
        ISerializable message = err is not null || reply is null ? new Rerror(job.Request.Tag, Nstrcpy($"cs: {err}", Errmax)) : reply;
        byte[] mdata = ConvS2M(message);

        // cs.c's error("mount write") is not reached here, nor there: a write on the pipe fails only
        // once its reader has gone, and the note that failure posts kills the writer first.
        await joblock.WaitAsync();
        try
        {
            if (!job.Flushed)
            {
                await ctx.Process.WriteAsync(mfd, mdata);
            }
        }
        finally
        {
            joblock.Release();
        }
    }

    // error: logged, and the process ends, so it never returns a T.
    private async Task<T> ErrorAsync<T>(Proc ctx, string s)
    {
        await Syslog.WriteAsync(ctx.Process, true, Logfile, $"{s}: {ctx.Errstr}");
        ctx.Process.Exits(null);
        return default!;
    }

    private async Task ReadipinterfacesAsync(Proc ctx)
    {
        int conf = 0, dns = 0;
        await ipifclock.WaitAsync();
        try
        {
            ipifcs = Ip.ReadIpifc(ctx.Process.Host);
            foreach (Iplifc lifc in ipifcs.SelectMany(ifc => ifc.Lifc))
            {
                if (Ip.Compare(lifc.Ip, Ip.NoAddr) == 0)
                {
                    continue;
                }

                if (Ip.IsV4(lifc.Ip))
                {
                    conf |= V4;
                    byte[] loopbacknet = new byte[Ip.AddrLen];
                    Ip.Parse(loopbacknet, "127.0.0.0");
                    byte[] loopbackmask = new byte[Ip.AddrLen];
                    Ip.ParseMask(loopbackmask, "/8", true);
                    if (Ip.Compare(Ip.MaskIp(lifc.Ip, loopbackmask), loopbacknet) == 0)
                    {
                        continue;
                    }

                    dns |= V4;
                }
                else
                {
                    conf |= V6;
                    byte[] loopback6 = new byte[Ip.AddrLen];
                    loopback6[15] = 1;
                    if (Ip.IsV6LinkLocal(lifc.Ip) || Ip.Compare(lifc.Ip, loopback6) == 0)
                    {
                        continue;
                    }

                    dns |= V6;
                }
            }
        }
        finally
        {
            ipifclock.Release();
        }

        confipvers = conf;
        dnsipvers = dns;
    }

    // ipid: the system name, from $sysname, then the database's entry for an interface's address,
    // then the address itself; written to /dev/sysname. Fog's #I has no ether devices, so there is
    // no ethernet address to look for a sys by.
    private async Task IpidAsync(Proc ctx)
    {
        if (mysysname is not null)
        {
            return;
        }

        Process process = ctx.Process;

        // The environment has priority.
        string? p = await GetenvAsync(process, "sysname");
        if (!string.IsNullOrEmpty(p) && Ndb.IpAttr(p) != "ip")
        {
            mysysname = p;
        }
        else
        {
            // Try configured interfaces; if nothing worked, use the ip address. myipinfo gives only
            // the attribute asked for.
            NdbTuple? t = await MyipinfoAsync(["sys"]) ?? await MyipinfoAsync(["ip"]);
            mysysname = t?.Val;
            if (mysysname is null)
            {
                return;
            }
        }

        // Set /dev/sysname now it is known.
        try
        {
            int f = await process.OpenAsync("/dev/sysname", NinePConstants.OWRITE);
            try
            {
                await process.WriteAsync(f, Encoding.UTF8.GetBytes(mysysname));
            }
            finally
            {
                await process.CloseAsync(f);
            }
        }
        catch (SyscallException)
        {
            // A sysname that cannot be set is not cs's failure.
        }
    }

    // netinit(1): netinit in the background, as refresh asks, in a process of its own, one at a time.
    private void Refresh(Proc ctx) => ctx.Process.Fork(RforkFlags.Proc | RforkFlags.Noteg | RforkFlags.Mem | RforkFlags.Nowait, async child =>
    {
        // Stryker disable once Statement : only two refreshes adding the same network in a race would show it; ipifclock already keeps their reading of the interfaces apart
        await netlock.WaitAsync();
        try
        {
            await NetinitAsync(new Proc(child));
        }
        finally
        {
            netlock.Release();
        }
    });

    // netinit: the default networks, those with a clone under the mount point; then the interfaces
    // and the system name.
    private async Task NetinitAsync(Proc ctx)
    {
        // Add the mounted networks to the default list.
        foreach (Network np in network)
        {
            if (np.Considered)
            {
                continue;
            }

            try
            {
                await ctx.Process.StatAsync($"{mntpt}/{np.Net}/clone");
            }
            catch (SyscallException)
            {
                continue;
            }

            Append(np);
        }

        // Find out what our ip addresses are.
        await ReadipinterfacesAsync(ctx);

        // Set the system name if we need to; these days ip is all we have.
        await IpidAsync(ctx);
        if (debug)
        {
            await Syslog.WriteAsync(ctx.Process, false, Logfile, $"mysysname {mysysname ?? "???"}");
        }
    }

    private void Append(Network np)
    {
        if (netlist is not null)
        {
            last!.Next = np;
        }
        else
        {
            netlist = np;
        }

        last = np;
        np.Next = null;
        np.Considered = true;
    }

    // netadd: networks added to the standard list.
    private void Netadd(string p)
    {
        foreach (string field in Getfields(p, 12, " "))
        {
            foreach (Network np in network)
            {
                if (field != np.Net)
                {
                    continue;
                }

                if (np.Considered)
                {
                    break;
                }

                Append(np);
            }
        }
    }

    // lookup: the replies of the request's next network, or of its own; net means the best
    // network to get there.
    // lookup: whether the request's next network, or its own, gave replies; net means the best
    // network to get there. The database is open, as cs ends when it cannot be.
    private async Task<bool> LookupAsync(Proc ctx, Mfile mf)
    {
        if (mf.Net is null)
        {
            // Must have been a genquery.
            return false;
        }

        bool rv = false;
        if (mf.Net == "net")
        {
            // Go through the set of default nets.
            Network? np;
            for (np = mf.Nextnet; np is not null && !rv; np = np.Next)
            {
                if (await NpLookupAsync(ctx, np, mf.Host!, mf.Serv) is not { } nt)
                {
                    continue;
                }

                bool fasttimeout = np.Fasttimeout && !Lookforproto(nt, np.Net);
                for (NdbTuple? t = nt; mf.Reply.Count < Nreply && t is not null; t = t.Entry)
                {
                    // Avoid duplicates.
                    if (await NpTransAsync(ctx, t, np, mf.Serv, mf.Rem, fasttimeout) is { } cp && !mf.Reply.Any(r => r.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(cp))))
                    {
                        mf.Reply.Add(Encoding.UTF8.GetBytes(cp));
                        rv = true;
                    }
                }
            }

            mf.Nextnet = np;
            return rv;
        }

        // Not /net: we only get one lookup.
        if (mf.Reply.Count != 0)
        {
            return false;
        }

        // Look for a specific network.
        if (network.FirstOrDefault(n => !n.Fasttimeout && n.Net == mf.Net) is { } known)
        {
            NdbTuple? nt = await NpLookupAsync(ctx, known, mf.Host!, mf.Serv);
            for (NdbTuple? t = nt; mf.Reply.Count < Nreply && t is not null; t = t.Entry)
            {
                if (await NpTransAsync(ctx, t, known, mf.Serv, mf.Rem, false) is { } cp)
                {
                    mf.Reply.Add(Encoding.UTF8.GetBytes(cp));
                    rv = true;
                }
            }

            return rv;
        }

        // Not a known network; don't translate host or service.
        string r = mf.Serv is not null ? $"{mntpt}/{mf.Net}/clone {mf.Host}!{mf.Serv}" : $"{mntpt}/{mf.Net}/clone {mf.Host}";
        mf.Reply.Add(Encoding.UTF8.GetBytes(Nstrcpy(r, Maxreply)));
        return true;
    }

    private Task<NdbTuple?> NpLookupAsync(Proc ctx, Network np, string host, string? serv)
        => np.Telco ? TelcolookupAsync(host) : IplookupAsync(ctx, np, host, serv);

    private Task<string?> NpTransAsync(Proc ctx, NdbTuple t, Network np, string? serv, string? rem, bool fasttimeout)
        => np.Telco ? Task.FromResult(Telcotrans(t, np, serv, rem)) : IptransAsync(ctx, t, np, serv, rem, fasttimeout);

    // ipserv: the port of an ip service, with !r for a restricted one; * needs no translation.
    private async Task<string?> IpservAsync(Network np, string name)
    {
        // '*' means any service.
        if (name == "*")
        {
            return Nstrcpy(name, Maxservice);
        }

        // See if it's numeric or symbolic.
        bool alpha = false;
        foreach (char c in name)
        {
            if (char.IsAsciiDigit(c))
            {
                continue;
            }

            if (char.IsAsciiLetter(c) || c is '-' or '$')
            {
                alpha = true;
            }
            else
            {
                return null;
            }
        }

        string? p = null;
        NdbTuple? t = null;
        if (alpha)
        {
            (p, t) = await db!.GetValueAsync(null, np.Net, name, "port");
            if (p is null)
            {
                return null;
            }
        }
        else
        {
            // Look up only tcp ports below 1024, for the restricted attribute; the port is the name.
            if (Atoi(name) < 1024 && np.Net == "tcp")
            {
                (_, t) = await db!.GetValueAsync(null, "port", name, "port");
            }

            p = name;
        }

        bool restr = t?.Entries().Any(nt => nt.Attr == "restricted") ?? false;
        return Nstrcpy(p + (restr ? "!r" : string.Empty), Maxservice);
    }

    private async Task<NdbTuple?> MyipinfoAsync(IReadOnlyList<string> list)
    {
        NdbTuple? t = null;
        await ipifclock.WaitAsync();
        try
        {
            foreach (Iplifc lifc in ipifcs.SelectMany(ifc => ifc.Lifc))
            {
                t = Ndb.Concatenate(t, await db!.IpInfoAsync("ip", Ip.Format(lifc.Ip), list));
            }
        }
        finally
        {
            ipifclock.Release();
        }

        return Ndb.Dedup(t);
    }

    // ipreorder: the address on a network of one of our interfaces first.
    private async Task<NdbTuple?> IpreorderAsync(NdbTuple? t)
    {
        await ipifclock.WaitAsync();
        try
        {
            foreach (Iplifc lifc in ipifcs.SelectMany(ifc => ifc.Lifc))
            {
                byte[] net = Ip.MaskIp(lifc.Ip, lifc.Mask);
                for (NdbTuple? nt = t; nt is not null; nt = nt.Entry)
                {
                    byte[] ip = new byte[Ip.AddrLen];

                    // Stryker disable once Statement,Block : a tuple skipped here could match only a network of no bits, and the entry would then turn at a point with no address before it, as it does at the first address
                    if (nt.Attr != "ip" || Ip.Parse(ip, nt.Val) == -1)
                    {
                        continue;
                    }

                    if (Ip.Compare(net, Ip.MaskIp(ip, lifc.Mask)) == 0)
                    {
                        return Ndb.Reorder(t!, nt);
                    }
                }
            }
        }
        finally
        {
            ipifclock.Release();
        }

        return t;
    }

    // iplookuphost: the entries of an ip destination, the matched line first.
    private async Task<NdbTuple?> IplookuphostAsync(Proc ctx, Network np, string host)
    {
        // Turn '[ip address]' into just 'ip address'.
        if (host.StartsWith('['))
        {
            host = Nstrcpy(host, Maxhost)[1..];
            if (host.IndexOf(']', StringComparison.Ordinal) is int x and >= 0)
            {
                host = host[..x];
            }
        }

        // For dial strings with no host, and :: as 0.0.0.0 till we go v6.
        if (host is "*" or "::")
        {
            return Ndbnew("ip", "*");
        }

        // Just accept addresses.
        string attr = Ndb.IpAttr(host);
        if (attr == "ip")
        {
            return Ndbnew("ip", host);
        }

        // Give the domain name server the first opportunity to resolve domain names; if that
        // fails, try the database.
        NdbTuple? t = null;
        if (attr == "dom")
        {
            t = await DnsiplookupAsync(ctx, host, np.Ipvers);
        }

        if (t is null)
        {
            var s = new Ndbs();
            for (NdbTuple? nt = await db!.SearchAsync(s, attr, host); nt is not null; nt = await s.Db!.SnextAsync(s, attr, host))
            {
                if (Ndb.FindAttr(nt, s.T, "ip") is not null)
                {
                    t = Ndb.Concatenate(t, Ndb.Reorder(nt, s.T!));
                }
            }
        }

        if (t is null)
        {
            if (attr != "dom")
            {
                (string? dnsname, _) = await db!.GetValueAsync(null, attr, host, "dom");
                if (dnsname is not null)
                {
                    t = await DnsiplookupAsync(ctx, dnsname, np.Ipvers);
                }
            }

            t ??= await DnsiplookupAsync(ctx, host, np.Ipvers);
        }

        // cs reorders t to have the matched line first, but by then its matched tuple is t's first.
        return t;
    }

    private async Task<NdbTuple?> IplookupAsync(Proc ctx, Network np, string host, string? serv)
    {
        // Start with the service, since it's the most likely to fail and costs the least.
        if (serv is null || await IpservAsync(np, serv) is null)
        {
            ctx.Errstr = "can't translate service";
            return null;
        }

        // '$' means the rest of the name is an attribute to search for.
        ctx.Errstr = "can't translate address";
        NdbTuple? t = null;
        if (host.StartsWith('$'))
        {
            string attr = host[1..];
            NdbTuple? l = await MyipinfoAsync([attr]);
            for (NdbTuple? nt = l; nt is not null; nt = nt.Entry)
            {
                if (nt.Attr == attr)
                {
                    t = Ndb.Concatenate(t, await IplookuphostAsync(ctx, np, nt.Val));
                }
            }
        }
        else
        {
            t = await IplookuphostAsync(ctx, np, host);
        }

        return await IpreorderAsync(t);
    }

    // iptrans: the clone and address of an ip address, if this network takes its version.
    private async Task<string?> IptransAsync(Proc ctx, NdbTuple t, Network np, string? serv, string? rem, bool fasttimeout)
    {
        if (t.Attr != "ip")
        {
            return null;
        }

        // iplookup has translated the service already.
        string ts = (await IpservAsync(np, serv!))!;
        string x = rem is not null ? Nstrcpy($"!{rem}", Maxservice) : string.Empty;
        if (t.Val.StartsWith('*'))
        {
            return Nstrcpy($"{mntpt}/{np.Net}/clone {ts}{x}", Maxreply);
        }

        byte[] ip = new byte[Ip.AddrLen];
        if (Ip.Parse(ip, t.Val) == -1 || (np.Ipvers & confipvers & (Ip.IsV4(ip) ? V4 : V6)) == 0)
        {
            return null;
        }

        return Nstrcpy($"{mntpt}/{np.Net}/clone {Ip.Format(ip)}!{ts}{x}{(fasttimeout ? "!fasttimeout" : string.Empty)}", Maxreply);
    }

    // telcolookup: a telephone number, as the database's sys names it or as it is.
    private async Task<NdbTuple?> TelcolookupAsync(string host)
    {
        var s = new Ndbs();
        (_, NdbTuple? t) = await db!.GetValueAsync(s, "sys", host, "telco");
        return t is null ? Ndbnew("telco", host) : Ndb.Reorder(t, s.T!);
    }

    private string? Telcotrans(NdbTuple t, Network np, string? serv, string? rem)
    {
        if (t.Attr != "telco")
        {
            return null;
        }

        string x = rem is not null ? Nstrcpy($"!{rem}", Maxservice) : string.Empty;
        return Nstrcpy(serv is not null ? $"{mntpt}/{np.Net}/clone {t.Val}!{serv}{x}" : $"{mntpt}/{np.Net}/clone {t.Val}{x}", Maxreply);
    }

    // slave: the rest of the request in a process of its own, so one request does not block
    // another; the master goes back to the job loop.
    private async Task SlaveAsync(Proc ctx, string host)
    {
        if (ctx.IsSlave)
        {
            // We're already a slave process.
            return;
        }

        if (Interlocked.Increment(ref active) >= Maxactive)
        {
            Interlocked.Decrement(ref active);
            return;
        }

        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Process.Fork(RforkFlags.Proc | RforkFlags.Noteg | RforkFlags.Mem | RforkFlags.Nowait, async child =>
        {
            started.SetResult(child);
            await done.Task;
        });

        ctx.Process = await started.Task;
        ctx.Done = done;
        ctx.IsSlave = true;
        if (debug)
        {
            await Syslog.WriteAsync(ctx.Process, false, Logfile, $"slave {ctx.Process.Pid}");
        }

        await ProcsetnameAsync(ctx.Process, host);
        ctx.Slaved.SetResult();
    }

    // dnsiplookup: the addresses dns gives for a name, in a slave; the error says why there are none.
    private async Task<NdbTuple?> DnsiplookupAsync(Proc ctx, string host, int ipvers)
    {
        ipvers &= dnsipvers & lookipvers;
        if (ipvers == 0)
        {
            ctx.Errstr = "no ip address";
            return null;
        }

        dblock.Release();
        await SlaveAsync(ctx, host);
        if (!ctx.IsSlave)
        {
            await dblock.WaitAsync();
            ctx.Errstr = "too much activity";
            return null;
        }

        (NdbTuple? t, string? error) = Ndb.IpAttr(host) == "ip" ? await Ndb.DnsQueryAsync(ctx.Process, host, "ptr") : await DnsAddressesAsync(ctx.Process, host, ipvers);
        if (t is null)
        {
            // What dnsquery leaves in errstr; a query it gives up on early leaves errstr as it was.
            ctx.Errstr = error ?? ctx.Errstr;

            // Stryker disable once String : an errstr holding neither is only a null address's, from a name the database gave, and the lookup of the name itself that follows always sets errstr again
            bool failed = ctx.Errstr.Contains("dns failure", StringComparison.Ordinal);
            if (ctx.Errstr.Contains("exist", StringComparison.Ordinal))
            {
                ctx.Errstr = $"can't translate address: {ctx.Errstr}";
            }
            else if (failed)
            {
                ctx.Errstr = $"temporary problem: {ctx.Errstr}";
            }
        }

        await dblock.WaitAsync();
        return t;
    }

    // genquery: attr1=val1 attr2=val2 ... gives the entry matching all of them; ipinfo attr=val
    // attr1 attr2 ... gives the attributes associated with the ip address, as ndbipinfo does.
    private async Task<string?> GenqueryAsync(Proc ctx, Mfile mf, string query)
    {
        // Values such as vendor's can hold spaces, so these are tokens, quoted with ''.
        List<string> attr = Process.Tokenize(query, Maxattr);
        int n = attr.Count;
        if (n == 0)
        {
            return "bad query";
        }

        if (attr[0] == "ipinfo")
        {
            return await IpinfoqueryAsync(ctx, mf, attr);
        }

        // Parse pairs.
        var val = new List<string>();
        for (int i = 0; i < n; i++)
        {
            int p = attr[i].IndexOf('=', StringComparison.Ordinal);
            if (p < 0)
            {
                return "bad query";
            }

            val.Add(attr[i][(p + 1)..]);
            attr[i] = attr[i][..p];
        }

        // Give dns a chance.
        if (attr[0] is "dom" or "ip")
        {
            NdbTuple? d = await DnsiplookupAsync(ctx, val[0], lookipvers);
            if (d is not null && Qmatch(d, attr, val, n))
            {
                Qreply(mf, d);
                return null;
            }
        }

        // The first pair is always the key; it can't be a '*'. The search is the and of all the pairs.
        var s = new Ndbs();
        for (NdbTuple? t = await db!.SearchAsync(s, attr[0], val[0]); t is not null; t = await s.Db!.SnextAsync(s, attr[0], val[0]))
        {
            if (Qmatch(t, attr, val, n))
            {
                Qreply(mf, t);
                return null;
            }
        }

        return "no match";
    }

    // ipresolve: the ip addresses of the host, each as attr.
    private async Task<NdbTuple?> IpresolveAsync(Proc ctx, string attr, string host)
    {
        NdbTuple? first = null;

        // Stryker disable once String : ipserv takes an empty service as port 0, so any service it gives back lets the lookup go on as * does
        NdbTuple? found = await IplookupAsync(ctx, network[Ntcp], host, "*");
        for (NdbTuple? nt = found, next; nt is not null; nt = next)
        {
            next = nt.Entry;
            nt.Entry = null;
            if (nt.Attr != "ip")
            {
                continue;
            }

            nt.Attr = Nstrcpy(attr, Ndb.Alen);
            first = Ndb.Concatenate(first, nt);
        }

        return first;
    }

    private async Task<string?> IpinfoqueryAsync(Proc ctx, Mfile mf, List<string> args)
    {
        // Skip 'ipinfo'.
        var list = args.Skip(1).ToList();
        if (list.Count < 1)
        {
            return "bad query";
        }

        // Get the search attribute=value, or assume my ip.
        string? attr = null, val = null;
        int eq = list[0].IndexOf('=', StringComparison.Ordinal);
        if (eq >= 0)
        {
            (attr, val) = (list[0][..eq], list[0][(eq + 1)..]);
            list.RemoveAt(0);
        }

        if (list.Count < 1)
        {
            return "bad query";
        }

        // Don't let ndbipinfo resolve the addresses; we're better at it.
        bool[] resolve = [.. list.Select(a => a.StartsWith('@'))];
        list = [.. list.Select(a => a.StartsWith('@') ? a[1..] : a)];

        NdbTuple? t = attr is null ? await MyipinfoAsync(list) : await db!.IpInfoAsync(attr, val!, list);
        if (t is null)
        {
            return "no match";
        }

        if (resolve.Any(r => r))
        {
            for (NdbTuple? nt = t, next; nt is not null; nt = next)
            {
                next = nt.Entry;

                // Already an address, or not one the user wants resolved? Each is one ndbipinfo was asked for.
                if (Ndb.IpAttr(nt.Val) == "ip" || !resolve[list.IndexOf(nt.Attr)])
                {
                    continue;
                }

                t = Ndb.Substitute(t, nt, await IpresolveAsync(ctx, nt.Attr, nt.Val));
            }

            t = Ndb.Dedup(t);
        }

        Qreply(mf, t);
        return null;
    }

    // The process a request is answered in, and the error it has; a slave's, once dns is asked.
    private sealed class Proc(Process process)
    {
        public Process Process { get; set; } = process;

        public string Errstr { get; set; } = string.Empty;

        public bool IsSlave { get; set; }

        public TaskCompletionSource Slaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Done { get; set; }
    }

    private sealed class Job(ISerializable request)
    {
        public ISerializable Request { get; } = request;

        public bool Flushed { get; set; }
    }

    private sealed class Network(string net, bool telco, bool considered, bool fasttimeout, int ipvers)
    {
        public string Net { get; } = net;

        public bool Telco { get; } = telco;

        public bool Considered { get; set; } = considered;

        public bool Fasttimeout { get; } = fasttimeout;

        public int Ipvers { get; } = ipvers;

        public Network? Next { get; set; }
    }

    private sealed class Mfile
    {
        public bool Busy { get; set; }

        public int Ref { get; set; }

        public string? User { get; set; }

        public Qid Qid { get; set; }

        // The current request.
        public string? Net { get; set; }

        public string? Host { get; set; }

        public string? Serv { get; set; }

        public string? Rem { get; set; }

        // The result of the last lookup.
        public Network? Nextnet { get; set; }

        public List<byte[]> Reply { get; private set; } = [];

        public void CopyFrom(Mfile mf)
        {
            (Busy, Ref, User, Qid, Nextnet) = (mf.Busy, mf.Ref, mf.User, mf.Qid, mf.Nextnet);
            (Net, Host, Serv, Rem) = (mf.Net, mf.Host, mf.Serv, mf.Rem);
            Reply = [.. mf.Reply];
        }

        // cleanmf: the request and its replies forgotten.
        public void Clean(Network? netlist)
        {
            (Net, Host, Serv, Rem) = (null, null, null, null);
            Reply.Clear();
            Nextnet = netlist;
        }
    }
}
