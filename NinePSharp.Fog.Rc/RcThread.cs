namespace NinePSharp.Fog.Rc;

// exec.h's thread: a code vector being run, with its stacks, and who continues when it returns.
internal sealed class RcThread(RcCode code, int pc, RcVar? local, RcRedir? redir, RcThread? ret)
{
    public RcCode Code { get; } = code;

    public int Pc { get; set; } = pc;

    public int Line { get; set; }

    public RcList? Argv { get; set; }

    public RcRedir? Redir { get; set; } = redir;

    public RcRedir? StartRedir { get; set; } = redir;

    public RcVar? Local { get; set; } = local;

    public RcReading? Lex { get; set; }

    public bool IFlag { get; set; }

    public long Pid { get; set; }

    public string? Status { get; set; }

    public RcThread? Ret { get; set; } = ret;
}
