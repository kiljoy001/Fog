using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;
using static NinePSharp.Fog.Rc.Tests.Support.SecstoreHarness;

namespace NinePSharp.Fog.Rc.Tests;

// auth/secuser and auth/aescbc on their own: secuser's prompts, what it refuses and the files it
// cannot make, and aescbc's keys, its formats at each size, and each way a file fails it.
public sealed class SecuserTests : IAsyncLifetime
{
    private const string Prompts = "expires [DDMMYYYY, default = 13112024]: Enabled or Disabled [default Enabled]: require STA? [default no]: comments [default = ]: change written\n";
    private SecstoreHarness h = null!;

    public async Task InitializeAsync() => h = await StartAsync();

    public async Task DisposeAsync() => await h.DisposeAsync();

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData]
    [InlineData("a", "b")]
    public async Task OneUserIsAllItTakes(params string[] args)
        => Assert.Equal(("usage: secuser [-v] <user>\n", "usage"), await h.RunAsync(string.Empty, ["auth/secuser", .. args]));

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task VSaysWhatItMakesAndCountsThePassword()
    {
        h.Console.Type("secretpw\nsecretpw\n\n\n\n\n");
        (string output, string status) = await h.RunAsync(Cons, "auth/secuser", "-v", "glenda");
        Assert.Equal(string.Empty, status);
        Assert.Equal(
            "first time setup for secstore: create /adm/secstore 20000000755\n" +
            "first time setup for secstore: create /adm/secstore/who 20000000755\n" +
            "first time setup for secstore: create /adm/secstore/store 20000000700\n" +
            "secuser glenda\n" +
            "new account (because /adm/secstore/glenda accounts glenda and FICTITIOUS do not exist)\n" +
            "8 characters\nconfirming...\n" + Prompts,
            output.Replace(Prompts, string.Empty, StringComparison.Ordinal) + Prompts);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AChangeIsLoggedToSecstoresLog()
    {
        h.Console.Type("secretpw\nsecretpw\n\n\n\n\n");
        Assert.Equal(string.Empty, (await RunWithConsoleAsync("glenda")).Status);
        Assert.EndsWith(" CHANGELOGIN for 'glenda'\n", await h.FileAsync("/sys/log/secstore"), StringComparison.Ordinal);
        Assert.DoesNotContain("CHANGELOGIN", h.Console.Text, StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnAccountsAnswersAreReadFromStandardInputLineByLine()
    {
        h.Console.Type("secretpw\nsecretpw\n");
        (string output, string status) = await h.RunAsync("\n", "auth/secuser", "glenda");
        Assert.Equal("read error", status);
        Assert.EndsWith("expires [DDMMYYYY, default = 13112024]: Enabled or Disabled [default Enabled]: ", output, StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnAnswerLongerThanItsBufferIsTooLarge()
    {
        h.Console.Type($"secretpw\nsecretpw\n\n\n\n{new string('c', 80)}\n");
        (string output, string status) = await RunWithConsoleAsync("glenda");
        Assert.Equal("input too large", status);
        Assert.EndsWith("comments [default = ]: ", output, StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnAnswerReadInPiecesIsTakenWhole()
    {
        h.Console.Type("secretpw\nsecretpw\n");
        (string output, string status) = await h.RunAsync("01012\n", "auth/secuser", "glenda");
        Assert.Equal("read error", status);
        Assert.Contains("!bad date format: 01012\n", output, StringComparison.Ordinal);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("31022030", 2030, 3, 3)]
    [InlineData("01012030", 2030, 1, 1)]
    [InlineData("29022024", 2024, 2, 29)]
    [InlineData("31121970", 1970, 12, 31)]
    public async Task AnExpiryIsTheLastSecondOfTheDayGivenRollingPastTheMonthsEnd(string date, int year, int month, int day)
    {
        h.Console.Type($"secretpw\nsecretpw\n{date}\n\n\n\n");
        Assert.Equal(string.Empty, (await RunWithConsoleAsync("glenda")).Status);
        long exp = new DateTimeOffset(year, month, day, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.StartsWith($"exp\t{exp}\n", await h.FileAsync("/adm/secstore/who/glenda"), StringComparison.Ordinal);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("aa012030", "!bad day of month: 539")]
    [InlineData("00012030", "!bad day of month: 0")]
    [InlineData("01002030", "!bad month: 0")]
    [InlineData("01012x30", "!bad year: 2")]
    [InlineData("0101196x", "!bad year: 196")]
    public async Task ADateItCannotTakeIsAskedForAgain(string date, string why)
    {
        h.Console.Type($"secretpw\nsecretpw\n{date}\n\n\n\n\n");
        (string output, string status) = await RunWithConsoleAsync("glenda");
        Assert.Equal(string.Empty, status);
        Assert.Contains($"{why}\nexpires", output, StringComparison.Ordinal);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("e", "n", (ushort)Pw.Enabled)]
    [InlineData("d", "Y", (ushort)Pw.Sta)]
    [InlineData("Dis", "yes", (ushort)Pw.Sta)]
    [InlineData("E", "N", (ushort)Pw.Enabled)]
    public async Task AnAnswerIsTakenByItsFirstLetter(string enabled, string sta, ushort status)
    {
        h.Console.Type($"secretpw\nsecretpw\n\n{enabled}\n{sta}\n\n");
        Assert.Equal(string.Empty, (await RunWithConsoleAsync("glenda")).Status);
        Assert.Equal(status, (await Password.GetAsync(h.Init, "glenda", true)).Pw!.Status);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnEditKeepsWhatIsNotAnsweredAndClearsTheFailures()
    {
        await h.AccountAsync("glenda", $"exp\t1800000000\nDISABLED\nSTA\nfailed\t4\nother\tsponsor\nPAK-Hi\t{GlendaHi}\n");
        h.Console.Type("\n\n\n\n\n");
        (string output, string status) = await RunWithConsoleAsync("glenda");
        Assert.Equal(string.Empty, status);
        Assert.Equal("expires [DDMMYYYY, default = 15012027]: clearing 4 failed login attempts\nEnabled or Disabled [default Disabled]: require STA? [default yes]: comments [default = sponsor]: change written\n", output);
        Assert.Equal($"exp\t1800000000\nDISABLED\nSTA\nother\tsponsor\nPAK-Hi\t{GlendaHi}\n", await h.FileAsync("/adm/secstore/who/glenda"));
        Assert.Equal("glenda password [default = don't change]: \n", h.Console.Text);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnEditSetsANewPassword()
    {
        await h.GlendaAsync();
        h.Console.Type("newsecretpw\nnewsecretpw\n\n\n\n\n");
        Assert.Equal(string.Empty, (await RunWithConsoleAsync("glenda")).Status);
        Assert.Contains(Pak.Hi("glenda", "newsecretpw").HexHi, await h.FileAsync("/adm/secstore/who/glenda"), StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ANewAccountsPasswordIsAskedForTwiceRawOnTheConsole()
    {
        h.Console.Type("seven77\nseven77\n\n\n\n\n");
        Assert.Equal(string.Empty, (await RunWithConsoleAsync("glenda")).Status);
        Assert.Equal("glenda password: \nretype password: \n", h.Console.Text);
        Assert.Contains(Pak.Hi("glenda", "seven77").HexHi, await h.FileAsync("/adm/secstore/who/glenda"), StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ANewAccountsPasswordMustBeSevenBytes()
    {
        h.Console.Type("\nshort\nsecretpw\nsecretpw\n\n\n\n\n");
        (string output, string status) = await RunWithConsoleAsync("glenda");
        Assert.Equal(string.Empty, status);
        Assert.Contains("password must be at least 7 characters\npassword must be at least 7 characters\nexpires", output, StringComparison.Ordinal);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData("", "password input aborted")]
    [InlineData("secretpw\n", "password input aborted")]
    [InlineData("secretpw\nsecretpx\n", "passwords didn't match")]
    public async Task APasswordNotTypedOrMistypedIsFatal(string typed, string why)
    {
        h.Console.Type(typed);
        Assert.Equal(why, (await RunWithConsoleAsync("glenda")).Status);
        Assert.False(await h.ExistsAsync("/adm/secstore/who/glenda"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ANewUserWithAStoreAlreadyIsRefused()
    {
        await h.MkdirAsync("/adm/secstore");
        await h.MkdirAsync("/adm/secstore/store");
        await h.MkdirAsync("/adm/secstore/store/glenda");
        Assert.Equal(("new account (because /adm/secstore/glenda accounts glenda and FICTITIOUS do not exist)\nauth/secuser: new user, but directory /adm/secstore/store/glenda already exists\n", "new user, but directory /adm/secstore/store/glenda already exists"), await RunWithConsoleAsync("glenda"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ADirectoryItCannotMakeIsFatal()
    {
        await h.WriteFileAsync("/adm/secstore", []);
        (string output, string status) = await RunWithConsoleAsync("glenda");
        Assert.StartsWith("auth/secuser: unable to create /adm/secstore/who: ", output, StringComparison.Ordinal);
        Assert.StartsWith("unable to create /adm/secstore/who: ", status, StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AnAccountItCannotWriteIsFatal()
    {
        await h.MkdirAsync("/adm/secstore");
        await h.MkdirAsync("/adm/secstore/who");
        await h.MkdirAsync("/adm/secstore/who/glenda");
        h.Console.Type("secretpw\nsecretpw\n\n\n\n\n");
        (string output, string status) = await RunWithConsoleAsync("glenda");
        Assert.True(output.EndsWith("auth/secuser: can't write password file: can't open PW file for glenda\n", StringComparison.Ordinal), $"PROBE [{output}] [{status}]");
        Assert.Equal("can't write password file: can't open PW file for glenda", status);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AStoreItCannotMakeIsFatal()
    {
        await h.MkdirAsync("/adm/secstore");
        await h.WriteFileAsync("/adm/secstore/store", []);
        h.Console.Type("secretpw\nsecretpw\n\n\n\n\n");
        (string output, string status) = await RunWithConsoleAsync("glenda");
        Assert.Contains("change written\nauth/secuser: unable to create /adm/secstore/store/glenda: ", output, StringComparison.Ordinal);
        Assert.StartsWith("unable to create /adm/secstore/store/glenda: ", status, StringComparison.Ordinal);
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(9000)]
    public async Task AescbcDecryptsWhatItEncryptsAtAnySize(int size)
    {
        byte[] data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(size);
        await h.WriteFileAsync("/tmp/clear", data);
        Assert.Equal(string.Empty, (await AescbcAsync("key\n", "-e", "/tmp/clear", "/tmp/c.aes")).Status);
        byte[] c = await h.BytesAsync("/tmp/c.aes");
        Assert.Equal(16 + 32 + size + 20, c.Length);
        Assert.Equal("AES CBC SHA1  2\n"u8.ToArray(), c[..16]);
        Assert.Equal(string.Empty, (await AescbcAsync("key\n\n\n", "-d", "/tmp/c.aes", "/tmp/back")).Status);
        Assert.Equal(data, await h.BytesAsync("/tmp/back"));
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(60)]
    public async Task AescbcRefusesAFileCutShortOrAltered(int keep)
    {
        await h.WriteFileAsync("/tmp/clear", "hello there\n"u8.ToArray());
        await AescbcAsync("key\n", "-e", "/tmp/clear", "/tmp/c.aes");
        byte[] c = await h.BytesAsync("/tmp/c.aes");
        await h.WriteFileAsync("/tmp/cut", c[..keep]);
        (string output, string status) = await AescbcAsync("key\n", "-d", "/tmp/cut", "/tmp/back");
        Assert.Equal(("auth/aescbc: read error\n", "read error"), (output, status));
        c[^1] ^= 1;
        await h.WriteFileAsync("/tmp/bad", c);
        Assert.Equal("decrypted file failed to authenticate", (await AescbcAsync("key\n", "-d", "/tmp/bad", "/tmp/back")).Status);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AescbcDecryptsASecstoreFileAndRefusesOneWithoutItsCheck()
    {
        byte[] key = System.Security.Cryptography.SHA1.HashData([.. "aescbc file"u8, .. "key"u8])[..16];
        byte[] file = [.. new byte[16], .. "hello there\n"u8, .. Encoding.ASCII.GetBytes(new string('X', 16))];
        using (var aes = new Aesstate(key, new byte[16]))
        {
            aes.CbcEncrypt(file);
        }

        await h.WriteFileAsync("/tmp/stored", file);
        Assert.Equal(string.Empty, (await AescbcAsync("key\n", "-d", "/tmp/stored", "/tmp/back")).Status);
        Assert.Equal("hello there\n", await h.FileAsync("/tmp/back"));
        Assert.Equal("decrypted file failed to authenticate", (await AescbcAsync("wrong\n", "-d", "/tmp/stored", "/tmp/back")).Status);
        await h.WriteFileAsync("/tmp/short", file[..20]);
        Assert.Equal("read error", (await AescbcAsync("key\n", "-d", "/tmp/short", "/tmp/back")).Status);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AescbcTakesItsKeyFromTheConsoleOrTheNvram()
    {
        await h.WriteFileAsync("/tmp/clear", "hello\n"u8.ToArray());
        h.Console.Type("key\nkey\n");
        Assert.Equal(string.Empty, (await AescbcAsync(null, "-e", "/tmp/clear", "/tmp/c.aes")).Status);
        Assert.Equal(string.Empty, (await AescbcAsync(null, "-d", "/tmp/c.aes", "/tmp/back")).Status);
        Assert.Equal("hello\n", await h.FileAsync("/tmp/back"));
        Assert.Contains("aescbc key: \naescbc key: \n", h.Console.Text, StringComparison.Ordinal);
        Assert.Equal("key input aborted", (await AescbcAsync(null, "-e", "/tmp/clear", "/tmp/c.aes")).Status);
        h.Console.Type($"{new string('k', 4096)}\n");
        Assert.Equal("key too long", (await AescbcAsync(null, "-e", "/tmp/clear", "/tmp/c.aes")).Status);
        h.Console.Type($"{new string('k', 4095)}\n");
        Assert.Equal(string.Empty, (await AescbcAsync(null, "-e", "/tmp/clear", "/tmp/c.aes")).Status);
        h.Console.Type("\n");
        Assert.Equal("no key", (await AescbcAsync(null, "-e", "/tmp/clear", "/tmp/c.aes")).Status);
        Assert.StartsWith("readnvram: ", (await AescbcAsync(null, "-n", "/tmp/clear", "/tmp/c.aes")).Status, StringComparison.Ordinal);
        await using SecstoreHarness withNvram = await StartAsync(Nvram("key"));
        await withNvram.WriteFileAsync("/tmp/clear", "hello\n"u8.ToArray());
        Assert.Equal(string.Empty, (await RunAescbcAsync(withNvram, null, "-en", "/tmp/clear", "/tmp/c.aes")).Status);
        Assert.Equal(string.Empty, (await RunAescbcAsync(withNvram, "key\n", "-d", "/tmp/c.aes", "/tmp/back")).Status);
        Assert.Equal("hello\n", await withNvram.FileAsync("/tmp/back"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AescbcIWithNothingOnItsDescriptorIsUsageAndOnlyNewlinesNoKey()
    {
        await h.WriteFileAsync("/tmp/clear", "hello\n"u8.ToArray());
        Assert.Equal(("auth/aescbc: usage: echo password |[3=1] auth/aescbc -i ...\n", "usage: echo password |[3=1] auth/aescbc -i ..."), await AescbcAsync(string.Empty, "-e", "/tmp/clear", "/tmp/c.aes"));
        Assert.Equal(("auth/aescbc: no key\n", "no key"), await AescbcAsync("\n\n", "-e", "/tmp/clear", "/tmp/c.aes"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AescbcReadsAKeyOfAtMost4115BytesFromDescriptorThree()
    {
        await h.WriteFileAsync("/tmp/clear", "hello\n"u8.ToArray());
        Assert.Equal(string.Empty, (await AescbcAsync(new string('a', 4116), "-e", "/tmp/clear", "/tmp/c.aes")).Status);
        Assert.Equal(string.Empty, (await AescbcAsync(new string('a', 4115), "-d", "/tmp/c.aes", "/tmp/back")).Status);
        Assert.Equal("decrypted file failed to authenticate", (await AescbcAsync(new string('a', 4114), "-d", "/tmp/c.aes", "/tmp/back")).Status);
        Assert.Equal(string.Empty, (await AescbcAsync("k", "-e", "/tmp/clear", "/tmp/c.aes")).Status);
        Assert.Equal(string.Empty, (await AescbcAsync("k\n", "-d", "/tmp/c.aes", "/tmp/back")).Status);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AescbcEncryptsTheSameFileDifferentlyEachTime()
    {
        await h.WriteFileAsync("/tmp/clear", "hello\n"u8.ToArray());
        await AescbcAsync("key", "-e", "/tmp/clear", "/tmp/c1");
        await AescbcAsync("key", "-e", "/tmp/clear", "/tmp/c2");
        Assert.NotEqual((await h.BytesAsync("/tmp/c1"))[16..32], (await h.BytesAsync("/tmp/c2"))[16..32]);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AescbcWritesEachBlockOfItsInputAsItComes()
    {
        (int inr, int inw) = await h.Init.PipeAsync();
        (int outr, int outw) = await h.Init.PipeAsync();
        (int kr, int kw) = await h.Init.PipeAsync();
        h.Init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            // The key is kept clear of the pipes' descriptors until they are closed.
            await child.DupAsync(inr, 0);
            await child.DupAsync(outw, 1);
            await child.DupAsync(kr, 20);
            foreach (int fd in new[] { inr, inw, outr, outw, kr, kw })
            {
                await child.CloseAsync(fd);
            }

            await child.DupAsync(20, 3);
            await child.CloseAsync(20);
            await child.ExecAsync("/bin/auth/aescbc", ["auth/aescbc", "-i", "-e"]);
        });
        foreach (int fd in new[] { inr, outw, kr })
        {
            await h.Init.CloseAsync(fd);
        }

        await h.Init.WriteAsync(kw, "key"u8.ToArray());
        await h.Init.CloseAsync(kw);
        await h.Init.WriteAsync(inw, new byte[4096]);
        int got = 0;
        while (got < 16 + 32 + 4096)
        {
            got += (await h.Init.ReadAsync(outr, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Length;
        }

        await h.Init.CloseAsync(inw);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AescbcThatCannotReadItsInputOrWriteItsOutputFails()
    {
        await using var server = new Kernel.Tests.Support.FileServerHarness { ReadRefusal = "disk on fire", WriteRefusal = "disk full" };
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("in", "x"u8.ToArray()));
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("out", []));
        (int client, int served) = await h.Init.PipeAsync();
        server.Serve(h.Init, served);
        await h.MkdirAsync("/n");
        await h.Init.MountAsync(client, -1, "/n", NinePSharp.Namespaces.MountFlags.Replace, string.Empty);
        Assert.Equal(("auth/aescbc: read error\n", "read error"), await AescbcAsync("key", "-d", "/n/in", "/tmp/back"));
        Assert.Equal(("auth/aescbc: read error\n", "read error"), await AescbcAsync("key", "-e", "/n/in", "/tmp/cipher"));
        await h.WriteFileAsync("/tmp/clear", "hello\n"u8.ToArray());
        Assert.Equal(("auth/aescbc: write error\n", "write error"), await AescbcAsync("key", "-e", "/tmp/clear", "/n/out"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AescbcTakesNoArguments()
        => Assert.Equal(("usage: auth/aescbc -d < cipher.aes > clear.txt\n   or: auth/aescbc -e < clear.txt > cipher.aes\n", "usage"), await h.RunAsync(string.Empty, "auth/aescbc", "x"));

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AescbcThatCannotWriteItsOutputFails()
    {
        await h.WriteFileAsync("/tmp/clear", "hello\n"u8.ToArray());
        await using var server = new Kernel.Tests.Support.FileServerHarness { WriteLimit = 2 };
        server.Root.AddChild(new NinePSharp.Server.FileSystem.NinePFile("out", []));
        (int client, int served) = await h.Init.PipeAsync();
        server.Serve(h.Init, served);
        await h.MkdirAsync("/n");
        await h.Init.MountAsync(client, -1, "/n", NinePSharp.Namespaces.MountFlags.Replace, string.Empty);
        Assert.Equal(("auth/aescbc: write error\n", "write error"), await AescbcAsync("key\n", "-e", "/tmp/clear", "/n/out"));
    }

    private static async Task<(string Out, string Status)> RunAescbcAsync(SecstoreHarness harness, string? key, string flags, string input, string output)
    {
        // The key on descriptor 3 with -i, and the files on 0 and 1.
        (int kr, int kw) = await harness.Init.PipeAsync();
        (int er, int ew) = await harness.Init.PipeAsync();
        long pid = harness.Init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            await child.DupAsync(await child.OpenAsync(input, NinePConstants.OREAD), 0);
            await child.DupAsync(await child.CreateAsync(output, NinePConstants.OWRITE, 0b110_110_110), 1);
            await child.DupAsync(ew, 2);
            await child.DupAsync(kr, 3);
            foreach (int fd in new[] { kw, er, ew })
            {
                await child.CloseAsync(fd);
            }

            await child.ExecAsync("/bin/auth/aescbc", key is null ? ["auth/aescbc", flags] : ["auth/aescbc", "-i", flags]);
        });
        await harness.Init.CloseAsync(kr);
        await harness.Init.CloseAsync(ew);
        if (!string.IsNullOrEmpty(key))
        {
            await harness.Init.WriteAsync(kw, Encoding.UTF8.GetBytes(key));
        }

        await harness.Init.CloseAsync(kw);
        var bytes = new List<byte>();
        for (ReadOnlyMemory<byte> b; !(b = await harness.Init.ReadAsync(er, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(30))).IsEmpty;)
        {
            bytes.AddRange(b.Span);
        }

        await harness.Init.CloseAsync(er);
        while (true)
        {
            Waitmsg w = await harness.Init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (w.Pid == pid)
            {
                return (Encoding.UTF8.GetString([.. bytes]), System.Text.RegularExpressions.Regex.Replace(w.Message, @"^\S+ \d+: ", string.Empty));
            }
        }
    }

    private Task<(string Out, string Status)> AescbcAsync(string? key, string flags, string input, string output) => RunAescbcAsync(h, key, flags, input, output);

    // secuser with its standard input the console, as typed.
    private Task<(string Out, string Status)> RunWithConsoleAsync(string user) => h.RunAsync(Cons, "auth/secuser", user);
}
