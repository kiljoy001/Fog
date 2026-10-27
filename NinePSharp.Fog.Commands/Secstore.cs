using System.Security.Cryptography;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// auth/secstore, as 9front's secstore.c: logs in to the secure store with PAK, and gets, puts and
// removes the files of the user's account, encrypted with AES-CBC under the password, or changes
// the password, encrypting each file again under the new one.
public sealed class Secstore(Process process, string argv0)
{
    private const int Chk = 16;
    private const int Maxfiles = 100;
    private const int Passlen = 64;
    private static readonly byte[] Check = Encoding.ASCII.GetBytes(new string('X', Chk));
    private bool verbose;

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Secstore(process, argv[0]).RunAsync(argv);

    // strecpy into the 64 bytes of AuthConn's pass.
    private static string Strecpy(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        return b.Length < Passlen ? s : Encoding.UTF8.GetString(b, 0, Passlen - 1);
    }

    // The key of a stored file: SHA1 of "aescbc file" and the password, to AES's 16 bytes.
    private static byte[] Skey(byte[] key) => SHA1.HashData([.. "aescbc file"u8, .. key])[..Aesstate.Aesbsize];

    private async Task<string> UsageAsync()
    {
        await PrintAsync("usage: secstore [-cinv] [-[gG] getfile] [-p putfile] [-r rmfile] [-s tcp!server!5356] [-u user]\n");
        process.Exits("usage");
        return string.Empty;
    }

    private async Task RunAsync(IReadOnlyList<string> argv)
    {
        bool chpass = false, passStdin = false, passNvram = false;
        string? user = await Newns.GetuserAsync(process);
        var dest = new List<string>();
        var gfile = new List<(string Name, bool G)>();
        var pfile = new List<string>();
        var rfile = new List<string>();
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'c':
                    chpass = true;
                    break;
                case 'G':
                case 'g':
                    if (gfile.Count >= Maxfiles)
                    {
                        process.Exits("too many gfiles");
                    }

                    gfile.Add((await arguments.EargfAsync(UsageAsync), c == 'G'));
                    break;
                case 'i':
                    passStdin = true;
                    break;
                case 'n':
                    passNvram = true;
                    break;
                case 'p':
                    if (pfile.Count >= Maxfiles)
                    {
                        process.Exits("too many pfiles");
                    }

                    pfile.Add(await arguments.EargfAsync(UsageAsync));
                    break;
                case 'r':
                    if (rfile.Count >= Maxfiles)
                    {
                        process.Exits("too many rfiles");
                    }

                    rfile.Add(await arguments.EargfAsync(UsageAsync));
                    break;
                case 's':
                    // Seven at most, the last taking each one more.
                    string d = await arguments.EargfAsync(UsageAsync);
                    if (dest.Count < 7)
                    {
                        dest.Add(d);
                    }
                    else
                    {
                        dest[^1] = d;
                    }

                    break;
                case 'u':
                    user = await arguments.EargfAsync(UsageAsync);
                    break;
                case 'v':
                    verbose = true;
                    break;
                default:
                    await UsageAsync();
                    break;
            }
        }

        if (arguments.Rest().Count != 0 || user is null)
        {
            await UsageAsync();
        }

        if (chpass && (gfile.Count != 0 || pfile.Count != 0 || rfile.Count != 0))
        {
            await PrintAsync("secstore: Get, put, and remove invalid with password change.\n");
            process.Exits("usage");
        }

        if (dest.Count == 0 && await Cs.GetenvAsync(process, "secstore") is { } env)
        {
            dest.AddRange(Process.Tokenize(env, 7));
        }

        if (dest.Count == 0)
        {
            dest.Add("$auth");
        }

        AuthConn? ac = await LoginAsync(user!, dest, passStdin, passNvram);
        if (ac is null)
        {
            await SysfatalAsync("authentication failed");
        }

        int rc = chpass ? await ChpasswdAsync(ac!, user!) : await CmdAsync(ac!, gfile, pfile, rfile);
        if (rc < 0)
        {
            await SysfatalAsync("cmd failed");
        }

        process.Exits(string.Empty);
    }

    // getfile: the file got from the store and decrypted, into the file of its name or, wanted
    // in memory, returned; or the store's listing of the account, for ".".
    private async Task<(int Rc, byte[]? Buf)> GetfileAsync(SConn conn, string gf, bool inMemory, byte[]? key)
    {
        await conn.WriteAsync($"GET {gf}");

        // Get the file's size.
        (int n, string s) = await conn.ReadstrAsync();
        if (n < 0)
        {
            await PrintAsync($"secstore: remote: {s}\n");
            return (-1, null);
        }

        int len = Libc.Strtol(s, 10, out _);
        switch (len)
        {
            case -1:
                await PrintAsync($"secstore: remote file {gf} does not exist\n");
                return (-1, null);
            case -3:
                await PrintAsync($"secstore: implausible filesize for {gf}\n");
                return (-1, null);
            case < 0:
                await PrintAsync($"secstore: GET refused for {gf}\n");
                return (-1, null);
        }

        byte[] chunk = new byte[SConn.Maxmsg + 1];

        // The directory listing.
        if (gf == ".")
        {
            var listing = new List<byte>();
            for (int i = 0; i < len; i += n)
            {
                if ((n = await conn.ReadAsync(chunk, SConn.Maxmsg)) <= 0)
                {
                    await PrintAsync("secstore: empty file chunk\n");
                    return (-1, null);
                }

                if (inMemory)
                {
                    listing.AddRange(chunk.AsSpan(0, n));
                }
                else
                {
                    await process.WriteAsync(1, chunk.AsMemory(0, n));
                }
            }

            return (0, [.. listing]);
        }

        // conn is already encrypted against wiretappers, but gf is also encrypted against
        // server breakin.
        int fd = -1;
        if (!inMemory)
        {
            try
            {
                fd = await process.CreateAsync(gf, NinePConstants.OWRITE, 0b110_000_000);
            }
            catch (SyscallException error)
            {
                await PrintAsync($"secstore: can't open {gf}: {error.Message}\n");
                return (-1, null);
            }
        }

        var buf = new List<byte>();
        byte[] ib = new byte[SConn.Maxmsg + Chk];
        int ibr = 0, ibw = 0;
        Aesstate? aes = null;
        try
        {
            for (int nr = 0; nr < len;)
            {
                if ((n = await conn.ReadAsync(chunk, SConn.Maxmsg)) <= 0)
                {
                    await PrintAsync($"secstore: empty file chunk n={n} nr={nr} len={len}: {Errstr.Get(process)}\n");
                    return (-1, null);
                }

                chunk.AsSpan(0, n).CopyTo(ib.AsSpan(ibw));
                nr += n;
                ibw += n;

                // The first time, read the 16 byte IV.
                if (aes is null)
                {
                    if (n < Aesstate.Aesbsize)
                    {
                        await PrintAsync("secstore: no IV in file\n");
                        return (-1, null);
                    }

                    aes = new Aesstate(Skey(key!), ib.AsSpan(ibr, Aesstate.Aesbsize));
                    ibr += Aesstate.Aesbsize;
                    n -= Aesstate.Aesbsize;
                }

                aes.CbcDecrypt(ib.AsSpan(ibw - n, n));
                n = ibw - ibr - Chk;
                if (n > 0)
                {
                    if (inMemory)
                    {
                        buf.AddRange(ib.AsSpan(ibr, n));
                    }
                    else if (await process.WriteAsync(fd, ib.AsMemory(ibr, n)) != n)
                    {
                        await PrintAsync($"secstore: write error on {gf}");
                        return (-1, null);
                    }

                    ibr += n;
                }

                ib.AsSpan(ibr, ibw - ibr).CopyTo(ib);
                ibw -= ibr;
                ibr = 0;
            }
        }
        finally
        {
            aes?.Dispose();
        }

        if (!inMemory)
        {
            await process.CloseAsync(fd);
        }

        if (ibw - ibr != Chk || !ib.AsSpan(0, Chk).SequenceEqual(Check))
        {
            await PrintAsync("secstore: decrypted file failed to authenticate!\n");
            return (-1, null);
        }

        return (0, [.. buf]);
    }

    // putfile: the file sent to the store, after a random IV, encrypted and ended by its check,
    // as aescbc can decrypt it in an emergency.
    private async Task<int> PutfileAsync(SConn conn, string pf, byte[]? buf, byte[] key)
    {
        // Create the initialization vector.
        byte[] iv = new byte[Aesstate.Aesbsize];
        Authsrv.Genrandom(iv);
        using var aes = new Aesstate(Skey(key), iv);
        await conn.WriteAsync($"PUT {pf}");
        int fd = -1;
        long len;
        if (buf is null)
        {
            // Get the file's size.
            try
            {
                fd = await process.OpenAsync(pf, NinePConstants.OREAD);
            }
            catch (SyscallException error)
            {
                await PrintAsync($"secstore: can't open {pf}: {error.Message}\n");
                return -1;
            }

            len = await process.SeekAsync(fd, 0, 2);
            await process.SeekAsync(fd, 0, 0);
        }
        else
        {
            len = buf.Length;
        }

        if (len > Secstored.Maxfilesize)
        {
            await PrintAsync($"secstore: implausible filesize {len} for {pf}\n");
            return -1;
        }

        // Send the file's size.
        await conn.WriteAsync((len + Aesstate.Aesbsize + Chk).ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Send the IV and the file and its check in Maxmsg chunks.
        byte[] b = new byte[Chk + SConn.Maxmsg];
        int ivo = Aesstate.Aesbsize, bufi = 0;
        iv.CopyTo(b, 0);
        for (bool done = false; !done;)
        {
            int n;
            if (buf is null)
            {
                try
                {
                    ReadOnlyMemory<byte> r = await process.ReadAsync(fd, SConn.Maxmsg - ivo);
                    r.Span.CopyTo(b.AsSpan(ivo));
                    n = r.Length;
                }
                catch (SyscallException error)
                {
                    await PrintAsync($"secstore: read error on {pf}: {error.Message}\n");
                    return -1;
                }
            }
            else
            {
                n = Math.Min(buf.Length - bufi, SConn.Maxmsg - ivo);
                buf.AsSpan(bufi, n).CopyTo(b.AsSpan(ivo));
                bufi += n;
            }

            n += ivo;
            ivo = 0;

            // At the end of the input, add the check, which may take n past Maxmsg.
            if (n < SConn.Maxmsg)
            {
                Check.CopyTo(b, n);
                n += Chk;
                done = true;
            }

            aes.CbcEncrypt(b.AsSpan(0, n));
            if (n > SConn.Maxmsg)
            {
                await conn.WriteAsync(b, SConn.Maxmsg);
                n -= SConn.Maxmsg;
                b.AsSpan(SConn.Maxmsg, n).CopyTo(b);
            }

            await conn.WriteAsync(b, n);
        }

        if (buf is null)
        {
            await process.CloseAsync(fd);
        }

        await PrintAsync($"secstore: saved {len} bytes\n");
        return 0;
    }

    private async Task<int> RemovefileAsync(SConn conn, string rf)
    {
        if (rf.Contains('/', StringComparison.Ordinal))
        {
            await PrintAsync($"secstore: simple filenames, not paths like {rf}\n");
            return -1;
        }

        await conn.WriteAsync($"RM {rf}");
        return 0;
    }

    private async Task<int> CmdAsync(AuthConn c, List<(string Name, bool G)> gf, List<string> pf, List<string> rf)
    {
        try
        {
            foreach ((string name, bool g) in gf)
            {
                if (verbose)
                {
                    await PrintAsync($"get {name}\n");
                }

                (int rc, byte[]? memfile) = await GetfileAsync(c.Conn, name, g, c.Pass);
                if (rc < 0)
                {
                    return -1;
                }

                if (g)
                {
                    await WriteLinesAsync(memfile!);
                }
            }

            foreach (string name in pf)
            {
                if (verbose)
                {
                    await PrintAsync($"put {name}\n");
                }

                if (await PutfileAsync(c.Conn, name, null, c.Pass) < 0)
                {
                    return -1;
                }
            }

            foreach (string name in rf)
            {
                if (verbose)
                {
                    await PrintAsync($"rm  {name}\n");
                }

                if (await RemovefileAsync(c.Conn, name) < 0)
                {
                    return -1;
                }
            }

            await c.Conn.WriteAsync("BYE");
            return 0;
        }
        finally
        {
            await c.Conn.FreeAsync();
        }
    }

    // A line at a time, as /mnt/factotum/ctl needs.
    private async Task WriteLinesAsync(byte[] memfile)
    {
        for (int cur = 0; cur < memfile.Length;)
        {
            int next = Array.IndexOf(memfile, (byte)'\n', cur);
            int end = next < 0 ? memfile.Length : next + 1;
            await process.WriteAsync(1, memfile.AsMemory(cur, end - cur));
            cur = end;
        }
    }

    private async Task<int> ChpasswdAsync(AuthConn c, string id)
    {
        string? newpass = null;
        try
        {
            // Changing our password is vulnerable to connection failure.
            while (true)
            {
                newpass = await Authsrv.ReadconsAsync(process, $"new password for {id}", null, true);
                if (newpass is null)
                {
                    return -1;
                }

                if (Encoding.UTF8.GetByteCount(newpass) >= 7)
                {
                    break;
                }

                if (newpass.Length == 0)
                {
                    await PrintAsync("!password change aborted\n");
                    return -1;
                }

                await process.WriteAsync(1, "!password must be at least 7 characters\n"u8.ToArray());
            }

            string? passck = await Authsrv.ReadconsAsync(process, "retype password", null, true);
            if (passck is null)
            {
                await PrintAsync("secstore: input aborted\n");
                return -1;
            }

            if (passck != newpass)
            {
                await PrintAsync("secstore: passwords didn't match\n");
                return -1;
            }

            await c.Conn.WriteAsync("CHPASS");
            await c.Conn.WriteAsync(Pak.Hi(id, newpass).HexHi);
            (int rc, byte[]? list) = await GetfileAsync(c.Conn, ".", true, null);
            if (rc < 0)
            {
                await PrintAsync("secstore: directory listing failed.\n");
                return -1;
            }

            // Encrypt each file again under the new password, going on after an error.
            string[] lines = Encoding.UTF8.GetString(list!).Split('\n');
            foreach (string line in lines.Take(lines.Length - 1))
            {
                string[] f = [.. Process.Tokenize(line, 8)];
                if (f.Length < 1)
                {
                    break;
                }

                string f0 = f[0];

                await PrintAsync($"secstore: reencrypting '{f0}'\n");
                (rc, byte[]? memfile) = await GetfileAsync(c.Conn, f0, true, c.Pass);
                if (rc < 0)
                {
                    await PrintAsync($"secstore: getfile of '{f0}' failed\n");
                    continue;
                }

                if (await PutfileAsync(c.Conn, f0, memfile, Encoding.UTF8.GetBytes(newpass)) < 0)
                {
                    await PrintAsync($"secstore: putfile of '{f0}' failed\n");
                }
            }

            await c.Conn.WriteAsync("BYE");
            return 0;
        }
        finally
        {
            await c.Conn.FreeAsync();
        }
    }

    // login: a connection to the first store of dest that answers, logged in as id with the
    // password from the nvram, standard input or the console, which may be typed again.
    private async Task<AuthConn?> LoginAsync(string id, List<string> dest, bool passStdin, bool passNvram)
    {
        string pass = string.Empty;
        string? pinsta = null;
        if (passNvram)
        {
            var nvr = new Nvrsafe();
            if (await Authsrv.ReadnvramAsync(process, nvr, 0) < 0)
            {
                if (verbose)
                {
                    await PrintAsync($"secstore: readnvram: {Errstr.Get(process)}\n");
                }

                process.Exits("readnvram failed");
            }

            pass = Strecpy(Authsrv.Str(nvr.Config));
        }

        if (passStdin)
        {
            // So that len(PINSTA) < Maxmsg-3.
            byte[] s = await Authsrv.ReadnAsync(process, 0, SConn.Maxmsg - 2);
            if (s.Length < 1)
            {
                process.Exits("no password on standard input");
            }

            string text = Encoding.UTF8.GetString(s);
            int nl = text.IndexOf('\n', StringComparison.Ordinal);
            if (nl >= 0)
            {
                pinsta = text[(nl + 1)..];
                int nl2 = pinsta.IndexOf('\n', StringComparison.Ordinal);
                pinsta = nl2 < 0 ? pinsta : pinsta[..nl2];
                text = text[..nl];
            }

            pass = Strecpy(text);
        }

        int d = 0, ntry = 0;
        SConn? conn = null;
        string? server = null;
        while (true)
        {
            int fd;
            for (; ; d++)
            {
                if (verbose)
                {
                    await PrintAsync($"dialing {dest[d]}\n");
                }

                try
                {
                    fd = await Net.DialAsync(process, Net.Netmkaddr(dest[d], "tcp", "5356"));
                    break;
                }
                catch (SyscallException error)
                {
                    if (d + 1 == dest.Count)
                    {
                        if (verbose)
                        {
                            await PrintAsync($"secstore: can't dial {dest[d]}: {error.Message}\n");
                        }

                        process.Exits("dial failed");
                    }
                }
            }

            conn = new SConn(process, fd);
            ntry++;
            if (!passStdin && !passNvram)
            {
                string? typed = await Authsrv.ReadconsAsync(process, "secstore password", null, true);
                if (typed is null)
                {
                    await PrintAsync("secstore: password input aborted\n");
                    process.Exits("password input aborted");
                }

                if (Encoding.UTF8.GetByteCount(typed!) >= Passlen)
                {
                    await PrintAsync("secstore: password too long, skipping secstore login\n");
                    process.Exits("password too long");
                }

                pass = typed!;
            }

            if (pass.Length == 0)
            {
                await PrintAsync("secstore: null password, skipping secstore login\n");
                process.Exits("no password");
            }

            int rc;
            (rc, server) = await Pak.ClientAsync(process, argv0, verbose, conn, id, pass);
            if (rc >= 0)
            {
                break;
            }

            await conn.FreeAsync();
            if (passStdin)
            {
                process.Exits("invalid password on standard input");
            }

            if (passNvram)
            {
                process.Exits("invalid password in nvram");
            }

            // And let the user try typing the password again.
            if (ntry == 3)
            {
                await PrintAsync("Enter an empty password to quit.\n");
            }
        }

        await PrintAsync($"{server}\n");
        (int n, string reply) = await conn!.ReadstrAsync();
        if (n < 0)
        {
            await conn.FreeAsync();
            return null;
        }

        if (reply == "STA")
        {
            string response;
            if (passStdin)
            {
                if (pinsta is null)
                {
                    process.Exits("missing PIN+SecureID on standard input");
                }

                response = pinsta!;
            }
            else
            {
                response = await Authsrv.ReadconsAsync(process, "STA PIN+SecureID", null, true) ?? string.Empty;
            }

            if (verbose)
            {
                await PrintAsync($"{Encoding.UTF8.GetByteCount(response)}\n");
            }

            await conn.WriteAsync($"STA{response}");
            (_, reply) = await conn.ReadstrAsync();
        }

        if (reply != "OK")
        {
            await PrintAsync($"secstore: {reply}\n");
            await conn.FreeAsync();
            return null;
        }

        return new AuthConn(conn, Encoding.UTF8.GetBytes(pass));
    }

    private async Task SysfatalAsync(string message)
    {
        await PrintAsync($"{argv0}: {message}\n");
        process.Exits(message);
    }

    private Task PrintAsync(string text) => process.WriteAsync(2, Encoding.UTF8.GetBytes(text)).AsTask();

    private sealed record AuthConn(SConn Conn, byte[] Pass);
}
