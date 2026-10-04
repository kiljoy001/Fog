using System.Globalization;

namespace NinePSharp.Fog.Rc;

// exec.c's operations. Arguments are on the stack (...), in line [...], or code in line {...}
// with a jump around it.
internal sealed partial class RcShell
{
    // The X functions, as rc's code vectors hold pointers to them.
    private static readonly Dictionary<RcOp, Func<RcShell, ValueTask>> Operations = new()
    {
        [RcOp.Append] = s => s.XredirAsync(">>", 1),
        [RcOp.Assign] = s => s.XassignAsync(),
        [RcOp.Async] = s => s.XasyncAsync(),
        [RcOp.Backq] = s => s.XbackqAsync(),
        [RcOp.Bang] = s => s.Done(() => s.Setstatus(s.Truestatus() ? "false" : string.Empty)),
        [RcOp.Case] = s => s.Done(s.Xcase),
        [RcOp.Close] = s => s.PushcloseAsync(s.Inline()),
        [RcOp.Conc] = s => s.XconcAsync(),
        [RcOp.Count] = s => s.XcountAsync(),
        [RcOp.Delfn] = s => s.Done(s.Xdelfn),
        [RcOp.Dol] = s => s.XdolAsync(),
        [RcOp.Dup] = s => s.PushredirAsync(RcRedir.Dup, s.Inline(), s.Inline()),
        [RcOp.Eflag] = s => s.Truestatus() ? ValueTask.CompletedTask : s.XexitAsync(),
        [RcOp.Exit] = s => s.XexitAsync(),
        [RcOp.False] = s => s.Done(() => s.Branch(!s.Truestatus())),
        [RcOp.Fn] = s => s.Done(s.Xfn),
        [RcOp.For] = s => s.Done(s.Xfor),
        [RcOp.Glob] = s => s.XglobAsync(),
        [RcOp.Here] = s => s.XhereAsync(quoted: false),
        [RcOp.Hereq] = s => s.XhereAsync(quoted: true),
        [RcOp.If] = s => s.Done(() =>
        {
            s.ifnot = true;
            s.Branch(s.Truestatus());
        }),
        [RcOp.Ifnot] = s => s.Done(() => s.Branch(s.ifnot)),
        [RcOp.Jump] = s => s.Done(() => s.runq!.Pc = s.runq.Code[s.runq.Pc].I),
        [RcOp.Local] = s => s.XlocalAsync(),
        [RcOp.Mark] = s => s.Done(s.Pushlist),
        [RcOp.Match] = s => s.Done(s.Xmatch),
        [RcOp.Pipe] = s => s.XpipeAsync(),
        [RcOp.Pipefd] = s => s.XpipefdAsync(),
        [RcOp.Pipewait] = s => s.XpipewaitAsync(),
        [RcOp.Popm] = s => s.Done(() => s.Poplist()),
        [RcOp.Popredir] = s => s.XpopredirAsync(),
        [RcOp.Push] = s => s.Done(s.Xpush),
        [RcOp.Qw] = s => s.Done(s.Xqw),
        [RcOp.Rdcmds] = s => s.XrdcmdsAsync(),
        [RcOp.Rdwr] = s => s.XredirAsync("<>", 2),
        [RcOp.Read] = s => s.XredirAsync("<", 0),
        [RcOp.Return] = s => s.XreturnAsync(),
        [RcOp.Settrue] = s => s.Done(() => s.Setstatus(string.Empty)),
        [RcOp.Simple] = s => s.XsimpleAsync(),
        [RcOp.Srcline] = s => s.Done(() => s.runq!.Line = s.Inline()),
        [RcOp.Sub] = s => s.Done(s.Xsub),
        [RcOp.Subshell] = s => s.XsubshellAsync(),
        [RcOp.True] = s => s.Done(() => s.Branch(s.Truestatus())),
        [RcOp.Unlocal] = s => s.Done(s.Xunlocal),
        [RcOp.Wastrue] = s => s.Done(() => s.ifnot = false),
        [RcOp.Word] = s => s.Done(() => s.Pushword(s.runq!.Code[s.runq.Pc++].S!)),
        [RcOp.Write] = s => s.XredirAsync(">", -1),
    };

    // conclist: each word of the left list joined to each of the right, a singleton to every one.
    private static RcWord Conclist(RcWord lp, RcWord rp, RcWord? tail)
    {
        var head = new RcWord(lp.Text + rp.Text, null);
        RcWord end = head;
        while (lp.Next is not null || rp.Next is not null)
        {
            lp = lp.Next ?? lp;
            rp = rp.Next ?? rp;
            end.Next = new RcWord(lp.Text + rp.Text, null);
            end = end.Next;
        }

        end.Next = tail;
        return head;
    }

    // The value of the leading digits, and whether the whole string was digits.
    private static (int Value, bool Digits) Number(string s)
    {
        int n = 0, t = 0;
        for (; t < s.Length && char.IsAsciiDigit(s[t]); t++)
        {
            n = (n * 10) + s[t] - '0';
        }

        return (n, t == s.Length);
    }

    // subwords: $a(n), $a(n-m) and $a(n-) for each subscript, in order.
    private static RcWord? Subwords(RcWord? val, int len, RcWord? sub, RcWord? a)
    {
        if (sub is null)
        {
            return a;
        }

        a = Subwords(val, len, sub.Next, a);
        string s = sub.Text;
        int i = 0, n = 0, m = 0;
        for (; i < s.Length && char.IsAsciiDigit(s[i]); i++)
        {
            n = (n * 10) + s[i] - '0';
        }

        if (i < s.Length && s[i] == '-')
        {
            if (++i == s.Length)
            {
                m = len;
            }
            else
            {
                for (; i < s.Length && char.IsAsciiDigit(s[i]); i++)
                {
                    m = (m * 10) + s[i] - '0';
                }

                m -= n;
            }
        }

        if (n < 1 || n > len || m < 0)
        {
            return a;
        }

        m = Math.Min(m, len - n);
        while (--n > 0)
        {
            val = val!.Next;
        }

        // copynwords
        var copy = new RcWord(val!.Text, null);
        RcWord end = copy;
        for (int k = 0; k < m; k++)
        {
            val = val!.Next;
            end.Next = new RcWord(val!.Text, null);
            end = end.Next;
        }

        end.Next = a;
        return copy;
    }

    // concstatus: the statuses of a pipeline's commands, joined by |.
    private static string Concstatus(string s, string? t)
        => t is null ? s : s.Length > 0 ? s + "|" + t : t;

    // A byte, with the one or two after it that rc copies as it is.
    private static int Pbytes(RcIo f, string s, int i)
    {
        int skip = s[i] is >= (char)0xA0 and <= (char)0xF5 ? 1 : s[i] is (char)0xF6 or (char)0xF7 ? 2 : 0;
        for (int k = 0; k <= skip && i < s.Length; k++)
        {
            f.Pchr(s[i++]);
        }

        return i;
    }

    private ValueTask ExecuteAsync(RcInstruction op) => Operations[op.Op](this);

    private ValueTask Done(Action action)
    {
        action();
        return ValueTask.CompletedTask;
    }

    // An in-line number.
    private int Inline() => runq!.Code[runq.Pc++].I;

    // Run the code that follows when the test holds, or jump around it.
    private void Branch(bool run)
    {
        if (run)
        {
            runq!.Pc++;
        }
        else
        {
            runq!.Pc = runq.Code[runq.Pc].I;
        }
    }

    // Xappend, Xread, Xrdwr and Xwrite: open the file on the stack for the descriptor in line;
    // mode -1 creates it, and mode 1, writing, appends to it.
    private async ValueTask XredirAsync(string name, int mode)
    {
        bool append = mode == 1;
        switch (RcWord.Count(runq!.Argv!.Words))
        {
            case 0:
                await Xerror1Async(name + " requires file");
                return;
            case 1:
                break;
            default:
                await Xerror1Async(name + " requires singleton");
                return;
        }

        string file = runq.Argv.Words!.Text;
        int? fd = mode < 0 ? await CreatAsync(file) : await OpenAsync(file, mode);
        if (append && fd is null)
        {
            fd = await CreatAsync(file);
        }

        if (fd is null)
        {
            await Xerror3Async(name + (mode < 0 ? " can't create" : " can't open"), file, errstr);
            return;
        }

        if (append)
        {
            await SeekAsync(fd.Value, 0, 2);
        }

        await PushredirAsync(RcRedir.Open, fd.Value, Inline());
        Poplist();
    }

    private async ValueTask XexitAsync()
    {
        RcVar? trap = Trapexit();
        if (trap is null)
        {
            await ExitAsync();
        }

        if (runq is not null)
        {
            --runq.Pc;
        }

        Startfunc(trap!, RcWord.Copy(Vlook("*").Val, null), null, null);
        trapped = true;
    }

    // trapexit: the sigexit function runs when the shell itself exits, once.
    private RcVar? Trapexit()
    {
        if (process.Pid == mypid && !trapped && Vlook("sigexit") is { Fn: not null } trap)
        {
            return trap;
        }

        return null;
    }

    // Xpush: the single word Xqw made goes on the list below.
    private void Xpush()
    {
        RcWord h = Poplist()!;
        h.Next = runq!.Argv!.Words;
        runq.Argv.Words = h;
    }

    // herefile: a new file /tmp/hereXXXXXXXXXXYY, its X's the pid and its Y's a serial number.
    private async ValueTask<(int? Fd, string File)> HerefileAsync()
    {
        char[] tmp = "/tmp/hereXXXXXXXXXXYY".ToCharArray();
        int s = tmp.Length - 1;
        int i = heres++;
        for (; tmp[s] == 'Y'; s--, i /= 26)
        {
            tmp[s] = (char)((i % 26) + 'A');
        }

        long pid = process.Pid;
        for (; tmp[s] == 'X'; s--, pid /= 10)
        {
            tmp[s] = (char)((pid % 10) + '0');
        }

        s++;
        for (char c = 'a'; c < 'z'; c++)
        {
            string file = new(tmp);
            if (!await AccessAsync(file))
            {
                if (await CreatAsync(file) is { } fd)
                {
                    return (fd, file);
                }
            }

            tmp[s] = c;
        }

        return (null, new string(tmp));
    }

    // Xhere and Xhereq: the here document, with $ substitution unless its tag was quoted, in a
    // temporary file that is removed when it is closed.
    private async ValueTask XhereAsync(bool quoted)
    {
        (int? fd, string file) = await HerefileAsync();
        if (fd is null)
        {
            await Xerror3Async("<< can't get temp file", file, errstr);
            return;
        }

        string body = runq!.Code[runq.Pc++].S!;
        RcIo io = RcIo.OpenFd(fd.Value);
        if (quoted)
        {
            io.Pstr(body);
        }
        else
        {
            Psubst(io, body);
        }

        await io.FlushAsync(process);
        await io.CloseAsync(process);

        // The file was just made, so opening it again does not fail.
        await PushredirAsync(RcRedir.Open, (await OpenAsync(file, 3))!.Value, Inline());
    }

    // psubst: $name, $n and $$ in a here document. After a byte from 0xA0 to 0xF5 the next byte is
    // copied as it is, and after 0xF6 or 0xF7 the next two, as rc steps over its UTF-8.
    private void Psubst(RcIo f, string s)
    {
        for (int i = 0; i < s.Length;)
        {
            i = s[i] == '$' ? Pdollar(f, s, i + 1) : Pbytes(f, s, i);
        }
    }

    // $$, or $name or $n and a ^ after it, from just past the $.
    private int Pdollar(RcIo f, string s, int i)
    {
        int t = i;
        if (t < s.Length && s[t] == '$')
        {
            f.Pchr('$');
            return t + 1;
        }

        while (t < s.Length && RcLexer.IdChar(s[t]))
        {
            t++;
        }

        Pvar(f, s[i..t]);
        return t < s.Length && s[t] == '^' ? t + 1 : t;
    }

    // An argument of the shell's, or a variable's words with spaces between, as pstrs prints them.
    private void Pvar(RcIo f, string name)
    {
        (int n, bool digits) = Number(name);
        if (n != 0 && digits)
        {
            RcWord? star = Vlook("*").Val;
            if (n <= RcWord.Count(star))
            {
                while (--n != 0)
                {
                    star = star!.Next;
                }

                f.Pstr(star!.Text);
            }
        }
        else
        {
            for (RcWord? w = Vlook(name).Val; w is not null; w = w.Next)
            {
                f.Pstr(w.Text);
                if (w.Next is not null)
                {
                    f.Pchr(' ');
                }
            }
        }
    }

    private async ValueTask XpopredirAsync()
    {
        RcRedir rp = runq!.Redir!;
        runq.Redir = rp.Next;
        if (rp.Type == RcRedir.Open)
        {
            await CloseAsync(rp.From);
        }
    }

    private async ValueTask XreturnAsync()
    {
        while (runq!.Redir != runq.StartRedir)
        {
            await XpopredirAsync();
        }

        await PopthreadAsync();
        if (runq is null)
        {
            await XexitAsync();
        }
    }

    private void Xmatch()
    {
        string s = runq!.Argv!.Words!.Text;
        bool matched = false;
        for (RcWord? p = runq.Argv.Next!.Words; p is not null && !matched; p = p.Next)
        {
            matched = RcGlob.Match(s, p.Text, '\0');
        }

        Setstatus(matched ? string.Empty : "no match");

        Poplist();
        Poplist();
    }

    private void Xcase()
    {
        string s = runq!.Argv!.Next!.Words!.Text;
        bool ok = false;
        for (RcWord? p = runq.Argv.Words; p is not null && !ok; p = p.Next)
        {
            ok = RcGlob.Match(s, p.Text, '\0');
        }

        Branch(ok);
        Poplist();
    }

    private async ValueTask XconcAsync()
    {
        RcWord? lp = runq!.Argv!.Words;
        RcWord? rp = runq.Argv.Next!.Words;
        RcWord? vp = runq.Argv.Next.Next!.Words;
        int lc = RcWord.Count(lp), rc = RcWord.Count(rp);
        if (lc != 0 || rc != 0)
        {
            if (lc == 0 || rc == 0)
            {
                await Xerror1Async("null list in concatenation");
                return;
            }

            if (lc != 1 && rc != 1 && lc != rc)
            {
                await Xerror1Async("mismatched list lengths in concatenation");
                return;
            }

            vp = Conclist(lp!, rp!, vp);
        }

        Poplist();
        Poplist();
        runq.Argv!.Words = vp;
    }

    private async ValueTask XassignAsync()
    {
        if (RcWord.Count(runq!.Argv!.Words) != 1)
        {
            await Xerror1Async("= variable name not singleton!");
            return;
        }

        RcVar v = Vlook(runq.Argv.Words!.Text);
        Poplist();
        v.Val = Poplist();
        v.Changed = true;
    }

    private async ValueTask XdolAsync()
    {
        if (RcWord.Count(runq!.Argv!.Words) != 1)
        {
            await Xerror1Async("$ variable name not singleton!");
            return;
        }

        string s = runq.Argv.Words!.Text;
        (int n, bool digits) = Number(s);
        RcWord? a = runq.Argv.Next!.Words;
        if (n == 0 || !digits)
        {
            a = RcWord.Copy(Vlook(s).Val, a);
        }
        else
        {
            RcWord? star = Vlook("*").Val;
            if (n <= RcWord.Count(star))
            {
                while (--n != 0)
                {
                    star = star!.Next;
                }

                a = new RcWord(star!.Text, a);
            }
        }

        Poplist();
        runq.Argv!.Words = a;
    }

    private void Xqw()
    {
        RcWord? a = runq!.Argv!.Words;
        if (a is null)
        {
            Pushword(string.Empty);
            return;
        }

        var text = new System.Text.StringBuilder(a.Text);
        for (RcWord? p = a.Next; p is not null; p = p.Next)
        {
            text.Append(' ').Append(p.Text);
        }

        a.Text = text.ToString();
        a.Next = null;
    }

    // Xsub; yacc shifts the subscript onto an inner $, so the grammar gives Xsub a single name and
    // its check for one never fails.
    private void Xsub()
    {
        RcWord? v = Vlook(runq!.Argv!.Next!.Words!.Text).Val;
        RcWord? a = Subwords(v, RcWord.Count(v), runq.Argv.Words, runq.Argv.Next.Next!.Words);
        Poplist();
        Poplist();
        runq.Argv!.Words = a;
    }

    private async ValueTask XcountAsync()
    {
        if (RcWord.Count(runq!.Argv!.Words) != 1)
        {
            await Xerror1Async("$# variable name not singleton!");
            return;
        }

        string s = runq.Argv.Words!.Text;
        (int n, bool digits) = Number(s);
        int count;
        if (n == 0 || !digits)
        {
            count = RcWord.Count(Vlook(s).Val);
        }
        else
        {
            RcWord? star = Vlook("*").Val;
            count = n <= RcWord.Count(star) ? 1 : 0;
        }

        Poplist();
        Pushword(count.ToString(CultureInfo.InvariantCulture));
    }

    private async ValueTask XlocalAsync()
    {
        if (RcWord.Count(runq!.Argv!.Words) != 1)
        {
            await Xerror1Async("local variable name must be singleton");
            return;
        }

        runq.Local = new RcVar(runq.Argv.Words!.Text, runq.Local);
        Poplist();
        runq.Local.Val = Poplist();
        runq.Local.Changed = true;
    }

    private void Xunlocal()
    {
        RcVar v = runq!.Local!;
        runq.Local = v.Next;
        Vlook(v.Name).Changed = true;
    }

    private void Xfn()
    {
        int pc = runq!.Pc;
        runq.Pc = runq.Code[pc].I;
        for (RcWord? a = runq.Argv!.Words; a is not null; a = a.Next)
        {
            RcVar v = Gvlook(a.Text);
            v.Fn = runq.Code;
            v.Pc = pc + 2;
            v.FnChanged = true;
        }

        Poplist();
    }

    private void Xdelfn()
    {
        for (RcWord? a = runq!.Argv!.Words; a is not null; a = a.Next)
        {
            RcVar v = Gvlook(a.Text);
            v.Fn = null;
            v.FnChanged = true;
        }

        Poplist();
    }

    private async ValueTask XpipewaitAsync()
    {
        string? old = Takestatus();
        if (runq!.Pid == -1)
        {
            Setstatus(Concstatus(runq.Status!, old));
            runq.Status = null;
        }
        else
        {
            await WaitforAsync(runq.Pid);
            Setstatus(Concstatus(Takestatus()!, old));
        }
    }

    private void Xfor()
    {
        RcWord? a = runq!.Argv!.Words;
        if (a is null)
        {
            Poplist();
            runq.Pc = runq.Code[runq.Pc].I;
            return;
        }

        runq.Argv.Words = a.Next;
        a.Next = null;
        runq.Local!.Val = a;
        runq.Local.Changed = true;
        runq.Pc++;
    }

    private async ValueTask XglobAsync()
    {
        for (RcWord? a = runq!.Argv!.Words; a is not null;)
        {
            RcWord? x = a.Next;
            await GlobwordAsync(a);
            a = x;
        }
    }
}
