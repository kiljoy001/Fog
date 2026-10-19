using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// libauth's newns and addns, as 9front's newns.c: a namespace built, or added to, from a file of
// bind, mount, unmount, cd, chdev, clear and . lines, each argument's $names read from #e. Fog has
// no factotum yet, so a mount is made without authentication, as famount makes one when fauth
// fails; and no #c/drivers, so chdev always fails as newns.c fails it without one. Each fails by
// throwing what it would leave in errstr.
public static class Newns
{
    private const int AnameLen = 28;
    private const int MaxArg = 10 * AnameLen;
    private const int MaxArgs = 16;
    private const string Wocp = "sys: write on closed pipe";

    public static Task NewnsAsync(Process process, string user, string? file, bool debug = false) => BuildAsync(process, true, user, file, debug);

    public static Task AddnsAsync(Process process, string user, string? file, bool debug = false) => BuildAsync(process, false, user, file, debug);

    // getuser: the user /dev/user names.
    public static async Task<string> GetuserAsync(Process process)
    {
        int fd = await process.OpenAsync("/dev/user", NinePConstants.OREAD | NinePConstants.OCEXEC);
        try
        {
            return Encoding.UTF8.GetString((await process.ReadAsync(fd, AnameLen - 1)).Span);
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    private static async Task BuildAsync(Process process, bool newns, string user, string? file, bool debug)
    {
        var ns = new Builder(process, debug);
        int afd = await ns.TryOpenAsync("/mnt/factotum/rpc", NinePConstants.ORDWR | NinePConstants.OCEXEC);
        try
        {
            ns.Drivers = await ns.TryOpenAsync("#c/drivers", NinePConstants.OWRITE | NinePConstants.OCEXEC);
            byte[] contents;
            try
            {
                if (file is null && !newns)
                {
                    throw new SyscallException("no namespace file specified");
                }

                file ??= "/lib/namespace";
                contents = await ReadFileAsync(process, file);
            }
            finally
            {
                await CloseAsync(process, ns.Drivers);
            }

            if (newns)
            {
                await process.RforkAsync(RforkFlags.Envg | RforkFlags.Cnameg);
                await SetenvAsync(process, "user", user);
                await SetenvAsync(process, "home", $"/usr/{user}");
            }

            bool cdroot = await ns.FileAsync(newns ? "newns" : "addns", contents);

            // Make sure the process is in the new namespace.
            if (newns && !cdroot)
            {
                try
                {
                    await process.ChdirAsync(await GetwdAsync(process));
                }
                catch (SyscallException)
                {
                    await process.ChdirAsync("/");
                }
            }
        }
        finally
        {
            await CloseAsync(process, afd);
        }
    }

    // Bopen and its reads: the whole file, or the error its open left.
    private static async Task<byte[]> ReadFileAsync(Process process, string file)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync(file, NinePConstants.OREAD | NinePConstants.OCEXEC);
        }
        catch (SyscallException error)
        {
            throw new SyscallException($"can't open {file}: {error.Message}");
        }

        try
        {
            var all = new List<byte>();
            for (ReadOnlyMemory<byte> block; !(block = await process.ReadAsync(fd, 8192)).IsEmpty;)
            {
                all.AddRange(block.Span);
            }

            return [.. all];
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    private static async Task<string> GetwdAsync(Process process)
    {
        int fd = await process.OpenAsync(".", NinePConstants.OREAD);
        try
        {
            return await process.Fd2PathAsync(fd);
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    private static async Task SetenvAsync(Process process, string name, string value)
    {
        int fd;
        try
        {
            fd = await process.CreateAsync($"#e/{name}", NinePConstants.OWRITE | NinePConstants.OCEXEC, 0b110_110_100);
        }
        catch (SyscallException)
        {
            return;
        }

        try
        {
            await process.WriteAsync(fd, Encoding.UTF8.GetBytes(value));
        }
        catch (SyscallException)
        {
            // setenv's failure is not newns's.
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    private static async Task CloseAsync(Process process, int fd)
    {
        if (fd >= 0)
        {
            await process.CloseAsync(fd);
        }
    }

    // gettokens: tokens split at the separators outside quotes, the quotes kept; at most max.
    private static List<byte[]> Gettokens(byte[] s, int max, string sep)
    {
        var args = new List<byte[]>();
        int p = 0;
        while (args.Count < max)
        {
            while (p < s.Length && sep.Contains((char)s[p], StringComparison.Ordinal))
            {
                p++;
            }

            if (p == s.Length)
            {
                break;
            }

            int start = p;
            bool quoting = false;
            while (p < s.Length && (quoting || !sep.Contains((char)s[p], StringComparison.Ordinal)))
            {
                if (s[p] == '\'' && quoting && p + 1 < s.Length && s[p + 1] == '\'')
                {
                    p += 2;
                    continue;
                }

                quoting ^= s[p] == '\'';
                p++;
            }

            args.Add(s[start..p]);
        }

        return args;
    }

    // unquote: the quotes taken out, and a doubled quote within them left as one.
    private static byte[] Unquote(byte[] s)
    {
        var w = new List<byte>(s.Length);
        bool inquote = false;
        for (int r = 0; r < s.Length; r++)
        {
            if (s[r] != '\'')
            {
                w.Add(s[r]);
            }
            else if (!inquote)
            {
                inquote = true;
            }
            else if (r + 1 < s.Length && s[r + 1] == '\'')
            {
                w.Add((byte)'\'');
                r++;
            }
            else
            {
                inquote = false;
            }
        }

        return [.. w];
    }

    // nextdollar: the first $ outside quotes, or -1.
    private static int Nextdollar(byte[] arg, int from)
    {
        bool inquote = false;
        for (int p = from; p < arg.Length; p++)
        {
            inquote ^= arg[p] == '\'';
            if (arg[p] == '$' && !inquote)
            {
                return p;
            }
        }

        return -1;
    }

    private sealed class Builder(Process process, bool debug)
    {
        public int Drivers { get; set; } = -1;

        public async Task<int> TryOpenAsync(string name, int mode)
        {
            try
            {
                return await process.OpenAsync(name, mode);
            }
            catch (SyscallException error)
            {
                await DebugAsync($"open {name}: {error.Message}\n");
                return -1;
            }
        }

        // nsfile: each line up to its newline, without leading blanks, and not a comment; a last line
        // with no newline is not read, as Brdline does not return it. A write on a closed pipe is
        // caught while the file is run.
        public async Task<bool> FileAsync(string fn, byte[] contents)
        {
            Func<string, bool>? previous = null;
            previous = process.Notify(note => note.StartsWith(Wocp, StringComparison.Ordinal) || (previous?.Invoke(note) ?? false));
            try
            {
                bool cdroot = false;
                for (int start = 0, end; (end = Array.IndexOf(contents, (byte)'\n', start)) >= 0; start = end + 1)
                {
                    int cmd = start;
                    while (cmd < end && contents[cmd] is (byte)' ' or (byte)'\t')
                    {
                        cmd++;
                    }

                    if (cmd < end && contents[cmd] == '#')
                    {
                        continue;
                    }

                    if (await SplitargsAsync(contents[cmd..end]) is { Count: > 0 } argv)
                    {
                        cdroot |= await OpAsync(fn, argv);
                    }
                }

                return cdroot;
            }
            finally
            {
                process.Notify(previous);
            }
        }

        // splitargs: the tokens, each expanded and unquoted; none if there are too many or one is too long.
        private async Task<List<string>?> SplitargsAsync(byte[] line)
        {
            List<byte[]> tokens = Gettokens(line, MaxArgs, " \t\r");
            if (tokens.Count == MaxArgs)
            {
                return null;
            }

            var argv = new List<string>(tokens.Count);
            foreach (byte[] token in tokens)
            {
                if (await ExpandargAsync(token) is not { } expanded)
                {
                    return null;
                }

                argv.Add(Encoding.UTF8.GetString(Unquote(expanded)));
            }

            return argv;
        }

        // expandarg: each $name outside quotes replaced by the first element of #e/name; a name of
        // ANAMELEN or more is dropped, and an argument that grows to MAXARG is refused.
        private async Task<byte[]?> ExpandargAsync(byte[] arg)
        {
            var buf = new List<byte>();
            int at = 0;
            for (int p; (p = Nextdollar(arg, at)) >= 0;)
            {
                if (buf.Count + (p - at) + AnameLen >= MaxArg - 1)
                {
                    return null;
                }

                buf.AddRange(arg[at..p]);
                p++;
                int end = Array.FindIndex(arg, p, c => c is (byte)'/' or (byte)'.' or (byte)'!' or (byte)'\'' or (byte)'$');
                at = end < 0 ? arg.Length : end;
                int len = at - p;
                if (len == 0 || len >= AnameLen)
                {
                    continue;
                }

                buf.AddRange(await EnvAsync(Encoding.UTF8.GetString(arg[p..at])));
            }

            if (buf.Count + (arg.Length - at) >= MaxArg - 1)
            {
                return null;
            }

            buf.AddRange(arg[at..]);
            return [.. buf];
        }

        // Some singleton variables have trailing NULs, and lists separate their entries with NULs:
        // the first element is taken.
        private async Task<byte[]> EnvAsync(string name)
        {
            int fd;
            try
            {
                fd = await process.OpenAsync($"#e/{name}", NinePConstants.OREAD | NinePConstants.OCEXEC);
            }
            catch (SyscallException)
            {
                return [];
            }

            try
            {
                byte[] value = (await process.ReadAsync(fd, AnameLen - 1)).ToArray();
                int nul = Array.IndexOf(value, (byte)0);
                return nul < 0 ? value : value[..nul];
            }
            finally
            {
                await process.CloseAsync(fd);
            }
        }

        // nsop: the line's command, with bind and mount's flags; true for a cd to a rooted name.
        private async Task<bool> OpAsync(string fn, List<string> argv)
        {
            await DebugAsync(string.Concat(argv.Select(arg => arg + " ")) + "\n");
            MountFlags flags = MountFlags.Replace;
            var arguments = new Arguments(argv);
            foreach (char c in arguments.Flags())
            {
                flags |= c switch
                {
                    'a' => MountFlags.After,
                    'b' => MountFlags.Before,
                    'c' => MountFlags.Create,
                    'C' => MountFlags.Cache,
                    _ => MountFlags.Replace,
                };
            }

            IReadOnlyList<string> args = arguments.Rest();
            switch (argv[0], args.Count)
            {
                case (".", 1):
                    byte[] contents;
                    try
                    {
                        contents = await ReadFileAsync(process, args[0]);
                    }
                    catch (SyscallException)
                    {
                        return false;
                    }

                    return await FileAsync(fn, contents);
                case ("clear", 0):
                    await process.RforkAsync(RforkFlags.Cnameg);
                    break;
                case ("bind", 2):
                    await TryAsync(() => process.BindAsync(args[0], args[1], flags).AsTask(), $"{fn}: bind: {args[0]} {args[1]}");
                    break;
                case ("unmount", 1 or 2):
                    await TryAsync(() => process.UnmountAsync(args.Count == 2 ? args[0] : null, args[^1]).AsTask(), null);
                    break;
                case ("mount", _):
                    await MountAsync(fn, args, flags);
                    break;
                case ("cd", 1):
                    return await TryAsync(() => process.ChdirAsync(args[0]).AsTask(), null) && args[0].StartsWith('/');
                case ("chdev", _):
                    // chdev cannot be honoured without #c/drivers, which Fog has not.
                    await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{process.Text}: chdev requested, but could not open #c/drivers\n"));
                    process.Exits("chdev requested, but could not open #c/drivers");
                    break;
            }

            return false;
        }

        // famount: the file server on the file, mounted without authentication.
        private async Task MountAsync(string fn, IReadOnlyList<string> args, MountFlags flags)
        {
            int fd;
            try
            {
                fd = await process.OpenAsync(args[0], NinePConstants.ORDWR);
            }
            catch (SyscallException error)
            {
                await DebugAsync($"{fn}: mount: {args[0]}: {error.Message}\n");
                return;
            }

            if (args.Count is not (2 or 3))
            {
                await process.CloseAsync(fd);
                return;
            }

            string aname = args.Count == 3 ? args[2] : string.Empty;
            if (!await TryAsync(() => process.MountAsync(fd, -1, args[1], flags, aname).AsTask(), $"{fn}: mount: {string.Join(' ', args)}"))
            {
                await process.CloseAsync(fd);
            }
        }

        private async Task<bool> TryAsync(Func<Task> call, string? what)
        {
            try
            {
                await call();
                return true;
            }
            catch (SyscallException error)
            {
                if (what is not null)
                {
                    await DebugAsync($"{what}: {error.Message}\n");
                }

                return false;
            }
        }

        private async Task DebugAsync(string message)
        {
            if (debug)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes(message));
            }
        }
    }
}
