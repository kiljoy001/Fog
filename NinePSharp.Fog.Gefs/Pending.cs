namespace NinePSharp.Fog.Gefs;

// A commit between its passes: the blocks it retired and the arena headers it packed.
internal sealed record Pending(IReadOnlyList<long> Retired, IReadOnlyList<Bptr> Pointers);
