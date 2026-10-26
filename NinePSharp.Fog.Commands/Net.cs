using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// libc's dial(2) and its companions, as 9front's dial.c and announce.c: each fails as they do, with
// the error they leave in errstr. Only what Fog's programs call is here: dial without a local
// address, which cs never gives either, and no reject or getnetconninfo.
public static class Net
{
    private const int Maxstring = 128;
    private const string Interrupted = "interrupted";

    // netmkaddr: the address with the network and service given for what it lacks.
    public static string Netmkaddr(string addr, string? defnet, string? defsrv)
    {
        int cp = addr.IndexOf('!', StringComparison.Ordinal);
        if (cp < 0)
        {
            string net = defnet ?? "net";
            return defsrv is null ? $"{net}!{addr}" : $"{net}!{addr}!{defsrv}";
        }

        // Stryker disable once Equality : a second bang is after the first, never at the start
        return addr.IndexOf('!', cp + 1) >= 0 || defsrv is null ? addr : $"{addr}!{defsrv}";
    }

    // dial without a ctl: the call's data, its ctl closed.
    public static async Task<int> DialAsync(Process process, string dest)
    {
        Call call = await DialCtlAsync(process, dest);
        await process.CloseAsync(call.Ctl);
        return call.Data;
    }

    // dial: through /net, then /net.alt unless /net's call was refused or interrupted; /net.alt's
    // error is kept only when it is more than a lack of translation or of the network.
    public static async Task<Call> DialCtlAsync(Process process, string dest)
    {
        var ds = Parse(dest);
        if (ds.NetDir is not null)
        {
            return await CsdialAsync(process, ds);
        }

        try
        {
            return await CsdialAsync(process, ds with { NetDir = "/net" });
        }
        catch (SyscallException first) when (first.Message != Interrupted && !first.Message.Contains("refused", StringComparison.Ordinal))
        {
            try
            {
                return await CsdialAsync(process, ds with { NetDir = "/net.alt" });
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
            // Stryker disable once Statement,Arithmetic,String : no network Fog serves needs an accept, and #I refuses every one, as devip does
            await process.WriteAsync(ctl, Encoding.UTF8.GetBytes($"accept {dir[(dir.LastIndexOf('/') + 1)..]}"));
        }
        catch (SyscallException)
        {
            // A network that needs no accept refuses it.
        }

        return await process.OpenAsync($"{dir}/data", NinePConstants.ORDWR);
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

        // A # device's directory starts at its first /, if that comes before the !.
        int start = buf.StartsWith('#') ? Math.Max(buf.IndexOf('/', 0, p), 0) : 0;

        if (buf[start] != '/')
        {
            return new Dialstring(null, buf[..p], buf[(p + 1)..]);
        }

        int p2 = buf.LastIndexOf('/', p);
        return new Dialstring(buf[..p2], buf[(p2 + 1)..p], buf[(p + 1)..]);
    }

    // csdial: each translation cs gives, tried in turn, or the clone of the protocol without cs. The
    // error left is the last one that is not of a missing file, or the last one.
    private static async Task<Call> CsdialAsync(Process process, Dialstring ds)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync($"{ds.NetDir}/cs", NinePConstants.ORDWR);
        }
        catch (SyscallException)
        {
            return await CallAsync(process, $"{ds.NetDir}/{ds.Proto}/clone", ds.Rem, ds);
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

                try
                {
                    return await CallAsync(process, buf[..space], buf[(space + 1)..], ds);
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
    private static async Task<Call> CallAsync(Process process, string clone, string remote, Dialstring ds)
    {
        string p = clone;
        if (clone.StartsWith('/') || clone.StartsWith('#'))
        {
            // Past the first element, or all of it when there is no other.
            p = clone[(clone.IndexOf('/', 1) + 1)..];
        }

        string cname = $"{ds.NetDir}/{p}";
        int cfd = await process.OpenAsync(cname, NinePConstants.ORDWR);
        try
        {
            string name = (await ReadAsync(process, cfd, 255)).TrimStart(' ');
            string number = Libc.Strtoul(name, 0, out _).ToString(CultureInfo.InvariantCulture);
            string protodir = cname[..cname.LastIndexOf('/')];
            await process.WriteAsync(cfd, Encoding.UTF8.GetBytes($"connect {remote}"));
            return new Call(await process.OpenAsync($"{protodir}/{number}/data", NinePConstants.ORDWR), cfd);
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
            // identtrans: without cs, the protocol's clone takes the rest as it is; what is left of
            // the address still holds its first !.
            int bang = addr.IndexOf('!', StringComparison.Ordinal);
            return (addr[(bang + 1)..], $"{netdir}/{addr[..bang]}/clone");
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
        if (space == -1)
        {
            throw new SyscallException(string.Empty);
        }

        // A rooted file loses its first element, if it has another; nettrans is left pointing at
        // the address otherwise, which then names the file.
        string file = buf[..space], naddr = buf[(space + 1)..];
        return (naddr, $"{netdir}/{(file.StartsWith('/') ? file[(file.IndexOf('/', 1) + 1)..] : naddr)}");
    }

    private static async Task<string> ReadAsync(Process process, int fd, int count)
        => Encoding.UTF8.GetString((await process.ReadAsync(fd, count)).Span);

    // A dialed call: its data and its ctl.
    public readonly record struct Call(int Data, int Ctl);

    private sealed record Dialstring(string? NetDir, string Proto, string Rem);
}
