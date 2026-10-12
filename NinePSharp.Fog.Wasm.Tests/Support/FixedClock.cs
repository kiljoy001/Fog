namespace NinePSharp.Fog.Wasm.Tests.Support;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
