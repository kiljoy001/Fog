using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dp9ik;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// libauthsrv, as 9front's: tickets, authenticators and ticket requests to and from their wire forms,
// AuthPAK, a prompt on the console, the nvram, and the auth server dialed and talked to. dp9ik.net's
// tickets and authenticators seal and open what crosses the wire.
public static class Authsrv
{
    public const int Anamelen = 28;
    public const int Domlen = 48;
    public const int Deskeylen = 7;
    public const int Aeskeylen = 16;
    public const int Challen = 8;
    public const int Configlen = 14;
    public const int Noncelen = 32;
    public const int Passwdlen = 28;
    public const int Pakylen = 56;
    public const int Tickreqlen = (3 * Anamelen) + Challen + Domlen + 1;
    public const int Maxticketlen = 12 + Challen + (2 * Anamelen) + Noncelen + 16;
    public const int Maxauthentlen = 12 + Challen + Noncelen + 16;
    public const byte AuthTreq = 1;
    public const byte AuthOK = 4;
    public const byte AuthErr = 5;
    public const byte AuthOKvar = 9;
    public const byte AuthPAK = 19;
    public const byte AuthTs = 64;
    public const byte AuthTc = 65;
    public const byte AuthAs = 66;
    public const byte AuthAc = 67;
    public const int NVwrite = 1;
    public const int NVwriteonerr = 2;
    public const int NVwritemem = 4;
    private const string Pbmsg = "AS protocol botch";

    private static readonly (byte Num, byte[] Sig)[] Form1sig =
    [
        (3, "form1 PR"u8.ToArray()),
        (AuthTs, "form1 Ts"u8.ToArray()),
        (AuthTc, "form1 Tc"u8.ToArray()),
        (AuthAs, "form1 As"u8.ToArray()),
        (AuthAc, "form1 Ac"u8.ToArray()),
        (68, "form1 Tp"u8.ToArray()),
        (69, "form1 Hr"u8.ToArray()),
    ];

    // nvtab: Fog has no $cputype, and keeps its nvram in #c, as the host gives it, at the start.
    private static readonly (string File, long Off, int Len)[] Nvtab = [("#c/nvram", 0, Nvrsafe.Size)];

    // Where genrandom's bytes come from in what a test runs, so it can know them; null elsewhere.
    public static AsyncLocal<Action<Span<byte>>?> Random { get; } = new();

    // genrandom: random bytes.
    public static void Genrandom(Span<byte> b)
    {
        if (Random.Value is { } source)
        {
            source(b);
        }
        else
        {
            RandomNumberGenerator.Fill(b);
        }
    }

    // dec16: the bytes of the hex digits in s, anything else passed over, at most lim of them.
    public static int Dec16(byte[] output, int lim, string s)
    {
        int o = 0, w = 0, i = 0;
        foreach (char c in s)
        {
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'F' ? c - 'A' + 10 : c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;
            if (d < 0)
            {
                continue;
            }

            (w, i) = ((w << 4) + d, i + 1);
            if (i == 2)
            {
                if (o == lim)
                {
                    break;
                }

                output[o++] = (byte)w;
                (w, i) = (0, 0);
            }
        }

        return o;
    }

    // The text of a NUL-ended field.
    public static string Str(byte[] field)
    {
        int nul = Array.IndexOf(field, (byte)0);
        return Encoding.UTF8.GetString(field, 0, nul < 0 ? field.Length : nul);
    }

    // safecpy: at most all but the last byte of the field, the rest of it zero.
    public static void Safecpy(byte[] to, string from)
    {
        Array.Clear(to);
        byte[] b = Encoding.UTF8.GetBytes(from);
        b.AsSpan(0, Math.Min(b.Length, to.Length - 1)).CopyTo(to);
    }

    public static byte Nvcsum(ReadOnlySpan<byte> mem)
    {
        byte sum = 9;
        foreach (byte b in mem)
        {
            sum += b;
        }

        return sum;
    }

    public static byte[] ConvTR2M(Ticketreq f) => [f.Type, .. f.Authid, .. f.Authdom, .. f.Chal, .. f.Hostid, .. f.Uid];

    public static int ConvM2TR(byte[] ap, int n, Ticketreq f)
    {
        f.Type = 0;
        foreach (byte[] field in new[] { f.Authid, f.Authdom, f.Chal, f.Hostid, f.Uid })
        {
            Array.Clear(field);
        }

        if (n < Tickreqlen)
        {
            return -Tickreqlen;
        }

        int p = 0;
        f.Type = ap[p++];
        foreach (byte[] field in new[] { f.Authid, f.Authdom, f.Chal, f.Hostid, f.Uid })
        {
            Array.Copy(ap, p, field, 0, field.Length);
            p += field.Length;
        }

        f.Authid[^1] = f.Authdom[^1] = f.Hostid[^1] = f.Uid[^1] = 0;
        return p;
    }

    // form1check: the number a form1 message's signature gives, or -1.
    public static int Form1check(byte[] ap, int n)
    {
        if (n < 8)
        {
            return -1;
        }

        foreach ((byte num, byte[] sig) in Form1sig)
        {
            if (ap.AsSpan(0, 8).SequenceEqual(sig))
            {
                return num;
            }
        }

        return -1;
    }

    public static byte[] ConvT2M(Ticket f, Authkey key) => Sealed(f).Marshal(key.K);

    // convM2T: the length of the ticket in ap, or less than nothing, as much as it needs, when ap is
    // short; f is the ticket opened with k, all zero but its form when it does not open.
    public static int ConvM2T(byte[] ap, int n, Ticket? f, Authkey? k)
    {
        if (f is not null)
        {
            Clear(f);
        }

        if (n < 8)
        {
            return -8;
        }

        // Stryker disable once Equality : form1check gives a number of three or more, or -1
        bool form0 = Form1check(ap, n) < 0;
        int m = form0 ? 1 + Challen + (2 * Anamelen) + Deskeylen : Maxticketlen;
        if (n < m)
        {
            return -m;
        }

        if (f is null || k is null)
        {
            return m;
        }

        f.Form = (byte)(form0 ? 0 : 1);
        if (Dp9ik.Ticket.TryUnmarshal(k.K, ap.AsSpan(0, m), out Dp9ik.Ticket? t, out _))
        {
            f.Num = (byte)t!.Type;
            t.Challenge.CopyTo(f.Chal, 0);
            t.ClientUser.CopyTo(f.Cuid, 0);
            t.ServerUser.CopyTo(f.Suid, 0);
            t.SessionKey.CopyTo(f.Key, 0);
        }

        return m;
    }

    public static byte[] ConvA2M(Authenticator f, Ticket t)
    {
        var a = new Dp9ik.Authenticator((AuthMessageType)f.Num);
        a.SetChallenge(f.Chal);
        a.SetRandom(f.Rand);
        return a.Marshal(Sealed(t));
    }

    // convM2A: as convM2T, the authenticator opened with the ticket's key.
    public static int ConvM2A(byte[] ap, int n, Authenticator f, Ticket t)
    {
        f.Num = 0;
        Array.Clear(f.Chal);
        Array.Clear(f.Rand);
        int m = t.Form == 0 ? 1 + Challen + 4 : Maxauthentlen;
        if (n < m)
        {
            return -m;
        }

        if (Dp9ik.Authenticator.TryUnmarshal(Sealed(t), ap.AsSpan(0, m), out Dp9ik.Authenticator? a, out _))
        {
            f.Num = (byte)a!.Type;
            a.Challenge.CopyTo(f.Chal, 0);
            a.Random.CopyTo(f.Rand, 0);
        }

        return m;
    }

    // authpak_new: the public value y, the secret kept in p.
    public static byte[] AuthpakNew(out AuthPakState p, Authkey k, bool isclient)
    {
        p = new AuthPakState();
        return p.CreatePublicValue(k.K, isclient);
    }

    // authpak_finish: the PAK key from the peer's y, or -1 for a y refused.
    public static int AuthpakFinish(AuthPakState p, Authkey k, ReadOnlySpan<byte> y)
    {
        try
        {
            p.Finish(k.K, y);
            return 0;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    // readn: n bytes, or fewer when the file ends first.
    public static async Task<byte[]> ReadnAsync(Process up, int fd, int n)
    {
        var buf = new List<byte>();
        for (ReadOnlyMemory<byte> b; buf.Count < n && !(b = await up.ReadAsync(fd, n - buf.Count)).IsEmpty;)
        {
            buf.AddRange(b.Span);
        }

        return [.. buf];
    }

    // readcons: a reply to the prompt from the console, its default for an empty one, and edited
    // as it is typed: backspace takes back a rune, ^U the whole, and Del or the end of input gives
    // up; raw, it is not echoed.
    public static async Task<string?> ReadconsAsync(Process up, string prompt, string? def, bool raw)
    {
        int fdin = -1, fdout = -1, ctl = -1;
        try
        {
            fdin = await up.OpenAsync("/dev/cons", NinePConstants.OREAD | NinePConstants.OCEXEC);
            fdout = await up.OpenAsync("/dev/cons", NinePConstants.OWRITE | NinePConstants.OCEXEC);
            if (raw)
            {
                ctl = await up.OpenAsync("/dev/consctl", NinePConstants.OWRITE | NinePConstants.OCEXEC);
                await IgnoredAsync(up, ctl, "rawon");
            }

            string ask = def is null ? $"{prompt}: " : $"{prompt}[{def}]: ";
            await IgnoredAsync(up, fdout, ask);
            string? reply = await ReplyAsync(up, fdin, fdout, ask, def);
            if (raw)
            {
                await IgnoredAsync(up, fdout, "\n");
            }

            return reply;
        }
        catch (SyscallException error)
        {
            Errstr.Set(up, error.Message);
            return null;
        }
        finally
        {
            // Stryker disable once Equality : consctl is opened after both consoles, which take any lower descriptor, so it is never 0
            if (ctl >= 0)
            {
                await IgnoredAsync(up, ctl, "rawoff");
                await UnclosedAsync(up, ctl);
            }

            foreach (int fd in new[] { fdin, fdout }.Where(fd => fd >= 0))
            {
                await UnclosedAsync(up, fd);
            }
        }
    }

    // readnvram: the key information in the nvram into safe, or, as flag asks, the nvram written with
    // what safe holds or what is asked for on the console; -1 for an nvram unread or bad.
    public static async Task<int> ReadnvramAsync(Process up, Nvrsafe safep, int flag)
    {
        (int fd, int safelen, long safeoff, string? nvrfile) = await FindnvramAsync(up);

        // Stryker disable once Conditional : a length still -1 is no nvram's, which is not read or written
        safelen = safelen < 0 ? Nvrsafe.Size : Math.Min(safelen, 512);
        if (safeoff < 0)
        {
            await PrintAsync(up, "readnvram: couldn't find nvram\n");

            // Stryker disable once Bitwise,Statement,Block : what is not written from is cleared again before it is read into
            if ((flag & NVwritemem) == 0)
            {
                safep.Clear();
            }
        }

        try
        {
            bool err = (flag & NVwritemem) == 0 && await ReadsafeAsync(up, safep, fd, safelen, safeoff, nvrfile, flag);
            if ((flag & (NVwrite | NVwritemem)) != 0 || (err && (flag & NVwriteonerr) != 0))
            {
                if ((flag & NVwritemem) == 0 && !await AskforkeyAsync(up, safep))
                {
                    return -1;
                }

                err = !await WritesafeAsync(up, safep, fd, safelen, safeoff);
            }

            return err ? -1 : 0;
        }
        finally
        {
            if (fd >= 0)
            {
                await up.CloseAsync(fd);
            }
        }
    }

    // authdial: the auth server of the domain, as cs names it by authdom or dom, or p9auth.dom, each
    // of several tried in turn; or $auth, for no domain.
    public static async Task<int> AuthdialAsync(Process up, string? netroot, string? dom)
    {
        if (dom is null)
        {
            return await DialAsync(up, Net.Netmkaddr("$auth", null, "ticket"));
        }

        (string? p, NdbTuple? t) = await Ndb.CsGetValuesAsync(up, netroot, "authdom", dom, "auth");
        if (p is null)
        {
            (p, t) = await Ndb.CsGetValuesAsync(up, netroot, "dom", dom, "auth");
        }

        if (p is null)
        {
            t = new NdbTuple("auth", $"p9auth.{dom}");
        }

        foreach (NdbTuple nt in t!.Entries().Where(nt => nt.Attr == "auth"))
        {
            // Stryker disable once Equality : a call's data is opened while its ctl is, so it is never 0
            if (await DialAsync(up, Net.Netmkaddr(nt.Val, null, "ticket")) is int fd and >= 0)
            {
                return fd;
            }
        }

        return -1;
    }

    // _asrequest: as a write is whole or fails, only its failure is looked at.
    public static async Task<int> AsrequestAsync(Process up, int fd, Ticketreq tr)
    {
        try
        {
            await up.WriteAsync(fd, ConvTR2M(tr));
            return 0;
        }
        catch (SyscallException error)
        {
            Errstr.Set(up, error.Message);
            return -1;
        }
    }

    // _asrdresp: a reply of len bytes after AuthOK, or of as many as AuthOKvar says, no more than
    // len; the server's error after AuthErr.
    public static async Task<(int N, byte[] Buf)> AsrdrespAsync(Process up, int fd, int len)
    {
        byte[] buf = await ReadnAsync(up, fd, 1);
        if (buf.Length != 1)
        {
            return Botch(up);
        }

        switch (buf[0])
        {
            case AuthOK:
                buf = await ReadnAsync(up, fd, len);
                return buf.Length != len ? Botch(up) : (len, buf);
            case AuthErr:
                byte[] error = await ReadnAsync(up, fd, 64);
                if (error.Length != 64)
                {
                    return Botch(up);
                }

                error[^1] = 0;
                Errstr.Set(up, $"remote: {Str(error)}");
                return (-1, []);
            case AuthOKvar:
                byte[] count = await ReadnAsync(up, fd, 5);
                if (count.Length != 5)
                {
                    return Botch(up);
                }

                int n = Atoi(Str(count));
                if (n <= 0 || n > len)
                {
                    return Botch(up);
                }

                buf = await ReadnAsync(up, fd, n);
                return buf.Length != n ? Botch(up) : (n, [.. buf, .. new byte[len - n]]);
            default:
                return Botch(up);
        }
    }

    // _asgetticket: the two tickets the server gives for the request, read as their lengths say.
    public static async Task<(int R, byte[] Tbuf)> AsgetticketAsync(Process up, int fd, Ticketreq tr, int tbuflen)
    {
        string err = Errstr.Get(up);
        Errstr.Set(up, Pbmsg);
        if (await AsrequestAsync(up, fd, tr) < 0 || (await AsrdrespAsync(up, fd, 0)).N < 0)
        {
            return (-1, []);
        }

        var tbuf = new List<byte>();
        for (int i = 0; i < 2; i++)
        {
            var t = new List<byte>();

            // Stryker disable once Equality : convM2T never gives none
            for (int m; (m = ConvM2T([.. t], t.Count, null, null)) <= 0;)
            {
                m = -m;

                // Stryker disable once Equality : convM2T asks only for more than it has
                if (m <= t.Count || m > tbuflen)
                {
                    return (-1, []);
                }

                m -= t.Count;
                byte[] more = await ReadnAsync(up, fd, m);
                if (more.Length != m)
                {
                    return (-1, []);
                }

                t.AddRange(more);
            }

            tbuf.AddRange(t);
            tbuflen -= t.Count;
        }

        Errstr.Set(up, err);
        return (tbuf.Count, [.. tbuf]);
    }

    // The reply read a byte at a time to a newline or return, edited as it comes, or its default for
    // an empty one; null when the read fails or the input ends or is deleted.
    private static async Task<string?> ReplyAsync(Process up, int fdin, int fdout, string ask, string? def)
    {
        var s = new List<byte>();
        while (true)
        {
            ReadOnlyMemory<byte> b;
            try
            {
                b = await up.ReadAsync(fdin, 1);
            }
            catch (SyscallException error)
            {
                Errstr.Set(up, error.Message);
                return null;
            }

            if (b.IsEmpty || b.Span[0] == 0x7f)
            {
                Errstr.Set(up, "input aborted");
                return null;
            }

            byte p = b.Span[0];
            if (p is (byte)'\n' or (byte)'\r')
            {
                return s.Count == 0 && def is not null ? def : Encoding.UTF8.GetString([.. s]);
            }

            if (Edit(s, p))
            {
                await IgnoredAsync(up, fdout, "\n" + ask);
            }
        }
    }

    // A byte typed: backspace takes back a rune, ^U the whole, which asks again, and a control
    // character is no part of it. Whether to ask again.
    private static bool Edit(List<byte> s, byte p)
    {
        if (p == '\b')
        {
            while (s.Count > 0 && (s[^1] & 0xc0) == 0x80)
            {
                s.RemoveAt(s.Count - 1);
            }

            if (s.Count > 0)
            {
                s.RemoveAt(s.Count - 1);
            }
        }
        else if (p == 0x15)
        {
            s.Clear();
            return true;
        }
        else if (p >= ' ')
        {
            s.Add(p);
        }

        return false;
    }

    // The nvram read into safe, cleared first, and checked; whether it could not be read or was bad,
    // said so only when it may be written.
    private static async Task<bool> ReadsafeAsync(Process up, Nvrsafe safe, int fd, int safelen, long safeoff, string? nvrfile, int flag)
    {
        safe.Clear();
        byte[] buf = [];
        string? why = fd < 0 ? $"can't open {nvrfile ?? "<nil>"}: {Errstr.Get(up)}\n" : null;
        if (why is null)
        {
            try
            {
                buf = (await up.PReadAsync(fd, safelen, safeoff)).ToArray();
            }
            catch (SyscallException error)
            {
                Errstr.Set(up, error.Message);
            }

            why = buf.Length != safelen ? $"can't read {safelen} bytes from {nvrfile ?? "<nil>"}: {Errstr.Get(up)}\n" : null;
        }

        if (why is not null)
        {
            if ((flag & (NVwrite | NVwriteonerr)) != 0)
            {
                await PrintAsync(up, why);
            }

            return true;
        }

        Nvrsafe.FromBytes(buf).CopyTo(safe);
        return await BadAsync(up, safe);
    }

    // Whether what was read is bad: a field whose sum is wrong, cleared and said so, or no authid
    // or domain. A bad config or AES key is quietly cleared.
    private static async Task<bool> BadAsync(Process up, Nvrsafe safe)
    {
        bool err = await CheckAsync(up, safe.Machkey, safe.Machsum, "bad nvram des key");
        err = await CheckAsync(up, safe.Authid, safe.Authidsum, "bad authentication id") || err;
        err = await CheckAsync(up, safe.Authdom, safe.Authdomsum, "bad authentication domain") || err;
        if (Nvcsum(safe.Config) != safe.Configsum)
        {
            Array.Clear(safe.Config);
        }

        if (Nvcsum(safe.Aesmachkey) != safe.Aesmachsum)
        {
            Array.Clear(safe.Aesmachkey);
        }

        if (!err && (safe.Authid[0] == 0 || safe.Authdom[0] == 0))
        {
            await PrintAsync(up, "empty nvram authid or authdom\n");
            err = true;
        }

        return err;
    }

    // safe written to the nvram with its sums made, in as many bytes as the nvram's length; whether
    // it was.
    private static async Task<bool> WritesafeAsync(Process up, Nvrsafe safe, int fd, int safelen, long safeoff)
    {
        safe.Machsum = Nvcsum(safe.Machkey);
        safe.Configsum = Nvcsum(safe.Config);
        safe.Authidsum = Nvcsum(safe.Authid);
        safe.Authdomsum = Nvcsum(safe.Authdom);
        safe.Aesmachsum = Nvcsum(safe.Aesmachkey);
        byte[] buf = new byte[Math.Max(safelen, Nvrsafe.Size)];
        safe.ToBytes().CopyTo(buf, 0);
        bool written = false;
        try
        {
            written = fd >= 0 && await up.PWriteAsync(fd, buf.AsMemory(0, safelen), safeoff) == safelen;
        }
        catch (SyscallException error)
        {
            Errstr.Set(up, error.Message);
        }

        if (!written)
        {
            await PrintAsync(up, $"can't write key to nvram: {Errstr.Get(up)}\n");
        }

        return written;
    }

    private static (int N, byte[] Buf) Botch(Process up)
    {
        Errstr.Set(up, Pbmsg);
        return (-1, []);
    }

    // atoi: the number at the start, after any blanks.
    private static int Atoi(string s) => Libc.Strtol(s, 10, out _);

    private static void Clear(Ticket f)
    {
        (f.Num, f.Form) = (0, 0);
        foreach (byte[] field in new[] { f.Chal, f.Cuid, f.Suid, f.Key })
        {
            Array.Clear(field);
        }
    }

    // The ticket as dp9ik.net seals it.
    private static Dp9ik.Ticket Sealed(Ticket f)
    {
        var t = new Dp9ik.Ticket((AuthMessageType)f.Num, (TicketEncryptionForm)f.Form);
        f.Chal.CopyTo(t.Challenge, 0);
        f.Cuid.CopyTo(t.ClientUser, 0);
        f.Suid.CopyTo(t.ServerUser, 0);
        f.Key.CopyTo(t.SessionKey, 0);
        return t;
    }

    private static async Task<int> DialAsync(Process up, string addr)
    {
        try
        {
            return await Net.DialAsync(up, addr);
        }
        catch (SyscallException error)
        {
            Errstr.Set(up, error.Message);
            return -1;
        }
    }

    private static async Task PrintAsync(Process up, string s) => await IgnoredAsync(up, 2, s);

    // A write whose failure is not looked at, as fprint's and write's are not.
    private static async Task IgnoredAsync(Process up, int fd, string s)
    {
        try
        {
            await up.WriteAsync(fd, Encoding.UTF8.GetBytes(s));
        }
        catch (SyscallException)
        {
        }
    }

    // A close whose failure is not looked at, as readcons's are not: an exec by a process sharing
    // the descriptors may have closed what was opened OCEXEC.
    private static async Task UnclosedAsync(Process up, int fd)
    {
        try
        {
            await up.CloseAsync(fd);
        }
        catch (SyscallException)
        {
        }
    }

    // check: a field whose sum is wrong cleared, and said so.
    private static async Task<bool> CheckAsync(Process up, byte[] x, byte sum, string msg)
    {
        if (Nvcsum(x) == sum)
        {
            return false;
        }

        Array.Clear(x);
        await PrintAsync(up, msg + "\n");
        return true;
    }

    // ask: a reply shorter than the field, asked for until one is.
    private static async Task<bool> AskAsync(Process up, string prompt, byte[] buf, bool raw)
    {
        Array.Clear(buf);
        while (true)
        {
            if (await ReadconsAsync(up, prompt, null, raw) is not { } s)
            {
                return false;
            }

            byte[] b = Encoding.UTF8.GetBytes(s);
            if (b.Length < buf.Length)
            {
                b.CopyTo(buf, 0);
                return true;
            }

            await PrintAsync(up, $"{prompt} longer than {buf.Length - 1} characters; try again\n");
        }
    }

    // What readnvram asks for: the authid, its domain, the secstore key and the password, twice,
    // and whether the DES key for p9sk1 is kept as well as the AES key.
    private static async Task<bool> AskforkeyAsync(Process up, Nvrsafe safe)
    {
        byte[] pass = new byte[Passwdlen], pass2 = new byte[Passwdlen];
        if (!await AskAsync(up, "authid", safe.Authid, false) || !await AskAsync(up, "authdom", safe.Authdom, false) || !await AskAsync(up, "secstore key", safe.Config, true))
        {
            return false;
        }

        while (true)
        {
            if (!await AskAsync(up, "password", pass, true) || !await AskAsync(up, "confirm password", pass2, true))
            {
                return false;
            }

            if (pass.AsSpan().SequenceEqual(pass2))
            {
                break;
            }

            await PrintAsync(up, "password mismatch\n");
        }

        if (await ReadconsAsync(up, "enable legacy p9sk1", "no", false) is not { } dodes)
        {
            return false;
        }

        Authkey k = Authkey.Passtokey(Str(pass));
        if (dodes.StartsWith('y') || dodes.StartsWith('Y'))
        {
            k.K.DesKey.CopyTo(safe.Machkey, 0);
        }
        else
        {
            Array.Clear(safe.Machkey);
        }

        k.K.AesKey.CopyTo(safe.Aesmachkey, 0);
        return true;
    }

    // findnvram: $nvram, a file or file!dosfile, at $nvroff, or in the dos file system there; or
    // the first of nvtab that opens. The file is opened for writing if it can be.
    private static async Task<(int Fd, int Safelen, long Safeoff, string? Nvrfile)> FindnvramAsync(Process up)
    {
        string? nvrfile = await Cs.GetenvAsync(up, "nvram");

        // Stryker disable once Unary : the descriptor is set before it is looked at, and a length left -1 is no nvram's
        int fd = -1, safelen = -1;
        long safeoff = -1;
        if (!string.IsNullOrEmpty(nvrfile))
        {
            string[] v = [.. nvrfile.Split('!', StringSplitOptions.RemoveEmptyEntries).Take(2)];

            // Stryker disable once String : no name opens nothing, whatever the name
            v = v.Length < 1 ? [string.Empty] : v;
            fd = await OpenAsync(up, v[0]);
            safelen = Nvrsafe.Size;
            if (!v[0].Contains("/9fat", StringComparison.Ordinal))
            {
                safeoff = 0;
            }

            if (await Cs.GetenvAsync(up, "nvrlen") is { } nvrlen)
            {
                safelen = Libc.Strtol(nvrlen, 0, out _);
            }

            if (await Cs.GetenvAsync(up, "nvroff") is { } nvroff)
            {
                safeoff = nvroff == "dos" ? -1 : Libc.Strtoll(nvroff, 0, out _);
            }

            if (safeoff < 0 && fd >= 0)
            {
                safelen = 512;
                safeoff = await FinddosfileAsync(up, fd, v.Length == 2 ? v[1] : "plan9.nvr");

                // Stryker disable once Equality : a FAT file starts past the boot sector and the root
                if (safeoff < 0)
                {
                    await up.CloseAsync(fd);
                    fd = -1;
                }
            }
        }
        else
        {
            foreach ((string file, long off, int len) in Nvtab)
            {
                if ((fd = await OpenAsync(up, file)) < 0)
                {
                    continue;
                }

                (safeoff, safelen) = (off, len);

                // Stryker disable once Statement : nvtab has the one entry
                break;
            }
        }

        return (fd, safelen, safeoff, nvrfile);
    }

    private static async Task<int> OpenAsync(Process up, string file)
    {
        foreach (int mode in new[] { NinePConstants.ORDWR, NinePConstants.OREAD })
        {
            try
            {
                return await up.OpenAsync(file, mode | NinePConstants.OCEXEC);
            }
            catch (SyscallException error)
            {
                Errstr.Set(up, error.Message);
            }
        }

        return -1;
    }

    // dosparse: a name's part before its dot, or its extension, upper-cased and padded with spaces.
    private static (byte[] To, string? After) Dosparse(string? from, int len)
    {
        byte[] to = Enumerable.Repeat((byte)' ', len).ToArray();
        if (from is null)
        {
            return (to, null);
        }

        for (int i = 0; i < len && i < from.Length; i++)
        {
            char c = from[i];
            if (c == '.')
            {
                return (to, from[(i + 1)..]);
            }

            to[i] = (byte)(c is >= 'a' and <= 'z' ? c + 'A' - 'a' : c);
        }

        return (to, null);
    }

    // finddosfile: where the first block of a file in a FAT file system's root starts, or -1.
    private static async Task<long> FinddosfileAsync(Process up, int fd, string file)
    {
        (byte[] name, string? rest) = Dosparse(file, 8);
        (byte[] ext, _) = Dosparse(rest, 3);
        byte[] secbuf = (await up.ReadAsync(fd, 512)).ToArray();
        if (secbuf.Length != 512 || secbuf[0] != 0xEB || secbuf[1] != 0x3C || secbuf[2] != 0x90)
        {
            return -1;
        }

        int sectsize = Getshort(secbuf, 11);
        if (sectsize != 512)
        {
            return -1;
        }

        int rootoff = (Getshort(secbuf, 14) + (secbuf[16] * Getshort(secbuf, 22))) * sectsize;
        int nroot = Getshort(secbuf, 17);
        int rootsects = ((nroot * 32) + sectsize - 1) / sectsize;

        // Stryker disable once Equality : a root of no sectors holds no file either
        if (rootsects is <= 0 or > 64)
        {
            return -1;
        }

        byte[] root = (await up.PReadAsync(fd, rootsects * sectsize, rootoff)).ToArray();
        if (root.Length != rootsects * sectsize)
        {
            return -1;
        }

        int n = -1;
        for (int dp = 0; dp < nroot * 32; dp += 32)
        {
            if (root.AsSpan(dp, 8).SequenceEqual(name) && root.AsSpan(dp + 8, 3).SequenceEqual(ext))
            {
                n = Getshort(root, dp + 26);
                break;
            }
        }

        return n < 0 ? -1 : rootoff + (rootsects * sectsize) + ((n - 2) * sectsize * secbuf[13]);
    }

    private static int Getshort(byte[] p, int o) => (p[o + 1] << 8) | p[o];
}
