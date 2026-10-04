namespace NinePSharp.Fog.Rc;

// exec.h's redir: what to do to a descriptor before running a command, most recent first.
internal sealed class RcRedir(int type, int from, int to, RcRedir? next)
{
    // dup2(from, to); close(from)
    public const int Open = 1;

    // dup2(from, to)
    public const int Dup = 2;

    // close(to)
    public const int Close = 3;

    public int Type { get; set; } = type;

    public int From { get; set; } = from;

    public int To { get; set; } = to;

    public RcRedir? Next { get; set; } = next;
}
