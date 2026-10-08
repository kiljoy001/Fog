namespace NinePSharp.Fog.Gefs.Tests.Support;

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1000);

    public override DateTimeOffset GetUtcNow() => Now;
}
