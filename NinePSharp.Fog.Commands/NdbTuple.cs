namespace NinePSharp.Fog.Commands;

// A tuple of a parsed entry: entry joins every tuple of the entry, and line those of one line in
// a ring.
public sealed class NdbTuple(string attr, string val)
{
    public string Attr { get; set; } = attr;

    public string Val { get; set; } = val;

    public NdbTuple? Entry { get; set; }

    public NdbTuple? Line { get; set; }

    public ulong Ptr { get; set; }

    public IEnumerable<NdbTuple> Entries()
    {
        for (NdbTuple? t = this; t is not null; t = t.Entry)
        {
            yield return t;
        }
    }
}
