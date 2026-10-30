using System.Security.Cryptography;
using System.Text;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Namespaces;
using Xunit;
using static NinePSharp.Fog.Rc.Tests.Support.SecstoreHarness;

namespace NinePSharp.Fog.Rc.Tests;

// auth/secstore against Fog's secstored, or a store the test plays where secstored would not
// answer so: its flags, each source of its password, the SecurID step, each answer to GET it is
// given, files of each size across its chunks, and each turn of a password change.
public sealed class SecstoreClientTests : IAsyncLifetime
{
    private const string Usage = "usage: secstore [-cinv] [-[gG] getfile] [-p putfile] [-r rmfile] [-s tcp!server!5356] [-u user]\n";
    private SecstoreHarness h = null!;

    public async Task InitializeAsync() => h = await StartAsync();

    public async Task DisposeAsync() => await h.DisposeAsync();

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("x")]
    [InlineData("-z")]
    [InlineData("-s")]
    [InlineData("-u")]
    [InlineData("-g")]
    [InlineData("-G")]
    [InlineData("-p")]
    [InlineData("-r")]
    public async Task AnArgumentAFlagItDoesNotKnowOrOneWithoutItsArgumentIsUsage(string arg)
        => Assert.Equal((Usage, "usage"), await h.RunAsync(string.Empty, "auth/secstore", arg));

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("-g", "too many gfiles")]
    [InlineData("-G", "too many gfiles")]
    [InlineData("-p", "too many pfiles")]
    [InlineData("-r", "too many rfiles")]
    public async Task AHundredFilesOfAKindAreAllItTakes(string flag, string status)
    {
        string[] argv = ["auth/secstore", .. Enumerable.Range(0, 101).SelectMany(i => new[] { flag, $"f{i}" })];
        Assert.Equal((string.Empty, status), await h.RunAsync(string.Empty, argv));
        Assert.NotEqual(status, (await h.RunAsync(string.Empty, ["auth/secstore", .. argv[1..201], "-s", "tcp!127.0.0.1!1"])).Status);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("-g")]
    [InlineData("-p")]
    [InlineData("-r")]
    public async Task APasswordChangeTakesNoFiles(string flag)
        => Assert.Equal(("secstore: Get, put, and remove invalid with password change.\n", "usage"), await h.RunAsync(string.Empty, "auth/secstore", "-c", flag, "f"));

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task WithoutSTheStoreIsSecstoresAndWithoutThatAuths()
    {
        (string output, string status) = await h.RunAsync("secretpw\n", "auth/secstore", "-iv", "-G", "f");
        Assert.Equal(("dialing $auth\n", "dial failed"), (output[..(output.IndexOf('\n') + 1)], status));
        Assert.Contains("secstore: can't dial $auth: ", output, StringComparison.Ordinal);
        await StoreAsync(17450, "f", "kept\n");
        await h.WriteFileAsync("/env/secstore", "tcp!127.0.0.1!17450"u8.ToArray());
        Assert.Equal(("secstore\nkept\n", string.Empty), await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-G", "f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task SecstoresStoresAreTriedInTurn()
    {
        await StoreAsync(17451, "f", "kept\n");
        await h.WriteFileAsync("/env/secstore", "tcp!127.0.0.1!1 tcp!127.0.0.1!17451"u8.ToArray());
        Assert.Equal(("dialing tcp!127.0.0.1!1\ndialing tcp!127.0.0.1!17451\nfoxtrot\nsecstore\nget f\nkept\n", string.Empty), await h.RunAsync("secretpw\n", "auth/secstore", "-iv", "-G", "f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task EachStoreGivenIsTriedInTurnAndTheSeventhIsTheLastGiven()
    {
        string[] stores = [.. Enumerable.Range(1, 8).SelectMany(i => new[] { "-s", $"tcp!127.0.0.1!{i}" })];
        (string output, string status) = await h.RunAsync("secretpw\n", ["auth/secstore", "-iv", .. stores, "-G", "f"]);
        Assert.Equal("dial failed", status);
        Assert.Equal("dialing tcp!127.0.0.1!1\ndialing tcp!127.0.0.1!2\ndialing tcp!127.0.0.1!3\ndialing tcp!127.0.0.1!4\ndialing tcp!127.0.0.1!5\ndialing tcp!127.0.0.1!6\ndialing tcp!127.0.0.1!8\n", output[..output.LastIndexOf("dialing", StringComparison.Ordinal)] + "dialing tcp!127.0.0.1!8\n");
        Assert.Contains("secstore: can't dial tcp!127.0.0.1!8: ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("!7", output, StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AStoreNamedWithoutNetworkOrServiceIsDialledOnTcpAt5356()
    {
        (string output, string status) = await h.RunAsync("secretpw\n", "auth/secstore", "-iv", "-s", "127.0.0.1", "-G", "f");
        Assert.Equal(("dialing 127.0.0.1\nsecstore: can't dial 127.0.0.1: connection refused\n", "dial failed"), (output, status));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ADialThatFailsQuietlyWithoutV()
        => Assert.Equal((string.Empty, "dial failed"), await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!1", "-G", "f"));

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ThePasswordIsAskedForOnTheConsoleWithoutI()
    {
        await StoreAsync(17452, "f", "kept\n");
        h.Console.Type("secretpw\n");
        Assert.Equal(("secstore\nkept\n", string.Empty), await h.RunAsync(string.Empty, "auth/secstore", "-s", "tcp!127.0.0.1!17452", "-G", "f"));
        Assert.Contains("secstore password: ", h.Console.Text, StringComparison.Ordinal);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("", "secstore: password input aborted\n", "password input aborted")]
    [InlineData("\n", "secstore: null password, skipping secstore login\n", "no password")]
    public async Task APasswordNotTypedOrEmptyEndsTheLogin(string typed, string output, string status)
    {
        await StoreAsync(17453, "f", "kept\n");
        h.Console.Type(typed);
        Assert.Equal((output, status), await h.RunAsync(string.Empty, "auth/secstore", "-s", "tcp!127.0.0.1!17453", "-G", "f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APasswordOfSixtyFourBytesIsTooLongAndOneOfSixtyThreeIsTried()
    {
        await StoreAsync(17454, "f", "kept\n");
        h.Console.Type($"{new string('x', 64)}\n");
        Assert.Equal(("secstore: password too long, skipping secstore login\n", "password too long"), await h.RunAsync(string.Empty, "auth/secstore", "-s", "tcp!127.0.0.1!17454", "-G", "f"));
        h.Console.Type($"{new string('x', 63)}\n");
        Assert.Equal(("secstore: password input aborted\n", "password input aborted"), await h.RunAsync(string.Empty, "auth/secstore", "-s", "tcp!127.0.0.1!17454", "-G", "f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AWrongPasswordIsAskedForAgainAndAfterThreeTheWayOutIsSaid()
    {
        await StoreAsync(17455, "f", "kept\n");
        h.Console.Type("wrong1\nwrong2\nwrong3\nwrong4\n\n");
        Assert.Equal(("Enter an empty password to quit.\nsecstore: null password, skipping secstore login\n", "no password"), await h.RunAsync(string.Empty, "auth/secstore", "-s", "tcp!127.0.0.1!17455", "-G", "f"));
        h.Console.Type("wrong1\nsecretpw\n");
        Assert.Equal(("secstore\nkept\n", string.Empty), await h.RunAsync(string.Empty, "auth/secstore", "-s", "tcp!127.0.0.1!17455", "-G", "f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task IWithNothingOnStandardInputHasNoPassword()
        => Assert.Equal((string.Empty, "no password on standard input"), await h.RunAsync(string.Empty, "auth/secstore", "-i", "-s", "tcp!127.0.0.1!1"));

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task IWithAnEmptyLineHasANullPassword()
    {
        await h.GlendaAsync();
        await h.SecstoredAsync(17456);
        Assert.Equal(("secstore: null password, skipping secstore login\n", "no password"), await h.RunAsync("\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17456"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APasswordIsCutToSixtyThreeBytes()
    {
        await h.AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{Pak.Hi("glenda", new string('p', 63)).HexHi}\n");
        await h.SecstoredAsync(17457);
        Assert.Equal(("secstore\n", string.Empty), await h.RunAsync(new string('p', 70), "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17457"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task NTakesThePasswordFromTheNvramsConfig()
    {
        await using SecstoreHarness withNvram = await StartAsync(Nvram("secretpw"));
        await withNvram.GlendaAsync();
        await withNvram.WriteFileAsync("/adm/secstore/store/glenda/f", await EncryptedAsync("kept\n", "secretpw"));
        await withNvram.SecstoredAsync(17458);
        Assert.Equal(("secstore\nkept\n", string.Empty), await withNvram.RunAsync(string.Empty, "auth/secstore", "-n", "-s", "tcp!127.0.0.1!17458", "-G", "f"));
        await using SecstoreHarness wrong = await StartAsync(Nvram("wrongpw"));
        await wrong.GlendaAsync();
        await wrong.SecstoredAsync(17459);
        Assert.Equal((string.Empty, "invalid password in nvram"), await wrong.RunAsync(string.Empty, "auth/secstore", "-n", "-s", "tcp!127.0.0.1!17459"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task NWithoutAnNvramFails()
    {
        Assert.Equal((string.Empty, "readnvram failed"), await h.RunAsync(string.Empty, "auth/secstore", "-n", "-s", "tcp!127.0.0.1!1"));
        (string output, string status) = await h.RunAsync(string.Empty, "auth/secstore", "-nv", "-s", "tcp!127.0.0.1!1");
        Assert.Equal("readnvram failed", status);
        Assert.StartsWith("secstore: readnvram: ", output, StringComparison.Ordinal);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("secretpw\n1234\n", false, "STA1234", "4\n")]
    [InlineData("secretpw\n", false, "STA", "0\n")]
    [InlineData("", true, "STA5678", "4\n")]
    public async Task AStoreAskingForStaIsSentThePinFromStandardInputOrTheConsole(string stdin, bool console, string sent, string count)
    {
        if (console)
        {
            h.Console.Type("secretpw\n5678\n");
        }

        var got = new TaskCompletionSource<string>();
        Task store = await FakeStoreAsync(17460, async c =>
        {
            await c.WriteAsync("STA");
            got.SetResult((await c.ReadstrAsync()).S);
            await c.WriteAsync("OK");
            await c.ReadstrAsync();
        });
        (string output, string status) = await h.RunAsync(stdin, "auth/secstore", console ? "-v" : "-iv", "-s", "tcp!127.0.0.1!17460");
        Assert.Equal(sent, await got.Task);
        Assert.Equal(string.Empty, status);
        Assert.EndsWith($"secstore\n{count}", output, StringComparison.Ordinal);
        await store;
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("no", "secstore: no\nauth/secstore: authentication failed\n")]
    [InlineData(null, "auth/secstore: authentication failed\n")]
    public async Task AStoreThatDoesNotSayOkFailsTheLogin(string? answer, string output)
    {
        Task store = await FakeStoreAsync(17461, async c =>
        {
            if (answer is not null)
            {
                await c.WriteAsync(answer);
            }
        });
        Assert.Equal(($"secstore\n{output}", "authentication failed"), await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17461"));
        await store;
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("-3", "secstore: implausible filesize for f\n")]
    [InlineData("-2", "secstore: GET refused for f\n")]
    [InlineData("-1", "secstore: remote file f does not exist\n")]
    [InlineData("!no store", "secstore: remote: no store\n")]
    public async Task AGetAnsweredWithoutASizeFails(string answer, string output)
    {
        Task store = await FakeStoreAsync(17462, async c =>
        {
            await c.WriteAsync("OK");
            await c.ReadstrAsync();
            await c.WriteAsync(answer);
        });
        Assert.Equal(($"secstore\n{output}auth/secstore: cmd failed\n", "cmd failed"), await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17462", "-G", "f"));
        await store;
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData(new[] { 10 }, 26, "secstore: no IV in file\n")]
    [InlineData(new int[0], 50, "secstore: empty file chunk n=-1 nr=0 len=50: \n")]
    [InlineData(new[] { 16, 5 }, 21, "secstore: decrypted file failed to authenticate!\n")]
    public async Task AFileWithoutItsIvItsWholeLengthOrItsCheckFails(int[] chunks, int len, string output)
    {
        Task store = await FakeStoreAsync(17463, async c =>
        {
            await c.WriteAsync("OK");
            await c.ReadstrAsync();
            await c.WriteAsync(len.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (int n in chunks)
            {
                await c.WriteAsync(new byte[n], n);
            }
        });
        Assert.Equal(($"secstore\n{output}auth/secstore: cmd failed\n", "cmd failed"), await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17463", "-G", "f"));
        await store;
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AListingCutShortFails()
    {
        Task store = await FakeStoreAsync(17464, async c =>
        {
            await c.WriteAsync("OK");
            await c.ReadstrAsync();
            await c.WriteAsync("20");
            await c.WriteAsync("half");
        });
        Assert.Equal(("secstore\nhalfsecstore: empty file chunk\nauth/secstore: cmd failed\n", "cmd failed"), await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17464", "-g", "."));
        await store;
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4079)]
    [InlineData(4080)]
    [InlineData(4081)]
    [InlineData(8169)]
    [InlineData(8170)]
    [InlineData(8176)]
    [InlineData(12500)]
    public async Task AFileOfAnySizeIsPutAndGotBackAcrossItsChunks(int size)
    {
        await h.GlendaAsync();
        await h.SecstoredAsync(17465);
        byte[] data = RandomNumberGenerator.GetBytes(size);
        await h.WriteFileAsync("/tmp/f", data);
        Assert.Equal(($"secstore\nsecstore: saved {size} bytes\n", string.Empty), await h.RunInAsync("/tmp", "secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17465", "-p", "f"));
        for (int i = 0; i < 300 && !(await h.ExistsAsync("/adm/secstore/store/glenda/f") && (await h.BytesAsync("/adm/secstore/store/glenda/f")).Length == size + 32); i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(size + 32, (await h.BytesAsync("/adm/secstore/store/glenda/f")).Length);
        await h.Init.RemoveAsync("/tmp/f");
        Assert.Equal(("secstore\n", string.Empty), await h.RunInAsync("/tmp", "secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17465", "-g", "f"));
        Assert.Equal(data, await h.BytesAsync("/tmp/f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task GSplitsTheFileIntoLinesAndGWritesItWhole()
    {
        await StoreAsync(17466, "f", "one\ntwo\nthree");
        Assert.Equal(("secstore\none\ntwo\nthree", string.Empty), await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17466", "-G", "f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task GOfDotPrintsTheListing()
    {
        await StoreAsync(17467, "f", "kept\n");
        (string output, _) = await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17467", "-g", ".");
        Assert.Matches(@"^secstore\nf\t37 Nov 14 22:13:20 GMT 2023 [A-Za-z0-9+/]{27}=\n$", output);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AFileGotWhereItCannotBeMadeOrWrittenFails()
    {
        await StoreAsync(17468, "f", "kept\n");
        (string output, string status) = await h.RunInAsync("/proc", "secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17468", "-g", "f");
        Assert.StartsWith("secstore\nsecstore: can't open f: ", output, StringComparison.Ordinal);
        Assert.Equal("cmd failed", status);
        await using var server = new FileServerHarness { WriteLimit = 2 };
        (int client, int served) = await h.Init.PipeAsync();
        server.Serve(h.Init, served);
        await h.MkdirAsync("/n");
        await h.Init.MountAsync(client, -1, "/n", MountFlags.Replace | MountFlags.Create, string.Empty);
        Assert.Equal(("secstore\nsecstore: write error on fauth/secstore: cmd failed\n", "cmd failed"), await h.RunInAsync("/n", "secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17468", "-g", "f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APutOfAFileItCannotOpenOrReadOrThatIsTooBigFails()
    {
        await h.GlendaAsync();
        await h.SecstoredAsync(17469);
        (string output, string status) = await h.RunInAsync("/tmp", "secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17469", "-p", "nonesuch");
        Assert.StartsWith("secstore\nsecstore: can't open nonesuch: ", output, StringComparison.Ordinal);
        Assert.Equal("cmd failed", status);
        await h.WriteFileAsync("/tmp/big", new byte[(10 * 1024 * 1024) + 1]);
        Assert.Equal(("secstore\nsecstore: implausible filesize 10485761 for big\nauth/secstore: cmd failed\n", "cmd failed"), await h.RunInAsync("/tmp", "secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17469", "-p", "big"));
        await using var server = new FileServerHarness { ReadRefusal = "disk on fire" };
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("f", "hello"u8.ToArray()));
        (int client, int served) = await h.Init.PipeAsync();
        server.Serve(h.Init, served);
        await h.MkdirAsync("/n");
        await h.Init.MountAsync(client, -1, "/n", MountFlags.Replace, string.Empty);
        (output, status) = await h.RunInAsync("/n", "secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17469", "-p", "f");
        Assert.Equal(("secstore\nsecstore: read error on f: disk on fire\nauth/secstore: cmd failed\n", "cmd failed"), (output, status));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task VNamesEachFileGotPutAndRemoved()
    {
        await StoreAsync(17470, "f", "kept\n");
        await h.WriteFileAsync("/tmp/g", "x"u8.ToArray());
        Assert.Equal(("dialing tcp!127.0.0.1!17470\nfoxtrot\nsecstore\nget f\nput g\nsecstore: saved 1 bytes\nrm  f\n", string.Empty), await h.RunInAsync("/tmp", "secretpw\n", "auth/secstore", "-iv", "-s", "tcp!127.0.0.1!17470", "-g", "f", "-p", "g", "-r", "f"));
        Assert.Equal("kept\n", await h.FileAsync("/tmp/f"));
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("secretpw\n", "", "auth/secstore: cmd failed\n")]
    [InlineData("secretpw\n\n", "!password change aborted\n", "auth/secstore: cmd failed\n")]
    [InlineData("secretpw\nnewsecretpw\n", "secstore: input aborted\n", "auth/secstore: cmd failed\n")]
    [InlineData("secretpw\nnewsecretpw\nnewsecretpx\n", "secstore: passwords didn't match\n", "auth/secstore: cmd failed\n")]
    public async Task APasswordChangeNotTypedOrMistypedFails(string typed, string output, string fatal)
    {
        await StoreAsync(17471, "f", "kept\n");
        h.Console.Type(typed);
        Assert.Equal(($"secstore\n{output}{fatal}", "cmd failed"), await h.RunAsync(string.Empty, "auth/secstore", "-c", "-s", "tcp!127.0.0.1!17471"));
        Assert.Contains("new password for glenda: ", h.Console.Text, StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APasswordChangeAsksAgainForOneTooShort()
    {
        await StoreAsync(17472, "f", "kept\n");
        h.Console.Type("secretpw\nshort\nnewsecretpw\nnewsecretpw\n");
        Assert.Equal(("secstore\n!password must be at least 7 characters\nsecstore: reencrypting 'f'\nsecstore: saved 5 bytes\n", string.Empty), await h.RunAsync(string.Empty, "auth/secstore", "-c", "-s", "tcp!127.0.0.1!17472"));
        Assert.Equal(("secstore\nkept\n", string.Empty), await h.RunAsync("newsecretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17472", "-G", "f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APasswordChangeGoesOnPastAFileItCannotGetAndStopsAtABlankLine()
    {
        var put = new TaskCompletionSource<string>();
        Task store = await FakeStoreAsync(17473, async c =>
        {
            await c.WriteAsync("OK");
            Assert.Equal("CHPASS", (await c.ReadstrAsync()).S);
            await c.ReadstrAsync();
            Assert.Equal("GET .", (await c.ReadstrAsync()).S);
            string listing = "bad\t1 x\ngood\t1 x\n \nafter\t1 x\n";
            await c.WriteAsync(listing.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await c.WriteAsync(listing);
            Assert.Equal("GET bad", (await c.ReadstrAsync()).S);
            await c.WriteAsync("-1");
            Assert.Equal("GET good", (await c.ReadstrAsync()).S);
            byte[] file = await EncryptedAsync("g", "secretpw");
            await c.WriteAsync(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await c.WriteAsync(file, file.Length);
            put.SetResult((await c.ReadstrAsync()).S);
            await c.ReadstrAsync();
            await c.ReadAsync(new byte[SConn.Maxmsg + 1], SConn.Maxmsg);
            Assert.Equal("BYE", (await c.ReadstrAsync()).S);
        });
        h.Console.Type("secretpw\nnewsecretpw\nnewsecretpw\n");
        Assert.Equal(("secstore\nsecstore: reencrypting 'bad'\nsecstore: remote file bad does not exist\nsecstore: getfile of 'bad' failed\nsecstore: reencrypting 'good'\nsecstore: saved 1 bytes\n", string.Empty), await h.RunAsync(string.Empty, "auth/secstore", "-c", "-s", "tcp!127.0.0.1!17473"));
        Assert.Equal("PUT good", await put.Task);
        await store;
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APasswordChangeWhoseListingFailsFails()
    {
        Task store = await FakeStoreAsync(17474, async c =>
        {
            await c.WriteAsync("OK");
            await c.ReadstrAsync();
            await c.ReadstrAsync();
            await c.ReadstrAsync();
            await c.WriteAsync("-2");
        });
        h.Console.Type("secretpw\nnewsecretpw\nnewsecretpw\n");
        Assert.Equal(("secstore\nsecstore: GET refused for .\nsecstore: directory listing failed.\nauth/secstore: cmd failed\n", "cmd failed"), await h.RunAsync(string.Empty, "auth/secstore", "-c", "-s", "tcp!127.0.0.1!17474"));
        await store;
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APasswordOfSixtyFourBytesIsCutToSixtyThree()
    {
        await h.AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{Pak.Hi("glenda", new string('p', 63)).HexHi}\n");
        await h.SecstoredAsync(17477);
        Assert.Equal(("secstore\n", string.Empty), await h.RunAsync(new string('p', 64), "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17477"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AListingOfAnAccountWithNoFilesIsEmpty()
    {
        await h.GlendaAsync();
        await h.SecstoredAsync(17478);
        Assert.Equal(("secstore\n", string.Empty), await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17478", "-g", "."));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AFileEncryptedUnderAnotherPasswordFailsItsCheck()
    {
        await h.GlendaAsync();
        await h.WriteFileAsync("/adm/secstore/store/glenda/f", await EncryptedAsync("kept\n", "otherpw"));
        await h.SecstoredAsync(17479);
        Assert.Equal(("secstore\nsecstore: decrypted file failed to authenticate!\nauth/secstore: cmd failed\n", "cmd failed"), await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17479", "-G", "f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AFileIsStoredAsItsIvAndTheIvTheFileAndItsCheckEncrypted()
    {
        await h.GlendaAsync();
        await h.SecstoredAsync(17480);
        await h.WriteFileAsync("/tmp/f", "hello there\n"u8.ToArray());
        Authsrv.Random.Value = b => b.Fill(7);
        try
        {
            await h.RunInAsync("/tmp", "secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17480", "-p", "f");
        }
        finally
        {
            Authsrv.Random.Value = null;
        }

        byte[] iv = Enumerable.Repeat((byte)7, 16).ToArray();
        byte[] expected = [.. iv, .. "hello there\n"u8, .. System.Text.Encoding.ASCII.GetBytes(new string('X', 16))];
        using (var aes = new Aesstate(SHA1.HashData([.. "aescbc file"u8, .. "secretpw"u8])[..16], iv))
        {
            aes.CbcEncrypt(expected);
        }

        await WaitForAsync(async () => await h.ExistsAsync("/adm/secstore/store/glenda/f") && (await h.BytesAsync("/adm/secstore/store/glenda/f")).Length == expected.Length);
        Assert.Equal(expected, await h.BytesAsync("/adm/secstore/store/glenda/f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task TheClientSaysGoodbye()
    {
        await StoreAsync(17482, "f", "kept\n");
        await h.RunAsync("secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17482", "-G", "f");
        await WaitForAsync(async () => (await h.LogsAsync()).Contains("[glenda] BYE", StringComparison.Ordinal));
        Assert.DoesNotContain("without saying goodbye", await h.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task GWritesTheFileALineAtATime()
    {
        await StoreAsync(17483, "f", "\none\ntwo\nthree");
        (int r, int w) = await h.Init.PipeAsync();
        (int inr, int inw) = await h.Init.PipeAsync();
        h.Init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            await child.DupAsync(inr, 0);
            await child.DupAsync(w, 1);
            foreach (int fd in new[] { r, w, inr, inw })
            {
                await child.CloseAsync(fd);
            }

            await child.ExecAsync("/bin/auth/secstore", ["auth/secstore", "-i", "-s", "tcp!127.0.0.1!17483", "-G", "f"]);
        });
        await h.Init.CloseAsync(w);
        await h.Init.CloseAsync(inr);
        await h.Init.WriteAsync(inw, "secretpw\n"u8.ToArray());
        await h.Init.CloseAsync(inw);
        var writes = new List<string>();
        for (ReadOnlyMemory<byte> b; !(b = await h.Init.ReadAsync(r, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(30))).IsEmpty;)
        {
            writes.Add(System.Text.Encoding.UTF8.GetString(b.Span));
        }

        Assert.Equal(["\n", "one\n", "two\n", "three"], writes);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData(4080)]
    [InlineData(8170)]
    [InlineData(9000)]
    public async Task APasswordChangeEncryptsAFileOfAnySizeAgain(int size)
    {
        await h.GlendaAsync();
        await h.SecstoredAsync(17484);
        byte[] data = RandomNumberGenerator.GetBytes(size);
        await h.WriteFileAsync("/tmp/f", data);
        await h.RunInAsync("/tmp", "secretpw\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17484", "-p", "f");
        await WaitForAsync(async () => await h.ExistsAsync("/adm/secstore/store/glenda/f") && (await h.BytesAsync("/adm/secstore/store/glenda/f")).Length == size + 32);
        h.Console.Type("secretpw\nseven77\nseven77\n");
        Assert.Equal(($"secstore\nsecstore: reencrypting 'f'\nsecstore: saved {size} bytes\n", string.Empty), await h.RunAsync(string.Empty, "auth/secstore", "-c", "-s", "tcp!127.0.0.1!17484"));
        Assert.Equal("secstore password: \nnew password for glenda: \nretype password: \n", h.Console.Text);
        await WaitForAsync(async () => (await h.FileAsync("/adm/secstore/who/glenda")).Contains(Pak.Hi("glenda", "seven77").HexHi, StringComparison.Ordinal));
        await h.Init.RemoveAsync("/tmp/f");
        await WaitForAsync(async () => (await h.LogsAsync()).Split("[glenda] BYE").Length == 3);
        Assert.Equal(("secstore\n", string.Empty), await h.RunInAsync("/tmp", "seven77\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17484", "-g", "f"));
        Assert.Equal(data, await h.BytesAsync("/tmp/f"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APasswordChangeTakesALineOfOneFieldAndNoneWithoutItsNewline()
    {
        var got = new List<string>();
        Task store = await FakeStoreAsync(17485, async c =>
        {
            await c.WriteAsync("OK");
            await c.ReadstrAsync();
            await c.ReadstrAsync();
            await c.ReadstrAsync();
            string listing = "one\nlast\t1 x";
            await c.WriteAsync(listing.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await c.WriteAsync(listing);
            for ((int n, string s) = await c.ReadstrAsync(); n > 0 && s != "BYE"; (n, s) = await c.ReadstrAsync())
            {
                got.Add(s);
                if (s.StartsWith("GET", StringComparison.Ordinal))
                {
                    await c.WriteAsync("-1");
                }
            }
        });
        h.Console.Type("secretpw\nseven77\nseven77\n");
        Assert.Equal(("secstore\nsecstore: reencrypting 'one'\nsecstore: remote file one does not exist\nsecstore: getfile of 'one' failed\n", string.Empty), await h.RunAsync(string.Empty, "auth/secstore", "-c", "-s", "tcp!127.0.0.1!17485"));
        await store;
        Assert.Equal(["GET one"], got);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("secretpw\n\nmore", "STA")]
    [InlineData("secretpw\n12\n34", "STA12")]
    public async Task ThePinIsTheSecondLineOfStandardInput(string stdin, string sent)
    {
        var got = new TaskCompletionSource<string>();
        Task store = await FakeStoreAsync(17486, async c =>
        {
            await c.WriteAsync("STA");
            got.SetResult((await c.ReadstrAsync()).S);
            await c.WriteAsync("OK");
            await c.ReadstrAsync();
        });
        Assert.Equal(string.Empty, (await h.RunAsync(stdin, "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17486")).Status);
        Assert.Equal(sent, await got.Task);
        await store;
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task StandardInputIsReadToMaxmsgLessTwo()
    {
        var got = new TaskCompletionSource<string>();
        Task store = await FakeStoreAsync(17487, async c =>
        {
            await c.WriteAsync("STA");
            got.SetResult((await c.ReadstrAsync()).S);
            await c.WriteAsync("OK");
            await c.ReadstrAsync();
        });
        await h.RunAsync($"secretpw\n{new string('x', 5000)}", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17487");
        Assert.Equal($"STA{new string('x', SConn.Maxmsg - 2 - 9)}", await got.Task);
        await store;
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task APinNotTypedIsSentEmpty()
    {
        var got = new TaskCompletionSource<string>();
        Task store = await FakeStoreAsync(17488, async c =>
        {
            await c.WriteAsync("STA");
            got.SetResult((await c.ReadstrAsync()).S);
            await c.WriteAsync("OK");
            await c.ReadstrAsync();
        });
        h.Console.Type("secretpw\n");
        Assert.Equal(string.Empty, (await h.RunAsync(string.Empty, "auth/secstore", "-s", "tcp!127.0.0.1!17488")).Status);
        Assert.Equal("STA", await got.Task);
        Assert.Equal("secstore password: \nSTA PIN+SecureID: \n", h.Console.Text);
        await store;
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task UNamesTheUser()
    {
        await h.AccountAsync("other", $"exp\t0\nPAK-Hi\t{Pak.Hi("other", "otherpass").HexHi}\n");
        await h.SecstoredAsync(17475);
        Assert.Equal(("secstore\n", string.Empty), await h.RunAsync("otherpass\n", "auth/secstore", "-i", "-u", "other", "-s", "tcp!127.0.0.1!17475"));
        Assert.Equal(("auth/secstore: error: accounts glenda and FICTITIOUS do not exist\n", "invalid password on standard input"), await h.RunAsync("otherpass\n", "auth/secstore", "-i", "-s", "tcp!127.0.0.1!17475"));
    }

    private static async Task WaitForAsync(Func<Task<bool>> done)
    {
        for (int i = 0; i < 300 && !await done(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(await done());
    }

    // secstore's encryption of a file: an IV, then AES-CBC of it and the file and its check.
    private static Task<byte[]> EncryptedAsync(string text, string password)
    {
        byte[] key = SHA1.HashData([.. "aescbc file"u8, .. Encoding.UTF8.GetBytes(password)])[..16];
        byte[] b = [.. new byte[16], .. Encoding.UTF8.GetBytes(text), .. Encoding.ASCII.GetBytes(new string('X', 16))];
        using var aes = new Aesstate(key, new byte[16]);
        aes.CbcEncrypt(b);
        return Task.FromResult(b);
    }

    // glenda's account, the file stored in it encrypted as secstore stores it, and secstored on
    // the port.
    private async Task StoreAsync(int port, string name, string text)
    {
        await h.GlendaAsync();
        await h.WriteFileAsync($"/adm/secstore/store/glenda/{name}", await EncryptedAsync(text, "secretpw"));
        await h.SecstoredAsync(port);
    }

    // A store the test plays on the port: it takes one call, logs glenda in, and gives the
    // script the connection.
    private async Task<Task> FakeStoreAsync(int port, Func<SConn, Task> script)
    {
        await h.GlendaAsync();
        (int ctl, string dir) = await Net.AnnounceAsync(h.Init, $"tcp!127.0.0.1!{port}");
        return Task.Run(async () =>
        {
            (int lcfd, string ldir) = await Net.ListenAsync(h.Init, dir);
            var c = new SConn(h.Init, await Net.AcceptAsync(h.Init, lcfd, ldir));
            (_, string first) = await c.ReadstrAsync();
            Assert.Equal(0, (await Pak.ServerAsync(h.Init, "store", c, "secstore", first)).Rc);
            await script(c);
            await c.FreeAsync();
            await h.Init.CloseAsync(lcfd);
            await h.Init.CloseAsync(ctl);
        });
    }
}
