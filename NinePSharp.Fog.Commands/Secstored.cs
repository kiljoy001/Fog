using System.Security.Cryptography;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// auth/secstored, as 9front's secstored.c and dirls.c: the secure store, which announces its
// address, and for each call logs the client in with PAK and gets, puts and removes the files of
// its account in /adm/secstore/store, or changes its password.
public sealed class Secstored(Process process, string argv0)
{
    public const int Maxfilesize = 10 * 1024 * 1024;

    private const string Log = Password.Log;
    private const string AuthLog = "auth";
    private const uint Dmdir = 0x80000000;
    private Ndb? db;

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Secstored(process, argv[0]).RunAsync(argv);

    // dirls: the name, size, date and SHA1 of each file in path, in order of name; or null.
    public static async Task<string?> DirlsAsync(Process process, string path)
    {
        IReadOnlyList<Stat> dirbuf;
        try
        {
            if (((await process.StatAsync(path)).Qid.Type & QidType.QTDIR) == 0)
            {
                return null;
            }

            int fd = await process.OpenAsync(path, NinePConstants.OREAD);
            dirbuf = await process.DirReadAsync(fd);
            await process.CloseAsync(fd);
        }
        catch (SyscallException)
        {
            return null;
        }

        Stat[] sorted = [.. dirbuf.OrderBy(d => d.Name, StringComparer.Ordinal)];
        int nmwid = sorted.Length == 0 ? 0 : sorted.Max(d => Encoding.UTF8.GetByteCount(d.Name));
        int lenwid = sorted.Length == 0 ? 0 : sorted.Max(d => d.Length.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        var list = new StringBuilder();
        foreach (Stat d in sorted)
        {
            string date = Libc.Ctime(d.Mtime)[4..28];
            string dig = Convert.ToBase64String(await Sha1fileAsync(process, path, d.Name));

            // %-*s pads by runes to a width strlen counts in bytes.
            string name = d.Name + new string(' ', Math.Max(0, nmwid - d.Name.EnumerateRunes().Count()));
            list.Append(System.Globalization.CultureInfo.InvariantCulture, $"{name}\t{d.Length.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(lenwid)} {date} {dig}\n");
        }

        return list.ToString();
    }

    // sha1file: the SHA1 of the file's contents.
    private static async Task<byte[]> Sha1fileAsync(Process process, string pfx, string nm)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        try
        {
            int fd = await process.OpenAsync($"{pfx}/{nm}", NinePConstants.OREAD);
            for (ReadOnlyMemory<byte> b; !(b = await process.ReadAsync(fd, 8192)).IsEmpty;)
            {
                sha.AppendData(b.Span);
            }

            await process.CloseAsync(fd);
        }
        catch (SyscallException)
        {
            // A file that cannot be read is summed as an empty one.
        }

        return sha.GetHashAndReset();
    }

    private async Task<string> UsageAsync()
    {
        await PrintAsync("usage: secstored [-R] [-S servername] [-s tcp!*!5356] [-v] [-x netmtpt]\n");
        process.Exits("usage");
        return string.Empty;
    }

    private async Task RunAsync(IReadOnlyList<string> argv)
    {
        bool forceSta = false, verbose = false;
        string serve = "tcp!*!5356", s = "secstore", net = "/net";
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'R':
                    forceSta = true;
                    break;
                case 's':
                    serve = await arguments.EargfAsync(UsageAsync);
                    break;
                case 'S':
                    s = await arguments.EargfAsync(UsageAsync);
                    break;
                case 'x':
                    string x = await arguments.EargfAsync(UsageAsync);
                    net = x.StartsWith('/') ? x : $"/net{x}";
                    break;
                case 'v':
                    verbose = true;
                    break;
                default:
                    await UsageAsync();
                    break;
            }
        }

        if (verbose)
        {
            await ServeAsync($"{net}/{serve}", s, forceSta);
            return;
        }

        process.Fork(RforkFlags.Noteg | RforkFlags.Proc | RforkFlags.Fdg, child => new Secstored(child, argv0).ServeAsync($"{net}/{serve}", s, forceSta));
        process.Exits(null);
    }

    private async Task ServeAsync(string aserve, string s, bool forceSta)
    {
        string adir = string.Empty;
        try
        {
            (_, adir) = await Net.AnnounceAsync(process, aserve);
        }
        catch (SyscallException error)
        {
            await SysfatalAsync($"{aserve}: {error.Message}");
        }

        await Syslog.WriteAsync(process, false, Log, $"ANNOUNCE {aserve}");
        while (true)
        {
            (int lcfd, string ldir) = (-1, string.Empty);
            try
            {
                (lcfd, ldir) = await Net.ListenAsync(process, adir);
            }
            catch (SyscallException)
            {
                process.Exits("can't listen");
            }

            process.Fork(RforkFlags.Fdg | RforkFlags.Rend | RforkFlags.Proc, child => new Secstored(child, argv0).CallAsync(lcfd, ldir, s, forceSta));
            await process.CloseAsync(lcfd);
        }
    }

    private async Task CallAsync(int lcfd, string ldir, string s, bool forceSta)
    {
        // "/lib/ndb/common.radius does not exist" if db is set before the fork.
        (Ndb? auth, _) = await Ndb.OpenAsync(process, "/lib/ndb/auth");
        if (auth is null)
        {
            await Syslog.WriteAsync(process, false, Log, "no /lib/ndb/auth");
        }

        (Ndb? local, _) = await Ndb.OpenAsync(process, null);
        if (local is null)
        {
            await Syslog.WriteAsync(process, false, Log, "no /lib/ndb/local");
        }

        db = auth is null ? local : Ndb.Cat(auth, local);
        int dfd = -1;
        try
        {
            dfd = await Net.AcceptAsync(process, lcfd, ldir);
        }
        catch (SyscallException)
        {
            process.Exits("can't accept");
        }

        // 30 minutes.
        await process.AlarmAsync(30 * 60 * 1000);
        await Syslog.WriteAsync(process, false, Log, $"secstore from {await RemoteIpAsync(ldir)}");
        await DologinAsync(dfd, s, forceSta);
        process.Exits(null);
    }

    // remoteIP: the call's ipaddr!port, from the remote of its line directory.
    private async Task<string> RemoteIpAsync(string ldir)
    {
        string rp = $"{ldir}/remote";
        int fd;
        try
        {
            fd = await process.OpenAsync(rp, NinePConstants.OREAD);
        }
        catch (SyscallException)
        {
            return "?!?";
        }

        ReadOnlyMemory<byte> ap;
        try
        {
            ap = await process.ReadAsync(fd, 500);
        }
        catch (SyscallException error)
        {
            Errstr.Set(process, error.Message);
            ap = ReadOnlyMemory<byte>.Empty;
        }

        if (ap.Length is 0 or 500)
        {
            await PrintAsync($"secstored: error {ap.Length} reading {rp}: {Errstr.Get(process)}\n");
            return "?!?";
        }

        await process.CloseAsync(fd);
        string remote = Encoding.UTF8.GetString(ap.Span);
        return remote.EndsWith('\n') ? remote[..^1] : remote;
    }

    private async Task DologinAsync(int fd, string s, bool forceSta)
    {
        // Collect the first message.
        var conn = new SConn(process, fd);
        try
        {
            (int n, string msg) = await conn.ReadstrAsync();
            if (n < 0)
            {
                await PrintAsync($"secstored: remote: {msg}: {Errstr.Get(process)}\n");
                await conn.WriterrAsync("can't read your first message");
                return;
            }

            // Authenticate.
            (int rc, Pw? pw) = await Pak.ServerAsync(process, argv0, conn, s, msg);
            if (rc < 0)
            {
                if (pw is not null)
                {
                    await Syslog.WriteAsync(process, false, Log, $"secstore denied for {pw.Id}");
                }

                return;
            }

            if (forceSta || (pw!.Status & Pw.Sta) != 0)
            {
                await conn.WriteAsync("STA");
                (n, msg) = await conn.ReadstrAsync();
                if (n < 10 || !msg.StartsWith("STA", StringComparison.Ordinal))
                {
                    await Syslog.WriteAsync(process, false, Log, $"no STA from {pw!.Id}");
                    return;
                }

                if (await SecureidcheckAsync(pw!.Id, msg[3..]) is { } mess)
                {
                    await Syslog.WriteAsync(process, false, Log, $"secureidcheck denied {pw.Id} because {mess}");
                    return;
                }
            }

            await conn.WriteAsync("OK");
            await Syslog.WriteAsync(process, false, Log, $"AUTH {pw!.Id}");
            if (!await OperateAsync(conn, pw))
            {
                await conn.WriterrAsync("operation failed");
            }
        }
        finally
        {
            await conn.FreeAsync();
        }
    }

    // The operations the client asks for, until it says goodbye; false for one that failed.
    private async Task<bool> OperateAsync(SConn conn, Pw pw)
    {
        while (true)
        {
            (int n, string msg) = await conn.ReadstrAsync();
            if (n <= 0)
            {
                break;
            }

            int nl = msg.IndexOf('\n', StringComparison.Ordinal);
            msg = nl < 0 ? msg : msg[..nl];
            await Syslog.WriteAsync(process, false, Log, $"[{pw.Id}] {msg}");
            string? file;
            if (msg.StartsWith("GET ", StringComparison.Ordinal))
            {
                file = await Password.ValidatefileAsync(process, msg[4..]);
                if (file is null || await GetfileAsync(conn, pw.Id, file) < 0)
                {
                    return false;
                }
            }
            else if (msg.StartsWith("PUT ", StringComparison.Ordinal))
            {
                file = await Password.ValidatefileAsync(process, msg[4..]);
                if (file is null || await PutfileAsync(conn, pw.Id, file) < 0)
                {
                    await Syslog.WriteAsync(process, false, Log, $"failed PUT {pw.Id}/{file ?? "<nil>"}");
                    return false;
                }
            }
            else if (msg.StartsWith("RM ", StringComparison.Ordinal))
            {
                file = await Password.ValidatefileAsync(process, msg[3..]);
                if (file is null || await RemovefileAsync(conn, pw.Id, file) < 0)
                {
                    await Syslog.WriteAsync(process, false, Log, $"failed RM {pw.Id}/{file ?? "<nil>"}");
                    return false;
                }
            }
            else if (msg.StartsWith("CHPASS", StringComparison.Ordinal))
            {
                (n, msg) = await conn.ReadstrAsync();
                if (n < 0)
                {
                    await Syslog.WriteAsync(process, false, Log, $"protocol botch CHPASS for {pw.Id}");
                    await conn.WriterrAsync("protocol botch while setting PAK");
                    return true;
                }

                pw.Hi = Pak.Strtomp64(msg) ?? 0;
                int i;
                string? why = null;
                for (i = 0; i < 4 && (why = await Password.PutAsync(process, pw)) is not null; i++)
                {
                    await Syslog.WriteAsync(process, false, Log, $"password change failed for {pw.Id} ({i}): {why}");
                }

                if (i == 4)
                {
                    return true;
                }
            }
            else if (msg.StartsWith("BYE", StringComparison.Ordinal))
            {
                return true;
            }
            else
            {
                await conn.WriterrAsync("unrecognized operation");
                return true;
            }
        }

        await Syslog.WriteAsync(process, false, Log, $"{pw.Id} closed connection without saying goodbye");
        return true;
    }

    private async Task<int> GetdirAsync(SConn conn, string id)
    {
        string? ls = await DirlsAsync(process, $"{Password.SecstoreDir}/store/{id}");
        byte[] msg = Encoding.UTF8.GetBytes(ls ?? string.Empty);

        // Send the listing's size, then the listing in Maxmsg chunks.
        await conn.WriteAsync(msg.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (int i = 0; i < msg.Length; i += SConn.Maxmsg)
        {
            await conn.WriteAsync(msg[i..], Math.Min(SConn.Maxmsg, msg.Length - i));
        }

        return 0;
    }

    private async Task<int> GetfileAsync(SConn conn, string id, string gf)
    {
        if (gf == ".")
        {
            return await GetdirAsync(conn, id);
        }

        // Send the file's size.
        string s = $"{Password.SecstoreDir}/store/{id}/{gf}";
        int gd;
        Stat st;
        try
        {
            gd = await process.OpenAsync(s, NinePConstants.OREAD);
        }
        catch (SyscallException error)
        {
            return await RefuseAsync(conn, "-1", $"can't open {s}: {error.Message}");
        }

        try
        {
            st = await process.FStatAsync(gd);
        }
        catch (SyscallException error)
        {
            return await RefuseAsync(conn, "-1", $"can't stat {s}: {error.Message}");
        }

        if ((st.Mode & Dmdir) != 0)
        {
            return await RefuseAsync(conn, "-1", $"{s} should be a plain file, not a directory");
        }

        if (st.Length > Maxfilesize)
        {
            return await RefuseAsync(conn, "-3", $"implausible filesize {st.Length} for {gf}");
        }

        long len = (long)st.Length;
        await conn.WriteAsync(len.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Send the file in Maxmsg chunks.
        while (len > 0)
        {
            ReadOnlyMemory<byte> b;
            try
            {
                b = await process.ReadAsync(gd, SConn.Maxmsg);
            }
            catch (SyscallException error)
            {
                Errstr.Set(process, error.Message);
                b = ReadOnlyMemory<byte>.Empty;
            }

            if (b.IsEmpty)
            {
                await Syslog.WriteAsync(process, false, Log, $"read error on {gf}: {Errstr.Get(process)}");
                return -1;
            }

            await conn.WriteAsync(b.ToArray(), b.Length);
            len -= b.Length;
        }

        await process.CloseAsync(gd);
        return 0;
    }

    private async Task<int> RefuseAsync(SConn conn, string answer, string why)
    {
        await Syslog.WriteAsync(process, false, Log, why);
        await conn.WriteAsync(answer);
        return -1;
    }

    private async Task<int> PutfileAsync(SConn conn, string id, string pf)
    {
        // Get the file's size.
        (int n, string s) = await conn.ReadstrAsync();
        if (n < 0)
        {
            await Syslog.WriteAsync(process, false, Log, $"remote: {s}: {Errstr.Get(process)}");
            return -1;
        }

        long len = Libc.Strtol(s, 10, out _);
        if (len == -1)
        {
            await Syslog.WriteAsync(process, false, Log, $"remote file {pf} does not exist");
            return -1;
        }

        if (len is < 0 or > Maxfilesize)
        {
            await Syslog.WriteAsync(process, false, Log, $"implausible filesize {len} for {pf}");
            return -1;
        }

        string path = $"{Password.SecstoreDir}/store/{id}/{pf}";
        int pd;
        try
        {
            pd = await process.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_000);
        }
        catch (SyscallException error)
        {
            await Syslog.WriteAsync(process, false, Log, $"can't open {path}: {error.Message}");
            return -1;
        }

        byte[] buf = new byte[SConn.Maxmsg + 1];
        while (len > 0)
        {
            n = await conn.ReadAsync(buf, SConn.Maxmsg);
            if (n <= 0)
            {
                await Syslog.WriteAsync(process, false, Log, "empty file chunk");
                return -1;
            }

            try
            {
                if (await process.WriteAsync(pd, buf.AsMemory(0, n)) != n)
                {
                    throw new SyscallException("short write");
                }
            }
            catch (SyscallException error)
            {
                await Syslog.WriteAsync(process, false, Log, $"write error on {pf}: {error.Message}");
                return -1;
            }

            len -= n;
        }

        await process.CloseAsync(pd);
        return 0;
    }

    private async Task<int> RemovefileAsync(SConn conn, string id, string f)
    {
        string buf = $"{Password.SecstoreDir}/store/{id}/{f}";
        try
        {
            if (((await process.StatAsync(buf)).Mode & Dmdir) != 0)
            {
                await conn.WriterrAsync("can't remove a directory");
                return -1;
            }

            await process.RemoveAsync(buf);
        }
        catch (SyscallException error)
        {
            await conn.WriterrAsync($"remove failed: {error.Message}");
            return -1;
        }

        return 0;
    }

    // secureidcheck, as far as Fog has a way to the RADIUS server it asks: a response a fob would
    // not give is refused, as is any without a RADIUS secret in ndb for lra-radius.
    private async Task<string?> SecureidcheckAsync(string user, string response)
    {
        const string Failed = "authentication failed";
        if (response.Length < 4 || response.IndexOfAny("abcdefABCDEF".ToCharArray()) >= 0)
        {
            return Failed;
        }

        if (db is null || (await db.GetValueAsync(null, "radius", "lra-radius", "secret")).Value is null)
        {
            await Syslog.WriteAsync(process, false, AuthLog, "secureidcheck: nil radius secret: no radius secret");
            return Failed;
        }

        await Syslog.WriteAsync(process, false, AuthLog, $"secureidcheck: no RADIUS for {user}");
        return Failed;
    }

    private async Task SysfatalAsync(string message)
    {
        await PrintAsync($"{argv0}: {message}\n");
        process.Exits(message);
    }

    private Task PrintAsync(string text) => process.WriteAsync(2, Encoding.UTF8.GetBytes(text)).AsTask();
}
