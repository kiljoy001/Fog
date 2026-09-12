namespace NinePSharp.Fog;

/// <summary>Conservative payload reservations; managed object overhead is additionally host-bounded.</summary>
public sealed record FogTransactionLimits(
    int MaxTransactions,
    int MaxPerOwner,
    int MaxInputBytes,
    int MaxSnapshotBytes,
    long MaxReservedBytes,
    int MaxFiles,
    TimeSpan StagingLifetime,
    TimeSpan RetentionLifetime)
{
    internal long Reservation => (long)MaxInputBytes + MaxSnapshotBytes;

    internal void Validate()
    {
        if (MaxTransactions <= 0 || MaxPerOwner <= 0 || MaxInputBytes <= 0 || MaxSnapshotBytes <= 0 ||
            MaxReservedBytes < Reservation || MaxFiles <= 0 || StagingLifetime <= TimeSpan.Zero || RetentionLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FogTransactionLimits));
        }
    }
}
