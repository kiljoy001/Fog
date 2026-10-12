namespace NinePSharp.Fog.Wasm.Tests.Support;

// A clock that stands still: wall time at now, and the monotonic clock at five seconds, counted in
// microseconds.
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override long TimestampFrequency => 1_000_000;

    public override DateTimeOffset GetUtcNow() => now;

    public override long GetTimestamp() => 5_000_000;
}
