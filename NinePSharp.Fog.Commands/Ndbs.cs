namespace NinePSharp.Fog.Commands;

// The finger pointing at the current point of a search.
public sealed class Ndbs
{
    public Ndb? Db { get; set; }

    public long Ptr { get; set; }

    public NdbTuple? T { get; set; }
}
