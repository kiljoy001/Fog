using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Namespaces;
using Xunit;
using static NinePSharp.Fog.Commands.Libauth;

namespace NinePSharp.Fog.Rc.Tests;

// libauth's client of factotum, against a factotum the test plays on a pipe: each answer auth_rpc
// can be given, an authentication proxied through each of its phases and each way it fails, and
// auth_chuid spending a capability.
public sealed class LibauthTests : IAsyncLifetime
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private FogKernel kernel = null!;
    private Process init = null!;

    public async Task InitializeAsync()
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", clock, new FakeHostNetwork(), new FakeHostConsole());
        init = await kernel.BootAsync();
        for (int i = 0; i < 3; i++)
        {
            await init.OpenAsync("/dev/null", NinePConstants.ORDWR);
        }
    }

    public Task DisposeAsync()
    {
        foreach (Process process in kernel.Processes.Values)
        {
            process.PostNote("kill");
        }

        return Task.CompletedTask;
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("ok", Ar.Ok, "", "")]
    [InlineData("ok data", Ar.Ok, "data", "")]
    [InlineData("done", Ar.Done, "", "")]
    [InlineData("done x", Ar.Done, "x", "")]
    [InlineData("error", Ar.Error, "", "unspecified rpc error")]
    [InlineData("error bad thing", Ar.Error, "bad thing", "bad thing")]
    [InlineData("needkey proto=dp9ik", Ar.Needkey, "proto=dp9ik", "needkey proto=dp9ik")]
    [InlineData("badkey only", Ar.Badkey, "only", "badkey only")]
    [InlineData("badkey first\nsecond\nthird", Ar.Badkey, "first\nsecond\nthird", "badkey second")]
    [InlineData("phase wrong", Ar.Phase, "wrong", "phase error wrong")]
    [InlineData("toosmall 12", Ar.Toosmall, "12", "")]
    [InlineData("okay", Ar.Rpcfailure, "", "malformed rpc response: okay")]
    [InlineData("o", Ar.Rpcfailure, "", "malformed rpc response: o")]
    public async Task AnAnswerIsClassifiedByItsFirstWordAndItsArgumentKept(string answer, Ar type, string arg, string error)
    {
        (AuthRpc rpc, int far) = await RpcAsync();
        await init.WriteAsync(far, Encoding.UTF8.GetBytes(answer));
        Assert.Equal(type, await rpc.CallAsync("read", "x y"u8.ToArray()));
        Assert.Equal(arg, Encoding.UTF8.GetString(rpc.Arg));
        Assert.Equal(error, rpc.Error);
        Assert.Equal("read x y", await ReadAsync(far));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnRpcTooBigIsNotWrittenAndOneThatCannotBeIsAFailure()
    {
        (AuthRpc rpc, int far) = await RpcAsync();
        Assert.Equal(Ar.Toobig, await rpc.CallAsync("write", new byte[AuthRpcMax - 5]));
        Assert.Equal("rpc too big", rpc.Error);
        await init.WriteAsync(far, "ok"u8.ToArray());
        Assert.Equal(Ar.Ok, await rpc.CallAsync("write", new byte[AuthRpcMax - 6]));
        Assert.Equal(AuthRpcMax, (await ReadAllAsync(far, AuthRpcMax)).Length);

        var closed = new AuthRpc(init, 40);
        Assert.Equal(Ar.Rpcfailure, await closed.CallAsync("read", []));
        Assert.NotEmpty(closed.Error);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnAuthenticationIsRelayedThroughEachPhaseUntilItsAuthinfo()
    {
        (AuthRpc rpc, int far) = await RpcAsync();
        (int conv, int peer) = await init.PipeAsync();
        await init.WriteAsync(peer, "abcdefgh"u8.ToArray());
        Task<AuthInfo> proxied = FauthProxyAsync(conv, rpc, null, "proto=p9any role=server");
        Assert.Equal("start proto=p9any role=server", await AnswerAsync(far, "ok"));
        Assert.Equal("read ", await AnswerAsync(far, "ok to the peer"));
        Assert.Equal("to the peer", await ReadAsync(peer));
        Assert.Equal("read ", await AnswerAsync(far, "phase"));
        Assert.Equal("write ", await AnswerAsync(far, "toosmall 3"));
        Assert.Equal("write abc", await AnswerAsync(far, "toosmall 8"));
        Assert.Equal("write abcdefgh", await AnswerAsync(far, "ok"));
        Assert.Equal("read ", await AnswerAsync(far, "done"));
        Assert.Equal("authinfo ", await AnswerAsync(far, "ok " + Authinfo("bob", "glenda", "cap", [1, 2, 3])));
        AuthInfo ai = await proxied.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("bob", "glenda", "cap"), (ai.Cuid, ai.Suid, ai.Cap));
        Assert.Equal([1, 2, 3], ai.Secret);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AKeyFactotumNeedsIsAskedOfGetkeyUntilItGivesUp()
    {
        (AuthRpc rpc, int far) = await RpcAsync();
        var asked = new List<string>();
        int answer = 0;
        Task<int> Getkey(string arg)
        {
            asked.Add(arg);
            return Task.FromResult(answer--);
        }

        Task<AuthInfo> proxied = FauthProxyAsync(0, rpc, Getkey, "proto=dp9ik");
        Assert.Equal("start proto=dp9ik", await AnswerAsync(far, "needkey dom=fog"));
        Assert.Equal("start proto=dp9ik", await AnswerAsync(far, "badkey bad\ndom=fog"));
        string error = (await Assert.ThrowsAsync<SyscallException>(() => proxied)).Message;
        Assert.Equal("fauth_proxy start: badkey dom=fog", error);
        Assert.Equal(["dom=fog", "bad\ndom=fog"], asked);

        (rpc, far) = await RpcAsync();
        proxied = FauthProxyAsync(0, rpc, null, "proto=dp9ik");
        Assert.Equal("start proto=dp9ik", await AnswerAsync(far, "needkey dom=fog"));
        Assert.Equal("fauth_proxy start: needkey dom=fog", (await Assert.ThrowsAsync<SyscallException>(() => proxied)).Message);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("error no key", "", "fauth_proxy start: no key")]
    [InlineData("ok", "error gone", "auth_proxy rpc: gone")]
    [InlineData("ok", "toosmall 1", "auth_proxy rpc: ")]
    public async Task AStartOrReadFactotumRefusesEndsTheAuthentication(string start, string read, string error)
    {
        (AuthRpc rpc, int far) = await RpcAsync();
        Task<AuthInfo> proxied = FauthProxyAsync(0, rpc, null, "p");
        await AnswerAsync(far, start);
        if (read.Length != 0)
        {
            await AnswerAsync(far, read);
        }

        Assert.Equal(error, (await Assert.ThrowsAsync<SyscallException>(() => proxied)).Message);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("toosmall 0", "auth_proxy rpc write: phase error ")]
    [InlineData("toosmall 4097", "auth_proxy rpc write: phase error ")]
    [InlineData("error refused", "auth_proxy rpc write: refused")]
    public async Task APhaseFactotumCannotTakeEndsTheAuthentication(string answer, string error)
    {
        (AuthRpc rpc, int far) = await RpcAsync();
        Task<AuthInfo> proxied = FauthProxyAsync(0, rpc, null, "p");
        await AnswerAsync(far, "ok");
        await AnswerAsync(far, "phase");
        await AnswerAsync(far, answer);
        Assert.Equal(error, (await Assert.ThrowsAsync<SyscallException>(() => proxied)).Message);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APeerThatHangsUpOrCannotBeReadOrWrittenEndsTheAuthentication()
    {
        (AuthRpc rpc, int far) = await RpcAsync();
        (int conv, int peer) = await init.PipeAsync();
        await init.CloseAsync(peer);
        Task<AuthInfo> proxied = FauthProxyAsync(conv, rpc, null, "p");
        await AnswerAsync(far, "ok");
        await AnswerAsync(far, "phase");
        await AnswerAsync(far, "toosmall 4");
        Assert.Equal("auth_proxy short read", (await Assert.ThrowsAsync<SyscallException>(() => proxied)).Message);

        (rpc, far) = await RpcAsync();
        proxied = FauthProxyAsync(40, rpc, null, "p");
        await AnswerAsync(far, "ok");
        await AnswerAsync(far, "phase");
        await AnswerAsync(far, "toosmall 4");
        Assert.StartsWith("auth_proxy read fd: ", (await Assert.ThrowsAsync<SyscallException>(() => proxied)).Message, StringComparison.Ordinal);

        (rpc, far) = await RpcAsync();
        proxied = FauthProxyAsync(40, rpc, null, "p");
        await AnswerAsync(far, "ok");
        await AnswerAsync(far, "ok data");
        Assert.StartsWith("auth_proxy write fd: ", (await Assert.ThrowsAsync<SyscallException>(() => proxied)).Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AuthinfoMayEndWithAnEmptySecret()
    {
        (AuthRpc rpc, int far) = await RpcAsync();
        await init.WriteAsync(far, Encoding.Latin1.GetBytes("ok " + Authinfo("a", "b", "c", [])));
        AuthInfo ai = await AuthGetinfoAsync(rpc);
        Assert.Equal(("a", "b", "c", 0), (ai.Cuid, ai.Suid, ai.Cap, ai.Secret.Length));
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public async Task AuthinfoThatEndsShortOfWhatItsLengthsSayIsBad(int cut)
    {
        byte[] whole = Encoding.Latin1.GetBytes(Authinfo("a", "b", "c", [9]));
        (AuthRpc rpc, int far) = await RpcAsync();
        await init.WriteAsync(far, (byte[])[.. "ok "u8, .. whole.AsSpan(0, whole.Length - cut)]);
        Assert.Equal("bad auth info from factotum", (await Assert.ThrowsAsync<SyscallException>(() => AuthGetinfoAsync(rpc))).Message);

        (rpc, far) = await RpcAsync();
        await init.WriteAsync(far, "error none"u8.ToArray());
        Assert.Equal("none", (await Assert.ThrowsAsync<SyscallException>(() => AuthGetinfoAsync(rpc))).Message);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AuthProxyNeedsFactotumsRpcFile()
    {
        string error = (await Assert.ThrowsAsync<SyscallException>(() => AuthProxyAsync(init, 0, null, "proto=p9any role=server"))).Message;
        Assert.StartsWith("opening /mnt/factotum/rpc: ", error, StringComparison.Ordinal);
        Assert.Equal(3, await init.OpenAsync("/dev/null", NinePConstants.OREAD));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AuthChuidSpendsTheCapabilityAndBuildsTheUsersNamespace()
    {
        await MkdirAsync("/root/usr/bob");
        Process p = await ChildAsync(_ => Task.CompletedTask);
        await AddcapAsync("glenda@bob", "k1");
        await AuthChuidAsync(p, new AuthInfo { Cuid = "bob", Cap = "glenda@bob@k1" }, null);
        Assert.Equal("bob", p.User);
        Assert.Equal("/usr/bob", await Libc.GetwdAsync(p));
        Assert.Equal("bob", await EnvAsync(p, "user"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AuthChuidMountsFactotumAgainWhenThereIsOne()
    {
        await MkdirAsync("/root/usr/bob");
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, child => child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n", "-p", "-s", "factotum"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        Process p = await ChildAsync(_ => Task.CompletedTask);
        await AddcapAsync("glenda@bob", "k2");
        await AuthChuidAsync(p, new AuthInfo { Cuid = "bob", Cap = "glenda@bob@k2" }, "/lib/namespace");
        Assert.Equal("bob", p.User);
        int ctl = await p.OpenAsync("/mnt/factotum/ctl", NinePConstants.OREAD);
        await p.CloseAsync(ctl);

        // A /srv/factotum that is not a file server is let be.
        await init.RemoveAsync("/srv/factotum");
        await WriteAsync("/srv/factotum", "not 9P");
        p = await ChildAsync(_ => Task.CompletedTask);
        await AddcapAsync("glenda@bob", "k3");
        await AuthChuidAsync(p, new AuthInfo { Cuid = "bob", Cap = "glenda@bob@k3" }, null);
        Assert.Equal("bob", p.User);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData(null, "no capability")]
    [InlineData("", "no capability")]
    [InlineData("glenda@bob@nokey", "writing glenda@bob@nokey to /dev/capuse: ")]
    public async Task AuthChuidNeedsACapabilityThatWasMade(string? cap, string error)
    {
        Process p = await ChildAsync(_ => Task.CompletedTask);
        string message = (await Assert.ThrowsAsync<SyscallException>(() => AuthChuidAsync(p, new AuthInfo { Cuid = "bob", Cap = cap }, null))).Message;
        Assert.StartsWith(error, message, StringComparison.Ordinal);
        Assert.Equal("glenda", p.User);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AuthChuidNeedsCapuse()
    {
        Process p = await ChildAsync(c => c.BindAsync("/tmp", "/dev", MountFlags.Replace).AsTask());
        string message = (await Assert.ThrowsAsync<SyscallException>(() => AuthChuidAsync(p, new AuthInfo { Cuid = "bob", Cap = "a@b@c" }, null))).Message;
        Assert.StartsWith("opening /dev/capuse: ", message, StringComparison.Ordinal);
    }

    // authinfo as factotum's convAI2M makes it: each string and the secret after its length.
    private static string Authinfo(string cuid, string suid, string cap, byte[] secret)
    {
        var m = new List<byte>();
        foreach (byte[] b in (byte[][])[Encoding.UTF8.GetBytes(cuid), Encoding.UTF8.GetBytes(suid), Encoding.UTF8.GetBytes(cap), secret])
        {
            byte[] n = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(n, (ushort)b.Length);
            m.AddRange(n);
            m.AddRange(b);
        }

        return Encoding.Latin1.GetString([.. m]);
    }

    private static async Task<string> EnvAsync(Process p, string name)
    {
        int fd = await p.OpenAsync($"/env/{name}", NinePConstants.OREAD);
        string value = Encoding.UTF8.GetString((await p.ReadAsync(fd, 256)).Span);
        await p.CloseAsync(fd);
        return value;
    }

    // The answer is ready before the request comes, and the request is what was read.
    private async Task<string> AnswerAsync(int far, string answer)
    {
        await init.WriteAsync(far, Encoding.Latin1.GetBytes(answer));
        return Encoding.Latin1.GetString((await init.ReadAsync(far, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span);
    }

    private async Task<(AuthRpc Rpc, int Far)> RpcAsync()
    {
        (int near, int far) = await init.PipeAsync();
        return (new AuthRpc(init, near), far);
    }

    private async Task<string> ReadAsync(int fd)
        => Encoding.UTF8.GetString((await init.ReadAsync(fd, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span);

    private async Task<byte[]> ReadAllAsync(int fd, int count)
    {
        var all = new List<byte>();
        while (all.Count < count)
        {
            all.AddRange((await init.ReadAsync(fd, count - all.Count)).Span);
        }

        return [.. all];
    }

    // A process of init's in a namespace of its own, which prepare changes.
    private async Task<Process> ChildAsync(Func<Process, Task> prepare)
    {
        var made = new TaskCompletionSource<Process>();
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg | RforkFlags.Envg, async c =>
        {
            await prepare(c);
            made.SetResult(c);
            await Task.Delay(Timeout.Infinite);
        });
        return await made.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // A capability the host owner makes, as factotum's mkcap does.
    private async Task AddcapAsync(string fromto, string key)
    {
        int fd = await init.OpenAsync("/dev/caphash", NinePConstants.OWRITE);
        await init.WriteAsync(fd, HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(fromto)));
        await init.CloseAsync(fd);
    }

    private async Task MkdirAsync(string path)
        => await init.CloseAsync(await init.CreateAsync(path, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));

    private async Task WriteAsync(string path, string text)
    {
        int f = await init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(f, Encoding.UTF8.GetBytes(text));
        await init.CloseAsync(f);
    }
}
