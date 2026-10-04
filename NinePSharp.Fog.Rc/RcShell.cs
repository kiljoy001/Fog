using System.Globalization;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Rc;

// rc's interpreter, exec.c: a run queue of threads, each running a code vector with an argument
// stack, redirections and local variables. A shell runs as one kernel process; fork copies it, as
// fork copies rc's memory, and the copy runs on in the child.
internal sealed partial class RcShell
{
    private const string Rcmain = "/rc/lib/rcmain";
    private const string Fdprefix = "/fd/";

    // A closed descriptor, as a redirection's or failed call's.
    private const int Closed = -1;
    private const string Plainflags = "srdiIlxebpvV";
    private const int Flags = 256;
    private static readonly string[] FlagSet = [];
    private readonly string argv0;
    private Dictionary<string, RcVar> gvar = new(StringComparer.Ordinal);
    private string[]?[] flag = new string[Flags][];
    private List<long> waitpids = new();
    private Process process;
    private RcIo err = RcIo.OpenFd(2);
    private RcThread? runq;
    private long mypid;
    private bool ifnot;
    private bool trapped;
    private string? promptstr;
    private string errstr = string.Empty;
    private int heres;

    private RcShell(Process process, string argv0)
    {
        this.process = process;
        this.argv0 = argv0;
    }

    public static Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        List<string> args = argv.Select(RcText.Rc).ToList();
        return new RcShell(process, args[0]).MainAsync(args);
    }

    private static bool Clobbers(RcRedir? rp, int fd)
    {
        for (; rp is not null; rp = rp.Next)
        {
            if (rp.To == fd)
            {
                return true;
            }
        }

        return false;
    }

    // Get the flags, read the environment, set $pid, $cflag and $rcname, and run
    // *=(argv); . -bq /rc/lib/rcmain $*
    private async Task MainAsync(List<string> argv)
    {
        int argc = Getflags(argv);
        if (argc == -1)
        {
            await UsageAsync("[file [arg ...]]");
        }

        if (argv[0].StartsWith('-'))
        {
            flag['l'] = FlagSet;
        }

        // rc -i is also implied when there are no arguments and fd2path names input a /dev/cons; the
        // kernel has no fd2path yet, so only -i makes rc interactive.
        if (flag['I'] is not null)
        {
            flag['i'] = null;
        }

        string rcmain = flag['m']?[0] ?? Rcmain;
        await VinitAsync();
        mypid = process.Pid;
        Setvar("pid", new RcWord(mypid.ToString(CultureInfo.InvariantCulture), null));
        Setvar("cflag", flag['c'] is { } command ? new RcWord(command[0], null) : null);
        Setvar("rcname", new RcWord(argv[0], null));
        RcCode bootstrap = new([
            default, new(RcOp.None, S: "*bootstrap*"),
            new(RcOp.Mark), new(RcOp.Word), new(RcOp.None, S: "*"), new(RcOp.Assign),
            new(RcOp.Mark), new(RcOp.Mark), new(RcOp.Word), new(RcOp.None, S: "*"), new(RcOp.Dol),
            new(RcOp.Word), new(RcOp.None, S: rcmain), new(RcOp.Word), new(RcOp.None, S: "-bq"),
            new(RcOp.Word), new(RcOp.None, S: "."), new(RcOp.Simple), new(RcOp.Exit),
        ]);
        Start(bootstrap, 2, null, null);
        Pushlist();
        for (int i = argc - 1; i != 0; --i)
        {
            Pushword(argv[i]);
        }

        await RunAsync();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            RcThread t = runq!;
            if (flag['r'] is not null)
            {
                await PfncAsync(err, t);
            }

            await ExecuteAsync(t.Code[t.Pc++]);
        }
    }

    // A copy of the shell for a forked child, made before the parent goes on, keeping which threads
    // share which locals and redirections, and with none of the parent's children to wait for. Each
    // thread reading commands gets a copy of its input and lexer, as forking copies rc's buffers.
    // Code vectors and values are shared, as the child never changes them.
    private RcShell Clone()
    {
        var vars = new Dictionary<RcVar, RcVar>(ReferenceEqualityComparer.Instance);
        var redirs = new Dictionary<RcRedir, RcRedir>(ReferenceEqualityComparer.Instance);

        RcVar? Var(RcVar? v)
        {
            if (v is null)
            {
                return null;
            }

            if (vars.TryGetValue(v, out RcVar? copy))
            {
                return copy;
            }

            copy = new RcVar(v.Name, null) { Val = v.Val, Fn = v.Fn, Pc = v.Pc, Changed = v.Changed, FnChanged = v.FnChanged };
            vars[v] = copy;
            copy.Next = Var(v.Next);
            return copy;
        }

        RcRedir? Redir(RcRedir? r)
        {
            if (r is null)
            {
                return null;
            }

            if (redirs.TryGetValue(r, out RcRedir? copy))
            {
                return copy;
            }

            copy = new RcRedir(r.Type, r.From, r.To, null);
            redirs[r] = copy;
            copy.Next = Redir(r.Next);
            return copy;
        }

        RcList? List(RcList? a) => a is null ? null : new RcList(RcWord.Copy(a.Words, null), List(a.Next));

        RcThread? Thread(RcThread? t)
        {
            if (t is null)
            {
                return null;
            }

            var copy = new RcThread(t.Code, t.Pc, Var(t.Local), Redir(t.Redir), null)
            {
                Line = t.Line,
                Argv = List(t.Argv),
                StartRedir = Redir(t.StartRedir),
                Lex = t.Lex,
                IFlag = t.IFlag,
                Pid = t.Pid,
                Status = t.Status,
            };
            copy.Ret = Thread(t.Ret);
            return copy;
        }

        var shell = new RcShell(process, argv0)
        {
            gvar = gvar.ToDictionary(pair => pair.Key, pair => Var(pair.Value)!, StringComparer.Ordinal),
            flag = (string[]?[])flag.Clone(),
            runq = Thread(runq),
            mypid = mypid,
            ifnot = ifnot,
            trapped = trapped,
            promptstr = promptstr,
            errstr = errstr,
            heres = heres,
        };
        for (RcThread? t = shell.runq; t is not null; t = t.Ret)
        {
            t.Lex = t.Lex is null ? null : shell.Reading(t.Lex);
        }

        return shell;
    }

    // Bytes from an io for a lexer, read by this shell's process.
    private RcInput Input(RcIo io) => new(() => io.RchrAsync(process));

    private RcReading Reading(RcIo input, RcLexer lexer)
    {
        lexer.Prompt = PpromptAsync;
        lexer.Failed = Setstatus;
        return new RcReading(input, lexer);
    }

    // A copy of another shell's reading for this one.
    private RcReading Reading(RcReading reading)
    {
        RcIo input = reading.Input.Copy();
        return Reading(input, reading.Lexer.Copy(Input(input), new RcIoWriter(err)));
    }

    private void Start(RcCode c, int pc, RcVar? local, RcRedir? redir)
        => runq = new RcThread(c, pc, local, redir, runq);

    private void Startfunc(RcVar func, RcWord? starval, RcVar? local, RcRedir? redir)
    {
        Start(func.Fn!, func.Pc, local, redir);
        runq!.Local = new RcVar("*", runq.Local) { Val = starval, Changed = true };
    }

    private async ValueTask PopthreadAsync()
    {
        RcThread p = runq!;
        while (p.Argv is not null)
        {
            Poplist();
        }

        while (p.Local is not null && (p.Ret is null || p.Local != p.Ret.Local))
        {
            Xunlocal();
        }

        runq = p.Ret;
        if (p.Lex is not null)
        {
            await p.Lex.Input.CloseAsync(process);
        }
    }

    private RcWord Pushword(string s)
    {
        RcList a = runq!.Argv!;
        a.Words = new RcWord(s, a.Words);
        return a.Words;
    }

    private string Popword()
    {
        RcList a = runq!.Argv!;
        RcWord p = a.Words!;
        a.Words = p.Next;
        return p.Text;
    }

    private void Pushlist() => runq!.Argv = new RcList(null, runq.Argv);

    private RcWord? Poplist()
    {
        RcList p = runq!.Argv!;
        runq.Argv = p.Next;
        return p.Words;
    }

    // pushredir; a file redirection does not let a dup clobber the descriptor it is opened on.
    private async ValueTask PushredirAsync(int type, int from, int to)
    {
        if (type == RcRedir.Open && Clobbers(runq!.Redir, from))
        {
            await PushredirAsync(type, await Dup1Async(from) ?? Closed, to);
            await CloseAsync(from);
            return;
        }

        runq!.Redir = new RcRedir(type, from, to, runq.Redir);
    }

    private ValueTask PushcloseAsync(int fd) => PushredirAsync(RcRedir.Close, Closed, fd);

    // turfstack: a thread that exits on return is about to start, so the stack goes, keeping the
    // redirections. rc also keeps the locals the new thread refers to from being freed, which
    // memory that is collected does not need, and has dontclose keep the input of a thread still
    // reading commands, which cannot be on the stack when only exits follow.
    private async ValueTask TurfstackAsync()
    {
        while (runq is not null)
        {
            await PopthreadAsync();
        }
    }

    // shuffleredir: the newest redirection goes to the bottom of the thread's own stack.
    private void Shuffleredir()
    {
        RcRedir rp = runq!.Redir!;
        runq.Redir = rp.Next;
        rp.Next = runq.StartRedir;
        if (runq.Redir == rp.Next)
        {
            runq.Redir = rp;
            return;
        }

        RcRedir r = runq.Redir!;
        while (r.Next != rp.Next)
        {
            r = r.Next!;
        }

        r.Next = rp;
    }

    private RcVar Gvlook(string name)
    {
        if (!gvar.TryGetValue(name, out RcVar? v))
        {
            v = new RcVar(name, null);
            gvar.Add(name, v);
        }

        return v;
    }

    private RcVar Vlook(string name)
    {
        for (RcVar? v = runq?.Local; v is not null; v = v.Next)
        {
            if (v.Name == name)
            {
                return v;
            }
        }

        return Gvlook(name);
    }

    private void Setvar(string name, RcWord? val)
    {
        RcVar v = Vlook(name);
        v.Val = val;
        v.Changed = true;
    }

    private void Setstatus(string s) => Setvar("status", new RcWord(s, null));

    // Getstatus takes the status away.
    private string? Takestatus()
    {
        RcVar status = Vlook("status");
        RcWord? val = status.Val;
        status.Val = null;
        return val?.Text;
    }

    private string Getstatus() => Vlook("status").Val?.Text ?? string.Empty;

    private bool Truestatus() => Getstatus().All(c => c is '|' or '0');

    private string Srcfile(RcThread p) => p.Code.Source;

    private async ValueTask ErrorAsync(string status, string message)
    {
        Setstatus(status);
        err.Pfln(Srcfile(runq!), runq!.Line, argv0);
        err.Pstr(": " + message + "\n");
        while (!runq!.IFlag)
        {
            await XreturnAsync();
        }
    }

    // Xerror1, Xerror2 and Xerror3
    private ValueTask Xerror1Async(string s) => ErrorAsync("error", s);

    private ValueTask Xerror2Async(string s, string e) => ErrorAsync(e, $"{s}: {e}");

    private ValueTask Xerror3Async(string s, string m, string e) => ErrorAsync(e, $"{s}: {m}: {e}");
}
