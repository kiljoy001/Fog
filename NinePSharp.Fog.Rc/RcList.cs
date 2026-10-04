namespace NinePSharp.Fog.Rc;

// exec.h's list: the argument stack, each entry a word list.
internal sealed class RcList(RcWord? words, RcList? next)
{
    public RcWord? Words { get; set; } = words;

    public RcList? Next { get; } = next;
}
