namespace NinePSharp.Fog;

/// <summary>
/// A side-effect-free preparation produces all bounded output before ApplyAsync runs once.
/// The trusted service must provide its own CAS/atomicity boundary and bounded execution.
/// This in-memory primitive is not a durable transaction manager.
/// </summary>
public sealed record FogCommitPlan(IReadOnlyDictionary<string, byte[]> Outputs, Func<Task> ApplyAsync, string? Error = null);
