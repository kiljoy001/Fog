namespace NinePSharp.Fog.Commands;

// One of factotum's protocols, as dat.h's Proto: its name, how it starts, reads and writes a
// conversation and ends it, and, if it takes keys, how it adds one and what it asks for.
public abstract class Proto
{
    public abstract string Name { get; }

    public virtual string Keyprompt => string.Empty;

    public virtual bool TakesKeys => false;

    public abstract Task<Rpc> InitAsync(Factotum f, Fsstate fss);

    public virtual int Addkey(Factotum f, Key k, bool before) => throw new NotSupportedException();

    public virtual void Closekey(Key k)
    {
    }

    public abstract Task<Rpc> WriteAsync(Factotum f, Fsstate fss, byte[] a);

    // read: at most n bytes in a.
    public abstract Task<(Rpc Ret, byte[] A)> ReadAsync(Factotum f, Fsstate fss, int n);

    public abstract void Close(Fsstate fss);
}
