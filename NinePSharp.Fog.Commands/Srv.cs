using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// lib9p, as srv.c, req.c, fid.c, dirread.c, post.c and rfork.c serve a file server: each request
// is read in turn and given to the server's function for it, which responds now or later. Fog has
// no file trees, so a server walks with walk1 or walk. The process each call runs in is Up, as a
// Plan 9 program's is up.
public sealed class Srv
{
    internal const string Ebotch = "9P protocol botch";

    // fcall.h's QTTMP; NinePSharp's QidType.QTTMP is QTAPPEND's 0x40.
    private const QidType Qttmp = (QidType)0x04;

    // srvforker's rfork: a process of its own, sharing memory, whose end no one waits for.
    private const RforkFlags Srvforker = RforkFlags.Proc | RforkFlags.Mem | RforkFlags.Nowait;
    private const int Iohdrsz = 24;
    private const string Edupfid = "duplicate fid";
    private const string Eduptag = "duplicate tag";
    private const string Eunknownfid = "unknown fid";
    private const string Enotfound = "file not found";
    private const string Ewalknodir = "walk in non-directory";
    private const string Eisdir = "is a directory";
    private const string Eperm = "permission denied";
    private static readonly AsyncLocal<Process?> Current = new();

    private readonly Dictionary<uint, Fid> fids = [];
    private readonly Dictionary<ushort, Req> reqs = [];
    private readonly SemaphoreSlim rlock = new(1, 1);
    private readonly SemaphoreSlim wlock = new(1, 1);
    private readonly SemaphoreSlim slock = new(1, 1);
    private int sref;
    private int rref;
    private long spid;

    // up: the process the calling code runs in.
    public static Process Up => Current.Value!;

    // argv0: the name the program was run by, as lib9p's errors give it.
    public string Argv0 { get; init; } = string.Empty;

    public Func<Req, Task>? Attach { get; init; }

    public Func<Req, Task>? Auth { get; init; }

    public Func<Req, Task>? Open { get; init; }

    public Func<Req, Task>? Create { get; init; }

    public Func<Req, Task>? Read { get; init; }

    public Func<Req, Task>? Write { get; init; }

    public Func<Req, Task>? Remove { get; init; }

    public Func<Req, Task>? Stat { get; init; }

    public Func<Req, Task>? Wstat { get; init; }

    public Func<Req, Task>? Flush { get; init; }

    public Func<Req, Task>? Walk { get; init; }

    // walk1: the error, or null with the fid's qid set to the walked file's.
    public Func<Fid, string, string?>? Walk1 { get; init; }

    public Func<Fid, Fid, string?>? Clone { get; init; }

    public Action<Fid>? DestroyFid { get; init; }

    public Func<Srv, Task>? End { get; init; }

    public uint Msize { get; private set; } = 8192 + Iohdrsz;

    // The error postsrv failed with, as its errstr.
    public string? Errstr { get; private set; }

    public int Infd { get; set; }

    public int Outfd { get; set; }

    // srvforker: a process sharing memory, and descriptors unless flags say otherwise, that runs fn.
    public Func<Func<Task>, RforkFlags, Task> Forker { get; set; } = (fn, flags) =>
    {
        Up.Fork(Srvforker | flags, async child =>
        {
            Current.Value = child;
            await fn();
        });
        return Task.CompletedTask;
    };

    // Runs code as the given process, as a program's main does.
    public static void Enter(Process process) => Current.Value = process;

    // dirread9p: the entries gen gives from where the last read ended, as many as fit.
    public static void Dirread(Req r, Func<int, NinePSharp.Messages.Stat?> gen)
    {
        int start = r.Offset == 0 ? 0 : r.Fid!.Dirindex;
        var data = new List<byte>();
        for (NinePSharp.Messages.Stat? d; (d = gen(start)) is { } entry; start++)
        {
            if (data.Count + entry.Size > r.Count)
            {
                break;
            }

            byte[] bytes = new byte[entry.Size];
            int offset = 0;
            entry.WriteTo(bytes, ref offset);
            data.AddRange(bytes);
        }

        r.Fid!.Dirindex = start;
        r.Data = [.. data];
    }

    // postsrv: the server run in a process of its own on one end of a pipe, the other end posted
    // as /srv/name when a name is given, and returned.
    public async Task<int> PostAsync(string? name)
    {
        Process up = Up;
        (int fd0, int fd1) = await up.PipeAsync();
        int cfd = -1;
        if (name is not null)
        {
            try
            {
                cfd = await up.CreateAsync($"/srv/{name}", NinePConstants.OWRITE | NinePConstants.ORCLOSE | NinePConstants.OCEXEC, 0b110_000_000);
                await up.WriteAsync(cfd, Encoding.UTF8.GetBytes(fd0.ToString(CultureInfo.InvariantCulture)));
            }
            catch (SyscallException error)
            {
                Errstr = error.Message;
                await up.CloseAsync(fd0);
                fd0 = -1;

                // Stryker disable once Statement : the post fails only in its create, as a write of the pipe's end to a new file does not, so there is no file to close
                await CloseAsync(up, cfd);
                await up.CloseAsync(fd1);
                return fd0;
            }
        }

        // Now committed: the server's process takes the pipe's end before this one lets it go.
        Infd = Outfd = fd1;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Forker(
            async () =>
            {
                await Up.RforkAsync(RforkFlags.Fdg);
                await Up.CloseAsync(fd0);
                started.SetResult();
                await ServeAsync();
            },
            RforkFlags.Nameg | RforkFlags.Noteg);
        await started.Task;
        await CloseAsync(up, cfd);
        await up.CloseAsync(fd1);
        return fd0;
    }

    // postmountsrv: the server posted, and mounted on mtpt with its own attach, which closes the descriptor.
    public async Task PostMountAsync(string? name, string? mtpt, MountFlags flag)
    {
        int sfd = await PostAsync(name);
        if (sfd < 0)
        {
            throw new SyscallException($"postsrv: {Errstr}");
        }

        if (mtpt is not null)
        {
            await Up.MountAsync(sfd, -1, mtpt, flag, string.Empty);
        }
        else
        {
            await Up.CloseAsync(sfd);
        }
    }

    // srv: requests served until the connection ends.
    public async Task ServeAsync()
    {
        spid = Up.Pid;
        sref = 0;
        rref = 0;
        sref++;
        await WorkAsync();
    }

    // srvrelease: the server goes on reading requests while this one waits, in a new process if
    // none is left reading.
    public async Task ReleaseAsync()
    {
        if (Interlocked.Decrement(ref sref) == 0)
        {
            Interlocked.Increment(ref sref);
            await Forker(WorkAsync, 0);
        }

        slock.Release();
    }

    // srvacquire: the server's lock taken again.
    public async Task AcquireAsync()
    {
        Interlocked.Increment(ref sref);
        await slock.WaitAsync();
    }

    // respond: the reply, or the error, written; a flush waiting on this request is answered then.
    public async Task RespondAsync(Req r, string? error)
    {
        if (r.Responded)
        {
            throw new InvalidOperationException("respond: already responded");
        }

        r.Error = error;
        if (r.Type == MessageTypes.Tflush && !Rflush(r))
        {
            return;
        }

        if (r.Type is not MessageTypes.Tflush)
        {
            Rfinish(r);
        }

        ISerializable reply = r.Error is not null ? new Rerror(r.Tag, r.Error) : Reply(r);
        await wlock.WaitAsync();
        try
        {
            if (r.Pooled)
            {
                Closereq(Removereq(r.Tag));
            }

            try
            {
                await Up.WriteAsync(Outfd, Fcall.ConvS2M(reply));
            }
            catch (SyscallException)
            {
                // lib9p reports a failed reply on its error output and goes on.
            }
        }
        finally
        {
            wlock.Release();
        }

        List<Req> flushes;
        lock (r)
        {
            r.Responded = true;
            flushes = [.. r.Flushes];

            // Stryker disable once Statement : a request answered is looked at by no flush again
            r.Flushes.Clear();
        }

        foreach (Req f in flushes)
        {
            await RespondAsync(f, null);
        }

        if (r.Pooled)
        {
            Closereq(r);
        }

        Interlocked.Decrement(ref rref);
    }

    // walkandclone: the new fid cloned, then each name walked in turn; a walk that fails at its
    // first name fails, and one that fails later walks as far as it got.
    public async Task WalkAndCloneAsync(Req r, Func<Fid, string, string?> walk1, Func<Fid, Fid, string?>? clone)
    {
        if (r.Fid == r.Newfid && r.Wname.Length > 1)
        {
            await RespondAsync(r, "lib9p: unused documented feature not implemented");
            return;
        }

        if (r.Fid != r.Newfid)
        {
            r.Newfid!.Qid = r.Fid!.Qid;
            if (clone?.Invoke(r.Fid, r.Newfid) is { } failed)
            {
                await RespondAsync(r, failed);
                return;
            }
        }

        string? e = null;
        foreach (string name in r.Wname)
        {
            if ((e = walk1(r.Newfid!, name)) is not null)
            {
                break;
            }

            r.Wqid.Add(r.Newfid!.Qid);
        }

        // rwalk keeps the error only of a walk that walked nothing, as lib9p's respond does after walkandclone.
        await RespondAsync(r, e);
    }

    // A directory's read answered: where it ended.
    private static void Rread(Req r)
    {
        // Stryker disable once Bitwise : only a directory's read looks at where the last one ended
        if ((r.Fid!.Qid.Type & QidType.QTDIR) != 0)
        {
            r.Fid.Diroffset = ((Tread)r.Ifcall).Offset + (ulong)r.Data.Length;
        }
    }

    // The qid type a mode's top byte gives.
    // Stryker disable once Bitwise : a uint shifts right alike either way
    private static uint Modetype(uint mode) => mode >> 24;

    private static async Task CloseAsync(Process process, int fd)
    {
        // Stryker disable once Equality : the file posted is created after the pipe, so it is never descriptor 0
        if (fd >= 0)
        {
            await process.CloseAsync(fd);
        }
    }

    private static ISerializable Reply(Req r) => r.Type switch
    {
        MessageTypes.Tversion => new Rversion(r.Tag, r.Rmsize, r.Rversion),
        MessageTypes.Tauth => new Rauth(r.Tag, r.Qid),
        MessageTypes.Tattach => new Rattach(r.Tag, r.Qid),
        MessageTypes.Tflush => new Rflush(r.Tag),
        MessageTypes.Twalk => new Rwalk(r.Tag, [.. r.Wqid]),
        MessageTypes.Topen => new Ropen(r.Tag, r.Qid, r.Iounit),
        MessageTypes.Tcreate => new Rcreate(r.Tag, r.Qid, r.Iounit),
        MessageTypes.Tread => new Rread(r.Tag, r.Data),
        MessageTypes.Twrite => new Rwrite(r.Tag, r.Rcount),
        MessageTypes.Tclunk => new Rclunk(r.Tag),
        MessageTypes.Tremove => new Rremove(r.Tag),
        MessageTypes.Tstat => new Rstat(r.Tag, r.D),
        _ => new Rwstat(r.Tag),
    };

    // getreq: the next request, a fake one for a duplicate tag, or null at the end.
    private async Task<Req?> GetreqAsync()
    {
        byte[] buf;
        await rlock.WaitAsync();
        try
        {
            buf = await Fcall.Read9pmsgAsync(Up, Infd, (int)Msize);
        }
        catch (SyscallException)
        {
            buf = [];
        }
        finally
        {
            rlock.Release();
        }

        if (buf.Length == 0 || Fcall.ConvM2S(buf) is not { } f)
        {
            return null;
        }

        var r = new Req(this, f, (MessageTypes)buf[4]);
        lock (reqs)
        {
            if (!reqs.TryAdd(r.Tag, r))
            {
                r.Error = Eduptag;
                return r;
            }
        }

        r.Pooled = true;
        r.Ref = 2;
        return r;
    }

    // srvwork: requests read and served under the server's lock, until there are none, or, in a
    // process the server made, until it has more than enough.
    private async Task WorkAsync()
    {
        for (Req? r; (r = await GetreqAsync()) is not null;)
        {
            Interlocked.Increment(ref rref);
            if (r.Error is not null)
            {
                await RespondAsync(r, r.Error);
                continue;
            }

            await slock.WaitAsync();
            await DispatchAsync(r);
            if (Volatile.Read(ref sref) > 8 && spid != Up.Pid)
            {
                Interlocked.Decrement(ref sref);
                slock.Release();

                // Stryker disable once Statement : a process leaving that goes on instead lets the lock go a second time and dies of it, which ends it as returning does
                return;
            }

            slock.Release();
        }

        if (End is not null && Volatile.Read(ref sref) == 1)
        {
            await End(this);
        }

        Interlocked.Decrement(ref sref);
    }

    // Stryker disable once String : every message convM2S reads is one srvwork dispatches, so unknown message answers none
    private Task DispatchAsync(Req r) => r.Type switch
    {
        MessageTypes.Tversion => SversionAsync(r),
        MessageTypes.Tauth => SauthAsync(r),
        MessageTypes.Tattach => SattachAsync(r),
        MessageTypes.Tflush => SflushAsync(r),
        MessageTypes.Twalk => SwalkAsync(r),
        MessageTypes.Topen => SopenAsync(r),
        MessageTypes.Tcreate => ScreateAsync(r),
        MessageTypes.Tread => SreadAsync(r),
        MessageTypes.Twrite => SwriteAsync(r),
        MessageTypes.Tclunk => SclunkAsync(r),
        MessageTypes.Tremove => SremoveAsync(r),
        MessageTypes.Tstat => SstatAsync(r),
        MessageTypes.Twstat => SwstatAsync(r),
        _ => RespondAsync(r, "unknown message"),
    };

    private async Task SversionAsync(Req r)
    {
        var t = (Tversion)r.Ifcall;
        if (Volatile.Read(ref rref) != 1)
        {
            await RespondAsync(r, Ebotch);
            return;
        }

        if (!t.Version.StartsWith("9P", StringComparison.Ordinal))
        {
            (r.Rversion, r.Rmsize) = ("unknown", 256);
            await RespondAsync(r, null);
            return;
        }

        r.Rversion = "9P2000";
        if (t.MSize < 256)
        {
            await RespondAsync(r, "version: message size too small");
            return;
        }

        r.Rmsize = Math.Min(t.MSize, 1024 * 1024);
        await RespondAsync(r, null);
    }

    private async Task SauthAsync(Req r)
    {
        if ((r.Afid = Allocfid(((Tauth)r.Ifcall).Afid)) is null)
        {
            await RespondAsync(r, Edupfid);
        }
        else if (Auth is not null)
        {
            await Auth(r);
        }
        else
        {
            await RespondAsync(r, $"{Argv0}: authentication not required");
        }
    }

    private async Task SattachAsync(Req r)
    {
        var t = (Tattach)r.Ifcall;
        if ((r.Fid = Allocfid(t.Fid)) is null)
        {
            await RespondAsync(r, Edupfid);
            return;
        }

        if (t.Afid != NinePConstants.NoFid && (r.Afid = Lookupfid(t.Afid)) is null)
        {
            await RespondAsync(r, Eunknownfid);
            return;
        }

        r.Fid.Uid = t.Uname;
        await (Attach?.Invoke(r) ?? RespondAsync(r, null));
    }

    private async Task SflushAsync(Req r)
    {
        lock (reqs)
        {
            if (reqs.TryGetValue(((Tflush)r.Ifcall).OldTag, out Req? old))
            {
                // Stryker disable once Statement,Update : the flush's hold on the old request only puts its fids' end after the flush's answer rather than just before, which no reply shows
                old.Ref++;
                r.Oldreq = old;
            }
        }

        if (r.Oldreq is null || r.Oldreq == r || Flush is null)
        {
            await RespondAsync(r, null);
            return;
        }

        await Flush(r);
    }

    private async Task SwalkAsync(Req r)
    {
        var t = (Twalk)r.Ifcall;
        if ((r.Fid = Lookupfid(t.Fid)) is null)
        {
            await RespondAsync(r, Eunknownfid);
            return;
        }

        if (r.Fid.Omode != -1)
        {
            await RespondAsync(r, "cannot clone open fid");
            return;
        }

        if (t.Wname.Length != 0 && (r.Fid.Qid.Type & QidType.QTDIR) == 0)
        {
            await RespondAsync(r, Ewalknodir);
            return;
        }

        if (t.Fid != t.NewFid)
        {
            if ((r.Newfid = Allocfid(t.NewFid)) is null)
            {
                await RespondAsync(r, Edupfid);
                return;
            }

            r.Newfid.Uid = r.Fid.Uid;
        }
        else
        {
            r.Fid.Ref++;
            r.Newfid = r.Fid;
        }

        if (Walk1 is not null)
        {
            await WalkAndCloneAsync(r, Walk1, Clone);
        }
        else
        {
            await Walk!(r);
        }
    }

    private async Task SopenAsync(Req r)
    {
        var t = (Topen)r.Ifcall;
        if ((r.Fid = Lookupfid(t.Fid)) is null)
        {
            await RespondAsync(r, Eunknownfid);
            return;
        }

        if (r.Fid.Omode != -1)
        {
            await RespondAsync(r, Ebotch);
            return;
        }

        bool dir = (r.Fid.Qid.Type & QidType.QTDIR) != 0;
        if (dir && (t.Mode & ~NinePConstants.ORCLOSE) != NinePConstants.OREAD)
        {
            await RespondAsync(r, Eisdir);
            return;
        }

        r.Qid = r.Fid.Qid;
        await (Open?.Invoke(r) ?? RespondAsync(r, null));
    }

    private async Task ScreateAsync(Req r)
    {
        var t = (Tcreate)r.Ifcall;
        if ((r.Fid = Lookupfid(t.Fid)) is null)
        {
            await RespondAsync(r, Eunknownfid);
        }
        else if (r.Fid.Omode != -1)
        {
            await RespondAsync(r, Ebotch);
        }
        else if ((r.Fid.Qid.Type & QidType.QTDIR) == 0)
        {
            await RespondAsync(r, "create in non-directory");
        }
        else
        {
            await (Create?.Invoke(r) ?? RespondAsync(r, "create prohibited"));
        }
    }

    private async Task SreadAsync(Req r)
    {
        var t = (Tread)r.Ifcall;
        if ((r.Fid = Lookupfid(t.Fid)) is null)
        {
            await RespondAsync(r, Eunknownfid);
            return;
        }

        if (r.Fid.Omode == -1 || (r.Fid.Omode & 3) == NinePConstants.OWRITE)
        {
            await RespondAsync(r, Ebotch);
            return;
        }

        if ((long)t.Offset < 0 || ((r.Fid.Qid.Type & QidType.QTDIR) != 0 && t.Offset != 0 && t.Offset != r.Fid.Diroffset))
        {
            await RespondAsync(r, "bad offset");
            return;
        }

        if ((int)t.Count < 0)
        {
            await RespondAsync(r, Ebotch);
            return;
        }

        r.Count = Math.Min(t.Count, Msize - Iohdrsz);
        await (Read?.Invoke(r) ?? RespondAsync(r, "no srv->read"));
    }

    private async Task SwriteAsync(Req r)
    {
        var t = (Twrite)r.Ifcall;
        if ((r.Fid = Lookupfid(t.Fid)) is null)
        {
            await RespondAsync(r, Eunknownfid);
            return;
        }

        if (r.Fid.Omode == -1 || (r.Fid.Omode & 3) is NinePConstants.OREAD or NinePConstants.OEXEC || (r.Fid.Qid.Type & QidType.QTDIR) != 0 || (long)t.Offset < 0)
        {
            await RespondAsync(r, Ebotch);
            return;
        }

        r.Count = Math.Min(t.Count, Msize - Iohdrsz);
        r.Data = t.Data[..(int)Math.Min(r.Count, (uint)t.Data.Length)].ToArray();
        await (Write?.Invoke(r) ?? RespondAsync(r, "write prohibited"));
    }

    private async Task SclunkAsync(Req r)
    {
        r.Fid = Removefid(((Tclunk)r.Ifcall).Fid);
        await RespondAsync(r, r.Fid is null ? Eunknownfid : null);
    }

    private async Task SremoveAsync(Req r)
    {
        if ((r.Fid = Removefid(((Tremove)r.Ifcall).Fid)) is null)
        {
            await RespondAsync(r, Eunknownfid);
            return;
        }

        await (Remove?.Invoke(r) ?? RespondAsync(r, "remove prohibited"));
    }

    private async Task SstatAsync(Req r)
    {
        if ((r.Fid = Lookupfid(((Tstat)r.Ifcall).Fid)) is null)
        {
            await RespondAsync(r, Eunknownfid);
            return;
        }

        await (Stat?.Invoke(r) ?? RespondAsync(r, "stat prohibited"));
    }

    private async Task SwstatAsync(Req r)
    {
        var t = (Twstat)r.Ifcall;
        if ((r.Fid = Lookupfid(t.Fid)) is null)
        {
            await RespondAsync(r, Eunknownfid);
            return;
        }

        if (Wstat is null)
        {
            await RespondAsync(r, "wstat prohibited");
            return;
        }

        r.D = t.Stat;
        if (r.D.Qid.Path != ulong.MaxValue && r.D.Qid.Path != r.Fid.Qid.Path)
        {
            await RespondAsync(r, "wstat -- attempt to change qid.path");
        }
        else if (r.D.Qid.Version != uint.MaxValue && r.D.Qid.Version != r.Fid.Qid.Version)
        {
            await RespondAsync(r, "wstat -- attempt to change qid.vers");
        }
        else if (r.D.Mode != uint.MaxValue && (r.D.Mode & ~(uint)(NinePConstants.FileMode9P.DMDIR | NinePConstants.FileMode9P.DMAPPEND | NinePConstants.FileMode9P.DMEXCL | NinePConstants.FileMode9P.DMTMP | (NinePConstants.FileMode9P)0b111_111_111)) != 0)
        {
            await RespondAsync(r, "wstat -- unknown bits in mode");
        }
        else if (r.D.Mode != uint.MaxValue && (byte)r.D.Qid.Type != 0xFF && (byte)r.D.Qid.Type != (byte)Modetype(r.D.Mode))
        {
            await RespondAsync(r, "wstat -- qid.type/mode mismatch");
        }
        else if (r.D.Mode != uint.MaxValue ? ((Modetype(r.D.Mode) ^ (uint)r.Fid.Qid.Type) & ~(uint)(QidType.QTAPPEND | QidType.QTEXCL | Qttmp)) != 0 : (byte)r.D.Qid.Type != 0xFF && r.D.Qid.Type != r.Fid.Qid.Type)
        {
            await RespondAsync(r, "wstat -- attempt to change qid.type");
        }
        else
        {
            await Wstat(r);
        }
    }

    // rflush: a flush of a request not yet answered waits for that answer.
    private bool Rflush(Req r)
    {
        if (r.Oldreq is { } or)
        {
            lock (or)
            {
                if (!or.Responded)
                {
                    or.Flushes.Add(r);
                    return false;
                }
            }

            Closereq(or);
        }

        r.Oldreq = null;
        return true;
    }

    // The part of each reply that changes the server's state once the answer is known.
    private void Rfinish(Req r)
    {
        switch (r.Type)
        {
            case MessageTypes.Tversion when r.Error is null:
                Msize = r.Rmsize;
                break;
            case MessageTypes.Tauth when r.Afid is not null:
                Rauth(r);
                break;
            case MessageTypes.Tattach when r.Error is not null && r.Fid is not null:
                Closefid(Removefid(r.Fid.Number));
                break;
            case MessageTypes.Twalk:
                Rwalk(r);
                break;
            case MessageTypes.Topen or MessageTypes.Tcreate when r.Error is null:
                Ropen(r);
                break;
            case MessageTypes.Tread when r.Error is null:
                Rread(r);
                break;
        }
    }

    // An auth refused lets its afid go; one answered is open for reading and writing.
    private void Rauth(Req r)
    {
        if (r.Error is not null)
        {
            Closefid(Removefid(r.Afid!.Number));
        }
        else if (r.Afid!.Omode == -1)
        {
            r.Afid.Omode = NinePConstants.ORDWR;
        }
    }

    // An open or create answered: its iounit within the message, and its fid open.
    private void Ropen(Req r)
    {
        r.Iounit = Math.Min(r.Iounit, Msize - Iohdrsz);

        // Stryker disable once Equality,Bitwise,Block : a fid is read only once open, so one just opened has read nothing and its offset is still none
        if ((r.Qid.Type & QidType.QTDIR) != 0)
        {
            r.Fid!.Diroffset = 0;
        }

        r.Fid!.Qid = r.Qid;
        r.Fid.Omode = r.Type == MessageTypes.Topen ? ((Topen)r.Ifcall).Mode : ((Tcreate)r.Ifcall).Mode;
    }

    private void Rwalk(Req r)
    {
        var t = (Twalk)r.Ifcall;
        if (r.Error is not null || r.Wqid.Count < t.Wname.Length)
        {
            if (t.Fid != t.NewFid && r.Newfid is not null)
            {
                Closefid(Removefid(r.Newfid.Number));
            }

            // Stryker disable once Conditional : a walk of no names fails only with an error of its own, so here there are always names
            r.Error = r.Wqid.Count == 0 ? r.Error ?? (t.Wname.Length != 0 ? Enotfound : null) : null;
        }
        else
        {
            r.Newfid!.Qid = r.Wqid.Count == 0 ? r.Fid!.Qid : r.Wqid[^1];
        }
    }

    private Fid? Allocfid(uint number)
    {
        lock (fids)
        {
            var f = new Fid(number) { Ref = 2 };
            return fids.TryAdd(number, f) ? f : null;
        }
    }

    // lookupfid: the fid, held for the request.
    private Fid? Lookupfid(uint number)
    {
        lock (fids)
        {
            if (fids.TryGetValue(number, out Fid? f))
            {
                f.Ref++;
                return f;
            }

            return null;
        }
    }

    private Fid? Removefid(uint number)
    {
        lock (fids)
        {
            return fids.Remove(number, out Fid? f) ? f : null;
        }
    }

    // closefid: the fid let go once nothing holds it, and the server told.
    private void Closefid(Fid? f)
    {
        if (f is not null && --f.Ref == 0)
        {
            DestroyFid?.Invoke(f);
        }
    }

    private Req? Removereq(ushort tag)
    {
        lock (reqs)
        {
            return reqs.Remove(tag, out Req? r) ? r : null;
        }
    }

    private void Closereq(Req? r)
    {
        if (r is null || --r.Ref != 0)
        {
            return;
        }

        Closefid(r.Fid);
        Closefid(r.Newfid);
        Closefid(r.Afid);

        // Stryker disable once Statement : rflush lets a flush's old request go and forgets it before the flush is answered, so none is left here
        Closereq(r.Oldreq);
    }
}
