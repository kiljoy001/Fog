namespace NinePSharp.Fog.Server;

public sealed record FogNinePLimits(int Sessions, int FidsPerSession, int RequestsPerSession,
    uint MessageSize, long SnapshotBytesPerSession, TimeSpan SnapshotLifetime, TimeSpan SessionLifetime)
{
    // How long closing a session, Tversion or Tflush waits for cancelled requests before abandoning them.
    public TimeSpan Drain { get; init; } = TimeSpan.FromSeconds(5);
}
