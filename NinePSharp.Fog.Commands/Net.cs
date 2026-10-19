using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// libc's dial(2) and its companions, as 9front's dial.c, announce.c, netmkaddr.c and getnetconninfo.c:
// each fails as they do, with the error they leave in errstr.
public static class Net
{
    private const int Maxstring = 128;
    private const string Unknown = "???";
    private const string Interrupted = "interrupted";

    // netmkaddr: the network and service given by default where the address names none.
    public static string NetMkAddr(string addr, string? defnet, string? defsrv)
    {
        int bang = addr.IndexOf('!', StringComparison.Ordinal);
        if (bang < 0)
        {
            return defnet is null
                ? defsrv is null ? $"net!{addr}" : $"net!{addr}!{defsrv}"
                : defsrv is null ? $"{defnet}!{addr}" : $"{defnet}!{addr}!{defsrv}";
        }

        return addr.IndexOf('!', bang + 1) >= 0 || defsrv is null ? addr : $"{addr}!{defsrv}";
    }

    // dial: through /net, then /net.alt unless /net's call was refused or interrupted; /net.alt's
    // error is kept only when it is more than a lack of translation or of the network.
    public static async Task<Call> DialAsync(Process process, string dest, string? local = null, bool ctl = false)
    {
        var ds = Parse(dest);
        if (ds.NetDir is not null)
        {
            return await CsdialAsync(process, ds, local, ctl);
        }

        try
        {
            return await CsdialAsync(process, ds with { NetDir = "/net" }, local, ctl);
        }
        catch (SyscallException first) when (first.Message != Interrupted && !first.Message.Contains("refused", StringComparison.Ordinal))
        {
            try
            {
                return await CsdialAsync(process, ds with { NetDir = "/net.alt" }, local, ctl);
            }
            catch (SyscallException alt) when (alt.Message.Contains("translate", StringComparison.Ordinal) || alt.Message.Contains("does not exist", StringComparison.Ordinal))
            {
                throw first;
            }
        }
    }

    // announce: the ctl of a conversation announced on the address, and its directory.
    public static async Task<(int Ctl, string Dir)> AnnounceAsync(Process process, string addr)
    {
        (string naddr, string netdir) = await NetTransAsync(process, addr);
        int ctl;
        try
        {
            ctl = await process.OpenAsync(netdir, NinePConstants.ORDWR);
        }
        catch (SyscallException error)
        {
            throw new SyscallException($"announce opening {netdir}: {error.Message}");
        }

        int slash = netdir.LastIndexOf('/');
        if (slash < 0)
        {
            await process.CloseAsync(ctl);
            throw new SyscallException($"announce arg format {netdir}");
        }

        string? dir = null;
        try
        {
            string line = await ReadAsync(process, ctl, Maxstring);
            if (line.Length == 0)
            {
                throw new SyscallException("read nothing");
            }

            dir = $"{netdir[..slash]}/{line}";
            await process.WriteAsync(ctl, Encoding.UTF8.GetBytes($"announce {naddr}"));
        }
        catch (SyscallException error)
        {
            await process.CloseAsync(ctl);
            throw new SyscallException(dir is null ? $"announce reading {netdir[..slash]}: {error.Message}" : $"announce writing {netdir[..slash]}: {error.Message}");
        }

        return (ctl, dir);
    }

    // listen: the ctl of the next call to an announced conversation, and the call's directory.
    public static async Task<(int Ctl, string Dir)> ListenAsync(Process process, string dir)
    {
        int ctl;
        try
        {
            ctl = await process.OpenAsync($"{dir}/listen", NinePConstants.ORDWR);
        }
        catch (SyscallException error)
        {
            throw new SyscallException($"listen opening {dir}/listen: {error.Message}");
        }

        int slash = dir.LastIndexOf('/');
        if (slash < 0)
        {
            await process.CloseAsync(ctl);
            throw new SyscallException($"listen arg format {dir}");
        }

        try
        {
            string line = await ReadAsync(process, ctl, Maxstring);
            if (line.Length == 0)
            {
                throw new SyscallException("read nothing");
            }

            return (ctl, dir[..(slash + 1)] + line);
        }
        catch (SyscallException error)
        {
            await process.CloseAsync(ctl);
            throw new SyscallException($"listen reading {dir}/listen: {error.Message}");
        }
    }

    // accept: the call's data, after telling a network that wants it; most do not, so the answer is ignored.
    public static async Task<int> AcceptAsync(Process process, int ctl, string dir)
    {
        try
        {
            await process.WriteAsync(ctl, Encoding.UTF8.GetBytes($"accept {dir[(dir.LastIndexOf('/') + 1)..]}"));
        }
        catch (SyscallException)
        {
            // A network that needs no accept refuses it.
        }

        return await process.OpenAsync($"{dir}/data", NinePConstants.ORDWR);
    }

    // reject: the call is refused, with the cause given to the network.
    public static async Task RejectAsync(Process process, int ctl, string dir, string cause)
        => await process.WriteAsync(ctl, Encoding.UTF8.GetBytes($"reject {dir[(dir.LastIndexOf('/') + 1)..]} {cause}"));

    // getnetconninfo: the conversation's endpoints, from its directory or else from the descriptor's name.
    public static async Task<ConnInfo?> GetNetConnInfoAsync(Process process, string? dir, int fd)
    {
        if (string.IsNullOrEmpty(dir))
        {
            string path;
            try
            {
                path = await process.Fd2PathAsync(fd);
            }
            catch (SyscallException)
            {
                return null;
            }

            int slash = path.LastIndexOf('/');
            if (slash < 0)
            {
                return null;
            }

            dir = path[..slash];
        }

        int rootEnd = dir.IndexOf('/', 1);
        if (rootEnd < 0)
        {
            return null;
        }

        string spec;
        try
        {
            var d = await process.StatAsync(dir);
            spec = $"#{(char)d.Type}{d.Dev}";
        }
        catch (SyscallException)
        {
            spec = Unknown;
        }

        (string lsys, string lserv) = await EndpointAsync(process, dir, "local");
        (string rsys, string rserv) = await EndpointAsync(process, dir, "remote");
        string netname = dir.LastIndexOf('/') is int n and >= 0 ? dir[..n] : dir;
        if (netname.StartsWith("/net/", StringComparison.Ordinal))
        {
            netname = netname[5..];
        }

        return new ConnInfo(dir, dir[..rootEnd], spec, lsys, lserv, rsys, rserv, $"{netname}!{lsys}!{lserv}", $"{netname}!{rsys}!{rserv}");
    }

    // _dial_string_parse: [/netdir/]proto!rem, and net for a string without a !.
    private static Dialstring Parse(string str)
    {
        string buf = str.Length >= Maxstring ? str[..(Maxstring - 1)] : str;
        int p = buf.IndexOf('!', StringComparison.Ordinal);
        if (p < 0)
        {
            return new Dialstring(null, "net", buf);
        }

        int start = 0;
        if (buf.StartsWith('#'))
        {
            int slash = buf.IndexOf('/', StringComparison.Ordinal);
            start = slash < 0 || slash > p ? 0 : slash;
        }

        if (buf[start] != '/')
        {
            return new Dialstring(null, buf[..p], buf[(p + 1)..]);
        }

        int p2 = buf.LastIndexOf('/', p);
        return new Dialstring(buf[..p2], buf[(p2 + 1)..p], buf[(p + 1)..]);
    }

    // csdial: each translation cs gives, tried in turn, or the clone of the protocol without cs. The
    // error left is the last one that is not of a missing file, or the last one.
    private static async Task<Call> CsdialAsync(Process process, Dialstring ds, string? local, bool ctl)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync($"{ds.NetDir}/cs", NinePConstants.ORDWR);
        }
        catch (SyscallException)
        {
            return await CallAsync(process, $"{ds.NetDir}/{ds.Proto}/clone", ds.Rem, local, ds, ctl);
        }

        try
        {
            await process.WriteAsync(fd, Encoding.UTF8.GetBytes($"{ds.Proto}!{ds.Rem}"));
            await process.SeekAsync(fd, 0, 0);
            string? kept = null;
            string? last = null;
            for (string buf; (buf = Encoding.UTF8.GetString((await process.ReadAsync(fd, Maxstring - 1)).Span)).Length > 0;)
            {
                int space = buf.IndexOf(' ', StringComparison.Ordinal);
                if (space < 0)
                {
                    continue;
                }

                string clone = buf[..space];
                string rem = buf[(space + 1)..];
                string? loc = null;
                int space2 = rem.IndexOf(' ', StringComparison.Ordinal);
                if (space2 >= 0)
                {
                    loc = rem[(space2 + 1)..];
                    rem = rem[..space2];
                }

                try
                {
                    return await CallAsync(process, clone, rem, local ?? loc, ds, ctl);
                }
                catch (SyscallException error)
                {
                    last = error.Message;
                    if (error.Message == Interrupted)
                    {
                        break;
                    }

                    if (!error.Message.Contains("does not exist", StringComparison.Ordinal))
                    {
                        kept = error.Message;
                    }
                }
            }

            throw new SyscallException(kept ?? last ?? string.Empty);
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    // call: the clone, as named in this namespace's netdir, connected and its data opened.
    private static async Task<Call> CallAsync(Process process, string clone, string remote, string? local, Dialstring ds, bool wantCtl)
    {
        string p = clone;
        if (clone.StartsWith('/') || clone.StartsWith('#'))
        {
            int slash = clone.IndexOf('/', 1);
            p = slash < 0 ? clone : clone[(slash + 1)..];
        }

        string cname = $"{ds.NetDir}/{p}";
        int cfd = await process.OpenAsync(cname, NinePConstants.ORDWR);
        try
        {
            string name = (await ReadAsync(process, cfd, 255)).TrimStart(' ');
            string number = Libc.Strtoul(name, 0, out _).ToString(CultureInfo.InvariantCulture);
            string protodir = cname[..cname.LastIndexOf('/')];
            await process.WriteAsync(cfd, Encoding.UTF8.GetBytes(local is null ? $"connect {remote}" : $"connect {remote} {local}"));
            int fd = await process.OpenAsync($"{protodir}/{number}/data", NinePConstants.ORDWR);
            if (wantCtl)
            {
                return new Call(fd, cfd, $"{protodir}/{number}");
            }

            await process.CloseAsync(cfd);
            return new Call(fd, -1, $"{protodir}/{number}");
        }
        catch (SyscallException)
        {
            await process.CloseAsync(cfd);
            throw;
        }
    }

    // nettrans: cs's translation of an address, or the protocol's clone without cs.
    private static async Task<(string Naddr, string File)> NetTransAsync(Process process, string addr)
    {
        int p = addr.IndexOf('!', StringComparison.Ordinal);
        if (p < 0)
        {
            throw new SyscallException($"bad dial string: {addr}");
        }

        string netdir = "/net";
        if (addr.StartsWith('/') || addr.StartsWith('#'))
        {
            int p2 = addr.LastIndexOf('/', p);
            if (p2 <= 0)
            {
                throw new SyscallException($"bad dial string: {addr}");
            }

            netdir = addr[..p2];
            addr = addr[(p2 + 1)..];
        }

        int fd;
        try
        {
            fd = await process.OpenAsync($"{netdir}/cs", NinePConstants.ORDWR);
        }
        catch (SyscallException)
        {
            // identtrans: without cs, the protocol's clone takes the rest as it is.
            int bang = addr.IndexOf('!', StringComparison.Ordinal);
            return (bang < 0 ? string.Empty : addr[(bang + 1)..], $"{netdir}/{(bang < 0 ? addr : addr[..bang])}/clone");
        }

        string buf;
        try
        {
            await process.WriteAsync(fd, Encoding.UTF8.GetBytes(addr));
            await process.SeekAsync(fd, 0, 0);
            buf = Encoding.UTF8.GetString((await process.ReadAsync(fd, 255)).Span);
        }
        finally
        {
            await process.CloseAsync(fd);
        }

        int space = buf.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            throw new SyscallException(string.Empty);
        }

        string file = buf[..space];
        if (file.StartsWith('/'))
        {
            int slash = file.IndexOf('/', 1);
            file = slash < 0 ? file : file[(slash + 1)..];
        }

        return (buf[(space + 1)..], $"{netdir}/{file}");
    }

    private static async Task<string> ReadAsync(Process process, int fd, int count)
        => Encoding.UTF8.GetString((await process.ReadAsync(fd, count)).Span);

    // getendpoint: sys!serv from the file, without its newline, and ??? for what cannot be read.
    private static async Task<(string Sys, string Serv)> EndpointAsync(Process process, string dir, string file)
    {
        try
        {
            int fd = await process.OpenAsync($"{dir}/{file}", NinePConstants.OREAD | NinePConstants.OCEXEC);
            try
            {
                string buf = await ReadAsync(process, fd, Maxstring - 1);
                if (buf.Length == 0)
                {
                    return (Unknown, Unknown);
                }

                buf = buf[..^1];
                int bang = buf.IndexOf('!', StringComparison.Ordinal);
                return bang < 0 ? (buf, Unknown) : (buf[..bang], buf[(bang + 1)..]);
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException)
        {
            return (Unknown, Unknown);
        }
    }

    // A dialed call: its data, its ctl if it was asked for or -1, and its directory.
    public readonly record struct Call(int Data, int Ctl, string Dir);

    // getnetconninfo's endpoints of a conversation.
    public sealed record ConnInfo(string Dir, string Root, string Spec, string Lsys, string Lserv, string Rsys, string Rserv, string Laddr, string Raddr);

    private sealed record Dialstring(string? NetDir, string Proto, string Rem);
}
