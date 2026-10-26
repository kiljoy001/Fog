using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// factotum's p9any, as p9any.c: the negotiation of a protocol, which then runs in its place. The
// server offers proto@domain for each key it has, separated by spaces and ended by a NUL; the
// client answers with the protocol and domain it has a key for, "proto domain", also NUL-ended. In
// version 2, offered as v.2 first, the server then says OK. dp9ik is the one protocol negotiated.
public sealed class P9any : Proto
{
    private const int CNeedProtos = 0;
    private const int CHaveProto = 1;
    private const int CNeedOK = 2;
    private const int CRelay = 3;
    private const int SHaveProtos = 4;
    private const int SNeedProto = 5;
    private const int SHaveOK = 6;
    private const int SRelay = 7;
    private const int Maxphase = 8;

    // A relayed phase's errors are its protocol's, and a server, of version 1, has no OK to give, so
    // CRelay, SHaveOK and SRelay are never named.
    private static readonly string[] Phasenames =
    [
        "CNeedProtos", "CHaveProto", "CNeedOK",

        // Stryker disable all : a relayed phase's errors are its protocol's
        "CRelay",

        // Stryker restore all
        "SHaveProtos", "SNeedProto",

        // Stryker disable all : a server is of version 1, and so never has an OK to give, and a relayed phase's errors are its protocol's
        "SHaveOK", "SRelay",

        // Stryker restore all
    ];

    private readonly Proto[] negotiable;

    public P9any(params Proto[] negotiable) => this.negotiable = negotiable;

    public override string Name => "p9any";

    public override Task<Rpc> InitAsync(Factotum f, Fsstate fss)
    {
        int iscli = Factotum.Isclient(Attr.StrFind(fss.Attr, "role"));
        if (iscli < 0)
        {
            return Task.FromResult(f.Failure(fss, null));
        }

        (fss.Phasename, fss.Maxphase) = (Phasenames, Maxphase);
        fss.Phase = iscli != 0 ? CNeedProtos : SHaveProtos;
        fss.Ps = new State();
        return Task.FromResult(Rpc.Ok);
    }

    // Stryker disable all : the protocols negotiated close nothing of their own, the collector letting go of what they held
    public override void Close(Fsstate fss)
    {
        var s = (State)fss.Ps!;
        if (s.Subproto is not null && s.Subfss.Ps is not null)
        {
            s.Subproto.Close(s.Subfss);
        }
    }

    // Stryker restore all
    public override async Task<(Rpc Ret, byte[] A)> ReadAsync(Factotum f, Fsstate fss, int n)
    {
        var s = (State)fss.Ps!;
        switch (fss.Phase)
        {
            case SHaveProtos:
                // Stryker disable once all : a server is of version 1, as p9anyinit makes it
                var negstr = new StringBuilder(s.Version == 2 ? "v.2 " : string.Empty);
                var ki = new Keyinfo(fss, null) { Attr = null, Noconf = true, User = null };
                foreach (Proto p in negotiable)
                {
                    ki.Attr = Factotum.Setattr(Attr.Copy(fss.Attr), $"proto={Libc.Quote(p.Name)} dom?");
                    for (ki.Skip = 0; f.Findkey(out Key? k, ki, null) == Rpc.Ok; ki.Skip++)
                    {
                        negstr.Append(negstr.Length != 0 ? " " : string.Empty).Append(p.Name).Append('@').Append(Attr.StrFind(k!.Attr, "dom"));
                    }
                }

                if (negstr.Length == 0)
                {
                    return (f.Failure(fss, Factotum.Enegotiation), []);
                }

                byte[] a = [.. Encoding.UTF8.GetBytes(negstr.ToString()), 0];
                if (n < a.Length)
                {
                    return (Factotum.Toosmall(fss, a.Length), []);
                }

                fss.Phase = SNeedProto;
                return (Rpc.Ok, a);

            case CHaveProto:
                byte[] proto = [.. Encoding.UTF8.GetBytes($"{s.Subproto!.Name} {s.Subdom}"), 0];
                if (n < proto.Length)
                {
                    return (Factotum.Toosmall(fss, proto.Length), []);
                }

                fss.Phase = s.Version == 1 ? CRelay : CNeedOK;
                return (Rpc.Ok, proto);

            // Stryker disable all : a server is of version 1, as p9anyinit makes it, so it has no OK to give
            case SHaveOK:
                if (n < 3)
                {
                    return (Factotum.Toosmall(fss, 3), []);
                }

                fss.Phase = SRelay;
                return (Rpc.Ok, "OK\0"u8.ToArray());

            // Stryker restore all
            case CRelay:
            case SRelay:
                int ophase = s.Subfss.Phase;
                (Rpc ret, byte[] read) = await s.Subproto!.ReadAsync(f, s.Subfss, n);
                f.Rpcrdwrlog(s.Subfss, "read", read.Length, ophase, ret);
                return (Passret(fss, s, ret), read);

            default:
                return (f.Phaseerror(fss, "read"), []);
        }
    }

    public override async Task<Rpc> WriteAsync(Factotum f, Fsstate fss, byte[] a)
    {
        var s = (State)fss.Ps!;
        int n = a.Length;
        switch (fss.Phase)
        {
            case CNeedProtos:
                if (n == 0 || a[n - 1] != 0)
                {
                    return Factotum.Toosmall(fss, 2048);
                }

                return await ProtosAsync(f, fss, s, Encoding.UTF8.GetString(a, 0, Array.IndexOf(a, (byte)0)));

            case SNeedProto:
                if (n == 0 || a[n - 1] != 0)
                {
                    return Factotum.Toosmall(fss, n + 1);
                }

                return await ProtoAsync(f, fss, s, Encoding.UTF8.GetString(a, 0, Array.IndexOf(a, (byte)0)));

            case CNeedOK:
                if (n < 3)
                {
                    return Factotum.Toosmall(fss, 3);
                }

                if (Encoding.UTF8.GetString(a, 0, Math.Max(0, Array.IndexOf(a, (byte)0))) != "OK")
                {
                    return f.Failure(fss, "server gave up");
                }

                fss.Phase = CRelay;
                return Rpc.Ok;

            case CRelay:
            case SRelay:
                int ophase = s.Subfss.Phase;
                Rpc ret = await s.Subproto!.WriteAsync(f, s.Subfss, a);
                f.Rpcrdwrlog(s.Subfss, "write", n, ophase, ret);
                return Passret(fss, s, ret);

            default:
                return f.Phaseerror(fss, "write");
        }
    }

    // getdom: what follows the @ of proto@domain, or nothing.
    private static string Getdom(string p)
    {
        int at = p.IndexOf('@', StringComparison.Ordinal);

        // Stryker disable once Equality : what has an @ first names no protocol, so its domain is never looked for
        return at < 0 ? string.Empty : p[(at + 1)..];
    }

    // setupfss: the protocol and domain chosen put in the conversation's attributes, which the
    // protocol's own state starts from.
    private static void Setupfss(Fsstate fss, State s, Key k)
    {
        fss.Attr = Factotum.Setattr(fss.Attr, $"proto={Libc.Quote(s.Subproto!.Name)}");
        fss.Attr = Factotum.Setattr(fss.Attr, $"dom={Libc.Quote(Attr.StrFind(k.Attr, "dom")!)}");
        s.Subfss.Attr = fss.Attr;
        s.Subfss.Proto = s.Subproto;
        s.Subfss.Phase = Fsstate.Notstarted;
        s.Subfss.Sysuser = fss.Sysuser;
        s.Subfss.Seqnum = fss.Seqnum;
        s.Subfss.Conf = fss.Conf;
    }

    // passret: what the protocol in its place said, as the conversation's.
    private static Rpc Passret(Fsstate fss, State s, Rpc ret)
    {
        switch (ret)
        {
            case Rpc.Failure:
                // Stryker disable once all : no protocol of factotum's breaks its conversation
                if (s.Subfss.Phase == Fsstate.Broken)
                {
                    fss.Phase = Fsstate.Broken;
                }

                fss.Err = s.Subfss.Err;
                break;
            case Rpc.Needkey:
                fss.Keyinfo = s.Subfss.Keyinfo;
                break;
            case Rpc.Ok:
                // The authinfo copied, as p9any.c copies the struct.
                if (s.Subfss.Haveai)
                {
                    AuthInfo ai = s.Subfss.Ai;
                    (fss.Haveai, fss.Ai) = (true, new AuthInfo { Cuid = ai.Cuid, Suid = ai.Suid, Secret = ai.Secret });

                    // Stryker disable once Boolean : the protocol is done once it has its authinfo, and never looked at again
                    s.Subfss.Haveai = false;
                }

                if (s.Subfss.Phase == Fsstate.Established)
                {
                    fss.Phase = Fsstate.Established;
                }

                break;
            case Rpc.Toosmall:
                fss.Nwant = s.Subfss.Nwant;
                break;
            case Rpc.Confirm:
                fss.Conf = s.Subfss.Conf;
                break;
        }

        return ret;
    }

    // findprotox: where the protocol of proto or proto@domain is among those negotiated, or -1.
    private int Findprotox(string name)
    {
        int at = name.IndexOf('@', StringComparison.Ordinal);

        // Stryker disable once Equality : what has an @ first names no protocol either way
        string proto = at < 0 ? name : name[..at];
        return Array.FindIndex(negotiable, p => p.Name == proto);
    }

    private Proto? Findneg(string name) => Findprotox(name) is int x and >= 0 ? negotiable[x] : null;

    // The client's write of what the server offers: a key for one of them, those it prefers first,
    // or one asked for, each in turn.
    private async Task<Rpc> ProtosAsync(Factotum f, Fsstate fss, State s, string a)
    {
        List<string> token = Process.Tokenize(a, 20);
        if (token.Count > 0 && token[0].StartsWith("v.", StringComparison.Ordinal))
        {
            s.Version = Libc.Strtol(token[0][2..], 10, out _);
            if (s.Version != 2)
            {
                return f.Failure(fss, "unknown version of p9any");
            }

            token.RemoveAt(0);
        }

        // Those preferred first.
        // Stryker disable once Linq : with dp9ik the one protocol negotiated, the order only puts first or last what is passed over either way
        token = [.. token.OrderByDescending(Findprotox)];

        // Stryker disable once String : role is no part of a key's match, and is left out of what a needkey asks
        Attr? anew = Attr.Delete(Attr.Delete(Attr.Copy(fss.Attr), "proto"), "role");
        (Rpc ret, Key? k, Proto? p, string? dom) = Owned(f, fss, token, anew);
        if (ret == Rpc.Confirm)
        {
            return ret;
        }

        // No key will do: each proto@domain in turn, a key asked for if one may be.
        if (k is null)
        {
            (k, p, dom, bool asking) = Asked(f, fss, s, token, anew);
            if (k is null)
            {
                return asking ? Rpc.Needkey : f.Failure(fss, s.Keyasked != 0 ? null : Factotum.Enegotiation);
            }
        }

        (s.Subdom, s.Subproto) = (dom, p);
        Setupfss(fss, s, k);
        Rpc started = await s.Subproto!.InitAsync(f, s.Subfss);
        f.Rpcstartlog(s.Subfss.Attr, s.Subfss, started);
        if (started == Rpc.Ok)
        {
            fss.Phase = CHaveProto;
        }

        return Passret(fss, s, started);
    }

    // The first offer a key will do for: one to speak for anyone, if the client is factotum's owner,
    // or else the client's own; Confirm for one to be confirmed first.
    private (Rpc Ret, Key? K, Proto? P, string? Dom) Owned(Factotum f, Fsstate fss, List<string> token, Attr? anew)
    {
        Attr? anewsf = Attr.Delete(Attr.Copy(anew), "user");
        string? user = Attr.StrFind(anew, "user");
        Key? k = null;
        Proto? p = null;
        string? dom = null;
        foreach (string t in token)
        {
            if ((p = Findneg(t)) is null)
            {
                continue;
            }

            dom = Getdom(t);
            Rpc ret = Rpc.Failure;
            if (user is null || user == fss.Sysuser)
            {
                ret = f.Findkey(out k, new Keyinfo(fss, null) { Attr = anewsf, User = null }, $"proto={Libc.Quote(p.Name)} dom={Libc.Quote(dom)} role=speakfor {p.Keyprompt}");
            }

            if (ret is not (Rpc.Ok or Rpc.Confirm))
            {
                ret = f.Findkey(out k, new Keyinfo(fss, null) { Attr = anew, User = fss.Sysuser }, $"proto={Libc.Quote(p.Name)} dom={Libc.Quote(dom)} role=client {p.Keyprompt}");
            }

            if (ret is Rpc.Confirm or Rpc.Ok)
            {
                return (ret, k, p, dom);
            }
        }

        return (Rpc.Failure, k, p, dom);
    }

    // The offers not yet asked about, in turn, until a key may be asked for one: the key, if one
    // was found after all, and whether one is being asked for.
    private (Key? K, Proto? P, string? Dom, bool Asking) Asked(Factotum f, Fsstate fss, State s, List<string> token, Attr? anew)
    {
        Key? k = null;
        Proto? p = null;
        string? dom = null;
        bool asking = false;
        while (!asking && s.Keyasked < token.Count)
        {
            if ((p = Findneg(token[s.Keyasked])) is null)
            {
                s.Keyasked++;
                continue;
            }

            dom = Getdom(token[s.Keyasked]);
            Rpc ret = f.Findkey(out k, new Keyinfo(fss, null) { Attr = anew }, $"proto={Libc.Quote(p.Name)} dom={Libc.Quote(dom)} role=client {p.Keyprompt}");
            s.Keyasked++;
            asking = ret == Rpc.Needkey;
        }

        return (k, p, dom, asking);
    }

    // The server's read of the client's choice: the protocol started with the server's key for it.
    private async Task<Rpc> ProtoAsync(Factotum f, Fsstate fss, State s, string a)
    {
        List<string> token = Process.Tokenize(a, 20);
        if (token.Count != 2)
        {
            return f.Failure(fss, Factotum.Ebadarg);
        }

        if (Findneg(token[0]) is not { } p)
        {
            return f.Failure(fss, Factotum.Enegotiation);
        }

        Attr? attr = Attr.Delete(Attr.Copy(fss.Attr), "proto");
        Rpc ret = f.Findkey(out Key? k, new Keyinfo(fss, null) { Attr = attr, User = null }, $"proto={Libc.Quote(token[0])} dom={Libc.Quote(token[1])} role=server");
        if (ret == Rpc.Confirm)
        {
            return ret;
        }

        if (ret != Rpc.Ok)
        {
            return f.Failure(fss, Factotum.Enegotiation);
        }

        s.Subproto = p;
        Setupfss(fss, s, k!);
        ret = await p.InitAsync(f, s.Subfss);
        if (ret == Rpc.Ok)
        {
            // Stryker disable once all : a server is of version 1, as p9anyinit makes it
            fss.Phase = s.Version == 1 ? SRelay : SHaveOK;
        }

        return Passret(fss, s, ret);
    }

    // p9any.c's State.
    private sealed class State
    {
        public Fsstate Subfss { get; } = new();

        public Proto? Subproto { get; set; }

        public int Keyasked { get; set; }

        public string? Subdom { get; set; }

        public int Version { get; set; } = 1;
    }
}
