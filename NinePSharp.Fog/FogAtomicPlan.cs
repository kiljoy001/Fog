namespace NinePSharp.Fog;

/// <summary>A bounded in-memory effect and its precomputed reply, committed under the store's atomic boundary.</summary>
/// <remarks>No blocking IO, asynchronous work, or reentrant store calls are permitted in preparation or Apply.</remarks>
public sealed record FogAtomicPlan(IReadOnlyDictionary<string, byte[]> Outputs, Action Apply, string? Error = null);
