using System.Security.Cryptography;
using System.Text;
using Dp9ik;
using NinePSharp.Fog.Kernel;
using static NinePSharp.Fog.Commands.Authsrv;

namespace NinePSharp.Fog.Commands;

// factotum's dp9ik, as p9sk1.c: Plan 9 shared key authentication, AuthPAK keeping the key from the
// password safe from an offline guess. p9sk1, which the file also holds, is left out, as Fog's auth
// server speaks only dp9ik.
//
// The client writes its challenge, reads the server's ticket request and AuthPAK value, writes its
// own value, writes the ticket and its authenticator, and reads the server's authenticator. The
// server does the other half. A login writes the user and then the password, and the server checks
// them with the auth server itself.
public sealed class P9sk : Proto
{
    // The client's phases, then the server's, then a login's; p9sk1's own, CNeedTreq and SHaveTreq,
    // are left out with it.
    private const int CHaveChal = 0;
    private const int CNeedPAKreq = 1;
    private const int CHavePAKy = 2;
    private const int CHaveTicket = 3;
    private const int CNeedAuth = 4;
    private const int SNeedChal = 5;
    private const int SHavePAKreq = 6;
    private const int SNeedPAKy = 7;
    private const int SNeedTicket = 8;
    private const int SHaveAuth = 9;
    private const int SNeedUser = 10;
    private const int SNeedPass = 11;
    private const int Maxphase = 12;

    // What the auth server's exchange reads into, as getastickets's tbuf holds it.
    private const int Tbuflen = (2 * Pakylen) + (2 * Maxticketlen);

    private static readonly string[] Phasenames =
    [
        "CHaveChal", "CNeedPAKreq", "CHavePAKy", "CHaveTicket", "CNeedAuth",
        "SNeedChal", "SHavePAKreq", "SNeedPAKy", "SNeedTicket", "SHaveAuth",
        "SNeedUser", "SNeedPass",
    ];

    public override string Name => "dp9ik";

    public override string Keyprompt => "user? !password?";

    public override bool TakesKeys => true;

    public override Task<Rpc> InitAsync(Factotum f, Fsstate fss)
    {
        string? role = Attr.StrFind(fss.Attr, "role");
        int iscli;
        if (role == "login")
        {
            iscli = 0;
        }
        else if ((iscli = Factotum.Isclient(role)) < 0)
        {
            return Task.FromResult(f.Failure(fss, null));
        }

        var s = new State();
        (fss.Phasename, fss.Maxphase, fss.Proto) = (Phasenames, Maxphase, this);
        if (iscli != 0)
        {
            fss.Phase = CHaveChal;
            Genrandom(s.Cchal);
        }
        else
        {
            // The server's key, whoever asks: fss's attributes already name the protocol, and what
            // p9sk1.c sets of them as a login's role, role is no part of a key's match.
            fss.Phase = role == "login" ? SNeedUser : SNeedChal;
            s.Tr.Type = AuthTreq;
            Rpc ret = f.Findkey(out Key? k, new Keyinfo(fss, null) { User = null }, "user? dom?");
            if (ret != Rpc.Ok)
            {
                return Task.FromResult(ret);
            }

            Safecpy(s.Tr.Authid, Attr.StrFind(k!.Attr, "user")!);
            Safecpy(s.Tr.Authdom, Attr.StrFind(k.Attr, "dom")!);
            s.Key = k;
            s.K = ((Authkey)k.Priv!).Copy();
            Genrandom(s.Tr.Chal);
        }

        fss.Ps = s;
        return Task.FromResult(Rpc.Ok);
    }

    public override Task<(Rpc Ret, byte[] A)> ReadAsync(Factotum f, Fsstate fss, int n)
    {
        var s = (State)fss.Ps!;
        int m;
        byte[] a;
        switch (fss.Phase)
        {
            case CHaveChal:
                m = Challen;

                // Stryker disable once Statement,Equality,Block : an rpc read is of at least 61 bytes, more than a challenge
                if (n < m)
                {
                    return Toosmall(fss, m);
                }

                fss.Phase = CNeedPAKreq;
                return Ok(s.Cchal);

            case SHavePAKreq:
                m = Tickreqlen + Pakylen;
                if (n < m)
                {
                    return Toosmall(fss, m);
                }

                s.Tr.Type = AuthPAK;
                a = [.. ConvTR2M(s.Tr), .. AuthpakNew(out AuthPakState p, s.K, true)];
                s.P = p;
                fss.Phase = SNeedPAKy;
                return Ok(a);

            case CHavePAKy:
                m = Pakylen;

                // Stryker disable once Statement,Equality,Block : an rpc read is of at least 61 bytes, more than an AuthPAK value
                if (n < m)
                {
                    return Toosmall(fss, m);
                }

                fss.Phase = CHaveTicket;
                return Ok(s.Y);

            case CHaveTicket:
                if (n < s.Tbuf.Length)
                {
                    return Toosmall(fss, s.Tbuf.Length);
                }

                fss.Phase = CNeedAuth;
                return Ok(s.Tbuf);

            case SHaveAuth:
                if (n < s.Tbuf.Length)
                {
                    return Toosmall(fss, s.Tbuf.Length);
                }

                return Task.FromResult((Establish(fss), s.Tbuf));

            default:
                return Task.FromResult((f.Phaseerror(fss, "read"), Array.Empty<byte>()));
        }
    }

    public override async Task<Rpc> WriteAsync(Factotum f, Fsstate fss, byte[] a)
    {
        var s = (State)fss.Ps!;
        return fss.Phase switch
        {
            SNeedChal => Chal(fss, s, a),
            CNeedPAKreq => await PakreqAsync(f, fss, s, a),
            SNeedPAKy => Paky(f, fss, s, a),
            SNeedTicket => TicketWrite(f, fss, s, a),
            CNeedAuth => AuthWrite(f, fss, s, a),
            SNeedUser => User(f, fss, s, a),
            SNeedPass => await LoginAsync(f, fss, s, a),
            _ => f.Phaseerror(fss, "write"),
        };
    }

    // p9skclose: what the conversation held, which the collector lets go of.
    public override void Close(Fsstate fss)
    {
    }

    // p9skaddkey: the key of the AES key in !hex, or of !password, hashed for its user.
    public override int Addkey(Factotum f, Key k, bool before)
    {
        if (Attr.StrFind(k.Attr, "user") is not { } u)
        {
            Factotum.Errstr = "no user attribute";
            return -1;
        }

        Authkey akey;
        if (Attr.StrFind(k.Privattr, "!hex") is { } s)
        {
            byte[] aes = new byte[Aeskeylen];
            if (Dec16(aes, Aeskeylen, s) != Aeskeylen)
            {
                Factotum.Errstr = "malformed key data";
                return -1;
            }

            akey = Authkey.FromKeys(new byte[Deskeylen], aes);
        }
        else if (Attr.StrFind(k.Privattr, "!password") is { } password)
        {
            akey = Authkey.Passtokey(password);
        }
        else
        {
            Factotum.Errstr = "no key data";
            return -1;
        }

        akey.Hash(u);
        k.Priv = akey;
        return f.Replacekey(k, before);
    }

    public override void Closekey(Key k) => k.Priv = null;

    // The server's read of the client's challenge.
    private static Rpc Chal(Fsstate fss, State s, byte[] a)
    {
        int m = Challen;
        if (a.Length < m)
        {
            return Factotum.Toosmall(fss, m);
        }

        Array.Copy(a, s.Cchal, m);
        fss.Phase = SHavePAKreq;
        return Rpc.Ok;
    }

    // The server's read of the client's AuthPAK value, which finishes the server's own.
    private static Rpc Paky(Factotum f, Fsstate fss, State s, byte[] a)
    {
        int m = Pakylen;
        if (a.Length < m)
        {
            return Factotum.Toosmall(fss, m);
        }

        if (AuthpakFinish(s.P!, s.K, a.AsSpan(0, m)) != 0)
        {
            return f.Failure(fss, Factotum.Easproto);
        }

        fss.Phase = SNeedTicket;
        return Rpc.Ok;
    }

    // The server's read of the ticket and the client's authenticator, each of the server's
    // challenge, and the server's authenticator made in answer.
    private static Rpc TicketWrite(Factotum f, Fsstate fss, State s, byte[] a)
    {
        int n = a.Length;
        var auth = new Authenticator();
        int m = ConvM2T(a, n, s.T, s.K);

        // Stryker disable once Equality : convM2T and convM2A never give none
        if (m <= 0 || ConvM2A(a[m..], n - m, auth, s.T) <= 0)
        {
            return Factotum.Toosmall(fss, Maxticketlen + Maxauthentlen);
        }

        if (s.T.Form == 0 || s.T.Num != AuthTs || !Same(s.T.Chal, s.Tr.Chal) || auth.Num != AuthAc || !Same(auth.Chal, s.Tr.Chal))
        {
            return f.Failure(fss, Factotum.Easproto);
        }

        auth.Rand.CopyTo(s.Rand, 0);
        Genrandom(s.Rand.AsSpan(Noncelen));
        auth.Num = AuthAs;
        s.Cchal.CopyTo(auth.Chal, 0);
        s.Rand.AsSpan(Noncelen).CopyTo(auth.Rand);
        s.Tbuf = ConvA2M(auth, s.T);
        fss.Phase = SHaveAuth;
        return Rpc.Ok;
    }

    // The client's read of the server's authenticator, of the client's challenge.
    private static Rpc AuthWrite(Factotum f, Fsstate fss, State s, byte[] a)
    {
        var auth = new Authenticator();
        int m = ConvM2A(a, a.Length, auth, s.T);

        // Stryker disable once Equality : convM2A never gives none
        if (m <= 0)
        {
            return Factotum.Toosmall(fss, -m);
        }

        if (auth.Num != AuthAs || !Same(auth.Chal, s.Cchal))
        {
            return f.Failure(fss, Factotum.Easproto);
        }

        auth.Rand.CopyTo(s.Rand, Noncelen);
        return Establish(fss);
    }

    // A login's user.
    private static Rpc User(Factotum f, Fsstate fss, State s, byte[] a)
    {
        if (a.Length >= Anamelen)
        {
            return f.Failure(fss, "user id too long");
        }

        Strncpy(s.Tr.Hostid, a);
        Strncpy(s.Tr.Uid, a);
        fss.Phase = SNeedPass;
        return Rpc.Ok;
    }

    private static bool Same(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);

    // strncpy into a field still empty, which then ends: what follows a NUL in it is never read.
    private static void Strncpy(byte[] to, byte[] a) => a.CopyTo(to, 0);

    private static Task<(Rpc Ret, byte[] A)> Ok(byte[] a) => Task.FromResult((Rpc.Ok, a));

    private static Task<(Rpc Ret, byte[] A)> Toosmall(Fsstate fss, int m) => Task.FromResult((Factotum.Toosmall(fss, m), Array.Empty<byte>()));

    // establish: the users and the session secret the nonces and the ticket's key give.
    private static Rpc Establish(Fsstate fss)
    {
        var s = (State)fss.Ps!;
        fss.Ai.Cuid = Str(s.T.Cuid);
        fss.Ai.Suid = Str(s.T.Suid);
        fss.Ai.Secret = SessionSecret.Derive(s.Rand.AsSpan(0, Noncelen), s.Rand.AsSpan(Noncelen), s.T.Key);
        fss.Haveai = true;
        fss.Phase = Fsstate.Established;
        return Rpc.Ok;
    }

    // mkservertickets: tickets made up for a host's own user, when the auth server cannot be had.
    private static (int Ret, byte[] Tbuf) Mkservertickets(State s, byte[] y)
    {
        if (Str(s.Tr.Authid) != Str(s.Tr.Hostid))
        {
            return (-1, []);
        }

        var t = new Ticket { Form = 1 };
        s.Y = AuthpakNew(out AuthPakState p, s.K, false);
        AuthpakFinish(p, s.K, y);
        s.Tr.Chal.CopyTo(t.Chal, 0);
        Strncpy(t.Cuid, s.Tr.Uid);
        Strncpy(t.Suid, s.Tr.Uid);
        Genrandom(t.Key);
        t.Num = AuthTc;
        byte[] tbuf = ConvT2M(t, s.K);
        t.Num = AuthTs;
        tbuf = [.. tbuf, .. ConvT2M(t, s.K)];
        return (tbuf.Length, tbuf);
    }

    // The client's write of the server's ticket request and AuthPAK value: a key found to speak for
    // the user, or the user's own, the tickets got, and the authenticator made.
    private async Task<Rpc> PakreqAsync(Factotum f, Fsstate fss, State s, byte[] a)
    {
        int n = a.Length, m = Tickreqlen + Pakylen;
        if (n < m)
        {
            return Factotum.Toosmall(fss, m);
        }

        m = ConvM2TR(a, n, s.Tr);
        if (s.Tr.Type != AuthPAK)
        {
            return f.Failure(fss, Factotum.Easproto);
        }

        // fss's attributes already name the protocol, which p9sk1.c sets again.
        Attr? attr = Attr.Delete(Attr.Delete(Attr.Copy(fss.Attr), "role"), "user");
        string? user = Attr.StrFind(fss.Attr, "user");
        if (user is not null)
        {
            attr = Factotum.Setattr(attr, $"user={Libc.Quote(user)}");
        }

        if (Choose(f, fss, s, attr, user) is { } refused)
        {
            return refused;
        }

        Safecpy(s.Tr.Hostid, Attr.StrFind(s.Key!.Attr, "user")!);
        if (s.Speakfor)
        {
            Safecpy(s.Tr.Uid, fss.Sysuser);
        }
        else
        {
            s.Tr.Hostid.CopyTo(s.Tr.Uid, 0);
        }

        s.K = ((Authkey)s.Key.Priv!).Copy();
        (int got, byte[] tbuf) = await GetticketsAsync(f, s, a[m..(m + Pakylen)]);

        // Stryker disable once Equality : gettickets gives a ticket or less than none
        if (got <= 0)
        {
            return f.Failure(fss, null);
        }

        m = ConvM2T(tbuf, got, s.T, s.K);

        // Stryker disable once Equality : convM2T never gives none
        if (m <= 0 || s.T.Form == 0)
        {
            return f.Failure(fss, Factotum.Easproto);
        }

        if (s.T.Num != AuthTc)
        {
            return Unopened(f, fss, s, attr);
        }

        // Stryker disable once Update : a key's successes are only ever told from none, which counting either way leaves
        s.Key.Successes++;
        Genrandom(s.Rand.AsSpan(0, Noncelen));
        var auth = new Authenticator { Num = AuthAc };
        s.Tr.Chal.CopyTo(auth.Chal, 0);
        s.Rand.AsSpan(0, Noncelen).CopyTo(auth.Rand);
        s.Tbuf = [.. tbuf[m..got], .. ConvA2M(auth, s.T)];
        fss.Phase = CHavePAKy;
        return Rpc.Ok;
    }

    // The key to get tickets with: if the client is the one who started factotum, it may use any key
    // to speak as anyone; if not, a key is vouched for only as the user's own. The speakfor key is
    // looked for first so that a needkey asks for the right one. What the rpc is answered with when
    // there is none.
    private Rpc? Choose(Factotum f, Fsstate fss, State s, Attr? attr, string? user)
    {
        string dom = Libc.Quote(Str(s.Tr.Authdom));
        Key? srvkey = null;
        s.Speakfor = false;
        Rpc sret = Rpc.Failure;
        if (user is null || user == fss.Sysuser)
        {
            sret = f.Findkey(out srvkey, new Keyinfo(fss, Attr.Delete(Attr.Copy(attr), "user")) { User = null }, $"role=speakfor dom={dom} user?");
        }

        Rpc ret = f.Findkey(out Key? key, new Keyinfo(fss, attr), $"role=client dom={dom} {Keyprompt}");
        if (ret == Rpc.Ok)
        {
            s.Key = key;
            return null;
        }

        if (sret == Rpc.Ok)
        {
            (s.Key, s.Speakfor) = (srvkey, true);
            return null;
        }

        return ret == Rpc.Confirm || sret == Rpc.Confirm ? Rpc.Confirm : ret;
    }

    // A ticket that does not open: the key disabled if it has never worked, and another asked for,
    // unless it spoke for another or keys are not asked for.
    private Rpc Unopened(Factotum f, Fsstate fss, State s, Attr? attr)
    {
        if (s.Key!.Successes == 0 && !s.Speakfor)
        {
            f.Disablekey(s.Key);
        }

        if (f.Askforkeys && !s.Speakfor)
        {
            fss.Keyinfo = $"{Attr.Format(attr)} {Keyprompt}";
            return Rpc.Needkey;
        }

        return f.Failure(fss, Factotum.Ebadkey);
    }

    // A login's password: the client's key from it and the server's own each open their ticket,
    // which must hold the same key.
    private async Task<Rpc> LoginAsync(Factotum f, Fsstate fss, State s, byte[] a)
    {
        if (a.Length >= Passwdlen)
        {
            return f.Failure(fss, "password too long");
        }

        // The server's key is kept while the client's, from the password, gets the tickets.
        Authkey ks = s.K;
        int nul = Array.IndexOf(a, (byte)0);
        s.K = Authkey.Passtokey(Encoding.UTF8.GetString(a, 0, nul < 0 ? a.Length : nul));
        s.K.Hash(Str(s.Tr.Hostid));
        byte[] ys = AuthpakNew(out AuthPakState ps, ks, true);
        (int ret, byte[] tbuf) = await GetticketsAsync(f, s, ys);

        // Stryker disable once Equality : gettickets gives a ticket or less than none
        if (ret > 0 && AuthpakFinish(ps, ks, s.Y) != 0)
        {
            return f.Failure(fss, Factotum.Easproto);
        }

        // Stryker disable once Equality : gettickets gives a ticket or less than none
        if (ret <= 0)
        {
            return f.Failure(fss, null);
        }

        // The client's ticket.
        int m = ConvM2T(tbuf, ret, s.T, s.K);

        // Stryker disable once Equality : convM2T never gives none
        if (m <= 0 || s.T.Form == 0)
        {
            return f.Failure(fss, Factotum.Easproto);
        }

        if (s.T.Num != AuthTc || !Same(s.T.Chal, s.Tr.Chal))
        {
            return f.Failure(fss, Factotum.Ebadkey);
        }

        byte[] tk = [.. s.T.Key];

        // The server's, with its key again.
        s.K = ks;
        byte[] rest = tbuf[m..ret];
        m = ConvM2T(rest, rest.Length, s.T, s.K);

        // Stryker disable once Equality : convM2T never gives none
        if (m <= 0 || s.T.Form == 0)
        {
            return f.Failure(fss, Factotum.Easproto);
        }

        if (s.T.Num != AuthTs || !Same(s.T.Chal, s.Tr.Chal) || !Same(tk, s.T.Key))
        {
            return f.Failure(fss, Factotum.Ebadkey);
        }

        Genrandom(s.Rand);
        return Establish(fss);
    }

    // gettickets: from the auth server, or made up when it cannot be had.
    private async Task<(int Ret, byte[] Tbuf)> GetticketsAsync(Factotum f, State s, byte[] y)
    {
        if (s.Tr.Authdom[0] == 0 || s.Tr.Authid[0] == 0 || s.Tr.Hostid[0] == 0 || s.Tr.Uid[0] == 0)
        {
            Factotum.Errstr = "bad ticket request";
            return (-1, []);
        }

        (int ret, byte[] tbuf) = await GetasticketsAsync(f, s, y);

        // Stryker disable once Equality : _asgetticket gives two tickets or less than none
        return ret > 0 ? (ret, tbuf) : Mkservertickets(s, y);
    }

    // getastickets: the AuthPAK values exchanged with the auth server of the key's domain, and the
    // tickets it gives, within 30 seconds.
    private async Task<(int Ret, byte[] Tbuf)> GetasticketsAsync(Factotum f, State s, byte[] y)
    {
        // The key was found by its domain, a client's by the request's and a server's by dom?, so
        // p9sk1.c's key without one is never had.
        Process up = Srv.Up;
        int asfd = await f.AuthdialAsync(Attr.StrFind(s.Key!.Attr, "dom")!);

        // Stryker disable once Equality : factotum's 0 is its own, so a call is never given it
        if (asfd < 0)
        {
            return (-1, []);
        }

        await up.AlarmAsync(30 * 1000);
        (int Ret, byte[] Tbuf) rv = (-1, []);
        try
        {
            // A write is whole or fails, and a failure is the catch's.
            s.Tr.Type = AuthPAK;

            // Stryker disable once Statement,Equality,Block : a write on a call fails only once the host has heard it hung up, which no test can wait for
            if (await AsrequestAsync(up, asfd, s.Tr) != 0)
            {
                return rv;
            }

            await up.WriteAsync(asfd, y);
            await up.WriteAsync(asfd, AuthpakNew(out AuthPakState p, s.K, true));

            (int n, byte[] tbuf) = await AsrdrespAsync(up, asfd, 2 * Pakylen);
            if (n != 2 * Pakylen)
            {
                return rv;
            }

            s.Y = tbuf[..Pakylen];
            if (AuthpakFinish(p, s.K, tbuf.AsSpan(Pakylen, Pakylen)) != 0)
            {
                return rv;
            }

            s.Tr.Type = AuthTreq;
            return rv = await AsgetticketAsync(up, asfd, s.Tr, Tbuflen);
        }
        catch (SyscallException error)
        {
            Factotum.Errstr = error.Message;
            return rv;
        }
        finally
        {
            // Stryker disable once Statement : an alarm left set interrupts only the server's wait for its next message, which it then waits for again
            await up.AlarmAsync(0);
            await up.CloseAsync(asfd);
        }
    }

    // p9sk1.c's State.
    private sealed class State
    {
        public Key? Key { get; set; }

        public Ticket T { get; } = new();

        public Ticketreq Tr { get; } = new();

        public Authkey K { get; set; } = null!;

        public AuthPakState? P { get; set; }

        public byte[] Cchal { get; } = new byte[Challen];

        public byte[] Y { get; set; } = new byte[Pakylen];

        public byte[] Rand { get; } = new byte[2 * Noncelen];

        public byte[] Tbuf { get; set; } = [];

        public bool Speakfor { get; set; }
    }
}
