using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Namespaces;
using Xunit;
using static NinePSharp.Fog.Rc.Tests.Support.SecstoreHarness;

namespace NinePSharp.Fog.Rc.Tests;

// secstored as a client sees it, logged in over PAK: each operation, each way one fails and what
// it answers then, the SecurID step, its flags, and what it logs.
public sealed class SecstoredTests : IAsyncLifetime
{
    private SecstoreHarness h = null!;

    public async Task InitializeAsync() => h = await StartAsync();

    public async Task DisposeAsync() => await h.DisposeAsync();

    [Fact]
    public async Task AGetOfAFileTheStoreDoesNotHaveIsMinusOneAndTheOperationFails()
    {
        SConn c = await LoggedInAsync(17400);
        await c.WriteAsync("GET nonesuch");
        Assert.Equal((2, "-1"), await c.ReadstrAsync());
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains("can't open /adm/secstore/store/glenda/nonesuch: ", await h.LogsAsync(), StringComparison.Ordinal);
        Assert.Contains("[glenda] GET nonesuch", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGetOfADirectoryIsMinusOne()
    {
        await h.GlendaAsync();
        await h.MkdirAsync("/adm/secstore/store/glenda/sub");
        SConn c = await LoggedInAsync(17401);
        await c.WriteAsync("GET sub");
        Assert.Equal((2, "-1"), await c.ReadstrAsync());
        Assert.Contains("/adm/secstore/store/glenda/sub should be a plain file, not a directory", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGetOfAFileOverTenMegabytesIsMinusThreeAndOneOfTenIsSent()
    {
        await h.GlendaAsync();
        await h.WriteFileAsync("/adm/secstore/store/glenda/big", new byte[(10 * 1024 * 1024) + 1]);
        await h.WriteFileAsync("/adm/secstore/store/glenda/most", new byte[10 * 1024 * 1024]);
        SConn c = await LoggedInAsync(17402);
        await c.WriteAsync("GET big");
        Assert.Equal((2, "-3"), await c.ReadstrAsync());
        Assert.Contains("implausible filesize 10485761 for big", await h.LogsAsync(), StringComparison.Ordinal);
        c = await LoggedInAsync(17402);
        await c.WriteAsync("GET most");
        Assert.Equal((8, "10485760"), await c.ReadstrAsync());
        long got = 0;
        byte[] buf = new byte[SConn.Maxmsg + 1];
        for (int n; got < 10 * 1024 * 1024 && (n = await c.ReadAsync(buf, SConn.Maxmsg)) > 0;)
        {
            Assert.InRange(n, 1, SConn.Maxmsg);
            got += n;
        }

        Assert.Equal(10 * 1024 * 1024, got);
    }

    [Fact]
    public async Task AFileIsSentInChunksOfMaxmsgAfterItsSize()
    {
        await h.GlendaAsync();
        byte[] data = RandomNumberGenerator.GetBytes((2 * SConn.Maxmsg) + 10);
        await h.WriteFileAsync("/adm/secstore/store/glenda/f", data);
        SConn c = await LoggedInAsync(17403);
        await c.WriteAsync("GET f");
        Assert.Equal((4, "8202"), await c.ReadstrAsync());
        byte[] buf = new byte[SConn.Maxmsg + 1];
        var got = new List<byte>();
        foreach (int size in new[] { SConn.Maxmsg, SConn.Maxmsg, 10 })
        {
            Assert.Equal(size, await c.ReadAsync(buf, SConn.Maxmsg));
            got.AddRange(buf.AsSpan(0, size));
        }

        Assert.Equal(data, got);
    }

    [Fact]
    public async Task AGetOfDotListsTheAccountsFilesInChunks()
    {
        await h.GlendaAsync();
        for (int i = 0; i < 80; i++)
        {
            await h.WriteFileAsync($"/adm/secstore/store/glenda/{new string('f', 40)}{i:D2}", new byte[i]);
        }

        string listing = (await Secstored.DirlsAsync(h.Init, "/adm/secstore/store/glenda"))!;
        Assert.True(listing.Length > SConn.Maxmsg);
        SConn c = await LoggedInAsync(17404);
        await c.WriteAsync("GET .");
        Assert.Equal(listing.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), (await c.ReadstrAsync()).S);
        byte[] buf = new byte[SConn.Maxmsg + 1];
        var got = new List<byte>();
        for (int n; got.Count < listing.Length;)
        {
            n = await c.ReadAsync(buf, SConn.Maxmsg);
            Assert.Equal(Math.Min(SConn.Maxmsg, listing.Length - got.Count), n);
            got.AddRange(buf.AsSpan(0, n));
        }

        Assert.Equal(listing, Encoding.UTF8.GetString([.. got]));
    }

    [Fact]
    public async Task AGetOfDotForAnAccountWithNoStoreIsEmpty()
    {
        await h.AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{GlendaHi}\n", store: false);
        await h.SecstoredAsync(17405);
        SConn c = await h.LoginAsync(17405);
        Assert.Equal((2, "OK"), await c.ReadstrAsync());
        await c.WriteAsync("GET .");
        Assert.Equal((1, "0"), await c.ReadstrAsync());
        await c.WriteAsync("BYE");
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync());
    }

    [Fact]
    public async Task DirlsListsEachFilesSizeDateAndSumInOrderOfNamePaddedToTheLongest()
    {
        await h.GlendaAsync();
        await h.WriteFileAsync("/adm/secstore/store/glenda/zz", new byte[100]);
        await h.WriteFileAsync("/adm/secstore/store/glenda/a", "x"u8.ToArray());
        await h.MkdirAsync("/adm/secstore/store/glenda/café");
        string sumX = Convert.ToBase64String(SHA1.HashData("x"u8));
        string sumZero = Convert.ToBase64String(SHA1.HashData(new byte[100]));
        string sumNone = Convert.ToBase64String(SHA1.HashData([]));
        string[] lines = (await Secstored.DirlsAsync(h.Init, "/adm/secstore/store/glenda"))!.Split('\n');
        Assert.Equal($"a    \t  1 Nov 14 22:13:20 GMT 2023 {sumX}", lines[0]);
        Assert.Matches(@"^café \t  0 Nov 14 22:13:20 GMT 2023 [A-Za-z0-9+/]{27}=$", lines[1]);
        Assert.Equal($"zz   \t100 Nov 14 22:13:20 GMT 2023 {sumZero}", lines[2]);
        Assert.Equal(string.Empty, lines[3]);
        Assert.NotEqual(sumNone, sumX);
    }

    [Fact]
    public async Task DirlsGivesNothingForAFileOrAMissingDirectoryAndNothingListedForAnEmptyOne()
    {
        await h.GlendaAsync();
        await h.WriteFileAsync("/adm/secstore/store/glenda/a", "x"u8.ToArray());
        Assert.Null(await Secstored.DirlsAsync(h.Init, "/adm/secstore/store/glenda/a"));
        Assert.Null(await Secstored.DirlsAsync(h.Init, "/adm/secstore/store/nobody"));
        await h.MkdirAsync("/adm/secstore/store/empty");
        Assert.Equal(string.Empty, await Secstored.DirlsAsync(h.Init, "/adm/secstore/store/empty"));
    }

    [Fact]
    public async Task DirlsSumsAFileItCannotReadAsAnEmptyOne()
    {
        await h.GlendaAsync();
        await using var server = new FileServerHarness();
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("secret", "x"u8.ToArray()));
        server.Refusal = ("secret", "permission denied");
        (int client, int served) = await h.Init.PipeAsync();
        server.Serve(h.Init, served);
        await h.Init.MountAsync(client, -1, "/adm/secstore/store/glenda", MountFlags.Replace, string.Empty);
        Assert.EndsWith($" {Convert.ToBase64String(SHA1.HashData([]))}\n", await Secstored.DirlsAsync(h.Init, "/adm/secstore/store/glenda"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APutStoresTheFileGivenInChunksAfterItsSize()
    {
        SConn c = await LoggedInAsync(17406);
        await c.WriteAsync("PUT f");
        await c.WriteAsync("5000");
        await c.WriteAsync(new byte[SConn.Maxmsg], SConn.Maxmsg);
        await c.WriteAsync(Enumerable.Repeat((byte)'y', 904).ToArray(), 904);
        await c.WriteAsync("BYE");
        await WaitAsync(async () => await h.ExistsAsync("/adm/secstore/store/glenda/f") && (await h.BytesAsync("/adm/secstore/store/glenda/f")).Length == 5000);
        Assert.Equal('y', (char)(await h.BytesAsync("/adm/secstore/store/glenda/f"))[^1]);
    }

    [Theory]
    [InlineData("-1", "remote file f does not exist")]
    [InlineData("-2", "implausible filesize -2 for f")]
    [InlineData("10485761", "implausible filesize 10485761 for f")]
    public async Task APutOfASizeItCannotTakeFails(string size, string why)
    {
        SConn c = await LoggedInAsync(17407);
        await c.WriteAsync("PUT f");
        await c.WriteAsync(size);
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains(why, await h.LogsAsync(), StringComparison.Ordinal);
        Assert.Contains("failed PUT glenda/f", await h.LogsAsync(), StringComparison.Ordinal);
        Assert.False(await h.ExistsAsync("/adm/secstore/store/glenda/f"));
    }

    [Theory]
    [InlineData("0", 17490)]
    [InlineData("10485760", 17491)]
    public async Task APutOfNothingOrOfTheMostItTakesIsTaken(string size, int port)
    {
        SConn c = await LoggedInAsync(port);
        await c.WriteAsync("PUT f");
        await c.WriteAsync(size);
        await c.WriteAsync("BYE");
        await WaitAsync(async () => await h.ExistsAsync("/adm/secstore/store/glenda/f"));
        Assert.DoesNotContain("implausible", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageItTakesIsOnlyItsFirstLine()
    {
        SConn c = await LoggedInAsync(17492);
        await c.WriteAsync("\nBYE");
        Assert.Equal((-1, "unrecognized operation"), await c.ReadstrAsync());
        Assert.DoesNotContain("BYE", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallIsHungUpAfterThirtyMinutes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        await using SecstoreHarness timed = await StartAsync(clock: clock);
        await timed.GlendaAsync();
        await timed.SecstoredAsync(17493);
        SConn c = await timed.LoginAsync(17493);
        Assert.Equal("OK", (await c.ReadstrAsync()).S);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task TheDaemonIsInANoteGroupOfItsOwnAndListensOn5356()
    {
        await h.StartDaemonAsync("auth/secstored");
        await WaitAsync(async () => (await h.LogsAsync()).Contains("ANNOUNCE /net/tcp!*!5356", StringComparison.Ordinal));
        long daemon = h.Kernel.Processes.Values.Single(p => p.Text == "secstored").Pid;
        Assert.NotEqual(await h.FileAsync($"/proc/{h.Init.Pid}/noteid"), await h.FileAsync($"/proc/{daemon}/noteid"));
    }

    [Fact]
    public async Task APutTheStoreTakesOnlyPartOfFails()
    {
        SConn c = await LoggedInAsync(17494);
        await using var server = new FileServerHarness { WriteLimit = 2 };
        (int client, int served) = await h.Init.PipeAsync();
        server.Serve(h.Init, served);
        await h.Init.MountAsync(client, -1, "/adm/secstore/store/glenda", MountFlags.Replace | MountFlags.Create, string.Empty);
        await c.WriteAsync("PUT f");
        await c.WriteAsync("5");
        await c.WriteAsync("hello");
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains("write error on f: short write", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APutWhoseSizeIsAnErrorFails()
    {
        SConn c = await LoggedInAsync(17408);
        await c.WriteAsync("PUT f");
        await c.WriterrAsync("no file");
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains("remote: no file: ", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APutOfANameTheStoreRefusesFailsWithoutAName()
    {
        SConn c = await LoggedInAsync(17409);
        await c.WriteAsync("PUT a/b");
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains("illegal name: a/b", await h.LogsAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("illegal name", h.Console.Text, StringComparison.Ordinal);
        Assert.Contains("failed PUT glenda/<nil>", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APutForAnAccountWithNoStoreCannotMakeItsFile()
    {
        await h.AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{GlendaHi}\n", store: false);
        await h.SecstoredAsync(17410);
        SConn c = await h.LoginAsync(17410);
        await c.ReadstrAsync();
        await c.WriteAsync("PUT f");
        await c.WriteAsync("5");
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains("can't open /adm/secstore/store/glenda/f: ", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APutCutShortLeavesWhatCameAndFails()
    {
        SConn c = await LoggedInAsync(17411);
        await c.WriteAsync("PUT f");
        await c.WriteAsync("100");
        await c.WriteAsync("abc");
        await c.FreeAsync();
        await WaitAsync(async () => (await h.LogsAsync()).Contains("empty file chunk", StringComparison.Ordinal));
        Assert.Contains("failed PUT glenda/f", await h.LogsAsync(), StringComparison.Ordinal);
        Assert.Equal("abc", await h.FileAsync("/adm/secstore/store/glenda/f"));
    }

    [Fact]
    public async Task APutTheFileCannotTakeFails()
    {
        await h.GlendaAsync();
        await using var server = new FileServerHarness { WriteLimit = 2 };
        (int client, int served) = await h.Init.PipeAsync();
        server.Serve(h.Init, served);
        await h.Init.MountAsync(client, -1, "/adm/secstore/store/glenda", MountFlags.Replace | MountFlags.Create, string.Empty);
        await h.SecstoredAsync(17412);
        SConn c = await h.LoginAsync(17412);
        await c.ReadstrAsync();
        await c.WriteAsync("PUT f");
        await c.WriteAsync("5");
        await c.WriteAsync("abcde");
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains("write error on f: ", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGetCutShortByAReadErrorFailsAfterItsSize()
    {
        await h.GlendaAsync();
        await using var server = new FileServerHarness { ReadRefusal = "disk on fire" };
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("f", "hello"u8.ToArray()));
        (int client, int served) = await h.Init.PipeAsync();
        server.Serve(h.Init, served);
        await h.Init.MountAsync(client, -1, "/adm/secstore/store/glenda", MountFlags.Replace, string.Empty);
        await h.SecstoredAsync(17413);
        SConn c = await h.LoginAsync(17413);
        await c.ReadstrAsync();
        await c.WriteAsync("GET f");
        Assert.Equal((1, "5"), await c.ReadstrAsync());
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains("read error on f: disk on fire", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemoveIsDoneWithoutAnAnswer()
    {
        await h.GlendaAsync();
        await h.WriteFileAsync("/adm/secstore/store/glenda/f", "x"u8.ToArray());
        await h.SecstoredAsync(17414);
        SConn c = await h.LoginAsync(17414);
        await c.ReadstrAsync();
        await c.WriteAsync("RM f");
        await c.WriteAsync("GET f");
        Assert.Equal((2, "-1"), await c.ReadstrAsync());
    }

    [Theory]
    [InlineData("RM sub", "can't remove a directory")]
    [InlineData("RM nonesuch", "remove failed: ")]
    public async Task ARemoveOfADirectoryOrAMissingFileFailsWithWhy(string op, string why)
    {
        SConn c = await LoggedInAsync(17415);
        await h.MkdirAsync("/adm/secstore/store/glenda/sub");
        await c.WriteAsync(op);
        Assert.StartsWith(why, (await c.ReadstrAsync()).S, StringComparison.Ordinal);
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains($"failed RM glenda/{op[3..]}", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemoveOfANameTheStoreRefusesFails()
    {
        SConn c = await LoggedInAsync(17416);
        await c.WriteAsync("RM ..");
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
        Assert.Contains("failed RM glenda/<nil>", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGetOfANameTheStoreRefusesFails()
    {
        SConn c = await LoggedInAsync(17417);
        await c.WriteAsync("GET a\tb");
        Assert.Equal((-1, "operation failed"), await c.ReadstrAsync());
    }

    [Fact]
    public async Task AnOperationIsCutAtItsNewline()
    {
        await h.GlendaAsync();
        await h.WriteFileAsync("/adm/secstore/store/glenda/f", "x"u8.ToArray());
        await h.SecstoredAsync(17418);
        SConn c = await h.LoginAsync(17418);
        await c.ReadstrAsync();
        await c.WriteAsync("GET f\nmore");
        Assert.Equal((1, "1"), await c.ReadstrAsync());
        Assert.Contains("[glenda] GET f\n", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChpassSetsTheAccountsHiFromTheNextRecord()
    {
        SConn c = await LoggedInAsync(17419);
        await c.WriteAsync("CHPASS");
        await c.WriteAsync(Pak.Hi("glenda", "newsecretpw").HexHi);
        await c.WriteAsync("BYE");
        await WaitAsync(async () => (await h.FileAsync("/adm/secstore/who/glenda")).Contains(Pak.Hi("glenda", "newsecretpw").HexHi, StringComparison.Ordinal));
        Assert.Equal("OK", (await (await h.LoginAsync(17419, "glenda", "newsecretpw")).ReadstrAsync()).S);
        Assert.Contains(" AUTH glenda\n", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChpassWithoutAHiIsAProtocolBotch()
    {
        SConn c = await LoggedInAsync(17420);
        await c.WriteAsync("CHPASS");
        await c.WriterrAsync("gone");
        Assert.Equal((-1, "protocol botch while setting PAK"), await c.ReadstrAsync());
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync());
        Assert.Contains("protocol botch CHPASS for glenda", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChpassTriesFourTimesToWriteTheAccountThenGivesUp()
    {
        SConn c = await LoggedInAsync(17421);
        await h.Init.RemoveAsync("/adm/secstore/who/glenda");
        await h.Init.RemoveAsync("/adm/secstore/who");
        await c.WriteAsync("CHPASS");
        await c.WriteAsync(Pak.Hi("glenda", "newsecretpw").HexHi);
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync());
        for (int i = 0; i < 4; i++)
        {
            Assert.Contains($"password change failed for glenda ({i}): can't open PW file for glenda", await h.LogsAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnOperationItDoesNotKnowEndsTheConnection()
    {
        SConn c = await LoggedInAsync(17422);
        await c.WriteAsync("LIST");
        Assert.Equal((-1, "unrecognized operation"), await c.ReadstrAsync());
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync());
        Assert.DoesNotContain("closed connection without saying goodbye", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClientThatHangsUpWithoutByeIsLogged()
    {
        SConn c = await LoggedInAsync(17423);
        await c.FreeAsync();
        await WaitAsync(async () => (await h.LogsAsync()).Contains("glenda closed connection without saying goodbye", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFirstMessageItCannotReadIsAnswered()
    {
        await h.GlendaAsync();
        await h.SecstoredAsync(17424);
        int fd = await Net.DialAsync(h.Init, "tcp!127.0.0.1!17424");
        await h.Init.WriteAsync(fd, new byte[] { 0, 0 });
        var c = new SConn(h.Init, fd);
        Assert.Equal((-1, "can't read your first message"), await c.ReadstrAsync());
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync());
    }

    [Fact]
    public async Task AFirstMessageItCannotReadIsReportedOnStandardError()
    {
        await h.GlendaAsync();
        int err = await h.Init.CreateAsync("/tmp/err", NinePConstants.OWRITE, 0b110_110_110);
        await h.Init.DupAsync(err, 2);
        await h.Init.CloseAsync(err);
        await h.SecstoredAsync(17489);
        int fd = await Net.DialAsync(h.Init, "tcp!127.0.0.1!17489");
        await h.Init.WriteAsync(fd, new byte[] { 0, 0 });
        Assert.Equal((-1, "can't read your first message"), await new SConn(h.Init, fd).ReadstrAsync());
        Assert.StartsWith("secstored: remote: ", await h.FileAsync("/tmp/err"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedLoginIsLoggedAsDenied()
    {
        await h.GlendaAsync();
        await h.SecstoredAsync(17425);
        var c = new SConn(h.Init, await Net.DialAsync(h.Init, "tcp!127.0.0.1!17425"));
        Assert.Equal(-1, (await Pak.ClientAsync(h.Init, "secstore", false, c, "glenda", "wrongpw")).Rc);
        Assert.Equal((-1, "couldn't read verifier"), await c.ReadstrAsync());
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync());
        await WaitAsync(async () => (await h.LogsAsync()).Contains("secstore denied for glenda", StringComparison.Ordinal));
        Assert.DoesNotContain("AUTH", await h.LogsAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", await h.LogsAsync(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, h.Console.Text);
        Assert.Contains("secstore from 127.0.0.1!", await h.LogsAsync(), StringComparison.Ordinal);
        Assert.Contains("ANNOUNCE /net/tcp!127.0.0.1!17425", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("STA12345", "no STA from glenda")]
    [InlineData("STB1234567", "no STA from glenda")]
    [InlineData("STA123456789a", "secureidcheck denied glenda because authentication failed")]
    [InlineData("STAa234567", "secureidcheck denied glenda because authentication failed")]
    [InlineData("STA1234567", "secureidcheck: nil radius secret: no radius secret")]
    public async Task AnAccountNeedingStaIsAskedForItAndRefusedWithoutRadius(string response, string why)
    {
        await h.AccountAsync("glenda", $"exp\t0\nSTA\nPAK-Hi\t{GlendaHi}\n");
        await h.SecstoredAsync(17426);
        SConn c = await h.LoginAsync(17426);
        Assert.Equal((3, "STA"), await c.ReadstrAsync());
        await c.WriteAsync(response);
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync());
        await WaitAsync(async () => (await h.LogsAsync()).Contains(why, StringComparison.Ordinal));

        // A response refused for its form is not taken on to the RADIUS check.
        string logs = await h.LogsAsync() + await h.FileAsync("/sys/log/auth");
        if (why.StartsWith("no STA", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("secureidcheck", logs, StringComparison.Ordinal);
        }
        else if (why.EndsWith("because authentication failed", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("radius", logs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithARadiusSecretStaIsStillRefusedForWantOfRadius()
    {
        await h.MkdirAsync("/lib/ndb");
        await h.WriteFileAsync("/lib/ndb/local", "radius=lra-radius secret=shh\n"u8.ToArray());
        await h.GlendaAsync();
        await h.SecstoredAsync(17427, "-R");
        SConn c = await h.LoginAsync(17427);
        Assert.Equal((3, "STA"), await c.ReadstrAsync());
        await c.WriteAsync("STA1234567890");
        Assert.Equal((-1, "connection read error"), await c.ReadstrAsync());
        await WaitAsync(async () => (await h.LogsAsync()).Contains("secureidcheck: no RADIUS for glenda", StringComparison.Ordinal));
        Assert.Contains("no /lib/ndb/auth", await h.LogsAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("no /lib/ndb/local", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAnAuthDatabaseBothAreRead()
    {
        await h.MkdirAsync("/lib/ndb");
        await h.WriteFileAsync("/lib/ndb/auth", "radius=lra-radius secret=shh\n"u8.ToArray());
        await h.GlendaAsync();
        await h.SecstoredAsync(17428, "-R");
        SConn c = await h.LoginAsync(17428);
        await c.ReadstrAsync();
        await c.WriteAsync("STA1234567890");
        await WaitAsync(async () => (await h.LogsAsync()).Contains("secureidcheck: no RADIUS for glenda", StringComparison.Ordinal));
        Assert.Contains("no /lib/ndb/local", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SNamesTheServerToTheClient()
    {
        await h.GlendaAsync();
        await h.SecstoredAsync(17429, "-S", "the store");
        var c = new SConn(h.Init, await Net.DialAsync(h.Init, "tcp!127.0.0.1!17429"));
        Assert.Equal((0, "the store"), await Pak.ClientAsync(h.Init, "secstore", false, c, "glenda", "secretpw"));
    }

    [Fact]
    public async Task VServesWithoutForkingAway()
    {
        await h.GlendaAsync();
        long pid = h.Init.Fork(RforkFlags.Proc | RforkFlags.Fdg, child => child.ExecAsync("/bin/auth/secstored", ["auth/secstored", "-v", "-s", "tcp!127.0.0.1!17430"]));
        await WaitAsync(async () =>
        {
            try
            {
                await h.Init.CloseAsync(await Net.DialAsync(h.Init, "tcp!127.0.0.1!17430"));
                return true;
            }
            catch (SyscallException)
            {
                return false;
            }
        });
        Assert.Contains(h.Kernel.Processes.Values, p => p.Pid == pid);
    }

    [Theory]
    [InlineData("-x", "/net", "")]
    [InlineData("-x", ".alt", "/net.alt/tcp!127.0.0.1!17431: ")]
    [InlineData("-x", "/nonesuch", "/nonesuch/tcp!127.0.0.1!17431: ")]
    public async Task XNamesTheNetworksMountPoint(string flag, string net, string why)
    {
        (string output, string status) = await h.RunAsync(string.Empty, "auth/secstored", "-v", flag, net, "-s", "tcp!127.0.0.1!17431", "-z");
        Assert.StartsWith("usage: secstored", output, StringComparison.Ordinal);
        Assert.Equal("usage", status);
        if (why.Length == 0)
        {
            return;
        }

        (output, status) = await h.RunAsync(string.Empty, "auth/secstored", "-v", flag, net, "-s", "tcp!127.0.0.1!17431");
        Assert.StartsWith($"auth/secstored: {why}", output, StringComparison.Ordinal);
        Assert.StartsWith(why, status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-z")]
    [InlineData("-s")]
    [InlineData("-S")]
    [InlineData("-x")]
    public async Task AFlagItDoesNotKnowOrWithoutItsArgumentIsUsage(string flag)
        => Assert.Equal(("usage: secstored [-R] [-S servername] [-s tcp!*!5356] [-v] [-x netmtpt]\n", "usage"), await h.RunAsync(string.Empty, "auth/secstored", flag));

    [Fact]
    public async Task AnAddressItCannotAnnounceIsFatal()
    {
        (string output, string status) = await h.RunAsync(string.Empty, "auth/secstored", "-v", "-s", "bogus!x!y");
        Assert.StartsWith("auth/secstored: /net/bogus!x!y: ", output, StringComparison.Ordinal);
        Assert.StartsWith("/net/bogus!x!y: ", status, StringComparison.Ordinal);
    }

    private static async Task WaitAsync(Func<Task<bool>> done)
    {
        for (int i = 0; i < 300; i++)
        {
            if (await done())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(await done());
    }

    // glenda's account, secstored on the port, and a connection logged in to it, past its OK.
    private async Task<SConn> LoggedInAsync(int port)
    {
        await h.GlendaAsync();
        if (!h.Kernel.Processes.Values.Any(p => p.Text == "secstored"))
        {
            await h.SecstoredAsync(port);
        }

        SConn c = await h.LoginAsync(port);
        Assert.Equal((2, "OK"), await c.ReadstrAsync());
        return c;
    }
}
