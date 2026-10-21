using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// aux/trampoline, as 9front's trampoline.c: a call dialed to the address, its data copied to and
// from the standard input and output, or a call dialed to -a's; with -9 by 9P messages, with -t
// ended once nothing has passed for so long. -m lets through only a caller whose ethernet address,
// as the network's arp holds it, has trampok in the database cs serves.
public sealed class Trampoline(string argv0)
{
    private const int IoUnit = 32768;
    private volatile bool activity;
    private int timeout;

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Trampoline(argv[0]).RunAsync(process, argv);

    // getendpoint: sys and serv from the conversation's file, or unknown.
    private static async Task<(string Sys, string Serv)> EndpointAsync(Process process, string dir, string file)
    {
        try
        {
            int fd = await process.OpenAsync($"{dir}/{file}", NinePConstants.OREAD);
            try
            {
                byte[] buf = (await process.ReadAsync(fd, 127)).ToArray();
                if (buf.Length == 0)
                {
                    return ("unknown", "unknown");
                }

                string text = Encoding.UTF8.GetString(buf[..^1]);
                int bang = text.IndexOf('!', StringComparison.Ordinal);
                return bang == -1 ? (text, "unknown") : (text[..bang], text[(bang + 1)..]);
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException)
        {
            return ("unknown", "unknown");
        }
    }

    // iptomac: the ethernet address arp holds for the address, if any.
    private static async Task<string?> IptomacAsync(Process process, string ip, string net)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync($"{net}/arp", NinePConstants.OREAD);
        }
        catch (SyscallException)
        {
            return null;
        }

        try
        {
            var all = new List<byte>();
            for (ReadOnlyMemory<byte> block; !(block = await process.ReadAsync(fd, 8192)).IsEmpty;)
            {
                all.AddRange(block.Span);
            }

            string text = Encoding.UTF8.GetString([.. all]);
            foreach (string line in text.Split('\n')[..^1])
            {
                List<string> f = Process.Tokenize(line, 5);
                if (f.Count >= 4 && f[1] == "OK" && f[2] == ip)
                {
                    return f[3];
                }
            }

            return null;
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    // postnote(PNGROUP, ...): the note to the rest of the process's note group.
    private static async Task PostnoteGroupAsync(Process process, string note)
    {
        try
        {
            int fd = await process.OpenAsync($"/proc/{process.Pid}/notepg", NinePConstants.OWRITE);
            await process.WriteAsync(fd, Encoding.UTF8.GetBytes(note));

            // Stryker disable once Statement : every postnote here is followed by the exit of the process posting it, which closes the descriptor
            await process.CloseAsync(fd);
        }
        catch (SyscallException)
        {
            // postnote's failure is not trampoline's.
        }
    }

    // readn: as many bytes as asked, or fewer at the end of the input.
    private static async Task<byte[]> ReadnAsync(Process process, int fd, int count)
    {
        var buf = new List<byte>(count);
        while (buf.Count < count)
        {
            ReadOnlyMemory<byte> got;
            try
            {
                got = await process.ReadAsync(fd, count - buf.Count);
            }
            catch (SyscallException)
            {
                break;
            }

            if (got.IsEmpty)
            {
                break;
            }

            buf.AddRange(got.Span);
        }

        return [.. buf];
    }

    private async Task RunAsync(Process process, IReadOnlyList<string> argv)
    {
        string? altaddr = null, checkmac = null;
        bool nineP = false;
        var opts = new List<string>();
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case '9':
                    nineP = true;
                    break;
                case 'a':
                    altaddr = await arguments.EargfAsync(() => UsageAsync(process));
                    break;
                case 'm':
                    checkmac = await arguments.EargfAsync(() => UsageAsync(process));
                    break;
                case 't':
                    timeout = Libc.Strtol(await arguments.EargfAsync(() => UsageAsync(process)), 10, out _);
                    break;
                case 'o':
                    if (opts.Count >= 16)
                    {
                        await FatalAsync(process, "too many -o options");
                    }

                    opts.Add(await arguments.EargfAsync(() => UsageAsync(process)));
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count != 1)
        {
            await UsageAsync(process);
        }

        if (checkmac is not null)
        {
            await CheckmacAsync(process, checkmac);
        }

        if (timeout > 0)
        {
            await process.AlarmAsync(timeout);
        }

        int fd0 = 0, fd1 = 1;
        if (altaddr is not null)
        {
            fd0 = fd1 = await DialAsync(process, () => Net.DialAsync(process, altaddr), altaddr);
        }

        Net.Call call = await DialAsync(process, () => Net.DialCtlAsync(process, args[0]), args[0]);
        foreach (string opt in opts)
        {
            try
            {
                await process.WriteAsync(call.Ctl, Encoding.UTF8.GetBytes(opt));
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv0}: can't write {opt}: {error.Message}\n"));
            }
        }

        await process.CloseAsync(call.Ctl);
        await process.RforkAsync(RforkFlags.Noteg);
        if (timeout > 0)
        {
            await process.AlarmAsync(0);

            // Stryker disable once Bitwise : Fog's fork always makes a process, and a managed child shares what its body closes over, as RFMEM would
            process.Fork(RforkFlags.Proc | RforkFlags.Mem, child => RelayAsync(child, nineP, fd0, fd1, call.Data));

            // The parent watches for a timeout with nothing passing, as trampoline.c's does.
            do
            {
                activity = false;
                await process.SleepAsync(timeout);
            }
            while (activity);

            // Stryker disable once String : any note kills the halves relaying, and nothing waits for their status
            await PostnoteGroupAsync(process, "timeout");
            process.Exits("timeout");
        }

        await RelayAsync(process, nineP, fd0, fd1, call.Data);
    }

    // Each way in a process of its own; whichever ends first hangs the other up.
    private async Task RelayAsync(Process process, bool nineP, int fd0, int fd1, int fd)
    {
        // Stryker disable once Bitwise : Fog's fork always makes a process, and a managed child shares what its body closes over, as RFMEM would
        process.Fork(RforkFlags.Proc | RforkFlags.Mem, async child =>
        {
            await XferAsync(child, nineP, fd0, fd);
            await PostnoteGroupAsync(child, "hangup");
        });
        await XferAsync(process, nineP, fd, fd1);

        // Stryker disable once String : any note kills the half still relaying, and nothing waits for its status
        await PostnoteGroupAsync(process, "hangup");
        process.Exits(null);
    }

    private Task XferAsync(Process process, bool nineP, int from, int to) => nineP ? Xfer9pAsync(process, from, to) : XferBytesAsync(process, from, to);

    private async Task XferBytesAsync(Process process, int from, int to)
    {
        while (true)
        {
            ReadOnlyMemory<byte> buf;
            try
            {
                buf = await process.ReadAsync(from, IoUnit);
            }
            catch (SyscallException)
            {
                return;
            }

            if (buf.IsEmpty)
            {
                return;
            }

            try
            {
                await process.WriteAsync(to, buf);
            }
            catch (SyscallException)
            {
                return;
            }

            // Only a watcher of a timeout looks.
            activity = true;
        }
    }

    // xfer9p: whole 9P messages, each by the size it begins with.
    private async Task Xfer9pAsync(Process process, int from, int to)
    {
        while (true)
        {
            byte[] size = await ReadnAsync(process, from, 4);
            if (size.Length != 4)
            {
                return;
            }

            int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(size);
            byte[] rest = await ReadnAsync(process, from, n - 4);
            if (rest.Length != n - 4)
            {
                return;
            }

            // A write is whole or fails, as devpipe's and devip's are, so its count is not looked at.
            try
            {
                await process.WriteAsync(to, (byte[])[.. size, .. rest]);
            }
            catch (SyscallException)
            {
                return;
            }

            activity = true;
        }
    }

    private async Task CheckmacAsync(Process process, string dir)
    {
        int slash = dir.IndexOf('/', 1);
        string net = slash == -1 ? "/net" : dir[..slash];
        (string lsys, string lserv) = await EndpointAsync(process, dir, "local");
        (string rsys, string rserv) = await EndpointAsync(process, dir, "remote");
        string? mac = await IptomacAsync(process, rsys, net);

        // macok: the database lets the ethernet address through trampolines.
        if (mac is not null && await Ndb.CsGetValueAsync(process, "/net", "ether", mac, "trampok") is not null)
        {
            return;
        }

        await Syslog.WriteAsync(process, false, "trampoline", $"badmac {mac ?? "<nil>"} from {rsys}!{rserv} for {lsys}!{lserv} on {net}");
        process.Exits("bad mac");
    }

    private async Task<T> DialAsync<T>(Process process, Func<Task<T>> dial, string addr)
    {
        try
        {
            return await dial();
        }
        catch (SyscallException error)
        {
            return await FatalAsync<T>(process, $"dial {addr}: {error.Message}");
        }
    }

    private async Task<string> UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: trampoline [-9] [-a addr] [-m netdir] [-o opt] [-t timeout] addr\n"u8.ToArray());
        process.Exits("usage");
        return default!;
    }

    private Task FatalAsync(Process process, string message) => FatalAsync<bool>(process, message);

    // sysfatal, which exits, so it never returns a T.
    private async Task<T> FatalAsync<T>(Process process, string message)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv0}: {message}\n"));
        process.Exits(message);
        return default!;
    }
}
