namespace NinePSharp.Fog.Thread;

/// <summary>
/// The rendezvous group rfork's RFREND gives a process: the tags its threads meet on, and the chanlock
/// its channels share. A libthread program's channels all belong to its process, so here an alt's
/// channels must all belong to one group.
/// </summary>
public sealed class RendezvousGroup
{
    internal Rendezvous Rendezvous { get; } = new();

    // channel.c's chanlock: one lock for the group's channels, so an alt can examine all of them at once.
    internal object ChannelLock { get; } = new();
}
