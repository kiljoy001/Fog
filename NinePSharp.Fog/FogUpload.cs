namespace NinePSharp.Fog;

/// <summary>An input revision. Disposing aborts; only Seal models a successful file clunk.</summary>
public sealed class FogUpload : IDisposable
{
    private readonly FogTransactionStore store;
    internal readonly string Owner;
    internal readonly string Transaction;
    internal readonly string Session;
    internal readonly string File;
    internal readonly MemoryStream Buffer = new();

    internal FogUpload(FogTransactionStore store, string owner, string transaction, string session, string file)
    {
        this.store = store;
        Owner = owner;
        Transaction = transaction;
        Session = session;
        File = file;
    }

    public void Write(ulong offset, ReadOnlySpan<byte> bytes) => store.Write(this, offset, bytes);

    public void Seal() => store.Seal(this);

    public void Dispose() => store.Abort(this);
}
