using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Rc;

// simple.c: running a simple command, and the builtins, with plan9.c's finit and rfork.
internal sealed partial class RcShell
{
    // The builtins, as rc's table of them.
    private static readonly Dictionary<string, Func<RcShell, ValueTask>> Builtins = new(StringComparer.Ordinal)
    {
        ["cd"] = s => s.ExeccdAsync(),
        ["whatis"] = s => s.ExecwhatisAsync(),
        ["eval"] = s => s.ExecevalAsync(),
        ["exec"] = s => s.ExecexecAsync(),
        ["exit"] = s => s.ExecexitAsync(),
        ["shift"] = s => s.Execshift(),
        ["wait"] = s => s.ExecwaitAsync(),
        ["."] = s => s.ExecdotAsync(),
        ["flag"] = s => s.ExecflagAsync(),
        ["finit"] = s => s.ExecfinitAsync(),
        ["rfork"] = s => s.ExecrforkAsync(),
    };

    private static readonly Dictionary<char, RforkFlags> RforkLetters = new()
    {
        ['n'] = RforkFlags.Nameg,
        ['N'] = RforkFlags.Cnameg,
        ['m'] = RforkFlags.Nomnt,
        ['e'] = RforkFlags.Envg,
        ['E'] = RforkFlags.Cenvg,
        ['s'] = RforkFlags.Noteg,
        ['f'] = RforkFlags.Fdg,
        ['F'] = RforkFlags.Cfdg,
    };

    private static readonly RcCode Rdcmds = new([default, new(RcOp.None, S: "*rdcmds*"), new(RcOp.Rdcmds), new(RcOp.Return)]);

    // rfork's letters, or null for one it does not have.
    private static RforkFlags? Rforkflags(string letters)
    {
        RforkFlags arg = 0;
        foreach (char c in letters)
        {
            if (!RforkLetters.TryGetValue(c, out RforkFlags f))
            {
                return null;
            }

            arg |= f;
        }

        return arg;
    }

    private static bool IsBuiltin(string name) => Builtins.ContainsKey(name);

    // makepath
    private static string Makepath(string dir, string file)
    {
        if (dir.Length == 0)
        {
            return file;
        }

        return dir.TrimEnd('/') + "/" + file.TrimStart('/');
    }

    // atoi: the leading digits, after white space and an optional sign.
    private static int Atoi(string s)
    {
        s = s.TrimStart(' ', '\t', '\n', '\r', '\v', '\f');
        int i = 0, n = 0;
        bool negative = s.StartsWith('-');
        if (negative || s.StartsWith('+'))
        {
            i++;
        }

        for (; i < s.Length && char.IsAsciiDigit(s[i]); i++)
        {
            n = (n * 10) + s[i] - '0';
        }

        return negative ? -n : n;
    }

    private static void PrintValue(RcIo f, RcWord val)
    {
        if (val.Next is null)
        {
            f.Pwrd(val.Text);
            f.Pchr('\n');
            return;
        }

        char sep = '(';
        for (RcWord? b = val; b is not null; b = b.Next)
        {
            f.Pchr(sep);
            f.Pwrd(b.Text);
            sep = ' ';
        }

        f.Pstr(")\n");
    }

    // exitnext: whether the code that follows only exits, so a command may be exec'd without a fork.
    // An Xifnot met here comes after its if's Xwastrue, which leaves it jumping over its body.
    private bool Exitnext()
    {
        RcThread? p = runq;
        int c = p!.Pc;
        while (true)
        {
            RcOp op = p.Code[c].Op;
            if (op is RcOp.Popredir or RcOp.Unlocal or RcOp.Eflag)
            {
                c++;
            }
            else if (op == RcOp.Srcline)
            {
                c += 2;
            }
            else if (op == RcOp.Wastrue)
            {
                c++;
            }
            else if (op == RcOp.Ifnot)
            {
                c = p.Code[c + 1].I;
            }
            else if (op == RcOp.Return)
            {
                p = p.Ret;
                if (p is null)
                {
                    return true;
                }

                c = p.Pc;
            }
            else
            {
                return op == RcOp.Exit;
            }
        }
    }

    private async ValueTask XsimpleAsync()
    {
        RcWord? a = runq!.Argv!.Words;
        if (a is null)
        {
            await Xerror1Async("empty argument list");
            return;
        }

        if (flag['x'] is not null)
        {
            err.Pval(a);
            err.Pchr('\n');
        }

        RcVar v = Gvlook(a.Text);
        if (v.Fn is not null)
        {
            Execfunc(v);
            return;
        }

        if (a.Text == "builtin")
        {
            a = a.Next;
            if (a is null)
            {
                await Xerror1Async("builtin: empty argument list");
                return;
            }

            Popword();
        }

        if (IsBuiltin(a.Text))
        {
            await Builtins[a.Text](this);
            return;
        }

        if (Exitnext() && Trapexit() is null)
        {
            // fork and wait is redundant
            await ExecAsync();
        }
        else
        {
            long pid = await ExecforkexecAsync();
            Poplist();
            await WaitforAsync(pid);
        }
    }

    // doredir: the redirections, oldest first.
    private async ValueTask DoredirAsync(RcRedir? rp)
    {
        if (rp is null)
        {
            return;
        }

        await DoredirAsync(rp.Next);
        switch (rp.Type)
        {
            case RcRedir.Dup:
                await DupAsync(rp.From, rp.To);
                break;
            case RcRedir.Open when rp.From != rp.To:
                await DupAsync(rp.From, rp.To);
                await CloseAsync(rp.From);
                break;
            case RcRedir.Close:
                await CloseAsync(rp.To);
                break;
        }
    }

    // searchpath: the directories of $path, or $cdpath, for a name that is not already a path.
    private RcWord Searchpath(string w, string v)
    {
        bool path = w.Length != 0 && w[0] != '/' && w[0] != '#'
            && (w[0] != '.' || (w.Length > 1 && w[1] != '/' && (w[1] != '.' || (w.Length > 2 && w[2] != '/'))));
        return path && Vlook(v).Val is { } dirs ? dirs : new RcWord(string.Empty, null);
    }

    private async ValueTask ExecexecAsync()
    {
        Popword();
        if (runq!.Argv!.Words is null)
        {
            await Xerror1Async("exec: empty argument list");
            return;
        }

        await ExecAsync();
    }

    // Exec the command on the stack, or say why it could not be.
    private async ValueTask ExecAsync()
    {
        var argv = new List<string>();
        for (RcWord? a = runq!.Argv!.Words; a is not null; a = a.Next)
        {
            argv.Add(RcText.Kernel(a.Text));
        }

        await UpdenvAsync();
        await DoredirAsync(runq.Redir);
        string name = runq.Argv.Words!.Text;
        for (RcWord? path = Searchpath(name, "path"); path is not null; path = path.Next)
        {
            try
            {
                await process.ExecAsync(RcText.Kernel(Makepath(path.Text, name)), argv);
            }
            catch (SyscallException error)
            {
                errstr = RcText.Rc(error.Message);
            }
        }

        Setstatus(errstr);
        err.Pfln(Srcfile(runq), runq.Line, argv0);
        err.Pstr($": {name}: {Getstatus()}\n");
        await XexitAsync();
    }

    private void Execfunc(RcVar func)
    {
        Popword();
        Startfunc(func, Poplist(), runq!.Local, runq.Redir);
    }

    private async ValueTask ExeccdAsync()
    {
        RcWord a = runq!.Argv!.Words!;
        Setstatus("can't cd");
        switch (RcWord.Count(a))
        {
            case 2:
                await CdAsync(a.Next!.Text);
                break;
            case 1:
                if (Vlook("home").Val is { } home)
                {
                    if (await ChdirAsync(home.Text))
                    {
                        Setstatus(string.Empty);
                    }
                    else
                    {
                        err.Pstr($"Can't cd {home.Text}: {errstr}\n");
                    }
                }
                else
                {
                    err.Pstr("Can't cd -- $home empty\n");
                }

                break;
            default:
                err.Pstr("Usage: cd [directory]\n");
                break;
        }

        Poplist();
    }

    private async ValueTask CdAsync(string name)
    {
        for (RcWord? cdpath = Searchpath(name, "cdpath"); cdpath is not null; cdpath = cdpath.Next)
        {
            string dir = Makepath(cdpath.Text, name);
            if (await ChdirAsync(dir))
            {
                if (cdpath.Text is not ("" or "."))
                {
                    err.Pstr(dir + "\n");
                }

                Setstatus(string.Empty);
                return;
            }
        }

        err.Pstr($"Can't cd {name}: {errstr}\n");
    }

    private async ValueTask ExecexitAsync()
    {
        switch (RcWord.Count(runq!.Argv!.Words))
        {
            case 1:
                break;
            case 2:
                Setstatus(runq.Argv.Words!.Next!.Text);
                break;
            default:
                err.Pstr("Usage: exit [status]\nExiting anyway\n");
                Setstatus(runq.Argv.Words!.Next!.Text);
                break;
        }

        await XexitAsync();
    }

    private ValueTask Execshift()
    {
        int n;
        switch (RcWord.Count(runq!.Argv!.Words))
        {
            case 1:
                n = 1;
                break;
            case 2:
                n = Atoi(runq.Argv.Words!.Next!.Text);
                break;
            default:
                err.Pstr("Usage: shift [n]\n");
                Setstatus("shift usage");
                Poplist();
                return ValueTask.CompletedTask;
        }

        RcVar star = Vlook("*");
        for (; n > 0 && star.Val is not null; --n)
        {
            star.Val = star.Val.Next;
            star.Changed = true;
        }

        Setstatus(string.Empty);
        Poplist();
        return ValueTask.CompletedTask;
    }

    // mapfd: the descriptor that fd is redirected from.
    private int Mapfd(int fd)
    {
        for (RcRedir? rp = runq!.Redir; rp is not null; rp = rp.Next)
        {
            if (rp.To == fd)
            {
                fd = rp.From;
            }
        }

        return fd;
    }

    // execcmds: a thread reading and running commands from input.
    private async ValueTask ExeccmdsAsync(RcIo input, string file, RcVar? local, RcRedir? redir)
    {
        if (Exitnext())
        {
            await TurfstackAsync();
        }

        Start(Rdcmds, 2, local, redir);
        runq!.Lex = Reading(input, new RcLexer(Input(input), file, new RcIoWriter(err)));
    }

    private async ValueTask ExecevalAsync()
    {
        Popword();
        if (runq!.Argv!.Words is null)
        {
            await Xerror1Async("Usage: eval cmd ...");
            return;
        }

        Xqw();
        string cmds = Popword() + "\n";
        Poplist();
        RcIo f = RcIo.OpenStr();
        f.Pfln(Srcfile(runq), runq.Line, argv0);
        f.Pstr(" *eval*");
        await ExeccmdsAsync(RcIo.OpenCore(cmds), f.CloseStr(), runq.Local, runq.Redir);
    }

    // .'s flags, or null after the usage message for one it does not have.
    private async ValueTask<(bool B, bool I, bool Q)?> DotflagsAsync()
    {
        bool bflag = false, iflag = false, qflag = false;
        while (runq!.Argv!.Words is { } w && w.Text.StartsWith('-'))
        {
            if (w.Text.Length > 1 && w.Text[1] == '-')
            {
                Popword();
                break;
            }

            foreach (char c in w.Text[1..])
            {
                switch (c)
                {
                    case 'b':
                        bflag = true;
                        break;
                    case 'i':
                        iflag = true;
                        break;
                    case 'q':
                        qflag = true;
                        break;
                    default:
                        await Xerror1Async("Usage: . [-biq] file [arg ...]");
                        return null;
                }
            }

            Popword();
        }

        return (bflag, iflag, qflag);
    }

    // The file . reads, along $path, with /dev/stdin as standard input when it cannot be opened.
    private async ValueTask<(int? Fd, string? File)> DotopenAsync(string name)
    {
        string? file = null;
        int? opened = null;
        for (RcWord? path = Searchpath(name, "path"); path is not null && opened is null; path = path.Next)
        {
            file = Makepath(path.Text, name);
            opened = await OpenAsync(file, 0);
            if (opened is null && file == "/dev/stdin")
            {
                opened = await Dup1Async(0);
            }
        }

        return (opened, file);
    }

    private async ValueTask ExecdotAsync()
    {
        Popword();
        (bool B, bool I, bool Q)? flags = await DotflagsAsync();
        if (flags is null)
        {
            return;
        }

        (bool bflag, bool iflag, bool qflag) = flags.Value;
        if (runq!.Argv!.Words is null)
        {
            await Xerror1Async("Usage: . [-biq] file [arg ...]");
            return;
        }

        RcWord argv = Poplist()!;
        (int? opened, string? file) = await DotopenAsync(argv.Text);

        if (opened is null)
        {
            if (!qflag)
            {
                await Xerror3Async(". can't open", argv.Text, errstr);
            }

            return;
        }

        await ExeccmdsAsync(RcIo.OpenFd(opened.Value), file!, null, runq.Redir);
        await PushcloseAsync(opened.Value);
        runq.Lex!.Lexer.Quiet = qflag;
        runq.IFlag = iflag;
        if (iflag || (!bflag && flag['b'] is null))
        {
            runq.Lex.Lexer.ReadLines();
        }

        runq.Local = new RcVar("*", runq.Local) { Val = argv.Next, Changed = true };
        argv.Next = null;
        runq.Local = new RcVar("0", runq.Local) { Val = argv, Changed = true };
    }

    private async ValueTask ExecflagAsync()
    {
        RcWord a = runq!.Argv!.Words!;
        switch (RcWord.Count(a))
        {
            case 2:
                Setstatus(flag[a.Next!.Text.Length == 0 ? 0 : a.Next.Text[0] & 0xFF] is not null ? string.Empty : "flag not set");
                Poplist();
                return;
            case 3 when a.Next!.Text.Length == 1 && a.Next.Next!.Text is "+" or "-":
                flag[a.Next.Text[0] & 0xFF] = a.Next.Next.Text == "+" ? FlagSet : null;
                Setstatus(string.Empty);
                Poplist();
                return;
        }

        await Xerror1Async("Usage: flag [letter] [+-]");
    }

    private async ValueTask ExecwhatisAsync()
    {
        RcWord? a = runq!.Argv!.Words!.Next;
        if (a is null)
        {
            await Xerror1Async("Usage: whatis name ...");
            return;
        }

        Setstatus(string.Empty);
        RcIo @out = RcIo.OpenFd(Mapfd(1));
        for (; a is not null; a = a.Next)
        {
            bool found = Vlook(a.Text).Val is { } val;
            if (found)
            {
                @out.Pstr(a.Text + "=");
                PrintValue(@out, Vlook(a.Text).Val!);
            }

            RcVar v = Gvlook(a.Text);
            if (v.Fn is not null)
            {
                @out.Pstr("fn ");
                @out.Pwrd(v.Name);
                @out.Pstr(" " + v.Fn[v.Pc - 1].S + "\n");
            }
            else if (IsBuiltin(a.Text))
            {
                @out.Pstr($"builtin {a.Text}\n");
            }
            else if (await FindExecutableAsync(a.Text) is { } file)
            {
                @out.Pstr(file + "\n");
            }
            else if (!found)
            {
                Setstatus("not found");
            }

            await @out.FlushAsync(process);
        }

        Poplist();
    }

    private async ValueTask<string?> FindExecutableAsync(string name)
    {
        for (RcWord? path = Searchpath(name, "path"); path is not null; path = path.Next)
        {
            string file = Makepath(path.Text, name);
            if (await ExecutableAsync(file))
            {
                return file;
            }
        }

        return null;
    }

    private async ValueTask ExecwaitAsync()
    {
        switch (RcWord.Count(runq!.Argv!.Words))
        {
            case 1:
                await WaitforAsync(-1);
                break;
            case 2:
                await WaitforAsync(Atoi(runq.Argv.Words!.Next!.Text));
                break;
            default:
                await Xerror1Async("Usage: wait [pid]");
                return;
        }

        Poplist();
    }

    // finit, kept as rcmain calls it: the functions of the environment.
    private async ValueTask ExecfinitAsync()
    {
        int line = runq!.Line;
        Poplist();
        await ExeccmdsAsync(RcIo.OpenCore("for(i in '/env/fn#'*){. -bq $i}\n"), Srcfile(runq), runq.Local, runq.Redir);
        runq.Lex!.Lexer.Line = line;
        runq.Lex.Lexer.Quiet = true;
    }

    private async ValueTask ExecrforkAsync()
    {
        RcWord a = runq!.Argv!.Words!;
        RforkFlags? arg = RcWord.Count(a) switch
        {
            1 => RforkFlags.Envg | RforkFlags.Nameg | RforkFlags.Noteg,
            2 => Rforkflags(a.Next!.Text),
            _ => null,
        };
        if (arg is null)
        {
            RforkUsage(a);
            return;
        }

        try
        {
            await process.RforkAsync(arg.Value);
            if ((arg & RforkFlags.Cfdg) != 0)
            {
                Forgetredirs();
            }

            Setstatus(string.Empty);
        }
        catch (SyscallException)
        {
            err.Pstr($"{argv0}: {a.Text} failed\n");
            Setstatus("rfork failed");
        }

        Poplist();
    }

    // With no descriptors left, the redirections waiting for commands do nothing.
    private void Forgetredirs()
    {
        for (RcRedir? rp = runq!.Redir; rp is not null; rp = rp.Next)
        {
            rp.To = Closed;
            rp.Type = 0;
        }
    }

    private void RforkUsage(RcWord a)
    {
        err.Pstr($"Usage: {a.Text} [fnesFNEm]\n");
        Setstatus("rfork usage");
        Poplist();
    }
}
