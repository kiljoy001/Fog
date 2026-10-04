namespace NinePSharp.Fog.Rc;

// A code vector as rc lays it out: word 1 names the source file and code starts at pc 2. rc's
// zero word at the end, for codefree, is not needed.
internal sealed class RcCode(RcInstruction[] words)
{
    public string Source => words[1].S!;

    public RcInstruction this[int pc] => words[pc];
}
