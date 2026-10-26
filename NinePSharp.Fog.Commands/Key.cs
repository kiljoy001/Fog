namespace NinePSharp.Fog.Commands;

// A key on factotum's ring: its public and private attributes, its protocol, what that protocol
// made of it, and how often it has worked.
public sealed class Key
{
    public Attr? Attr { get; set; }

    public Attr? Privattr { get; set; }

    public Proto Proto { get; set; } = null!;

    public object? Priv { get; set; }

    public ulong Successes { get; set; }
}
