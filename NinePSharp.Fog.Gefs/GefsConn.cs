using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Gefs;

// fs.c's runfs for one connection: each 9P message read whole and answered in turn, each fid's
// tree, file and user kept as gefs's Fid keeps them, and an attach authenticated through an auth
// file factotum speaks for, as gefs does without -A. Where gefs would read or write a fid not
// opened for it, or fault on an auth fid used as a file, Fog refuses with gefs's errors.
public sealed class GefsConn(GefsServer server, Func<int, Task<byte[]>> readn, Func<byte[], Task> write, Func<Task<IGefsAuth>> authnew)
{
    // Max9p, and the iounit before Tversion sets one.
    public const int Max9p = 32 * 1024;
    private const int IoHdrSz = 24;
    private const int StatFixLen = 49;

    private const string Enofid = "unknown fid";
    private const string Efid = "fid in use";
    private const string Etype = "invalid fid type";
    private const string Esrch = "directory entry not found";
    private const string Elength = "name too long";
    private const string Eperm = "permission denied";
    private const string Einuse = "resource in use";
    private const string Enouser = "user does not exist";
    private const string Ebadu = "attach -- unknown user or failed authentication";
    private const string Erdonly = "file system read only";
    private const string Eauthp = "authread -- auth protocol not finished";
    private const string Eauthd = "authread -- not enough data";
    private const string Eauthph = "auth phase error";
    private const string Enone = "auth -- user 'none' requires no authentication";
    private const string Emode = "open/create -- unknown mode";
    private const string Ebotch = "protocol botch";

    private readonly Dictionary<uint, Fid> fids = [];
    private readonly string session = $"gefs/{Guid.NewGuid():N}";
    private ulong sequence;
    private bool versioned;
    private bool authok;
    private int iounit = Max9p;

    // Why the connection was hung up, as fshangup prints it, or null if it ended or still runs.
    public string? Hangup { get; private set; }

    // runfs: until the client hangs up, or sends what gefs hangs up on; then putconn clunks every fid.
    public async Task ServeAsync()
    {
        try
        {
            while (await ReadMessageAsync() is { } m)
            {
                if (Parse(m) is not { } request)
                {
                    Hangup = "invalid message";
                    break;
                }

                if (request is not Tversion && !versioned)
                {
                    Hangup = "version required";
                    break;
                }

                if (!await RespondAsync(await DispatchAsync(request)))
                {
                    break;
                }
            }
        }
        catch (IOException error)
        {
            Hangup = $"read message: {error.Message}";
        }
        finally
        {
            foreach (Fid f in fids.Values.ToArray())
            {
                await ClunkFidAsync(f);
            }
        }
    }

    // convM2S, for the T-messages a server takes.
    private static ISerializable? Parse(byte[] m)
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
            return null;
        }
    }

    // mode2bits
    private static int Bits(byte mode)
    {
        int bits = (mode & 3) switch
        {
            NinePConstants.OREAD => GefsFs.Read,
            NinePConstants.OWRITE => GefsFs.Write,
            NinePConstants.ORDWR => GefsFs.Read | GefsFs.Write,
            _ => GefsFs.Read | GefsFs.Exec,
        };
        return (mode & NinePConstants.OTRUNC) != 0 ? bits | GefsFs.Write : bits;
    }

    // dir2statbuf: the type and device ~0, times rounded to the nearest second; null if it does
    // not fit in n bytes.
    private static byte[]? StatBuf(Dir d, int n)
    {
        byte[][] names = [.. new[] { d.Name, d.User, d.Group, d.Muid }.Select(Encoding.UTF8.GetBytes)];
        int size = StatFixLen + names.Sum(name => name.Length);
        if (size > n)
        {
            return null;
        }

        var p = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(size - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), uint.MaxValue);
        p[8] = d.Qid.Type;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(9), d.Qid.Version);
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(13), d.Qid.Path);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(21), d.Mode);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(25), Seconds(d.Atime));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(29), Seconds(d.Mtime));
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(33), d.Length);
        int at = 41;
        foreach (byte[] name in names)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(at), (ushort)name.Length);
            name.CopyTo(p, at + 2);
            at += 2 + name.Length;
        }

        return p;
    }

    private static uint Seconds(long nanoseconds) => (uint)((nanoseconds + 500_000_000) / 1_000_000_000);

    private static NinePSharp.Constants.Qid QidOf(Dir d) => new((QidType)d.Qid.Type, d.Qid.Version, (ulong)d.Qid.Path);

    // readmsg: the size, then the rest; nothing at the end, and a message past the iounit refused.
    private async Task<byte[]?> ReadMessageAsync()
    {
        byte[] size = await readn(4);
        if (size.Length == 0)
        {
            return null;
        }

        if (size.Length != 4)
        {
            throw new IOException("short read");
        }

        int sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(size);
        if (sz > iounit || sz < 7)
        {
            throw new IOException("message size too large");
        }

        byte[] rest = await readn(sz - 4);
        return rest.Length == sz - 4 ? [.. size, .. rest] : throw new IOException("short read");
    }

    // respond: a reply that cannot be written hangs the connection up.
    private async Task<bool> RespondAsync(ISerializable reply)
    {
        var buf = new byte[reply.Size];
        reply.WriteTo(buf);
        try
        {
            await write(buf);
            return true;
        }
        catch (IOException)
        {
            Hangup = "i/o error";
            return false;
        }
    }

    private async Task<ISerializable> DispatchAsync(ISerializable request)
    {
        try
        {
            return request switch
            {
                Tversion m => Version(m),
                Tauth m => await AuthAsync(m),
                Tflush m => new Rflush(m.Tag),
                Tclunk m => await ClunkAsync(m),
                Tattach m => await AttachAsync(m),
                Twalk m => await WalkAsync(m),
                Topen m => await OpenAsync(m),
                Tcreate m => await CreateAsync(m),
                Tread m => await ReadAsync(m),
                Twrite m => await WriteAsync(m),
                Tremove m => await RemoveAsync(m),
                Tstat m => Stat(m),
                _ => await WstatAsync((Twstat)request),
            };
        }
        catch (Exception error) when (error is IOException or GefsException)
        {
            return new Rerror(request.Tag, error.Message);
        }
    }

    // fsversion: 9P2000, its minor version dropped, at most Max9p+IOHDRSZ; any other unknown.
    private Rversion Version(Tversion m)
    {
        uint msize = Max9p + IoHdrSz;
        string version = m.Version.Split('.')[0];
        if (version != "9P2000")
        {
            versioned = false;
            return new Rversion(m.Tag, msize, "unknown");
        }

        msize = Math.Min(msize, m.MSize);
        versioned = true;
        iounit = (int)msize;
        return new Rversion(m.Tag, msize, "9P2000");
    }

    // fsauth: an auth file for any user but none, with a new qid, its conversation factotum's.
    private async Task<ISerializable> AuthAsync(Tauth m)
    {
        if (m.Uname == "none")
        {
            throw new IOException(Enone);
        }

        IGefsAuth rpc = await authnew();
        if (fids.ContainsKey(m.Afid))
        {
            await rpc.DisposeAsync();
            throw new IOException(Efid);
        }

        long path = await server.NewQidAsync();
        fids[m.Afid] = new Fid(null, null, string.Empty) { Number = m.Afid, Auth = rpc };
        return new Rauth(m.Tag, new NinePSharp.Constants.Qid(QidType.QTAUTH, 0, (ulong)path));
    }

    // fsattach: the tree, the user, the authentication, then the permit for a name starting with %.
    private async Task<ISerializable> AttachAsync(Tattach m)
    {
        GefsFs fs = server.Mount(m.Aname, m.Uname);
        if (m.Afid != NinePConstants.NoFid)
        {
            Fid af = Get(m.Afid);
            await AuthReadAsync(af, 0);
            if (af.User != m.Uname)
            {
                throw new IOException(Ebadu);
            }

            authok = true;
        }
        else if (m.Uname != "none" || !authok)
        {
            throw new IOException(Ebadu);
        }

        fs = server.Permit(fs, m.Aname, m.Uname);
        Dir root = fs.Entry(fs.Root);
        Dup(m.Fid, new Fid(fs, fs.Root, m.Uname));
        return new Rattach(m.Tag, QidOf(root));
    }

    // authread: factotum's next message for the client, or once it is done, the user it proved.
    private async Task<byte[]> AuthReadAsync(Fid f, uint count)
    {
        IGefsAuth rpc = f.Auth ?? throw new IOException(Etype);
        (GefsAuthResult result, byte[] data) = await rpc.ReadAsync();
        switch (result)
        {
            case GefsAuthResult.Done:
                string cuid = await rpc.CuidAsync() ?? throw new IOException(Eauthph);
                f.User = server.HasUser(cuid) ? cuid : throw new IOException(Enouser);
                return [];
            case GefsAuthResult.Ok:
                return count < data.Length ? throw new IOException(Eauthd) : data;
            case GefsAuthResult.Phase:
                throw new IOException(Eauthph);
            default:
                throw new IOException(Eauthp);
        }
    }

    // fswalk: each name looked up while the fid may search where it is; a walk that found
    // nothing is an error, and one cut short gives what it found without a new fid.
    private async Task<ISerializable> WalkAsync(Twalk m)
    {
        Fid o = Get(m.Fid);
        if (o.Open is not null)
        {
            throw new IOException(Einuse);
        }

        if (o.Auth is not null)
        {
            throw new IOException(Etype);
        }

        ResourceHandle at = o.Handle;
        var wqid = new List<NinePSharp.Constants.Qid>();
        foreach (string name in m.Wname)
        {
            if (Encoding.UTF8.GetByteCount(name) > Format.MaxName)
            {
                throw new IOException(Elength);
            }

            if (!o.Fs.Allows(o.Fs.Entry(at), o.User, GefsFs.Exec) || await o.Fs.WalkAsync(at, name, CancellationToken.None) is not { } next)
            {
                break;
            }

            at = next;
            wqid.Add(QidOf(o.Fs.Entry(at)));
        }

        if (wqid.Count == 0 && m.Wname.Length != 0)
        {
            throw new IOException(Esrch);
        }

        if (wqid.Count == m.Wname.Length)
        {
            if (m.NewFid != m.Fid)
            {
                Dup(m.NewFid, new Fid(o.Fs, at, o.User));
            }
            else
            {
                o.Handle = at;
            }
        }

        return new Rwalk(m.Tag, [.. wqid]);
    }

    // fsopen: gefs's checks, then the fid open in the mode asked, unless it already is.
    private async Task<ISerializable> OpenAsync(Topen m)
    {
        Fid f = Get(m.Fid);
        if (f.Open is not null)
        {
            throw new IOException(Einuse);
        }

        f.Open = await f.Fs.OpenAsync(f.Handle, m.Mode, Context(f), CancellationToken.None);
        return new Ropen(m.Tag, QidOf(f.Fs.Entry(f.Handle)), (uint)iounit);
    }

    // fscreate: the fid becomes the file it made in its directory, open in the mode asked.
    private async Task<ISerializable> CreateAsync(Tcreate m)
    {
        Fid f = Get(m.Fid);
        if (f.Open is not null)
        {
            throw new IOException(Einuse);
        }

        ResourceOpenHandle open = await f.Fs.CreateAndOpenAsync(f.Handle, m.Name, m.Perm, m.Mode, Context(f), CancellationToken.None);
        f.Handle = open.Resource;
        f.Open = open;
        return new Rcreate(m.Tag, QidOf(f.Fs.Entry(f.Handle)), (uint)iounit);
    }

    // fsread: an auth file's next message, a directory's entries, or a file's bytes.
    private async Task<ISerializable> ReadAsync(Tread m)
    {
        Fid f = Get(m.Fid);
        if (f.Auth is not null)
        {
            return new Rread(m.Tag, await AuthReadAsync(f, m.Count));
        }

        if (f.Open is not { } open || (Bits(open.Mode) & GefsFs.Read) == 0)
        {
            throw new IOException(Einuse);
        }

        Dir d = f.Fs.Entry(f.Handle);
        if ((d.Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0)
        {
            return new Rread(m.Tag, ReadDir(f, m.Offset, (int)m.Count));
        }

        return new Rread(m.Tag, await f.Fs.ReadAsync(open, m.Offset, m.Count, CancellationToken.None));
    }

    // readdir: a scan from the start at offset 0, else on from where the last read stopped; an
    // entry that did not fit comes first in the next read, which must have room for it.
    private byte[] ReadDir(Fid f, ulong offset, int count)
    {
        if (offset == 0)
        {
            f.Scan = new DirScan();
        }

        DirScan s = f.Scan ??= new DirScan();
        if (s.Done)
        {
            return [];
        }

        var data = new List<byte>();
        List<(byte[] Key, Dir Dir)> entries = f.Fs.Entries(f.Handle, s.Last);
        foreach ((byte[] key, Dir d) in entries)
        {
            if (StatBuf(d, count - data.Count) is not { } stat)
            {
                if (s.Overflow && data.Count == 0)
                {
                    throw new IOException(Ebotch);
                }

                s.Overflow = true;
                return [.. data];
            }

            s.Overflow = false;
            data.AddRange(stat);
            s.Last = key;
        }

        s.Done = true;
        return [.. data];
    }

    // fswrite: an auth file's message for factotum, or bytes to a file open to write.
    private async Task<ISerializable> WriteAsync(Twrite m)
    {
        Fid f = Get(m.Fid);
        byte[] data = m.Data.ToArray();
        if (f.Auth is { } rpc)
        {
            return await rpc.WriteAsync(data) ? new Rwrite(m.Tag, m.Count) : throw new IOException(Ebotch);
        }

        if (f.Open is not { } open)
        {
            throw new IOException(Einuse);
        }

        return new Rwrite(m.Tag, await f.Fs.WriteAsync(open, m.Offset, data, Context(f), CancellationToken.None));
    }

    // fsclunk: the fid gone, and a file opened to be removed on close removed.
    private async Task<ISerializable> ClunkAsync(Tclunk m)
    {
        await ClunkFidAsync(Get(m.Fid));
        return new Rclunk(m.Tag);
    }

    // fsremove: the fid clunked whatever comes of it, then the file removed if it may be.
    private async Task<ISerializable> RemoveAsync(Tremove m)
    {
        Fid f = Get(m.Fid);
        fids.Remove(f.Number);
        if (f.Open is { } open)
        {
            await f.Fs.ClunkAsync(new ResourceOpenHandle(open.Resource, open.HandleId, (byte)(open.Mode & ~NinePConstants.ORCLOSE), open.IoUnit), Context(f), CancellationToken.None);
        }

        if (f.Auth is { } rpc)
        {
            await rpc.DisposeAsync();
            throw new IOException(Etype);
        }

        if (f.Fs.ReadOnly)
        {
            throw new IOException(Erdonly);
        }

        await f.Fs.RemoveAsync(f.Handle, null, Context(f), CancellationToken.None);
        return new Rremove(m.Tag);
    }

    // fsstat: an auth file's has no user gefs can name.
    private Rstat Stat(Tstat m)
    {
        Fid f = Get(m.Fid);
        if (f.Auth is not null)
        {
            throw new IOException(Eperm);
        }

        byte[] buf = StatBuf(f.Fs.Entry(f.Handle), int.MaxValue)!;
        int offset = 0;
        return new Rstat(m.Tag, new NinePSharp.Messages.Stat(buf, ref offset));
    }

    // fswstat: refused of an auth file; the rest as the tree checks it. A stat too short to read
    // is a message convM2S cannot read, which hangs the connection up.
    private async Task<ISerializable> WstatAsync(Twstat m)
    {
        Fid f = Get(m.Fid);
        if (f.Auth is not null)
        {
            throw new IOException(Emode);
        }

        NinePSharp.Messages.Stat d = m.Stat;
        var stat = new ResourceWStat(d.Type, d.Dev, d.Qid, d.Mode, d.Atime, d.Mtime, d.Length, d.Name, d.Uid, d.Gid, d.Muid, d.Size);
        await f.Fs.WStatAsync(f.Handle, stat, Context(f), CancellationToken.None);
        return new Rwstat(m.Tag);
    }

    private Fid Get(uint fid) => fids.TryGetValue(fid, out Fid? f) ? f : throw new IOException(Enofid);

    // dupfid: refused if the number is in use.
    private void Dup(uint number, Fid f)
    {
        if (!fids.TryAdd(number, f))
        {
            throw new IOException(Efid);
        }

        f.Number = number;
    }

    // clunkfid: a file opened to be removed on close removed, its failure no one's to hear, and an
    // auth file's conversation ended.
    private async Task ClunkFidAsync(Fid f)
    {
        fids.Remove(f.Number);
        if (f.Auth is { } rpc)
        {
            await rpc.DisposeAsync();
            return;
        }

        if (f.Open is { } open)
        {
            try
            {
                await f.Fs.ClunkAsync(open, Context(f), CancellationToken.None);
            }
            catch (IOException)
            {
                // gefs removes on close in its admin process, which answers no one.
            }
        }
    }

    private ResourceOperationContext Context(Fid f) => new(new ResourceOperationId(session, ++sequence), 0, f.User);

    private sealed class Fid(GefsFs? fs, ResourceHandle? handle, string user)
    {
        public uint Number { get; set; }

        // The tree and file of a fid attached or walked to; an auth file has neither.
        public GefsFs Fs => fs ?? throw new IOException(Etype);

        public ResourceHandle Handle { get => handle ?? throw new IOException(Etype); set => handle = value; }

        public string User { get; set; } = user;

        public ResourceOpenHandle? Open { get; set; }

        public DirScan? Scan { get; set; }

        public IGefsAuth? Auth { get; init; }
    }

    private sealed class DirScan
    {
        public byte[]? Last { get; set; }

        public bool Overflow { get; set; }

        public bool Done { get; set; }
    }
}
