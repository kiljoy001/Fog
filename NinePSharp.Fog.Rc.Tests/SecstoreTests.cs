using System.Numerics;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// secstore's pieces on their own: SConn's records over a pipe, each way one is malformed, PAK's
// numbers and each way either side of its exchange can be given a bad message, and the account
// files password.c reads and writes.
public sealed class SecstoreTests : IAsyncLifetime
{
    // H(secretpw)^-1 mod p for glenda, as pak.c's PAK_Hi computes it.
    private const string GlendaHi = "hOaEPUy9zkWLWGWGLvdWIueeQw49POlvKUdOe4LlGVdNwwcdCh3LdRnAMnUf4y+FOosIGyfTSjJpJCTQOQVfsSgNWkXXGlChgCakzJPctlRDjOCjB1RKvkeRZfwqMxMjlrRX2g41QGl6wR2C1EiaGgBqgJ8TORplC1GsLnyJi6E=";
    private readonly ManualClock clock = new() { Now = DateTimeOffset.FromUnixTimeSeconds(1700000000) };
    private readonly FakeHostConsole console = new();
    private FogKernel kernel = null!;
    private Process init = null!;

    public async Task InitializeAsync()
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", clock, new FakeHostNetwork(), console);
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

    [Fact]
    public async Task ARecordIsCountedAsSslCountsOneAndItsDataGivenBack()
    {
        (SConn a, SConn b, int fa, int fb) = await PairAsync();
        Assert.Equal(20, a.Secretlen);
        Assert.Equal(5, await a.WriteAsync("hello"));
        byte[] raw = await Authsrv.ReadnAsync(init, fb, 7);
        Assert.Equal([0x80, 5, .. "hello"u8], raw);
        await init.WriteAsync(fa, raw);
        Assert.Equal((5, "hello"), await b.ReadstrAsync());
    }

    [Fact]
    public async Task OnceTheSecretIsSetEachRecordIsEncryptedAndAuthenticatedEachWayInTurn()
    {
        (SConn a, SConn b, _, _) = await PairAsync();
        byte[] sigma = new byte[20];
        sigma[0] = 7;
        Assert.Equal(0, a.Secret(sigma, 0));
        Assert.Equal(0, b.Secret(sigma, 1));
        for (int i = 0; i < 3; i++)
        {
            await a.WriteAsync($"to b {i}");
            Assert.Equal((6, $"to b {i}"), await b.ReadstrAsync());
            await b.WriteAsync($"to a {i}");
            Assert.Equal((6, $"to a {i}"), await a.ReadstrAsync());
        }
    }

    [Fact]
    public async Task AnEncryptedRecordIsTwentyBytesOfDigestLongerAndNotTheData()
    {
        (SConn a, _, _, int fb) = await PairAsync();
        a.Secret(new byte[20], 0);
        await a.WriteAsync("hello");
        byte[] raw = await Authsrv.ReadnAsync(init, fb, 27);
        Assert.Equal([0x80, 25], raw[..2]);
        Assert.Equal(27, raw.Length);
        Assert.False(raw.AsSpan(22).SequenceEqual("hello"u8));
    }

    [Fact]
    public async Task AReplayedOrAlteredRecordFailsItsCheck()
    {
        (SConn a, SConn b, int fa, int fb) = await PairAsync();
        a.Secret(new byte[20], 0);
        b.Secret(new byte[20], 1);
        await a.WriteAsync("first");
        byte[] first = await Authsrv.ReadnAsync(init, fb, 27);
        await init.WriteAsync(fa, first);
        Assert.Equal((5, "first"), await b.ReadstrAsync());
        await init.WriteAsync(fa, first);
        byte[] buf = new byte[SConn.Maxmsg + 1];
        Assert.Equal(-1, await b.ReadAsync(buf, SConn.Maxmsg));
        Assert.Equal("!SC_read integrity check failed", Cstring(buf));
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 5 }, "!SC_read invalid count")]
    [InlineData(new byte[] { 0x80 }, "!SC_read invalid count")]
    [InlineData(new byte[] { 0x80, 0 }, "!SC_read implausible record length")]
    [InlineData(new byte[] { 0x90, 1 }, "!SC_read implausible record length")]
    [InlineData(new byte[] { 0x80, 5, (byte)'a', (byte)'b' }, "!SC_read expected 5 bytes, but got 2")]
    public async Task APlainRecordThatIsNotWholeIsRefusedWithWhy(byte[] sent, string why)
    {
        (_, SConn b, int fa, _) = await PairAsync();
        await init.WriteAsync(fa, sent);
        await init.CloseAsync(fa);
        byte[] buf = new byte[SConn.Maxmsg + 1];
        Assert.Equal(-1, await b.ReadAsync(buf, SConn.Maxmsg));
        Assert.Equal(why, Cstring(buf));
        Assert.Equal((-1, "connection read error"), await b.ReadstrAsync());
    }

    [Theory]
    [InlineData(20, 0, "!SC_read missing sha1")]
    [InlineData(20, 20, "!SC_read missing sha1")]
    [InlineData(25, 10, "!SC_read missing sha1")]
    [InlineData(25, 22, "!SC_read missing data")]
    public async Task AnEncryptedRecordWithoutItsDigestOrDataIsRefused(int count, int sent, string why)
    {
        (_, SConn b, int fa, _) = await PairAsync();
        b.Secret(new byte[20], 1);
        await init.WriteAsync(fa, new byte[] { 0x80, (byte)count }.Concat(new byte[sent]).ToArray());
        await init.CloseAsync(fa);
        byte[] buf = new byte[SConn.Maxmsg + 1];
        Assert.Equal(-1, await b.ReadAsync(buf, SConn.Maxmsg));
        Assert.Equal(why, Cstring(buf));
    }

    [Fact]
    public async Task AnEncryptedRecordLongerThanTheBufferIsRefusedAsMissingData()
    {
        (SConn a, SConn b, _, _) = await PairAsync();
        a.Secret(new byte[20], 0);
        b.Secret(new byte[20], 1);
        await a.WriteAsync(new byte[100], 100);
        byte[] buf = new byte[60];
        Assert.Equal(-1, await b.ReadAsync(buf, 50));
        Assert.Equal("!SC_read missing data", Cstring(buf));
    }

    [Fact]
    public async Task AnErrorIsCutToTheBufferWithRoomForItsNul()
    {
        (_, SConn b, int fa, _) = await PairAsync();
        await init.WriteAsync(fa, new byte[] { 0x00, 1 });
        byte[] buf = new byte[8];
        Assert.Equal(-1, await b.ReadAsync(buf, 6));
        Assert.Equal("!SC_r", Cstring(buf));
    }

    [Theory]
    [InlineData(0, "!SC_write invalid n 0")]
    [InlineData(-1, "!SC_write invalid n -1")]
    [InlineData(SConn.Maxmsg + 2, "!SC_write invalid n 4098")]
    public async Task AWriteOfNothingOrTooMuchIsRefused(int n, string why)
    {
        (SConn a, _, _, _) = await PairAsync();
        Assert.Equal(-1, await a.WriteAsync(new byte[SConn.Maxmsg + 2], n));
        Assert.Equal(why, a.Errstr);
    }

    [Fact]
    public async Task TheLongestRecordIsMaxmsgAndOne()
    {
        (SConn a, SConn b, _, _) = await PairAsync();
        Assert.Equal(SConn.Maxmsg + 1, await a.WriteAsync(new byte[SConn.Maxmsg + 1], SConn.Maxmsg + 1));
        byte[] buf = new byte[SConn.Maxmsg + 2];
        Assert.Equal(SConn.Maxmsg + 1, await b.ReadAsync(buf, SConn.Maxmsg + 1));
    }

    [Fact]
    public async Task AWriteToAClosedConnectionFailsWithWhy()
    {
        (int fa, int fb) = await init.PipeAsync();
        await init.CloseAsync(fb);
        (_, (string Plain, string Encrypted, int Freed) result) = await StderrAsync(async p =>
        {
            p.Notify(_ => true);
            var a = new SConn(p, fa);
            int plain = await a.WriteAsync("x");
            string why = a.Errstr;
            a.Secret(new byte[20], 0);
            int encrypted = await a.WriteAsync("x");
            await a.FreeAsync();
            await a.FreeAsync();
            return ($"{plain} {why}", $"{encrypted} {a.Errstr}", (await Assert.ThrowsAsync<SyscallException>(() => p.WriteAsync(fa, "x"u8.ToArray()).AsTask())).Message.Length);
        });
        Assert.Equal(("-1 !SC_write invalid count", "-1 !SC_write invalid count", Errors.BadFd.Length), result);
    }

    [Fact]
    public async Task NoWriteHasFailedYet()
    {
        (SConn a, _, _, _) = await PairAsync();
        Assert.Equal(string.Empty, a.Errstr);
    }

    [Fact]
    public async Task ARecordOfMaxmsgIsReadAsAString()
    {
        (SConn a, SConn b, _, _) = await PairAsync();
        await a.WriteAsync(new string('x', SConn.Maxmsg));
        Assert.Equal((SConn.Maxmsg, new string('x', SConn.Maxmsg)), await b.ReadstrAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriteCutShortAfterItsCountFailsWithWhy(bool encrypted)
    {
        await using var server = new FileServerHarness { WriteLimit = 2 };
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("out", []));
        (int client, int served) = await init.PipeAsync();
        server.Serve(init, served);
        await init.MountAsync(client, -1, "/n", NinePSharp.Namespaces.MountFlags.Replace, string.Empty);
        var c = new SConn(init, await init.OpenAsync("/n/out", NinePConstants.OWRITE));
        if (encrypted)
        {
            c.Secret(new byte[20], 0);
        }

        Assert.Equal(-1, await c.WriteAsync("hello"));
        Assert.Equal("!SC_write error on send", c.Errstr);
    }

    [Fact]
    public async Task ARecordThatCannotBeReadIsRefused()
    {
        await using var server = new FileServerHarness { ReadRefusal = "disk on fire" };
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("in", [0x80, 5]));
        (int client, int served) = await init.PipeAsync();
        server.Serve(init, served);
        await init.MountAsync(client, -1, "/n", NinePSharp.Namespaces.MountFlags.Replace, string.Empty);
        var c = new SConn(init, await init.OpenAsync("/n/in", NinePConstants.OREAD));
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync());
    }

    [Fact]
    public async Task ARecordWhoseDataTheReadIsInterruptedForIsRefused()
    {
        (int fa, int fb) = await init.PipeAsync();
        await init.WriteAsync(fa, new byte[] { 0x80, 5 });
        var waiting = new TaskCompletionSource<long>();
        Func<Process, Task<string>> reader = async p =>
        {
            p.Notify(_ => true);
            waiting.SetResult(p.Pid);
            byte[] buf = new byte[SConn.Maxmsg + 1];
            int n = await new SConn(p, fb).ReadAsync(buf, SConn.Maxmsg);
            return n == -1 ? Cstring(buf) : $"{n}";
        };
        Func<Task> interrupt = async () =>
        {
            long pid = await waiting.Task;
            await Task.Delay(100);
            int note = await init.OpenAsync($"/proc/{pid}/note", NinePConstants.OWRITE);
            await init.WriteAsync(note, "interrupt"u8.ToArray());
            await init.CloseAsync(note);
        };
        (_, string why) = await StderrAsync(reader, interrupt);
        Assert.Equal("!SC_read expected 5 bytes, but got 0", why);
    }

    [Fact]
    public async Task AnErrorIsSentInBandAfterABangAndReadBackWithoutIt()
    {
        (SConn a, SConn b, _, _) = await PairAsync();
        await a.WriterrAsync("no such account");
        Assert.Equal((-1, "no such account"), await b.ReadstrAsync());
        await a.WriterrAsync(new string('x', SConn.Maxmsg + 10));
        (int n, string s) = await b.ReadstrAsync();
        Assert.Equal((-1, SConn.Maxmsg - 2), (n, s.Length));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("255", 255)]
    [InlineData("256", 256)]
    [InlineData("-5", -5)]
    public void MptoaGivesTheBase64OfTheBigEndianBytesAndStrtompReadsThemBack(string value, int expected)
    {
        var b = BigInteger.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, (int)Pak.Strtomp64(Pak.Mptoa64(b))!.Value);
    }

    [Theory]
    [InlineData(0, "AA==")]
    [InlineData(1, "AQ==")]
    [InlineData(65536, "AQAA")]
    [InlineData(-1, "-AQ==")]
    public void MptoaPrintsAtLeastOneByte(int value, string text) => Assert.Equal(text, Pak.Mptoa64(value));

    [Theory]
    [InlineData("AQAA", 65536)]
    [InlineData(" \tAQAA", 65536)]
    [InlineData("--AQAA", 65536)]
    [InlineData("-AQAA", -65536)]
    [InlineData("AQA=", 256)]
    [InlineData("AQ", 1)]
    [InlineData("A", 0)]
    [InlineData("AQAA!more", 65536)]
    [InlineData("+/", 251)]
    [InlineData("zz", 207)]
    [InlineData("99", 247)]
    public void StrtompReadsTheDigitsThereAreAndDropsAGroupTooShortForAByte(string text, int value) => Assert.Equal(value, (int)Pak.Strtomp64(text)!.Value);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-")]
    [InlineData("=AA")]
    [InlineData("!")]
    public void StrtompGivesNothingWithoutADigit(string text) => Assert.Null(Pak.Strtomp64(text));

    [Fact]
    public void HiIsTheInverseOfTheLongHashAsPakHiComputesIt()
    {
        (BigInteger h, BigInteger hi, string hexHi) = Pak.Hi("glenda", "secretpw");
        Assert.Equal(GlendaHi, hexHi);
        Assert.Equal(5, (int)(h & 7));
        Assert.Equal(hi, Pak.Strtomp64(hexHi));
        Assert.NotEqual(Pak.Hi("glenda", "secretpx").HexHi, hexHi);
        Assert.NotEqual(Pak.Hi("glendb", "secretpw").HexHi, hexHi);
    }

    [Fact]
    public async Task TheClientAndServerAgreeOnTheSessionSecretAndTheServerNamesItself()
    {
        await AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{GlendaHi}\n");
        (SConn client, SConn server, _, _) = await PairAsync();
        Task<(int, string?)> c = Pak.ClientAsync(init, "secstore", false, client, "glenda", "secretpw");
        (int n, string first) = await server.ReadstrAsync();
        Assert.Equal($"secstore\tPAK\nC=glenda\nm=", first[..(first.IndexOf("m=", StringComparison.Ordinal) + 2)]);
        (int rc, Pw? pw) = await Pak.ServerAsync(init, "secstored", server, "the store", first);
        Assert.True(n > 0);
        Assert.Equal((0, "the store"), await c);
        Assert.Equal(0, rc);
        Assert.Equal("glenda", pw!.Id);
        await client.WriteAsync("over the secret");
        Assert.Equal((15, "over the secret"), await server.ReadstrAsync());
    }

    [Fact]
    public async Task ALoginWithNoFailuresToClearLeavesTheAccountAsItWas()
    {
        string account = $"exp\t0\nkept as written\nPAK-Hi\t{GlendaHi}\n";
        await AccountAsync("glenda", account);
        (SConn client, SConn server, _, _) = await PairAsync();
        Task<(int, string?)> c = Pak.ClientAsync(init, "secstore", false, client, "glenda", "secretpw");
        (_, string first) = await server.ReadstrAsync();
        Assert.Equal(0, (await Pak.ServerAsync(init, "secstored", server, "secstore", first)).Rc);
        Assert.Equal(0, (await c).Item1);
        Assert.Equal(account, await FileAsync("/adm/secstore/who/glenda"));
    }

    [Fact]
    public async Task AWrongPasswordFailsTheClientsCheckAndIsCountedAgainstTheAccount()
    {
        await AccountAsync("glenda", $"exp\t0\nfailed\t2\nPAK-Hi\t{GlendaHi}\n");
        (SConn client, SConn server, _, _) = await PairAsync();
        Task<(int, string?)> c = Pak.ClientAsync(init, "secstore", false, client, "glenda", "wrongpw");
        (_, string first) = await server.ReadstrAsync();
        Assert.Equal(-1, (await Pak.ServerAsync(init, "secstored", server, "secstore", first)).Rc);
        Assert.Equal((-1, "secstore"), await c);
        Assert.Contains("failed\t3\n", await FileAsync("/adm/secstore/who/glenda"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALoginClearsTheFailuresCounted()
    {
        await AccountAsync("glenda", $"exp\t0\nfailed\t4\nPAK-Hi\t{GlendaHi}\n");
        (SConn client, SConn server, _, _) = await PairAsync();
        Task<(int, string?)> c = Pak.ClientAsync(init, "secstore", false, client, "glenda", "secretpw");
        (_, string first) = await server.ReadstrAsync();
        Assert.Equal(0, (await Pak.ServerAsync(init, "secstored", server, "secstore", first)).Rc);
        Assert.Equal(0, (await c).Item1);
        Assert.Equal($"exp\t0\nPAK-Hi\t{GlendaHi}\n", await FileAsync("/adm/secstore/who/glenda"));
    }

    [Fact]
    public async Task AClientWhoseVerifierIsWrongGivesMinusTwo()
    {
        await AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{GlendaHi}\n");
        (SConn client, SConn server, _, _) = await PairAsync();
        Task<(int, Pw?)> s = Pak.ServerAsync(init, "secstored", server, "secstore", $"secstore\tPAK\nC=glenda\nm={Pak.Mptoa64(12345)}\n");
        (_, string reply) = await client.ReadstrAsync();
        Assert.StartsWith("mu=", reply, StringComparison.Ordinal);
        await client.WriteAsync("k'=wrong\n");
        Assert.Equal(-2, (await s).Item1);
        Assert.Contains("failed\t1\n", await FileAsync("/adm/secstore/who/glenda"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("secstore\tPAX\nC=glenda\n", "protocol should start with ver alg")]
    [InlineData("secstore\tPAK\nD=glenda\n", "PAK version mismatch")]
    [InlineData("secstore\tPAK\nC=glenda", "PAK version mismatch")]
    [InlineData("secstore\tPAK\nC=glenda\n", "PAK version mismatch")]
    [InlineData("secstore\tPAK\nC=glenda\nn=AQ==\n", "PAK version mismatch")]
    [InlineData("secstore\tPAK\nC=glenda\nm=AQ==", "PAK version mismatch")]
    [InlineData("secstore\tPAK\nC=nobody\nm=AQ==\n", "accounts nobody and FICTITIOUS do not exist")]
    [InlineData("secstore\tPAK\nC=glenda\nm=AA==\n", "account exists")]
    [InlineData("secstore\tPAK\nC=glenda\nm=\n", "account exists")]
    public async Task TheServerRefusesAFirstMessageItCannotTakeAndSaysWhy(string first, string why)
    {
        await AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{GlendaHi}\n");
        (SConn client, SConn server, _, _) = await PairAsync();
        (string err, (int Rc, Pw? Pw) result) = await StderrAsync(p => Pak.ServerAsync(p, "secstored", server, "secstore", first));
        Assert.Equal((-1, null), result);
        Assert.Equal((-1, why), await client.ReadstrAsync());
        string mess = first.StartsWith("secstore\tPAK\n", StringComparison.Ordinal) ? first["secstore\tPAK\n".Length..] : string.Empty;
        bool firstLine = mess.StartsWith("C=", StringComparison.Ordinal) && mess.Contains('\n', StringComparison.Ordinal);
        Assert.Equal(why == "PAK version mismatch" && !firstLine ? $"secstored: mess[1]={mess}\n" : string.Empty, err);
        Assert.DoesNotContain("failed", await FileAsync("/adm/secstore/who/glenda"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "couldn't read verifier")]
    [InlineData("k'=x", "verifier syntax error")]
    [InlineData("kk=x\n", "verifier syntax error")]
    public async Task TheServerCountsAVerifierItCannotReadAsAFailure(string? verifier, string why)
    {
        await AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{GlendaHi}\n");
        (SConn client, SConn server, _, _) = await PairAsync();
        Task<(int, Pw?)> s = Pak.ServerAsync(init, "secstored", server, "secstore", $"secstore\tPAK\nC=glenda\nm={Pak.Mptoa64(12345)}\n");
        await client.ReadstrAsync();
        if (verifier is null)
        {
            await client.WriterrAsync("gone");
        }
        else
        {
            await client.WriteAsync(verifier);
        }

        Assert.Equal(-1, (await s).Item1);
        Assert.Equal((-1, why), await client.ReadstrAsync());
        Assert.Contains("failed\t1\n", await FileAsync("/adm/secstore/who/glenda"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mu=AQ==\nk=x", "verifier syntax error for secstore 1.0")]
    [InlineData("mx=AQ==\nk=x\nS=s\n", "verifier syntax error")]
    [InlineData("mu=AQ==", "verifier syntax error")]
    [InlineData("mu=AQ==\nq=x\nS=s\n", "verifier syntax error")]
    [InlineData("mu=AQ==\nk=x\n", "verifier syntax error for secstore 1.0")]
    [InlineData("mu=AQ==\nk=x\nT=s\n", "verifier syntax error for secstore 1.0")]
    [InlineData("mu=AQ==\nk=x\nS=s", "verifier syntax error for secstore 1.0")]
    [InlineData("mu=AQ==\nk=x\nS=s\n", "verifier didn't match")]
    public async Task TheClientRefusesAnAnswerItCannotTakeAndSaysWhy(string answer, string why)
    {
        (SConn client, SConn server, _, _) = await PairAsync();
        Task<(int Rc, string? S)> c = Pak.ClientAsync(init, "secstore", false, client, "glenda", "secretpw");
        await server.ReadstrAsync();
        await server.WriteAsync(answer);
        Assert.Equal(-1, (await c).Rc);
        Assert.Equal((-1, why), await server.ReadstrAsync());
    }

    [Fact]
    public async Task TheClientPrintsTheServersErrorAndFeedbackForTyposWhenVerbose()
    {
        (SConn client, SConn server, _, _) = await PairAsync();
        (string err, (int Rc, string? S) result) = await StderrAsync(async p =>
        {
            Task<(int, string?)> c = Pak.ClientAsync(p, "auth/secstore", true, client, "glenda", "secretpw");
            await server.ReadstrAsync();
            await server.WriterrAsync("no account");
            return await c;
        });
        Assert.Equal((-1, null), result);
        Assert.Equal("foxtrot\nauth/secstore: error: no account\n", err);
        Assert.Equal((-1, "couldn't read g**y"), await server.ReadstrAsync());
    }

    [Theory]
    [InlineData("pw87", "alpha")]
    [InlineData("pw9", "bravo")]
    [InlineData("pw7", "charlie")]
    [InlineData("pw13", "delta")]
    [InlineData("pw1", "echo")]
    [InlineData("pw2", "foxtrot")]
    [InlineData("pw8", "golf")]
    [InlineData("pw0", "hotel")]
    public async Task VerboseTheClientNamesAWordForItsHashToCatchATypo(string password, string word)
    {
        (SConn client, _, _, int fb) = await PairAsync();
        (string err, _) = await StderrAsync(async p =>
        {
            var server = new SConn(p, fb);
            Task<(int, string?)> c = Pak.ClientAsync(p, "secstore", true, client, "glenda", password);
            await server.ReadstrAsync();
            await server.WriterrAsync("no");
            return await c;
        });
        Assert.StartsWith($"{word}\n", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("glenda", "glenda")]
    [InlineData("a b", "a b")]
    [InlineData(".", ".")]
    [InlineData("..", null)]
    [InlineData("a/b", null)]
    [InlineData("a\tb", null)]
    [InlineData("a\u007fb", "a\u007fb")]
    [InlineData("café", null)]
    public async Task ValidatefileTakesANameWithoutControlCharactersSlashesOrBytesPastAscii(string? name, string? valid)
        => Assert.Equal(valid, await Password.ValidatefileAsync(init, name));

    [Fact]
    public async Task ValidatefileTakesNamesShorterThan250Bytes()
    {
        Assert.NotNull(await Password.ValidatefileAsync(init, new string('a', 249)));
        Assert.Null(await Password.ValidatefileAsync(init, new string('a', 250)));
    }

    [Fact]
    public async Task AnAccountIsReadFieldByFieldAsPutPwWritesIt()
    {
        await AccountAsync("glenda", $"exp\t1800000000\nDISABLED\nSTA\nfailed\t3\nother\tmy  comment\nPAK-Hi\t{GlendaHi}\n");
        Pw pw = (await Password.GetAsync(init, "glenda", true)).Pw!;
        Assert.Equal((1800000000u, (ushort)Pw.Sta, (ushort)3, "my  comment"), (pw.Expire, pw.Status, pw.Failed, pw.Other));
        Assert.Equal(Pak.Strtomp64(GlendaHi), pw.Hi);
        await Password.PutAsync(init, pw);
        Assert.Equal($"exp\t1800000000\nDISABLED\nSTA\nfailed\t3\nother\tmy  comment\nPAK-Hi\t{GlendaHi}\n", await FileAsync("/adm/secstore/who/glenda"));
    }

    [Fact]
    public async Task StaTwiceIsStaAndOtherWithoutAValueIsEmpty()
    {
        await AccountAsync("glenda", $"STA\nSTA\nother\nPAK-Hi\t{GlendaHi}\n");
        Pw pw = (await Password.GetAsync(init, "glenda", true)).Pw!;
        Assert.Equal(((ushort)(Pw.Enabled | Pw.Sta), string.Empty), (pw.Status, pw.Other));
    }

    [Fact]
    public async Task ReadingAndWritingAnAccountLeaveNoDescriptorOpen()
    {
        await AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{GlendaHi}\n");
        int before = await OpenCountAsync();
        Pw pw = (await Password.GetAsync(init, "glenda", false)).Pw!;
        await Password.PutAsync(init, pw);
        Assert.Equal(before, await OpenCountAsync());
        await init.CloseAsync(await init.CreateAsync("/adm/secstore/store", NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
        await init.CloseAsync(await init.CreateAsync("/adm/secstore/store/f", NinePConstants.OWRITE, 0b110_110_110));
        await Secstored.DirlsAsync(init, "/adm/secstore/store");
        Assert.Equal(before, await OpenCountAsync());
    }

    [Fact]
    public async Task FieldsAreSplitAtBlanksAndALastLineWithoutANewlineIsNotRead()
    {
        await AccountAsync("glenda", $"exp \t 1800000000\nunknown field\nfailed\nPAK-Hi  {GlendaHi}\nDISABLED");
        Pw pw = (await Password.GetAsync(init, "glenda", false)).Pw!;
        Assert.Equal((1800000000u, (ushort)Pw.Enabled, (ushort)0, null), (pw.Expire, pw.Status, pw.Failed, pw.Other));
    }

    [Fact]
    public async Task AnAccountWithoutItsHiIsCorruptedAndOneWithNoneIsFictitious()
    {
        await AccountAsync("glenda", "exp\t0\nPAK-Hi\t!\n");
        Assert.Equal((null, "corrupted account file for glenda"), await Password.GetAsync(init, "glenda", true));
        Assert.Equal((null, "accounts nobody and FICTITIOUS do not exist"), await Password.GetAsync(init, "nobody", false));
        Assert.Equal((null, "accounts .. and FICTITIOUS do not exist"), await Password.GetAsync(init, "..", false));
        await AccountAsync("FICTITIOUS", $"exp\t0\nPAK-Hi\t{GlendaHi}\n");
        Assert.Equal("FICTITIOUS", (await Password.GetAsync(init, "nobody", false)).Pw!.Id);
        Assert.Equal("FICTITIOUS", (await Password.GetAsync(init, ".", false)).Pw!.Id);
    }

    [Theory]
    [InlineData("exp\t1700000000\n", "account glenda expired at Tue Nov 14 22:13:20 GMT 2023")]
    [InlineData("exp\t1699999999\n", "account glenda expired at Tue Nov 14 22:13:19 GMT 2023")]
    [InlineData("DISABLED\n", "account glenda disabled")]
    public async Task AnExpiredOrDisabledAccountIsRefusedUnlessWantedDeadOrAlive(string field, string why)
    {
        await AccountAsync("glenda", $"{field}PAK-Hi\t{GlendaHi}\n");
        Assert.Equal((null, why), await Password.GetAsync(init, "glenda", false));
        Assert.NotNull((await Password.GetAsync(init, "glenda", true)).Pw);
    }

    [Fact]
    public async Task AnAccountExpiringLaterIsTaken()
    {
        await AccountAsync("glenda", $"exp\t1700000001\nPAK-Hi\t{GlendaHi}\n");
        Assert.NotNull((await Password.GetAsync(init, "glenda", false)).Pw);
    }

    [Fact]
    public async Task TenFailuresRefuseTheAccountForFiveMinutesAfterTheLastThenAreForgotten()
    {
        await AccountAsync("glenda", $"exp\t0\nfailed\t9\nPAK-Hi\t{GlendaHi}\n");
        Assert.NotNull((await Password.GetAsync(init, "glenda", false)).Pw);
        await AccountAsync("glenda", $"exp\t0\nfailed\t10\nPAK-Hi\t{GlendaHi}\n");
        Assert.Equal((null, "too many failures; try again in five minutes"), await Password.GetAsync(init, "glenda", false));
        clock.Now += TimeSpan.FromSeconds(299);
        Assert.Equal((null, "too many failures; try again in five minutes"), await Password.GetAsync(init, "glenda", false));
        clock.Now += TimeSpan.FromSeconds(1);
        Pw pw = (await Password.GetAsync(init, "glenda", false)).Pw!;
        Assert.Equal(0, pw.Failed);
        Assert.Equal($"exp\t0\nPAK-Hi\t{GlendaHi}\n", await FileAsync("/adm/secstore/who/glenda"));
    }

    [Fact]
    public async Task AnAccountCannotBeWrittenForANameValidatefileRefusesOrWithoutItsDirectory()
    {
        Assert.Equal("can't open PW file for a/b", await Password.PutAsync(init, new Pw("a/b")));
        Assert.Equal("can't open PW file for .", await Password.PutAsync(init, new Pw(".")));
        Assert.Equal("can't open PW file for glenda", await Password.PutAsync(init, new Pw("glenda")));
    }

    private static string Cstring(byte[] buf) => Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0));

    // How many descriptors the test's process has open, as its fd file lists them.
    // The lines of /proc/n/fd, which a read returns one at a time: the directory, then each descriptor.
    private async Task<int> OpenCountAsync()
    {
        int fd = await init.OpenAsync($"/proc/{init.Pid}/fd", NinePConstants.OREAD);
        try
        {
            int lines = 0;
            while (!(await init.ReadAsync(fd, 8192)).IsEmpty)
            {
                lines++;
            }

            return lines;
        }
        finally
        {
            await init.CloseAsync(fd);
        }
    }

    // A connection of two SConns over a pipe, and each one's descriptor.
    private async Task<(SConn A, SConn B, int Fa, int Fb)> PairAsync()
    {
        (int fa, int fb) = await init.PipeAsync();
        return (new SConn(init, fa), new SConn(init, fb), fa, fb);
    }

    private async Task AccountAsync(string id, string text)
    {
        foreach (string dir in new[] { "/adm/secstore", "/adm/secstore/who" })
        {
            try
            {
                await init.StatAsync(dir);
            }
            catch (SyscallException)
            {
                await init.CloseAsync(await init.CreateAsync(dir, NinePConstants.OREAD, 0x80000000 | 0b111_101_101));
            }
        }

        int fd = await init.CreateAsync($"/adm/secstore/who/{id}", NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(fd, Encoding.UTF8.GetBytes(text));
        await init.CloseAsync(fd);
    }

    private async Task<string> FileAsync(string path)
    {
        int fd = await init.OpenAsync(path, NinePConstants.OREAD);
        try
        {
            return Encoding.UTF8.GetString((await init.ReadAsync(fd, 8192)).Span);
        }
        finally
        {
            await init.CloseAsync(fd);
        }
    }

    // What the body printed on its standard error, run in a process of its own, and its result.
    private async Task<(string Err, T Result)> StderrAsync<T>(Func<Process, Task<T>> body, Func<Task>? meanwhile = null)
    {
        (int r, int w) = await init.PipeAsync();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            await child.DupAsync(w, 2);
            await child.CloseAsync(w);
            await child.CloseAsync(r);
            try
            {
                result.SetResult(await body(child));
            }
            catch (Exception error)
            {
                result.SetException(error);
            }
        });
        await init.CloseAsync(w);
        if (meanwhile is not null)
        {
            await meanwhile();
        }

        var bytes = new List<byte>();
        for (ReadOnlyMemory<byte> b; !(b = await init.ReadAsync(r, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsEmpty;)
        {
            bytes.AddRange(b.Span);
        }

        await init.CloseAsync(r);
        return (Encoding.UTF8.GetString([.. bytes]), await result.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
