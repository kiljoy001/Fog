namespace NinePSharp.Fog.Rc;

// rc.h's var: a variable's value, and a function's code and the pc it starts at.
internal sealed class RcVar(string name, RcVar? next)
{
    public string Name { get; } = name;

    public RcVar? Next { get; set; } = next;

    public RcWord? Val { get; set; }

    public RcCode? Fn { get; set; }

    public int Pc { get; set; }

    public bool Changed { get; set; }

    public bool FnChanged { get; set; }
}
