namespace NinePSharp.Fog.Rc;

// A node of rc's parse tree, as struct tree in rc.h. Strings hold bytes, one per char.
internal sealed class RcTree(int type, int line)
{
    internal int Type { get; set; } = type;

    internal int RType { get; set; }

    internal int Fd0 { get; set; }

    internal int Fd1 { get; set; }

    internal int Line { get; set; } = line;

    // 0 a string, 1 a glob, 2 a pattern.
    internal int Glob { get; set; }

    internal bool Quoted { get; set; }

    internal string? Str { get; set; }

    internal RcTree?[] Child { get; } = new RcTree?[3];
}
