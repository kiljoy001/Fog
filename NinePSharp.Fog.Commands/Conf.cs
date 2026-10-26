namespace NinePSharp.Fog.Commands;

// A key's confirmation: the key, whether it may be used, 1, may not, 0, or is still asked, -1, and
// the tag the question was asked by.
public sealed class Conf(Key key, ulong tag)
{
    public Key Key { get; } = key;

    public int Canuse { get; set; } = -1;

    public ulong Tag { get; } = tag;
}
