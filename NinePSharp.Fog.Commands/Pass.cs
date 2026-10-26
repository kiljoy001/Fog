using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// factotum's pass, as pass.c: a repository for a user and password, which a client reads; there
// is no server side.
public sealed class Pass : Proto
{
    private const int HavePass = 0;

    public override string Name => "pass";

    public override string Keyprompt => "user? !password?";

    public override bool TakesKeys => true;

    public override int Addkey(Factotum f, Key k, bool before) => f.Replacekey(k, before);

    public override Task<Rpc> InitAsync(Factotum f, Fsstate fss)
    {
        Rpc ret = f.Findkey(out Key? k, new Keyinfo(fss, null), Keyprompt);
        if (ret != Rpc.Ok)
        {
            return Task.FromResult(ret);
        }

        Factotum.Setattrs(fss.Attr, k!.Attr);
        (fss.Ps, fss.Phase) = (k, HavePass);
        return Task.FromResult(Rpc.Ok);
    }

    public override void Close(Fsstate fss)
    {
    }

    public override Task<(Rpc Ret, byte[] A)> ReadAsync(Factotum f, Fsstate fss, int n)
    {
        // Stryker disable once Statement,String,Equality,Block : rpcread's rdwrcheck leaves pass only its one phase to be read in
        if (fss.Phase != HavePass)
        {
            return Task.FromResult((f.Phaseerror(fss, "read"), Array.Empty<byte>()));
        }

        var k = (Key)fss.Ps!;
        string? user = Attr.StrFind(k.Attr, "user"), pass = Attr.StrFind(k.Privattr, "!password");

        // Stryker disable once Statement,String,Logical,Block : findkey's user? and !password? match only a key with both
        if (user is null || pass is null)
        {
            return Task.FromResult((f.Failure(fss, "passread cannot happen"), Array.Empty<byte>()));
        }

        byte[] buf = Encoding.UTF8.GetBytes($"{Libc.Quote(user)} {Libc.Quote(pass)}");
        return Task.FromResult(buf.Length > n ? (Factotum.Toosmall(fss, buf.Length), Array.Empty<byte>()) : (Rpc.Ok, buf));
    }

    public override Task<Rpc> WriteAsync(Factotum f, Fsstate fss, byte[] a) => Task.FromResult(f.Phaseerror(fss, "write"));
}
