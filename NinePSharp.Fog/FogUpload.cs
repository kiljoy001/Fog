namespace NinePSharp.Fog;

/// <summary>An input revision. Disposing aborts; only Seal models a successful file clunk.</summary>
public sealed class FogUpload : IDisposable
{
    private readonly FogTransactionStore store;

    internal FogUpload(FogTransactionStore store, string owner, string transaction, string session, string file)
    {
        this.store = store;
        Owner = owner;
        Transaction = transaction;
        Session = session;
        File = file;
    }

    internal string Owner { get; }

    internal string Transaction { get; }

    internal string Session { get; }

    internal string File { get; }

    internal MemoryStream Buffer { get; } = new();

    public void Write(ulong offset, ReadOnlySpan<byte> bytes) => store.Write(this, offset, bytes);

    public void Seal() => store.Seal(this);

    public void Dispose() => store.Abort(this);
}
