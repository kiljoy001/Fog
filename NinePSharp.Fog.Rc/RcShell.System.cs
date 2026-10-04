using System.Runtime.CompilerServices;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Rc;

// plan9.c: the system calls rc makes, the environment in /env, and waiting for children; with
// glob.c's reading of directories, getflags.c, and Xrdcmds and pprompt from exec.c.
internal sealed partial class RcShell
{
    private const int EnvName = 128;

    // Open's modes; 3 is reading a file that is removed when closed, for here documents.
    private static readonly int[] OpenModes = [NinePConstants.OREAD, NinePConstants.OWRITE, NinePConstants.ORDWR, NinePConstants.OREAD | NinePConstants.ORCLOSE];

    // getflags.c's reason for a usage message.
    private string? usageReason;

    // Env: /env/name, or /env/fn#name, in rc's buffer of 128 bytes.
    private static string Env(string name, bool fn)
    {
        string path = "/env/" + (fn ? "fn#" : string.Empty) + name;
        return path.Length < EnvName ? path : path[..(EnvName - 1)];
    }

    // scanflag: how many arguments rc's flag c takes, or -1 when rc has no such flag.
    private static int Scanflag(char c) => c is 'c' or 'm' ? 1 : Plainflags.Contains(c) ? 0 : -1;

    // pfun: an operation's name, or as pfnc.c's table has no Xsettrue or Xsub, an address.
    private static string Pfun(RcInstruction op) => op.Op is RcOp.Settrue or RcOp.Sub
        ? $"{RuntimeHelpers.GetHashCode(op.Op.ToString()):X8}"
        : "X" + op.Op.ToString().ToLowerInvariant();

    // A system call's result, or null when it failed, with errstr saying why.
    private async ValueTask<int?> SyscallAsync(Func<ValueTask<int>> call)
    {
        try
        {
            return await call();
        }
        catch (SyscallException error)
        {
            errstr = RcText.Rc(error.Message);
            return null;
        }
    }

    private ValueTask<int?> OpenAsync(string file, int mode)
        => SyscallAsync(() => process.OpenAsync(RcText.Kernel(file), OpenModes[mode & 3]));

    // Creat
    private ValueTask<int?> CreatAsync(string file)
        => SyscallAsync(() => process.CreateAsync(RcText.Kernel(file), NinePConstants.OWRITE, 0b110_110_110));

    private async ValueTask CloseAsync(int fd) => await SyscallAsync(async () =>
    {
        await process.CloseAsync(fd);
        return 0;
    });

    private ValueTask<int?> DupAsync(int a, int b) => SyscallAsync(() => process.DupAsync(a, b));

    private ValueTask<int?> Dup1Async(int a) => DupAsync(a, -1);

    private async ValueTask SeekAsync(int fd, long offset, int whence) => await SyscallAsync(async () =>
    {
        await process.SeekAsync(fd, offset, whence);
        return 0;
    });

    // pipe: its two descriptors, or none when it failed.
    private async ValueTask<int[]> PipeAsync()
    {
        int[] fds = [];
        await SyscallAsync(async () =>
        {
            var (first, second) = await process.PipeAsync();
            fds = [first, second];
            return 0;
        });
        return fds;
    }

    private async ValueTask<bool> ChdirAsync(string dir)
        => await SyscallAsync(async () =>
        {
            await process.ChdirAsync(RcText.Kernel(dir));
            return 0;
        }) is not null;

    private async ValueTask<Stat?> DirstatAsync(string file)
    {
        Stat? stat = null;
        await SyscallAsync(async () =>
        {
            stat = await process.StatAsync(RcText.Kernel(file));
            return 0;
        });
        return stat;
    }

    private async ValueTask<Waitmsg?> WaitAsync()
    {
        Waitmsg? w = null;
        await SyscallAsync(async () =>
        {
            w = await process.WaitAsync();
            return 0;
        });
        return w;
    }

    // access(name, 0)
    private async ValueTask<bool> AccessAsync(string file) => await DirstatAsync(file) is not null;

    private async ValueTask<bool> ExecutableAsync(string file)
        => await DirstatAsync(file) is { } stat && (stat.Mode & 0b001_001_001) != 0 && (stat.Mode & (uint)NinePConstants.FileMode9P.DMDIR) == 0;

    private async ValueTask ExitAsync()
    {
        await UpdenvAsync();
        process.Exits(RcText.Kernel(Truestatus() ? string.Empty : Getstatus()));
    }

    // Waitfor: wait for pid, or for every child when pid is -1, keeping the statuses of a
    // pipeline's other commands for Xpipewait.
    private async ValueTask WaitforAsync(long pid)
    {
        if (pid >= 0 && !waitpids.Contains(pid))
        {
            return;
        }

        for (Waitmsg? w; (w = await WaitAsync()) is not null;)
        {
            long wpid = w.Pid;
            waitpids.RemoveAll(waiting => waiting == wpid);
            if (w.Pid == pid)
            {
                Setstatus(RcText.Rc(w.Message));
                return;
            }

            for (RcThread? p = runq!.Ret; p is not null; p = p.Ret)
            {
                if (p.Pid == w.Pid)
                {
                    p.Pid = -1;
                    p.Status = RcText.Rc(w.Message);
                }
            }
        }
    }

    // Vinit: the variables of /env, each file's value its words ended by NULs.
    private async ValueTask VinitAsync()
    {
        int? dir = await OpenAsync(Env(string.Empty, false), 0);
        if (dir is null)
        {
            err.Pstr($"{argv0}: can't open: {errstr}\n");
            return;
        }

        foreach (Stat entry in await process.DirReadAsync(dir.Value))
        {
            string name = RcText.Rc(entry.Name!);
            if (entry.Length == 0 || name.StartsWith("fn#", StringComparison.Ordinal))
            {
                continue;
            }

            int? fd = await OpenAsync(Env(name, false), 0);
            if (fd is null)
            {
                continue;
            }

            RcIo f = RcIo.OpenFd(fd.Value);
            var words = new List<string>();
            for (string? s; (s = await f.RstrAsync(process, string.Empty)) is not null;)
            {
                words.Add(s);
            }

            await f.CloseAsync(process);
            RcWord? val = null;
            for (int i = words.Count - 1; i >= 0; i--)
            {
                val = new RcWord(words[i], val);
            }

            Setvar(name, val);
            Vlook(name).Changed = false;
        }

        await CloseAsync(dir.Value);
    }

    // addenv: a changed variable or function is written to its file in /env.
    private async ValueTask AddenvAsync(RcVar v)
    {
        if (v.Changed)
        {
            v.Changed = false;
            await WriteEnvAsync(Env(v.Name, false), f =>
            {
                for (RcWord? w = v.Val; w is not null; w = w.Next)
                {
                    f.Pstr(w.Text);
                    f.Pchr('\0');
                }
            });
        }

        if (v.FnChanged)
        {
            v.FnChanged = false;
            await WriteEnvAsync(Env(v.Name, true), f =>
            {
                if (v.Fn is not null)
                {
                    f.Pstr("fn ");
                    f.Pwrd(v.Name);
                    f.Pstr(" " + v.Fn[v.Pc - 1].S + "\n");
                }
            });
        }
    }

    private async ValueTask WriteEnvAsync(string file, Action<RcIo> write)
    {
        int? fd = await CreatAsync(file);
        if (fd is null)
        {
            err.Pstr($"{argv0}: can't open: {errstr}\n");
            return;
        }

        RcIo f = RcIo.OpenFd(fd.Value);
        write(f);
        await f.FlushAsync(process);
        await f.CloseAsync(process);
    }

    // Updenv: the globals, then the locals from the outermost in, so the innermost wins.
    private async ValueTask UpdenvAsync()
    {
        foreach (RcVar v in gvar.Values)
        {
            await AddenvAsync(v);
        }

        var locals = new Stack<RcVar>();
        for (RcVar? v = runq?.Local; v is not null; v = v.Next)
        {
            locals.Push(v);
        }

        while (locals.TryPop(out RcVar? v))
        {
            await AddenvAsync(v);
        }

        await err.FlushAsync(process);
    }

    // Opendir and Readdir: a directory's names, or only its directories'.
    private async ValueTask<List<string>?> ReaddirAsync(string name, bool onlydirs)
    {
        int? fd = await OpenAsync(name, 0);
        if (fd is null)
        {
            return null;
        }

        IReadOnlyList<Stat> entries = await process.DirReadAsync(fd.Value);
        await CloseAsync(fd.Value);
        return entries
            .Where(entry => !onlydirs || (entry.Mode & (uint)NinePConstants.FileMode9P.DMDIR) != 0)
            .Select(entry => RcText.Rc(entry.Name!))
            .ToList();
    }

    // globword: a word holding a glob becomes the sorted names it matches, or itself without its
    // GLOB marks when it matches none.
    private async ValueTask GlobwordAsync(RcWord w)
    {
        if (!w.Text.Contains(RcGlob.Glob))
        {
            return;
        }

        RcWord? right = w.Next;
        RcWord? left = await GlobdirAsync(right, w.Text, string.Empty);
        if (left == right)
        {
            w.Text = RcGlob.Deglob(w.Text);
            return;
        }

        var names = new List<string>();
        for (RcWord? a = left; a != right; a = a!.Next)
        {
            names.Add(a!.Text);
        }

        names.Sort(StringComparer.Ordinal);
        RcWord? b = left;
        foreach (string name in names)
        {
            b!.Text = name;
            b = b.Next;
        }

        w.Text = left!.Text;
        w.Next = left.Next;
    }

    // globdir: the names under name matching pattern, in front of list.
    private async ValueTask<RcWord?> GlobdirAsync(RcWord? list, string pattern, string name)
    {
        while (pattern.StartsWith('/'))
        {
            name = Makepath(name, "/");
            pattern = pattern[1..];
        }

        if (pattern.Length == 0)
        {
            return new RcWord(name, list);
        }

        int glob = pattern.IndexOf(RcGlob.Glob);
        if (glob < 0)
        {
            name = Makepath(name, pattern);
            return await AccessAsync(name) ? new RcWord(name, list) : list;
        }

        // The directories before the component holding the glob, then the component and what follows.
        int start = pattern.LastIndexOf('/', glob) + 1;
        if (start != 0)
        {
            name = Makepath(name, pattern[..(start - 1)]);
            pattern = pattern[start..];
        }

        string[] component = pattern.Split('/', 2);
        bool more = component.Length == 2;
        List<string>? entries = await ReaddirAsync(name.Length != 0 ? name : ".", more);
        foreach (string entry in entries ?? [])
        {
            if (RcGlob.MatchName(entry, pattern))
            {
                list = await GlobdirAsync(list, more ? "/" + component[1] : string.Empty, Makepath(name, entry));
            }
        }

        return list;
    }

    private async ValueTask XrdcmdsAsync()
    {
        RcThread p = runq!;
        if (flag['s'] is not null && !Truestatus())
        {
            err.Pstr("status=");
            err.Pval(Vlook("status").Val);
            err.Pchr('\n');
        }

        await err.FlushAsync(process);
        RcLexer lex = p.Lex!.Lexer;
        if (p.IFlag)
        {
            promptstr = Vlook("prompt").Val?.Text ?? "% ";
        }

        lex.ErrorCount = 0;
        RcCode? code = null;
        RcParser.Outcome outcome = await p.Lex.Parser.ParseAsync(async tree =>
        {
            code = await RcCompiler.CompileAsync(lex, tree, flag['e'] is not null);
            return code is not null;
        });
        if (outcome == RcParser.Outcome.Stop)
        {
            if (p.IFlag && !lex.Eof)
            {
                --p.Pc;
            }

            return;
        }

        // rc's dontclose here keeps the input's own close from closing a file opened later on the same
        // descriptor; pushredir moves such a file off the descriptor, so that cannot happen.
        if (lex.Eof)
        {
            await p.Lex.Input.CloseAsync(process);
            p.Lex = null;
        }
        else
        {
            --p.Pc;
        }

        Start(code!, 2, p.Local, p.Redir);
    }

    // pprompt; Prompt's writing of the directory to rio's /dev/wdir is left out, as Fog has no rio.
    private async ValueTask PpromptAsync()
    {
        if (!runq!.IFlag)
        {
            return;
        }

        err.Pstr(promptstr!);
        await err.FlushAsync(process);

        runq.Lex!.Lexer.DoPrompt = false;
        RcWord? prompt = Vlook("prompt").Val;
        promptstr = prompt?.Next?.Text ?? "\t";
    }

    // pfnc, for rc -r: where each thread is and what it is about to do.
    private async ValueTask PfncAsync(RcIo f, RcThread t)
    {
        f.Pfln(Srcfile(t), t.Line, argv0);
        f.Pstr($" pid {process.Pid} cycle {RuntimeHelpers.GetHashCode(t.Code):X8} {t.Pc} {Pfun(t.Code[t.Pc])}");
        for (RcList? a = t.Argv; a is not null; a = a.Next)
        {
            f.Pstr(" (");
            f.Pval(a.Words);
            f.Pchr(')');
        }

        f.Pchr('\n');
        await f.FlushAsync(process);
    }

    // getflags, for rc's flags: the arguments left start at argv[1]. -1 when a flag is bad.
    private int Getflags(List<string> argv)
    {
        int argc = argv.Count;
        int i = 1;
        while (i != argc && argv[i].Length > 1 && argv[i][0] == '-')
        {
            string s = argv[i];
            for (int at = 1; at < s.Length;)
            {
                char c = s[at++];
                int count = Scanflag(c);
                if (count == -1)
                {
                    usageReason = $"Illegal flag -{c}\n";
                    return -1;
                }

                if (flag[c] is not null)
                {
                    usageReason = $"Flag -{c}: set twice\n";
                    return -1;
                }

                if (count == 0)
                {
                    flag[c] = FlagSet;
                    if (at == s.Length)
                    {
                        argv.RemoveAt(i);
                        argc--;
                    }

                    continue;
                }

                if (at == s.Length)
                {
                    argv.RemoveAt(i);
                    argc--;
                    if (i == argc)
                    {
                        usageReason = $"Flag -{c}: too few arguments\n";
                        return -1;
                    }

                    s = argv[i];
                    at = 0;
                }

                flag[c] = [s[at..]];
                argv.RemoveAt(i);
                argc--;
                break;
            }
        }

        return argc;
    }

    // usage: why the flags were bad, then rc's flags.
    private async ValueTask UsageAsync(string tail)
    {
        err.Pstr(usageReason!);
        err.Pstr($"Usage: {argv0} [-{Plainflags}] [-c arg] [-m command] {tail}\n");
        Setstatus("bad flags");
        await ExitAsync();
    }
}
