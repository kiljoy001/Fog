namespace NinePSharp.Fog.Rc;

// exec.h's word: a list of strings in order, first to last. Strings hold bytes, one per char.
internal sealed class RcWord(string text, RcWord? next)
{
    public string Text { get; set; } = text;

    public RcWord? Next { get; set; } = next;

    public static int Count(RcWord? w)
    {
        int n = 0;
        for (; w is not null; w = w.Next)
        {
            n++;
        }

        return n;
    }

    // copywords: a copy of a in front of tail.
    public static RcWord? Copy(RcWord? a, RcWord? tail)
    {
        if (a is null)
        {
            return tail;
        }

        var head = new RcWord(a.Text, null);
        RcWord end = head;
        for (a = a.Next; a is not null; a = a.Next)
        {
            end.Next = new RcWord(a.Text, null);
            end = end.Next;
        }

        end.Next = tail;
        return head;
    }
}
