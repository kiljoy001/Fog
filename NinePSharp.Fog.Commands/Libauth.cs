using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// libauth's client of factotum, as 9front's auth_rpc.c, auth_proxy.c and auth_chuid.c: an rpc to
// factotum's rpc file, an authentication proxied between it and a conversation, and becoming the
// user it authenticated. Each fails by throwing what it would leave in errstr, as Newns does.
public static class Libauth
{
    public const int AuthRpcMax = 4096;

    // auth_proxy's getkey: given needkey's or badkey's argument, it adds the key factotum wants, or
    // returns -1 when it cannot.
    public delegate Task<int> AuthGetkey(string arg);

    // auth.h's ARok to ARphase, and auth_proxy.c's ARgiveup.
    public enum Ar
    {
        Ok,
        Done,
        Error,
        Needkey,
        Badkey,
        Writenext,
        Toosmall,
        Toobig,
        Rpcfailure,
        Phase,
        Giveup = 100,
    }

    // auth_proxy: an authentication through /mnt/factotum/rpc, its messages relayed on fd.
    public static async Task<AuthInfo> AuthProxyAsync(Process process, int fd, AuthGetkey? getkey, string parameters)
    {
        int afd;
        try
        {
            afd = await process.OpenAsync("/mnt/factotum/rpc", NinePConstants.ORDWR | NinePConstants.OCEXEC);
        }
        catch (SyscallException error)
        {
            throw new SyscallException($"opening /mnt/factotum/rpc: {error.Message}");
        }

        try
        {
            return await FauthProxyAsync(fd, new AuthRpc(process, afd), getkey, parameters);
        }
        finally
        {
            await process.CloseAsync(afd);
        }
    }

    // fauth_proxy: what factotum reads is written to fd, and what it asks for is read from fd, until
    // it is done and gives the authinfo.
    public static async Task<AuthInfo> FauthProxyAsync(int fd, AuthRpc rpc, AuthGetkey? getkey, string parameters)
    {
        if (await DorpcAsync(rpc, "start", Encoding.UTF8.GetBytes(parameters), getkey) != Ar.Ok)
        {
            throw new SyscallException($"fauth_proxy start: {rpc.Error}");
        }

        byte[] buf = new byte[AuthRpcMax];
        while (true)
        {
            switch (await DorpcAsync(rpc, "read", [], getkey))
            {
                case Ar.Done:
                    return await AuthGetinfoAsync(rpc);
                case Ar.Ok:
                    await WriteAsync(rpc, fd);
                    break;
                case Ar.Phase:
                    await PhaseAsync(rpc, fd, buf, getkey);
                    break;
                default:
                    throw new SyscallException($"auth_proxy rpc: {rpc.Error}");
            }
        }
    }

    // auth_getinfo: the authinfo of an authentication done.
    public static async Task<AuthInfo> AuthGetinfoAsync(AuthRpc rpc)
    {
        if (await rpc.CallAsync("authinfo", []) != Ar.Ok)
        {
            throw new SyscallException(rpc.Error);
        }

        return ConvM2AI(rpc.Arg) ?? throw new SyscallException("bad auth info from factotum");
    }

    // auth_chuid: the capability spent, so the process is the user, factotum mounted again as the
    // user, and the user's namespace built from ns, or /lib/namespace.
    public static async Task AuthChuidAsync(Process process, AuthInfo ai, string? ns)
    {
        if (string.IsNullOrEmpty(ai.Cap))
        {
            throw new SyscallException("no capability");
        }

        int fd;
        try
        {
            fd = await process.OpenAsync("/dev/capuse", NinePConstants.OCEXEC | NinePConstants.OWRITE);
        }
        catch (SyscallException error)
        {
            throw new SyscallException($"opening /dev/capuse: {error.Message}");
        }

        try
        {
            await process.WriteAsync(fd, Encoding.UTF8.GetBytes(ai.Cap));
        }
        catch (SyscallException error)
        {
            throw new SyscallException($"writing {ai.Cap} to /dev/capuse: {error.Message}");
        }
        finally
        {
            await process.CloseAsync(fd);
        }

        await MountFactotumAsync(process);
        await Newns.NewnsAsync(process, ai.Cuid, ns);
    }

    // A link to factotum as the new user, when there is one to mount.
    private static async Task MountFactotumAsync(Process process)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync("/srv/factotum", NinePConstants.ORDWR);
        }
        catch (SyscallException)
        {
            return;
        }

        try
        {
            await process.MountAsync(fd, -1, "/mnt", MountFlags.Replace, string.Empty);
        }
        catch (SyscallException)
        {
            await process.CloseAsync(fd);
        }
    }

    // dorpc: a needkey or badkey answered by getkey, and the rpc made again.
    private static async Task<Ar> DorpcAsync(AuthRpc rpc, string verb, byte[] val, AuthGetkey? getkey)
    {
        while (true)
        {
            Ar ret = await rpc.CallAsync(verb, val);
            if (ret is not (Ar.Needkey or Ar.Badkey))
            {
                return ret;
            }

            if (getkey is null || await getkey(Encoding.UTF8.GetString(rpc.Arg)) < 0)
            {
                return Ar.Giveup;
            }
        }
    }

    // What factotum read, written to fd. A write is whole or fails, so there is no short one.
    private static async Task WriteAsync(AuthRpc rpc, int fd)
    {
        try
        {
            await rpc.Process.WriteAsync(fd, rpc.Arg);
        }
        catch (SyscallException error)
        {
            throw new SyscallException($"auth_proxy write fd: {error.Message}");
        }
    }

    // A phase of reading: factotum says how much it needs, and it is read from fd until it has it.
    private static async Task PhaseAsync(AuthRpc rpc, int fd, byte[] buf, AuthGetkey? getkey)
    {
        int n = 0;
        Array.Clear(buf);
        Ar ret;
        while ((ret = await DorpcAsync(rpc, "write", buf[..n], getkey)) == Ar.Toosmall)
        {
            int m = Libc.Strtol(Encoding.UTF8.GetString(rpc.Arg), 10, out _);
            if (m <= n || m > AuthRpcMax)
            {
                break;
            }

            ReadOnlyMemory<byte> read;
            try
            {
                read = await rpc.Process.ReadAsync(fd, m - n);
            }
            catch (SyscallException error)
            {
                throw new SyscallException($"auth_proxy read fd: {error.Message}");
            }

            if (read.IsEmpty)
            {
                throw new SyscallException("auth_proxy short read");
            }

            read.CopyTo(buf.AsMemory(n));
            n += read.Length;
        }

        if (ret != Ar.Ok)
        {
            throw new SyscallException($"auth_proxy rpc write: {rpc.Error}");
        }
    }

    // convM2AI: the users, capability and secret, each after its length, or null when they overrun.
    private static AuthInfo? ConvM2AI(byte[] m)
    {
        int p = 0;
        string?[] s = new string?[3];
        for (int i = 0; i < s.Length; i++)
        {
            byte[]? b = Gcarray(m, ref p);
            if (b is null)
            {
                return null;
            }

            s[i] = Encoding.UTF8.GetString(b);
        }

        byte[]? secret = Gcarray(m, ref p);
        return secret is null ? null : new AuthInfo { Cuid = s[0]!, Suid = s[1]!, Cap = s[2], Secret = secret };
    }

    // gcarray: a 16-bit length and that many bytes.
    private static byte[]? Gcarray(byte[] m, ref int p)
    {
        if (p + 2 > m.Length)
        {
            return null;
        }

        int n = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(p));
        p += 2;
        if (p + n > m.Length)
        {
            return null;
        }

        byte[] b = m[p..(p + n)];
        p += n;
        return b;
    }

    // An open rpc file: auth_rpc writes a verb and its argument and reads factotum's answer, whose
    // argument it keeps, leaving what went wrong in Error, as auth_rpc leaves it in errstr. A write is
    // whole or fails, so there is no short one.
    public sealed class AuthRpc(Process process, int afd)
    {
        // classify's table, auth_rpc.c's second "error" left out as it can never be matched.
        private static readonly (string Verb, Ar Val)[] Tab =
        [
            ("ok", Ar.Ok),
            ("done", Ar.Done),
            ("error", Ar.Error),
            ("needkey", Ar.Needkey),
            ("badkey", Ar.Badkey),
            ("phase", Ar.Phase),
            ("toosmall", Ar.Toosmall),
        ];

        public Process Process => process;

        public byte[] Arg { get; private set; } = [];

        public string Error { get; private set; } = string.Empty;

        public async Task<Ar> CallAsync(string verb, byte[] a)
        {
            byte[] v = Encoding.UTF8.GetBytes(verb);
            if (a.Length + v.Length + 1 > AuthRpcMax)
            {
                Error = "rpc too big";
                return Ar.Toobig;
            }

            ReadOnlyMemory<byte> ibuf;
            try
            {
                await process.WriteAsync(afd, (byte[])[.. v, (byte)' ', .. a]);
                ibuf = await process.ReadAsync(afd, AuthRpcMax);
            }
            catch (SyscallException error)
            {
                Error = error.Message;
                return Ar.Rpcfailure;
            }

            Ar type = Classify(ibuf.ToArray());
            Error = type switch
            {
                Ar.Error => Arg.Length == 0 ? "unspecified rpc error" : Encoding.UTF8.GetString(Arg),
                Ar.Needkey => $"needkey {Encoding.UTF8.GetString(Arg)}",
                Ar.Badkey => $"badkey {Badkey(Encoding.UTF8.GetString(Arg))}",
                Ar.Phase => $"phase error {Encoding.UTF8.GetString(Arg)}",
                _ => Error,
            };
            return type;
        }

        // badkey's key, after the first line of its argument, when it has one.
        private static string Badkey(string arg)
        {
            int nl = arg.IndexOf('\n', StringComparison.Ordinal);
            return arg[(nl + 1)..].Split('\n')[0];
        }

        private Ar Classify(byte[] buf)
        {
            foreach ((string verb, Ar val) in Tab)
            {
                int len = verb.Length;
                if (buf.Length >= len && buf.AsSpan(0, len).SequenceEqual(Encoding.ASCII.GetBytes(verb)) && (buf.Length == len || buf[len] == ' '))
                {
                    Arg = buf.Length == len ? [] : buf[(len + 1)..];
                    return val;
                }
            }

            Error = $"malformed rpc response: {Encoding.UTF8.GetString(buf)}";
            return Ar.Rpcfailure;
        }
    }
}
