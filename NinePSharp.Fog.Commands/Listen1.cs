using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// aux/listen1, as 9front's listen1.c: each call to the address runs the command in a process of its
// own, with the call's data as its input and output, as none in the namespace newns builds unless
// -t. Fog's devproc has no wait file, so -p finds none to count its processes by and, as listen1.c
// does then, forks each without waiting.
public sealed class Listen1(Process process, string argv0)
{
    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Listen1(process, argv[0]).RunAsync(argv);

    // remoteaddr: the system of the call's remote, or nothing.
    private static async Task<string> RemoteaddrAsync(Process process, string dir)
    {
        // Stryker disable once String : the call's remote file is there for as long as the listener holds its ctl, so it always opens
        string remote = string.Empty;
        try
        {
            int fd = await process.OpenAsync($"{dir}/remote", NinePConstants.OREAD);
            try
            {
                remote = Encoding.UTF8.GetString((await process.ReadAsync(fd, 128)).Span).Split('!')[0];
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException)
        {
            // remoteaddr gives an empty system for a remote it cannot read.
        }

        return remote;
    }

    private static async Task PutenvAsync(Process process, string name, string value)
    {
        try
        {
            int fd = await process.CreateAsync($"/env/{name}", NinePConstants.OWRITE, 0b110_110_100);
            try
            {
                await process.WriteAsync(fd, Encoding.UTF8.GetBytes(value));
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }
        catch (SyscallException)
        {
            // putenv's failure is not listen1's.
        }
    }

    private static Task PrintAsync(Process process, string text) => process.WriteAsync(2, Encoding.UTF8.GetBytes(text)).AsTask();

    // exec, which returns only when it fails, and then why.
    private static async Task<string> ExecAsync(Process process, string path, IReadOnlyList<string> args)
    {
        try
        {
            await process.ExecAsync(path, args.Skip(1).ToList());
        }
        catch (SyscallException error)
        {
            return error.Message;
        }

        throw new System.Diagnostics.UnreachableException();
    }

    private async Task RunAsync(IReadOnlyList<string> argv)
    {
        bool oneshot = false, trusted = false, quiet = false;
        string? nsfile = null;
        var sopts = new List<string>();

        // Stryker disable once String,Initializer : #I, like devip, refuses an empty message and keeps keepalive to the host's socket, where no file shows it
        var copts = new List<string> { "keepalive" };
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case '1':
                    oneshot = true;
                    break;
                case 't':
                    trusted = true;
                    break;
                case 'v':
                    break;
                case 'q':
                    quiet = true;
                    break;
                case 'p':
                    _ = Libc.Strtol(await arguments.EargfAsync(() => UsageAsync()), 10, out _);
                    break;
                case 'n':
                    nsfile = await arguments.EargfAsync(() => UsageAsync());
                    break;
                case 'o':
                    await OptionAsync(copts, await arguments.EargfAsync(() => UsageAsync()), "-o");
                    break;
                case 'O':
                    await OptionAsync(sopts, await arguments.EargfAsync(() => UsageAsync()), "-O");
                    break;
                default:
                    await UsageAsync();
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count < 2)
        {
            await UsageAsync();
        }

        if (!trusted)
        {
            await BecomeNoneAsync(nsfile);
        }

        if (!quiet)
        {
            await PrintAsync(process, $"listen {args[0]} started\n");
        }

        (int ctl, string dir) = await Or(() => Net.AnnounceAsync(process, args[0]), $"announce {args[0]}");
        foreach (string opt in sopts)
        {
            try
            {
                await process.WriteAsync(ctl, Encoding.UTF8.GetBytes(opt));
            }
            catch (SyscallException error)
            {
                await PrintAsync(process, $"{dir}/ctl: can't write {opt}: {error.Message}\n");
            }
        }

        while (true)
        {
            (int nctl, string ndir) = await Or(() => Net.ListenAsync(process, dir), $"listen {args[0]}");
            if (oneshot)
            {
                await AnswerAsync(process, args, ctl, nctl, ndir, copts, quiet, oneshot);
            }
            else
            {
                process.Fork(RforkFlags.Fdg | RforkFlags.Proc | RforkFlags.Mem | RforkFlags.Envg | RforkFlags.Nameg | RforkFlags.Noteg | RforkFlags.Rend | RforkFlags.Nowait, child => AnswerAsync(child, args, ctl, nctl, ndir, copts, quiet, oneshot));
                await process.CloseAsync(nctl);
            }
        }
    }

    // A call taken, its process given the call's data and its ctl options, and then the command,
    // which does not return; a oneshot listener whose accept failed returns, and listens again.
    private async Task AnswerAsync(Process process, IReadOnlyList<string> args, int ctl, int nctl, string ndir, List<string> copts, bool quiet, bool oneshot)
    {
        int fd;
        try
        {
            fd = await Net.AcceptAsync(process, nctl, ndir);
        }
        catch (SyscallException error)
        {
            await PrintAsync(process, $"accept {args[0]}: can't open  {ndir}/data: {error.Message}\n");
            if (oneshot)
            {
                await process.CloseAsync(nctl);
                return;
            }

            process.Exits("accept");
            return;
        }

        if (!quiet)
        {
            await PrintAsync(process, $"incoming call for {args[0]} from {await RemoteaddrAsync(process, ndir)} in {ndir}\n");
        }

        foreach (string opt in copts)
        {
            try
            {
                await process.WriteAsync(nctl, Encoding.UTF8.GetBytes(opt));
            }
            catch (SyscallException)
            {
                // listen1.c does not look at what these writes return.
            }
        }

        await process.CloseAsync(ctl);
        await process.CloseAsync(nctl);
        await PutenvAsync(process, "net", ndir);
        try
        {
            await process.BindAsync($"{ndir}/data", "/dev/cons", MountFlags.Replace);
        }
        catch (SyscallException)
        {
            // Nor at whether the bind was made.
        }

        await process.DupAsync(fd, 0);
        await process.DupAsync(fd, 1);
        if (fd > 2)
        {
            await process.CloseAsync(fd);
        }

        string why = await ExecAsync(process, args[1], args);
        if (!args[1].StartsWith('/'))
        {
            why = await ExecAsync(process, $"/bin/{args[1]}", args);
        }

        await PrintAsync(process, $"{argv0}: exec: {why}\n");
        process.Exits("exec");
    }

    // becomenone: the process made none, in the namespace newns builds for none.
    private async Task BecomeNoneAsync(string? nsfile)
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
            await FatalAsync($"can't become none: {error.Message}");
        }

        try
        {
            await Newns.NewnsAsync(process, "none", nsfile);
        }
        catch (SyscallException error)
        {
            await FatalAsync($"can't build namespace: {error.Message}");
        }
    }

    private async Task OptionAsync(List<string> opts, string opt, string flag)
    {
        if (opts.Count >= 16)
        {
            await FatalAsync($"too many {flag} options");
        }

        opts.Add(opt);
    }

    private async Task<T> Or<T>(Func<Task<T>> call, string what)
    {
        try
        {
            return await call();
        }
        catch (SyscallException error)
        {
            return await FatalAsync<T>($"{what}: {error.Message}");
        }
    }

    private async Task<string> UsageAsync()
    {
        await PrintAsync(process, $"usage: {argv0} [-1tq] [-n namespace] [-p maxprocs] [-O msg] [-o msg] address cmd args...\n");
        process.Exits("usage");
        return default!;
    }

    private Task FatalAsync(string message) => FatalAsync<bool>(message);

    // sysfatal, which exits, so it never returns a T.
    private async Task<T> FatalAsync<T>(string message)
    {
        await PrintAsync(process, $"{argv0}: {message}\n");
        process.Exits(message);
        return default!;
    }
}
