namespace NinePSharp.Fog;

/// <summary>Exact per-Twrite control commands; the 9P adapter still owns fid and reply/flush ordering.</summary>
public sealed class FogControlFile
{
    private readonly FogTransactionStore store;
    private readonly Func<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>, FogCommitPlan> prepare;

    public FogControlFile(FogTransactionStore store, Func<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>, FogCommitPlan> prepare)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(prepare);
        this.store = store;
        this.prepare = prepare;
    }

    public async Task<uint> WriteAsync(string owner, string id, ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Span.SequenceEqual("commit\n"u8))
        {
            await store.CommitAsync(owner, id, prepare, cancellationToken).ConfigureAwait(false);
            return 7;
        }

        if (command.Span.SequenceEqual("release\n"u8))
        {
            store.Release(owner, id);
            return 8;
        }

        throw new FogException("invalid-request");
    }
}
