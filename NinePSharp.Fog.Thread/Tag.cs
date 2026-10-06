namespace NinePSharp.Fog.Thread;

/// <summary>
/// channel.c's tag: the slot, shared by a blocked alt's entries, that whoever completes the alt commits to
/// its channel before meeting the alt in a rendezvous on the tag.
/// </summary>
internal sealed class Tag
{
    internal static readonly object ClosedWake = new();

    // channel.c's c = ~0: an interrupted alt nobody committed is marked so nobody tries to meet it.
    internal static readonly Channel Nowhere = new Unreachable();

    internal Channel? Committed { get; set; }

    internal bool Taken => Committed is not null;

    private sealed class Unreachable() : Channel(new RendezvousGroup(), 0);
}
