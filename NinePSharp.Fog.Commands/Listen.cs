using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// aux/listen, as 9front's listen.c: for each file of a service directory named for the protocol
// and a port, a process that announces the port and runs the file for each call, with the call's
// data as its input and output; services of -d as none in the namespace newns builds, those of -t
// in the invoker's. Fog's devproc has no wait file, so -p finds none to count calls by and, as
// listen.c does then, forks each without waiting.
public sealed class Listen(string argv0)
{
    private const int Maxpath = 1024;
    private const int Maxserv = 64;
    private const string Listenlog = "listen";
    private readonly List<string> sopts = [];

    // Stryker disable once String : keepalive is the host socket's, which no file of #I shows, and what writing it returns is not looked at
    private readonly List<string> copts = ["keepalive"];
    private readonly List<Announce> announcements = [];
    private bool quiet;
    private bool immutable;
    private string proto = "tcp";

    // Stryker disable once String : the start sets it before anything reads it
    private string protodir = "tcp";
    private string addr = "*";
    private string? nspath;

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Listen(argv[0]).RunAsync(process, argv);

    // procsetname: what the process's args read.
    private static async Task ProcsetnameAsync(Process process, string name)
    {
        try
        {
            int fd = await process.OpenAsync($"/proc/{process.Pid}/args", NinePConstants.OWRITE);
            try
            {
                await process.WriteAsync(fd, Encoding.UTF8.GetBytes(name));
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException)
        {
            // procsetname's failure is not listen's.
        }
    }

    private static async Task PostnoteAsync(Process process, long pid, string note)
    {
        try
        {
            int fd = await process.OpenAsync($"/proc/{pid}/note", NinePConstants.OWRITE);
            try
            {
                await process.WriteAsync(fd, Encoding.UTF8.GetBytes(note));
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException)
        {
            // A process gone already needs no note.
        }
    }

    // readstr: the device's file, or nothing.
    private static async Task<string> ReadstrAsync(Process process, string dir, string info)
    {
        try
        {
            int fd = await process.OpenAsync($"{dir}/{info}", NinePConstants.OREAD);
            try
            {
                return Encoding.UTF8.GetString((await process.ReadAsync(fd, 127)).Span);
            }

            // Stryker disable once Block,Statement : a call closes 3 through 19 before it runs its service
            finally
            {
                await process.CloseAsync(fd);
            }
        }

        // Stryker disable once Block,String : a call's directory holds its remote while the call has its data open
        catch (SyscallException)
        {
            return string.Empty;
        }
    }

    private static async Task<long> TimeAsync(Process process)
    {
        int fd = await process.OpenAsync("/dev/bintime", NinePConstants.OREAD);
        long nanoseconds = BinaryPrimitives.ReadInt64BigEndian((await process.PReadAsync(fd, 8, 0)).Span);
        await process.CloseAsync(fd);
        return nanoseconds / 1_000_000_000;
    }

    private async Task RunAsync(Process process, IReadOnlyList<string> argv)
    {
        var arguments = new Arguments(argv);
        (string? servdir, string? trustdir) = await FlagsAsync(process, arguments);
        if (servdir is null && trustdir is null)
        {
            servdir = "/bin/service";
        }

        if (servdir is not null && Encoding.UTF8.GetByteCount(servdir) + Maxserv >= Maxpath)
        {
            await ErrorAsync(process, "service directory too long");
        }

        if (trustdir is not null && Encoding.UTF8.GetByteCount(trustdir) + Maxserv >= Maxpath)
        {
            await ErrorAsync(process, "trusted service directory too long");
        }

        IReadOnlyList<string> args = arguments.Rest();
        switch (args.Count)
        {
            case 1:
                proto = args[0];
                break;
            case 0:
                break;
            default:
                await UsageAsync(process);
                break;
        }

        await Syslog.WriteAsync(process, false, Listenlog, $"started on {proto}");
        protodir = proto;
        proto = proto[(proto.LastIndexOf('/') + 1)..];
        Listendir(process, servdir, false);
        Listendir(process, trustdir, true);
        process.Exits(null);
    }

    // The flags, as listen.c's ARGBEGIN takes them: the service and trusted service directories.
    private async Task<(string? Servdir, string? Trustdir)> FlagsAsync(Process process, Arguments arguments)
    {
        string? servdir = null, trustdir = null;
        foreach (char c in arguments.Flags())
        {
            if (c == 'q')
            {
                quiet = true;
            }
            else if (c == 'i')
            {
                // A fixed configuration, without the periodic rescan of the service directory.
                immutable = true;
            }
            else if (c is 'd' or 't')
            {
                string value = await arguments.EargfAsync(() => UsageAsync(process));
                (servdir, trustdir) = c == 'd' ? (value, trustdir) : (servdir, value);
            }
            else if (c is 'a' or 'n' or 'p' or 'o' or 'O')
            {
                await ValueAsync(process, c, await arguments.EargfAsync(() => UsageAsync(process)));
            }
            else
            {
                await UsageAsync(process);
            }
        }

        return (servdir, trustdir);
    }

    // -a's address, -n's namespace, -p's priority, read and let be, and -o's and -O's options.
    private async Task ValueAsync(Process process, char c, string value)
    {
        if (c == 'a')
        {
            addr = value;
        }
        else if (c == 'n')
        {
            nspath = value;
        }
        else if (c == 'p')
        {
            // Without a wait file to count calls by, -p's value is let be.
        }
        else
        {
            await OptionAsync(process, c == 'o' ? copts : sopts, value, $"-{c}");
        }
    }

    // A process for the directory's services, insulated from later changes to the console's
    // environment and namespace.
    private void Listendir(Process process, string? srvdir, bool trusted)
    {
        if (srvdir is not null)
        {
            process.Fork(RforkFlags.Noteg | RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nowait | RforkFlags.Envg | RforkFlags.Nameg, child => ServeAsync(child, srvdir, trusted));
        }
    }

    private async Task ServeAsync(Process process, string srvdir, bool trusted)
    {
        await ProcsetnameAsync(process, $"{protodir} {addr} {srvdir}");
        if (!trusted)
        {
            await BecomeNoneAsync(process);
        }

        // dingdong: an alarm only wakes the wait; any other note has its default.
        process.Notify(note => note.Contains("alarm", StringComparison.Ordinal));
        long pid = process.Pid;
        await ScandirAsync(process, srvdir);
        while (true)
        {
            // A process for each service not announced: trusted services in the invoker's
            // namespace, untrusted in none's.
            foreach (Announce a in announcements.ToArray())
            {
                if (a.Announced > 0)
                {
                    continue;
                }

                await process.SleepAsync((pid * 10) % 200);
                string ds = $"{protodir}!{addr}!{a.Name}";
                string serv = $"{proto}{a.Name}";
                string prog = $"{srvdir}/{serv}";
                a.Announced = process.Fork(RforkFlags.Fdg | RforkFlags.Proc | RforkFlags.Mem, service => AnnounceAsync(service, a, ds, serv, prog, trusted));
            }

            // Unless the configuration is fixed, pick up the services that gave up, rescan, and
            // wait at least 60 seconds between rounds.
            long start = await TimeAsync(process);
            if (!immutable)
            {
                await process.AlarmAsync(60 * 1000);
            }

            while (await WaitOrInterruptedAsync(process) is { } wm)
            {
                foreach (Announce a in announcements.Where(a => a.Announced == wm.Pid))
                {
                    a.Announced = 0;

                    // Not to fill the log.
                    a.Whined |= wm.Message.Contains("addr-in-use", StringComparison.Ordinal);
                }

                if (immutable)
                {
                    break;
                }
            }

            if (!immutable)
            {
                await process.AlarmAsync(0);
                await ScandirAsync(process, srvdir);
            }

            long left = 60 - (await TimeAsync(process) - start);
            await process.SleepAsync(Math.Max(left, 0) * 1000);
        }
    }

    // The service's process: its own namespace if it has one, then the port announced again each
    // time listening on it ends.
    private async Task AnnounceAsync(Process process, Announce a, string ds, string serv, string prog, bool trusted)
    {
        string ns = $"{prog}.namespace";
        if (!trusted && await AccessAsync(process, ns))
        {
            try
            {
                await Newns.NewnsAsync(process, "none", ns);
            }
            catch (SyscallException error)
            {
                await Syslog.WriteAsync(process, true, Listenlog, $"can't build namespace {ns}: {error.Message}\n");
                process.Exits("newns");
            }
        }

        while (true)
        {
            int ctl;
            string dir;
            try
            {
                (ctl, dir) = await Net.AnnounceAsync(process, ds);
            }
            catch (SyscallException error)
            {
                if (!a.Whined)
                {
                    await Syslog.WriteAsync(process, true, Listenlog, $"giving up on {ds}: {error.Message}");
                }

                process.Exits(error.Message.Contains("address in use", StringComparison.Ordinal) ? "addr-in-use" : "ctl");
                return;
            }

            await ProcsetnameAsync(process, $"{dir} {ds}");
            await ListenOnAsync(process, dir, ctl, serv, prog);
            await process.CloseAsync(ctl);
        }
    }

    // dolisten: a process for each call, until listening fails.
    private async Task ListenOnAsync(Process process, string dir, int ctl, string serv, string prog)
    {
        foreach (string opt in sopts)
        {
            try
            {
                await process.WriteAsync(ctl, Encoding.UTF8.GetBytes(opt));
            }
            catch (SyscallException error)
            {
                await Syslog.WriteAsync(process, true, Listenlog, $"{dir}/ctl: can't write {opt}: {error.Message}");
            }
        }

        while (true)
        {
            int nctl;
            string ndir;
            try
            {
                (nctl, ndir) = await Net.ListenAsync(process, dir);
            }
            catch (SyscallException error)
            {
                if (!quiet)
                {
                    await Syslog.WriteAsync(process, true, Listenlog, $"listen: {error.Message}");
                }

                return;
            }

            process.Fork(RforkFlags.Fdg | RforkFlags.Proc | RforkFlags.Mem | RforkFlags.Envg | RforkFlags.Nameg | RforkFlags.Noteg | RforkFlags.Rend | RforkFlags.Nowait, async call =>
            {
                int data;
                try
                {
                    data = await Net.AcceptAsync(call, nctl, ndir);
                }
                catch (SyscallException error)
                {
                    await Syslog.WriteAsync(call, true, Listenlog, $"can't open {ndir}/data: {error.Message}");
                    call.Exits(null);
                    return;
                }

                foreach (string opt in copts)
                {
                    try
                    {
                        await call.WriteAsync(nctl, Encoding.UTF8.GetBytes(opt));
                    }
                    catch (SyscallException)
                    {
                        // listen.c does not look at what these writes return.
                    }
                }

                await call.CloseAsync(ctl);
                await call.CloseAsync(nctl);
                await NewcallAsync(call, data, ndir, prog, serv);
                call.Exits(null);
            });
            await process.CloseAsync(nctl);
        }
    }

    // newcall: the service run with the call's data as its input and output, and nothing else
    // open past standard error.
    private async Task NewcallAsync(Process process, int fd, string dir, string prog, string serv)
    {
        if (!quiet)
        {
            string remote = await ReadstrAsync(process, dir, "remote");
            int bang = remote.IndexOf('!', StringComparison.Ordinal);

            // Stryker disable once Conditional,Equality : a call's remote is an address and its port, which a bang parts
            await Syslog.WriteAsync(process, false, Listenlog, $"{proto} call for {serv} on chan {dir} ({(bang < 0 ? remote : remote[..bang])})");
        }

        try
        {
            await process.BindAsync($"{dir}/data", "/dev/cons", MountFlags.Replace);
        }
        catch (SyscallException)
        {
            // listen.c does not look at whether the bind was made.
        }

        await process.DupAsync(fd, 0);
        await process.DupAsync(fd, 1);

        // Stryker disable once Statement : 3 through 19 are closed next
        await process.CloseAsync(fd);
        for (int n = 3; n < 20; n++)
        {
            try
            {
                await process.CloseAsync(n);
            }
            catch (SyscallException)
            {
                // Most are not open.
            }
        }

        try
        {
            await process.ExecAsync(prog, [prog, serv, proto, dir]);
        }
        catch (SyscallException error)
        {
            await ErrorAsync(process, prog, error.Message);
        }
    }

    // scandir: an announcement for each plain, nonempty file named for the protocol and a port,
    // and none for those whose files have gone, whose processes are told to die.
    private async Task ScandirAsync(Process process, string dname)
    {
        foreach (Announce a in announcements)
        {
            a.Mark = true;
        }

        int fd;
        try
        {
            fd = await process.OpenAsync(dname, NinePConstants.OREAD);
        }
        catch (SyscallException)
        {
            return;
        }

        try
        {
            foreach (NinePSharp.Messages.Stat d in await process.DirReadAsync(fd))
            {
                string nm = d.Name;
                if ((d.Qid.Type & QidType.QTDIR) != 0 || d.Length == 0 || !nm.StartsWith(proto, StringComparison.Ordinal) || nm[proto.Length..].Contains(".namespace", StringComparison.Ordinal))
                {
                    continue;
                }

                Addannounce(nm[proto.Length..]);
            }
        }
        finally
        {
            await process.CloseAsync(fd);
        }

        foreach (Announce a in announcements.Where(a => a.Mark).ToArray())
        {
            announcements.Remove(a);
            if (a.Announced > 0)
            {
                await PostnoteAsync(process, a.Announced, "die");
            }
        }
    }

    private void Addannounce(string str)
    {
        if (announcements.Find(a => a.Name == str) is { } known)
        {
            known.Mark = false;
            return;
        }

        announcements.Add(new Announce(str));
    }

    private async Task BecomeNoneAsync(Process process)
    {
        try
        {
            int fd = await process.OpenAsync("/dev/user", NinePConstants.OWRITE);
            try
            {
                await process.WriteAsync(fd, "none"u8.ToArray());
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException error)
        {
            await ErrorAsync(process, "can't become none", error.Message);
        }

        try
        {
            await Newns.NewnsAsync(process, "none", nspath);
        }
        catch (SyscallException error)
        {
            await ErrorAsync(process, "can't build namespace", error.Message);
        }
    }

    private async Task<bool> AccessAsync(Process process, string name)
    {
        try
        {
            await process.StatAsync(name);
            return true;
        }
        catch (SyscallException)
        {
            return false;
        }
    }

    // wait, or nothing when it fails, as when the alarm interrupts it.
    private async Task<Waitmsg?> WaitOrInterruptedAsync(Process process)
    {
        try
        {
            return await process.WaitAsync();
        }

        // Stryker disable once Block : what an empty catch would return
        catch (SyscallException)
        {
            return null;
        }
    }

    private async Task OptionAsync(Process process, List<string> opts, string opt, string flag)
    {
        if (opts.Count >= 16)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv0}: too many {flag} options\n"));
            process.Exits($"too many {flag} options");
        }

        opts.Add(opt);
    }

    // error: logged, and the process ends, quietly.
    private async Task ErrorAsync(Process process, string s, string? errstr = null)
    {
        await Syslog.WriteAsync(process, true, Listenlog, $"{proto}: {s}: {errstr}");
        process.Exits(null);
    }

    private async Task<string> UsageAsync(Process process)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"usage: {argv0} [-iq] [-d srvdir] [-t trustsrvdir] [-n namespace] [-p maxprocs] [-o copt] [-O sopt] [-a addr] [proto]"));
        process.Exits("usage");
        return default!;
    }

    private sealed class Announce(string name)
    {
        public string Name { get; } = name;

        public long Announced { get; set; }

        public bool Whined { get; set; }

        public bool Mark { get; set; }
    }
}
