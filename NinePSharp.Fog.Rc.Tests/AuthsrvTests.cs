using System.Net;
using System.Net.Sockets;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// libauthsrv as factotum uses it: tickets, authenticators and their requests to and from the wire,
// AuthPAK, the auth server's replies read from a pipe the test writes, the console's prompt, the
// nvram, whether the host's, a file's or one in a FAT file system, and the auth server found by cs.
public sealed class AuthsrvTests : IAsyncLifetime
{
    private readonly FakeHostNetwork host = new();
    private readonly FakeHostConsole console = new();
    private readonly ManualClock clock = new() { Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };
    private readonly byte[] nvram = new byte[Nvrsafe.Size];
    private FogKernel kernel = null!;
    private Process init = null!;
    private int errors = -1;

    public async Task InitializeAsync()
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", clock, host, console, new HostNvram(nvram));
        init = await kernel.BootAsync();

        // Init's 0 and 1 are null, and its errors go to a pipe the test reads.
        for (int i = 0; i < 3; i++)
        {
            await init.OpenAsync("/dev/null", NinePConstants.ORDWR);
        }

        (errors, int written) = await init.PipeAsync();
        await init.DupAsync(written, 2);
        await init.CloseAsync(written);
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
    public void AFieldIsItsTextUpToANulAndSafecpyFillsAllButItsLastByte()
    {
        byte[] field = new byte[4];
        Authsrv.Safecpy(field, "abcdef");
        Assert.Equal("abc\0"u8.ToArray(), field);
        Assert.Equal("abc", Authsrv.Str(field));
        Authsrv.Safecpy(field, "x");
        Assert.Equal("x\0\0\0"u8.ToArray(), field);
        Assert.Equal("wxyz", Authsrv.Str("wxyz"u8.ToArray()));
        Assert.Equal((byte)9, Authsrv.Nvcsum([]));
        Assert.Equal((byte)9, Authsrv.Nvcsum([1, 255]));
        Assert.Equal((byte)12, Authsrv.Nvcsum([1, 2]));
    }

    [Fact]
    public void NetmkaddrAddsTheNetworkAndServiceAnAddressLacks()
    {
        Assert.Equal("net!fog", Net.Netmkaddr("fog", null, null));
        Assert.Equal("net!fog!ticket", Net.Netmkaddr("fog", null, "ticket"));
        Assert.Equal("tcp!fog", Net.Netmkaddr("fog", "tcp", null));
        Assert.Equal("tcp!fog!567", Net.Netmkaddr("fog", "tcp", "567"));
        Assert.Equal("il!fog!567", Net.Netmkaddr("il!fog", "tcp", "567"));
        Assert.Equal("il!fog", Net.Netmkaddr("il!fog", "tcp", null));
        Assert.Equal("il!fog!9fs", Net.Netmkaddr("il!fog!9fs", "tcp", "567"));
        Assert.Equal("!fog!x", Net.Netmkaddr("!fog", null, "x"));
    }

    [Fact]
    public void ATicketRequestCrossesTheWireWithItsNamesEnded()
    {
        var tr = new Ticketreq { Type = Authsrv.AuthPAK };
        Authsrv.Safecpy(tr.Authid, "bootes");
        Authsrv.Safecpy(tr.Authdom, "fog");
        Authsrv.Safecpy(tr.Hostid, "glenda");
        Authsrv.Safecpy(tr.Uid, "none");
        "chalenge"u8.CopyTo(tr.Chal);
        byte[] wire = Authsrv.ConvTR2M(tr);
        Assert.Equal(Authsrv.Tickreqlen, wire.Length);

        // Every byte of each name is filled, and what comes back ends each one.
        for (int i = 1; i < wire.Length; i++)
        {
            wire[i] = wire[i] == 0 ? (byte)'z' : wire[i];
        }

        var back = new Ticketreq();
        Assert.Equal(Authsrv.Tickreqlen, Authsrv.ConvM2TR(wire, wire.Length, back));
        Assert.Equal(Authsrv.AuthPAK, back.Type);
        Assert.Equal(("bootes" + new string('z', 21), "fog" + new string('z', 44), "chalenge"), (Authsrv.Str(back.Authid), Authsrv.Str(back.Authdom), Encoding.UTF8.GetString(back.Chal)));
        Assert.Equal(("glenda" + new string('z', 21), "none" + new string('z', 23)), (Authsrv.Str(back.Hostid), Authsrv.Str(back.Uid)));

        // A short one is refused, and leaves what it was read into empty.
        Assert.Equal(-Authsrv.Tickreqlen, Authsrv.ConvM2TR(wire, wire.Length - 1, back));
        Assert.Equal((0, 0, 0), (back.Type, back.Authid.Sum(b => b), back.Chal.Sum(b => b)));
        Assert.Equal((0, 0, 0), (back.Authdom.Sum(b => b), back.Hostid.Sum(b => b), back.Uid.Sum(b => b)));
    }

    [Fact]
    public void Form1checkKnowsEachSignatureAndNothingShorter()
    {
        string[] sigs = ["form1 PR", "form1 Ts", "form1 Tc", "form1 As", "form1 Ac", "form1 Tp", "form1 Hr"];
        int[] nums = [3, 64, 65, 66, 67, 68, 69];
        Assert.Equal(nums, sigs.Select(sig => Authsrv.Form1check(Encoding.ASCII.GetBytes(sig + "..."), 11)));
        Assert.Equal(-1, Authsrv.Form1check("form1 Ts"u8.ToArray(), 7));
        Assert.Equal(-1, Authsrv.Form1check("form1 Tx"u8.ToArray(), 8));
    }

    [Fact]
    public void ATicketIsSealedWithTheKeyAndOpenedOnlyByIt()
    {
        Authkey k = Paked("password", "glenda");
        var t = Ticket(Authsrv.AuthTc, 1);
        byte[] wire = Authsrv.ConvT2M(t, k);
        Assert.Equal(Authsrv.Maxticketlen, wire.Length);
        Assert.Equal(Authsrv.AuthTc, Authsrv.Form1check(wire, wire.Length));

        var back = new Ticket();
        Assert.Equal(Authsrv.Maxticketlen, Authsrv.ConvM2T([.. wire, 1, 2], wire.Length + 2, back, k));
        Assert.Equal((Authsrv.AuthTc, (byte)1, "glenda", "none"), (back.Num, back.Form, Authsrv.Str(back.Cuid), Authsrv.Str(back.Suid)));
        Assert.Equal(t.Chal, back.Chal);
        Assert.Equal(t.Key, back.Key);

        // How much a short one needs, and its length when it is not to be opened.
        Assert.Equal(-8, Authsrv.ConvM2T(wire, 7, back, k));
        Assert.Equal(-Authsrv.Maxticketlen, Authsrv.ConvM2T(wire, 8, back, k));
        Assert.Equal(Authsrv.Maxticketlen, Authsrv.ConvM2T(wire, wire.Length, null, k));
        Assert.Equal(Authsrv.Maxticketlen, Authsrv.ConvM2T(wire, wire.Length, back, null));
        Assert.Equal((0, 0), (back.Num, back.Form));

        // Another key opens nothing, and leaves a ticket of no number in the form it came in.
        Assert.Equal(Authsrv.Maxticketlen, Authsrv.ConvM2T(wire, wire.Length, back, Paked("password", "glenda")));
        Assert.Equal((0, 1), (back.Num, back.Form));
        Assert.Equal((0, 0, 0, 0), (back.Chal.Sum(b => b), back.Cuid.Sum(b => b), back.Suid.Sum(b => b), back.Key.Sum(b => b)));
    }

    [Fact]
    public void AFormZeroTicketAndAuthenticatorAreDes()
    {
        Authkey k = Key("password", "glenda");
        var t = Ticket(Authsrv.AuthTs, 0);
        byte[] wire = Authsrv.ConvT2M(t, k);
        int m = 1 + Authsrv.Challen + (2 * Authsrv.Anamelen) + Authsrv.Deskeylen;
        Assert.Equal(m, wire.Length);
        var back = new Ticket();
        Assert.Equal(-m, Authsrv.ConvM2T(wire, m - 1, back, k));
        Assert.Equal(m, Authsrv.ConvM2T(wire, m, back, k));
        Assert.Equal((Authsrv.AuthTs, (byte)0, "glenda"), (back.Num, back.Form, Authsrv.Str(back.Cuid)));
        Assert.Equal(t.Key[..Authsrv.Deskeylen], back.Key[..Authsrv.Deskeylen]);

        var a = new Authenticator { Num = Authsrv.AuthAc };
        "achallen"u8.CopyTo(a.Chal);
        byte[] sealedA = Authsrv.ConvA2M(a, back);
        Assert.Equal(1 + Authsrv.Challen + 4, sealedA.Length);
        var opened = new Authenticator();
        Assert.Equal(-13, Authsrv.ConvM2A(sealedA, 12, opened, back));
        Assert.Equal(13, Authsrv.ConvM2A(sealedA, 13, opened, back));
        Assert.Equal((Authsrv.AuthAc, "achallen"), (opened.Num, Encoding.UTF8.GetString(opened.Chal)));
    }

    [Fact]
    public void AnAuthenticatorIsSealedWithTheTicketsKeyAndOpenedOnlyByIt()
    {
        var t = Ticket(Authsrv.AuthTc, 1);
        var a = new Authenticator { Num = Authsrv.AuthAs };
        "schallen"u8.CopyTo(a.Chal);
        a.Rand.AsSpan().Fill(7);
        byte[] wire = Authsrv.ConvA2M(a, t);
        Assert.Equal(Authsrv.Maxauthentlen, wire.Length);

        var back = new Authenticator { Num = 1 };
        back.Chal[0] = back.Rand[0] = 1;
        Assert.Equal(-Authsrv.Maxauthentlen, Authsrv.ConvM2A(wire, wire.Length - 1, back, t));
        Assert.Equal((0, 0, 0), (back.Num, back.Chal[0], back.Rand[0]));
        Assert.Equal(Authsrv.Maxauthentlen, Authsrv.ConvM2A(wire, wire.Length, back, t));
        Assert.Equal((Authsrv.AuthAs, "schallen"), (back.Num, Encoding.UTF8.GetString(back.Chal)));
        Assert.Equal(a.Rand, back.Rand);

        t.Key[0] ^= 1;
        Assert.Equal(Authsrv.Maxauthentlen, Authsrv.ConvM2A(wire, wire.Length, back, t));
        Assert.Equal((0, 0, 0), (back.Num, back.Chal.Sum(b => b), back.Rand.Sum(b => b)));
    }

    [Fact]
    public void AuthpakGivesBothSidesOneKeyAndRefusesABadValueOrASecondFinish()
    {
        Authkey client = Key("password", "glenda"), server = Key("password", "glenda");
        byte[] yc = Authsrv.AuthpakNew(out Dp9ik.AuthPakState pc, client, true);
        byte[] ys = Authsrv.AuthpakNew(out Dp9ik.AuthPakState ps, server, false);
        Assert.Equal((Authsrv.Pakylen, Authsrv.Pakylen), (yc.Length, ys.Length));
        Assert.Equal(0, Authsrv.AuthpakFinish(pc, client, ys));
        Assert.Equal(0, Authsrv.AuthpakFinish(ps, server, yc));
        Assert.Equal(client.K.SharedKey, server.K.SharedKey);
        Assert.Equal(-1, Authsrv.AuthpakFinish(pc, client, ys));

        Authsrv.AuthpakNew(out Dp9ik.AuthPakState bad, client, true);
        Assert.Equal(-1, Authsrv.AuthpakFinish(bad, client, Enumerable.Repeat((byte)0xff, Authsrv.Pakylen).ToArray()));
    }

    [Fact]
    public void AKeyCopiedIsHashedForTheSameUserAndAPasswordsKeyIsPasstokeys()
    {
        Authkey k = Key("password", "glenda");
        Authkey copy = k.Copy();
        Assert.Equal("glenda", copy.Hashed);
        Assert.Equal(k.K.PakHash, copy.K.PakHash);
        Assert.Equal(k.K.AesKey, copy.K.AesKey);
        Assert.Equal(k.K.DesKey, copy.K.DesKey);
        Authkey bare = Authkey.Passtokey("password");
        Assert.Null(bare.Hashed);
        Assert.Null(bare.Copy().Hashed);
        Assert.Equal(Dp9ik.AuthKey.FromPassword("password").AesKey, bare.K.AesKey);
        Assert.Equal(new byte[Authsrv.Deskeylen], Authkey.FromKeys(new byte[Authsrv.Deskeylen], k.K.AesKey).K.DesKey);
    }

    [Fact]
    public async Task ReadnReadsAcrossWritesUntilItHasAllOrTheFileEnds()
    {
        (int r, int w) = await init.PipeAsync();
        await init.WriteAsync(w, "ab"u8.ToArray());
        await init.WriteAsync(w, "cd"u8.ToArray());
        Assert.Equal("abc"u8.ToArray(), await Authsrv.ReadnAsync(init, r, 3));
        await init.CloseAsync(w);
        Assert.Equal("d"u8.ToArray(), await Authsrv.ReadnAsync(init, r, 3));
        Assert.Empty(await Authsrv.ReadnAsync(init, r, 0));
    }

    [Fact]
    public async Task ARequestIsWrittenWholeOrNotAtAll()
    {
        init.Notify(note => note == "sys: write on closed pipe");
        (int r, int w) = await init.PipeAsync();
        var tr = new Ticketreq { Type = Authsrv.AuthTreq };
        Assert.Equal(0, await Authsrv.AsrequestAsync(init, w, tr));
        Assert.Equal(Authsrv.ConvTR2M(tr), (await init.ReadAsync(r, 1000)).ToArray());
        await init.CloseAsync(r);
        Assert.Equal(-1, await Authsrv.AsrequestAsync(init, w, tr));
        Assert.NotEqual(string.Empty, Errstr.Get(init));
    }

    [Fact]
    public async Task AReplyIsOkWithItsBytesOkvarWithAsManyAsItSaysOrTheServersError()
    {
        Assert.Equal((2, "xy"), await RdrespAsync([4, .. "xy"u8], 2));
        Assert.Equal((0, string.Empty), await RdrespAsync([4], 0));
        Assert.Equal((3, "abc\0\0"), await RdrespAsync([9, .. "00003abc"u8], 5));
        Assert.Equal((5, "abcde"), await RdrespAsync([9, .. "  5  abcde"u8], 5));
        Assert.Equal((-1, "remote: no such user"), await RdrespAsync([5, .. Pad("no such user", 64)], 5));
        Assert.Equal((-1, "remote: " + new string('e', 63)), await RdrespAsync([5, .. Enumerable.Repeat((byte)'e', 64)], 5));

        // Anything short, unknown or out of bounds is a protocol botch.
        foreach (byte[] botch in new byte[][] { [], [4, .. "x"u8], [5, .. "short"u8], [9, .. "000"u8], [9, .. "00000"u8], [9, .. "00006abcdef"u8], [9, .. "-0001x"u8], [9, .. "00003ab"u8], [7] })
        {
            Assert.Equal((-1, "AS protocol botch"), await RdrespAsync(botch, 5));
        }
    }

    [Fact]
    public async Task TwoTicketsAreReadAsTheirLengthsSayAndErrstrIsLeftAsItWas()
    {
        Authkey k = Key("password", "glenda");
        byte[] tc = Authsrv.ConvT2M(Ticket(Authsrv.AuthTc, 1), k), ts = Authsrv.ConvT2M(Ticket(Authsrv.AuthTs, 0), k);
        Errstr.Set(init, "before");
        (int r, byte[] tbuf, byte[] request, _) = await GetticketAsync([[4], tc[..20], tc[20..], ts], 1000);
        Assert.Equal((tc.Length + ts.Length, "before"), (r, Errstr.Get(init)));
        Assert.Equal([.. tc, .. ts], tbuf);
        Assert.Equal(Authsrv.ConvTR2M(new Ticketreq { Type = Authsrv.AuthTreq }), request);

        // A ticket longer than the room, one cut short, a refusal or a request not written fail.
        Assert.Equal((-1, "AS protocol botch"), Botch(await GetticketAsync([[4], tc, ts], 100)));
        Assert.Equal((-1, "AS protocol botch"), Botch(await GetticketAsync([[4], tc, ts], 150)));
        Assert.Equal(196, Botch(await GetticketAsync([[4], tc, ts], 196)).R);
        Assert.Equal((-1, "AS protocol botch"), Botch(await GetticketAsync([[4], tc[..10]], 1000)));
        Assert.Equal((-1, "AS protocol botch"), Botch(await GetticketAsync([[4], tc, ts[..30]], 1000)));
        Assert.Equal((-1, "remote: refused"), Botch(await GetticketAsync([[5, .. Pad("refused", 64)]], 1000)));
        Assert.Equal((-1, "remote: refused"), Botch(await GetticketAsync([[5, .. Pad("refused", 64)], tc, ts], 1000)));
        init.Notify(note => note == "sys: write on closed pipe");
        (int read, int written) = await init.PipeAsync();
        await init.CloseAsync(read);
        Assert.Equal(-1, (await Authsrv.AsgetticketAsync(init, written, new Ticketreq(), 1000)).R);
        Assert.NotEqual("AS protocol botch", Errstr.Get(init));
    }

    [Fact]
    public async Task ReadconsGivesUpWithoutAConsoleOrWhenItsReadIsInterrupted()
    {
        // Without /dev/cons, or /dev/consctl for a reply without echo.
        Assert.Null(await InNamespaceAsync(async c =>
        {
            await c.BindAsync("/tmp", "/dev", NinePSharp.Namespaces.MountFlags.Replace);
            string fds = await FdsAsync(c);
            string? s = await Authsrv.ReadconsAsync(c, "q", null, false);
            Assert.Equal(fds, await FdsAsync(c));
            return s;
        }));

        // A console it may read but not write is given up on, what it opened closed again.
        await init.CloseAsync(await init.CreateAsync("/tmp/r", NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
        await init.CloseAsync(await init.CreateAsync("/tmp/r/cons", NinePConstants.OREAD, 0b100_100_100));
        Assert.Null(await InNamespaceAsync(async c =>
        {
            await c.BindAsync("/tmp/r", "/dev", NinePSharp.Namespaces.MountFlags.Replace);
            string fds = await FdsAsync(c);
            string? s = await Authsrv.ReadconsAsync(c, "q", null, false);
            Assert.Equal(fds, await FdsAsync(c));
            return s;
        }));
        await init.CloseAsync(await init.CreateAsync("/tmp/d", NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
        await init.CloseAsync(await init.CreateAsync("/tmp/d/cons", NinePConstants.OWRITE, 0b110_110_110));
        Assert.Null(await InNamespaceAsync(async c =>
        {
            await c.BindAsync("#c/cons", "/tmp/d/cons", NinePSharp.Namespaces.MountFlags.Replace);
            await c.BindAsync("/tmp/d", "/dev", NinePSharp.Namespaces.MountFlags.Replace);
            string? s = await Authsrv.ReadconsAsync(c, "q", null, true);
            Assert.Contains("does not exist", Errstr.Get(c), StringComparison.Ordinal);
            return s;
        }));

        // A note interrupts the read, which gives up as the end of input does not: no input aborted.
        var typing = new TypingConsole();
        Process waiting = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda", clock, host, typing).BootAsync();
        waiting.Notify(note => note == "interrupt");
        Task<string?> asked = Authsrv.ReadconsAsync(waiting, "q", null, true);
        for (int i = 0; !typing.Raw; i++)
        {
            Assert.True(i < 5000, "the console was never made raw");
            await Task.Delay(1);
        }

        waiting.PostNote("interrupt");
        Assert.Null(await asked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("interrupted", Errstr.Get(waiting));
        Assert.False(typing.Raw);
        Assert.Equal("q: \n", typing.Written);
    }

    [Fact]
    public async Task ARawReplyIsEditedByReadconsWhichLeavesTheConsoleAndDescriptorsAsItFoundThem()
    {
        // A backspace with nothing typed takes back nothing, ^U clears what was typed and asks
        // again, and a space is part of the reply.
        string fds = await FdsAsync(init);
        int held = await init.OpenAsync("/dev/consctl", NinePConstants.OWRITE);
        console.Type("\bzz\u0015a b\n");
        Assert.Equal("a b", await Authsrv.ReadconsAsync(init, "q", null, true));
        Assert.EndsWith("q: \nq: \n", console.Text, StringComparison.Ordinal);

        // Its rawoff, not its close, ends raw while another holds consctl open.
        Assert.False(console.Raw);
        await init.CloseAsync(held);
        Assert.Equal(fds, await FdsAsync(init));

        // A console opened as descriptor 0 is closed too.
        console.Type("x\n");
        string? reply = await InNamespaceAsync(async c =>
        {
            await c.CloseAsync(0);
            string before = await FdsAsync(c);
            string? s = await Authsrv.ReadconsAsync(c, "q", null, false);
            Assert.Equal(before, await FdsAsync(c));
            return s;
        });
        Assert.Equal("x", reply);
    }

    [Fact]
    public async Task AnExecSharingReadconssDescriptorsClosesTheConsoleItAwaitsAReplyOn()
    {
        // OCEXEC: a process sharing the descriptors execs while the reply is awaited, and the
        // console readcons opened is gone before the reply ends, which it closes without a word.
        var typing = new TypingConsole();
        var programs = new Dictionary<string, ProgramMain> { ["true"] = (_, _) => Task.CompletedTask };
        Process waiting = await FogKernel.InMemory(programs, "glenda", clock, host, typing).BootAsync();
        Task<string?> asked = Authsrv.ReadconsAsync(waiting, "q", null, false);
        for (int i = 0; typing.Written != "q: "; i++)
        {
            Assert.True(i < 5000, $"never asked; the console has {typing.Written}");
            await Task.Delay(1);
        }

        waiting.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/true", ["true"]));
        Assert.EndsWith(": main", (await waiting.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message, StringComparison.Ordinal);
        typing.Type("x\n");
        Assert.Null(await asked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Errors.BadFd, Errstr.Get(waiting));
    }

    [Fact]
    public async Task TheHostsNvramIsReadAndCheckedAndAKeyAskedForOnlyWhenTheFlagsSay()
    {
        Safe("bootes", "fog", Aes(1), config: "sskey", machkey: [1, 2, 3, 4, 5, 6, 7]).ToBytes().CopyTo(nvram, 0);
        var safe = new Nvrsafe();
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, 0));
        Assert.Equal(("bootes", "fog", "sskey"), (Authsrv.Str(safe.Authid), Authsrv.Str(safe.Authdom), Authsrv.Str(safe.Config)));
        Assert.Equal(Aes(1), safe.Aesmachkey);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, safe.Machkey);

        // A bad config or AES key is quietly cleared; the rest are said to be bad.
        Nvrsafe broken = Safe("bootes", "fog", Aes(1), config: "sskey");
        (broken.Configsum, broken.Aesmachsum) = ((byte)(broken.Configsum + 1), (byte)(broken.Aesmachsum + 1));
        broken.ToBytes().CopyTo(nvram, 0);
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, 0));
        Assert.Equal((0, 0), (safe.Config.Sum(b => b), safe.Aesmachkey.Sum(b => b)));
        nvram.AsSpan().Clear();
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, 0));
        Assert.Equal("bad nvram des key\nbad authentication id\nbad authentication domain\n", await ErrorsAsync(3));

        Nvrsafe empty = Safe(string.Empty, "fog", Aes(1));
        empty.ToBytes().CopyTo(nvram, 0);
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, 0));
        Assert.Equal("empty nvram authid or authdom\n", await ErrorsAsync(1));
        Safe("bootes", string.Empty, Aes(1)).ToBytes().CopyTo(nvram, 0);
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, 0));
        Assert.Equal("empty nvram authid or authdom\n", await ErrorsAsync(1));
    }

    [Fact]
    public async Task ABadNvramIsAskedForAndWrittenWhenTheFlagsSayAndAlwaysForNVwrite()
    {
        // Each answer must fit, the password be typed the same twice, and p9sk1's DES key kept only when asked.
        console.Type($"{new string('a', 28)}\nbootes\nfog\nsskey\npw1\npw2\npw\npw\nyes\n");
        var safe = new Nvrsafe();
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, Authsrv.NVwriteonerr));
        Assert.Equal("bad nvram des key\nbad authentication id\nbad authentication domain\n", await ErrorsAsync(3));
        Assert.Equal("authid longer than 27 characters; try again\npassword mismatch\n", await ErrorsAsync(2));
        Nvrsafe written = Nvrsafe.FromBytes(nvram);
        Authkey k = Authkey.Passtokey("pw");
        Assert.Equal(("bootes", "fog", "sskey"), (Authsrv.Str(written.Authid), Authsrv.Str(written.Authdom), Authsrv.Str(written.Config)));
        Assert.Equal(k.K.AesKey, written.Aesmachkey);
        Assert.Equal(k.K.DesKey, written.Machkey);
        Assert.Equal(written.ToBytes(), Safe("bootes", "fog", k.K.AesKey, config: "sskey", machkey: k.K.DesKey).ToBytes());
        Assert.EndsWith("authid: authid: authdom: secstore key: \npassword: \nconfirm password: \npassword: \nconfirm password: \nenable legacy p9sk1[no]: ", console.Text, StringComparison.Ordinal);

        // Asked again however good it is, the DES key left out by default.
        console.Type("glenda\nfog\n\npw\npw\n\n");
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, Authsrv.NVwrite));
        written = Nvrsafe.FromBytes(nvram);
        Assert.Equal(("glenda", 0), (Authsrv.Str(written.Authid), written.Machkey.Sum(b => b)));

        // The nvram rewritten from what is given, without asking.
        Nvrsafe given = Safe("adm", "dom", Aes(3));
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, given, Authsrv.NVwritemem));
        Assert.Equal(given.ToBytes(), nvram);

        // Giving up on any question leaves the nvram unwritten.
        foreach (string typed in new[] { string.Empty, "a\n", "a\nb\n", "a\nb\nc\n", "a\nb\nc\npw\n", "a\nb\nc\npw\npw\n" })
        {
            console.Type(typed);
            Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, Authsrv.NVwrite));
            Assert.Equal(given.ToBytes(), nvram);
        }
    }

    [Fact]
    public async Task NvramIsTheFileNvramNamesAtItsOffsetAndLength()
    {
        await WriteFileAsync("/tmp/nvr", [.. new byte[10], .. Safe("bootes", "fog", Aes(2)).ToBytes()]);
        await WriteFileAsync("/env/nvram", "/tmp/nvr"u8.ToArray());
        await WriteFileAsync("/env/nvroff", "10"u8.ToArray());
        var safe = new Nvrsafe();
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, 0));
        Assert.Equal("bootes", Authsrv.Str(safe.Authid));

        // A length past what the file holds is a short read, said so only when it may be written.
        await WriteFileAsync("/env/nvrlen", "200"u8.ToArray());
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, 0));
        Assert.Equal(string.Empty, Authsrv.Str(safe.Authid));
        console.Type(string.Empty);
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, Authsrv.NVwriteonerr));
        Assert.StartsWith("can't read 200 bytes from /tmp/nvr: ", await ErrorsAsync(1), StringComparison.Ordinal);

        // One longer than 512 is read as 512.
        await WriteFileAsync("/tmp/nvr", [.. new byte[10], .. Safe("bootes", "fog", Aes(2)).ToBytes(), .. new byte[600]]);
        await WriteFileAsync("/env/nvrlen", "4096"u8.ToArray());
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, 0));

        // A file that cannot be opened.
        await WriteFileAsync("/env/nvram", "/tmp/none"u8.ToArray());
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, Authsrv.NVwriteonerr));
        Assert.StartsWith("can't open /tmp/none: ", await ErrorsAsync(1), StringComparison.Ordinal);
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, Authsrv.NVwritemem));
        Assert.StartsWith("can't write key to nvram: ", await ErrorsAsync(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NvramIsAFileInTheRootOfAFatFileSystemWhoseNameEndsIn9fat()
    {
        byte[] key = Safe("bootes", "fog", Aes(4)).ToBytes();
        await WriteFileAsync("/tmp/9fat", Fat("PLAN9   NVR", key));
        await WriteFileAsync("/env/nvram", "/tmp/9fat"u8.ToArray());
        var safe = new Nvrsafe();
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, 0));
        Assert.Equal("bootes", Authsrv.Str(safe.Authid));

        // Another file's name after a bang, upper-cased, or a FAT file system found by $nvroff.
        await WriteFileAsync("/tmp/disk", Fat("KEY     BIN", key));
        await WriteFileAsync("/env/nvram", "/tmp/disk!key.bin"u8.ToArray());
        await WriteFileAsync("/env/nvroff", "dos"u8.ToArray());
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, 0));
        await WriteFileAsync("/tmp/disk", Fat("LONGNAME   ", key, clust: 2));
        await WriteFileAsync("/env/nvram", "/tmp/disk!longnamexyz"u8.ToArray());
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, 0));

        // Whatever is not such a file system, or holds no such file, is no nvram.
        byte[][] bad =
        [
            Fat("PLAN9   NVR", key)[..100],
            [0, .. Fat("PLAN9   NVR", key)[1..]],
            Fat("PLAN9   NVR", key, magic1: 0x3d),
            Fat("PLAN9   NVR", key, magic2: 0x91),
            Fat("PLAN9   NVR", key, sectsize: 1024),
            Fat("PLAN9   NVR", key, nroot: 0),
            Fat("PLAN9   NVR", key, nroot: 2049),
            Fat("PLAN9   NVR", key)[..1100],
            Fat("PLAN9   NVQ", key),
            Fat("PLAN9   MVR", key),
        ];
        await WriteFileAsync("/env/nvram", "/tmp/disk"u8.ToArray());
        foreach (byte[] image in bad)
        {
            await WriteFileAsync("/tmp/disk", image);
            Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, 0));
            Assert.Equal("readnvram: couldn't find nvram\n", await ErrorsAsync(1));
        }

        // As many root entries as 64 sectors hold are read; and bangs before a name are passed over,
        // and with nothing but bangs there is no name.
        await WriteFileAsync("/tmp/disk", Fat("PLAN9   NVR", key, nroot: 1024));
        Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, 0));
        await init.RemoveAsync("/env/nvroff");
        foreach (string name in new[] { "!x", "!!" })
        {
            await WriteFileAsync("/env/nvram", Encoding.UTF8.GetBytes(name));
            Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, Authsrv.NVwriteonerr));
            Assert.StartsWith($"can't open {name}: ", await ErrorsAsync(1), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithoutAnNvramFileTheHostsIsReadAndOneOfNoBytesIsShort()
    {
        Process bare = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", clock, host, console).BootAsync();
        var safe = new Nvrsafe();
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(bare, safe, 0));
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(bare, safe, Authsrv.NVwritemem));

        // Without #c, there is none to be found.
        Assert.Equal(-1, await InNamespaceAsync(UnboundAsync));

        static async Task<int> UnboundAsync(Process c)
        {
            await c.BindAsync("/tmp", "#c", NinePSharp.Namespaces.MountFlags.Replace);
            return await Authsrv.ReadnvramAsync(c, new Nvrsafe(), 0);
        }
    }

    [Fact]
    public async Task AuthdialFindsTheAuthServerOfTheDomainByCsTryingEachInTurn()
    {
        int port = host.HostPort(17568);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        await WriteFileAsync("/tmp/ndb", Encoding.UTF8.GetBytes(
            "authdom=fog auth=127.0.0.2 auth=127.0.0.1\nauthdom=two dom=two.example\ndom=ip.example auth=127.0.0.1\nip=192.168.1.10 auth=127.0.0.1\ntcp=ticket port=17568\n"));
        host.Names["p9auth.two"] = [IPAddress.Loopback];
        init.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/ndb/cs", ["ndb/cs", "-f", "/tmp/ndb"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);

        foreach (string? dom in new[] { "fog", "ip.example", "two", null })
        {
            Task<Socket> accepted = listener.AcceptSocketAsync();
            int fd = await Authsrv.AuthdialAsync(init, null, dom);
            Assert.True(fd >= 0);
            using Socket call = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
            await init.WriteAsync(fd, "hi"u8.ToArray());
            byte[] got = new byte[2];
            Assert.Equal(2, await call.ReceiveAsync(got));
            await init.CloseAsync(fd);
        }

        // p9auth.dom of a name nothing resolves fails, saying why as dial does.
        string why = (await Assert.ThrowsAsync<SyscallException>(() => Net.DialAsync(init, "net!p9auth.none.example!ticket"))).Message;
        Errstr.Set(init, "before");
        Assert.Equal(-1, await Authsrv.AuthdialAsync(init, "/net", "none.example"));
        Assert.Equal(why, Errstr.Get(init));
    }

    [Fact]
    public async Task TheNvramMayBeDescriptorZeroAndIsClosedWhateverHappens()
    {
        Safe("bootes", "fog", Aes(1)).ToBytes().CopyTo(nvram, 0);
        await WriteFileAsync("/tmp/nvr", Safe("glenda", "fog", Aes(2)).ToBytes());
        await WriteFileAsync("/tmp/9fat", Fat("PLAN9   NVR", Safe("dos", "fog", Aes(2)).ToBytes()));
        string fds = await FdsAsync(init);
        Assert.Equal(0, await InNamespaceAsync(ReadAsZeroAsync));
        Assert.Equal("adm", Authsrv.Str(Nvrsafe.FromBytes(nvram).Authid));

        // Each was found where it was looked for, with nothing to say of it.
        await init.WriteAsync(2, "sentinel"u8.ToArray());
        Assert.Equal("sentinel", await ErrorsAsync(1));

        // A read, a write, a file system without the file: nothing is left open.
        await Authsrv.ReadnvramAsync(init, new Nvrsafe(), Authsrv.NVwritemem);
        await WriteFileAsync("/tmp/9fat", Fat("OTHER   NVR", []));
        await WriteFileAsync("/env/nvram", "/tmp/9fat"u8.ToArray());
        await Authsrv.ReadnvramAsync(init, new Nvrsafe(), 0);
        await ErrorsAsync(1);
        Assert.Equal(fds, await FdsAsync(init));
    }

    [Fact]
    public async Task WhatReadnvramSaysOfANvramItCannotOpenReadOrWriteNamesTheFile()
    {
        var safe = new Nvrsafe();
        await WriteFileAsync("/env/nvram", "/tmp/none"u8.ToArray());
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, Authsrv.NVwriteonerr));
        Assert.Equal("can't open /tmp/none: file does not exist: '/tmp/none'\n", await ErrorsAsync(1));

        // Without $nvram the file is #c's, which a short nvram cannot fill.
        await init.RemoveAsync("/env/nvram");
        Assert.Equal(-1, await InNamespaceAsync(UnboundAsync));
        Assert.Equal("readnvram: couldn't find nvram\n", await ErrorsAsync(1));

        static async Task<int> UnboundAsync(Process c)
        {
            await c.BindAsync("/tmp", "#c", NinePSharp.Namespaces.MountFlags.Replace);
            return await Authsrv.ReadnvramAsync(c, new Nvrsafe(), Authsrv.NVwriteonerr);
        }

        Assert.StartsWith("can't open <nil>: ", await ErrorsAsync(1), StringComparison.Ordinal);

        // One whose read fails, and one that cannot be written.
        await WriteFileAsync("/env/nvram", "/dev/caphash"u8.ToArray());
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, Authsrv.NVwriteonerr));
        Assert.Equal("can't read 126 bytes from /dev/caphash: permission denied\n", await ErrorsAsync(1));
        await WriteFileAsync("/tmp/ro", Safe("bootes", "fog", Aes(1)).ToBytes());
        NinePSharp.Messages.Stat d = Process.NullDir;
        await init.WStatAsync("/tmp/ro", new NinePSharp.Messages.Stat(0, d.Type, d.Dev, d.Qid, 0b100_100_100, d.Atime, d.Mtime, d.Length, d.Name, d.Uid, d.Gid, d.Muid));
        await WriteFileAsync("/env/nvram", "/tmp/ro"u8.ToArray());
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, Safe("adm", "fog", Aes(3)), Authsrv.NVwritemem));

        // Why the write failed, not why the file could not be opened for writing.
        string written = await ErrorsAsync(1);
        Assert.StartsWith("can't write key to nvram: ", written, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/ro", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABareKernelsShortNvramIsNamedAsNoFileWhenItIsReadFor()
    {
        Process bare = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", clock, host, console).BootAsync();
        for (int i = 0; i < 3; i++)
        {
            await bare.OpenAsync("/dev/null", NinePConstants.ORDWR);
        }

        (int read, int written) = await bare.PipeAsync();
        await bare.DupAsync(written, 2);
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(bare, new Nvrsafe(), Authsrv.NVwriteonerr));
        Assert.StartsWith("can't read 126 bytes from <nil>: ", Encoding.UTF8.GetString((await bare.ReadAsync(read, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneBadSumIsEnoughAndOnlyAFlagToWriteAsksAnything()
    {
        Nvrsafe one = Safe("bootes", "fog", Aes(1), machkey: [1, 2, 3, 4, 5, 6, 7]);
        one.Machsum++;
        one.ToBytes().CopyTo(nvram, 0);
        var safe = new Nvrsafe();
        string asked = console.Text;
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, safe, 0));
        Assert.Equal("bad nvram des key\n", await ErrorsAsync(1));
        Assert.Equal(("bootes", 0), (Authsrv.Str(safe.Authid), safe.Machkey.Sum(b => b)));
        Assert.Equal(asked, console.Text);

        // An nvram not found is no reason to clear what was to be written to it.
        await WriteFileAsync("/tmp/9fat", "x"u8.ToArray());
        await WriteFileAsync("/env/nvram", "/tmp/9fat"u8.ToArray());
        Nvrsafe given = Safe("adm", "fog", Aes(3));
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, given, Authsrv.NVwritemem));
        Assert.Equal("adm", Authsrv.Str(given.Authid));
    }

    [Fact]
    public async Task AnNvramLengthOfNoneReadsNothingAndOneLongerOrShorterIsWrittenWhole()
    {
        await WriteFileAsync("/tmp/nvr", Safe("bootes", "fog", Aes(2)).ToBytes());
        await WriteFileAsync("/env/nvram", "/tmp/nvr"u8.ToArray());
        await WriteFileAsync("/env/nvrlen", "0"u8.ToArray());
        Assert.Equal(-1, await Authsrv.ReadnvramAsync(init, new Nvrsafe(), 0));
        await ErrorsAsync(3);

        foreach (int len in new[] { 200, 10 })
        {
            await WriteFileAsync("/tmp/nvr", []);
            await WriteFileAsync("/env/nvrlen", Encoding.UTF8.GetBytes(len.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Equal(0, await Authsrv.ReadnvramAsync(init, Safe("adm", "fog", Aes(3)), Authsrv.NVwritemem));
            Assert.Equal((ulong)len, (await init.StatAsync("/tmp/nvr")).Length);
        }
    }

    [Fact]
    public async Task AFatNameIsUpperCasedAndPaddedAndItsFileFoundByTheFirstEntryAndItsCluster()
    {
        byte[] key = Safe("bootes", "fog", Aes(4)).ToBytes();
        var safe = new Nvrsafe();
        (string Nvram, byte[] Image)[] found =
        [
            ("/tmp/disk!ab", Fat("AB         ", key)),
            ("/tmp/disk!z.nvz", Fat("Z       NVZ", key)),
            ("/tmp/disk", Fat("PLAN9   NVR", key, nfats: 2, fatsize: 3)),
            ("/tmp/disk", Fat("PLAN9   NVR", key, start: 0)),
            ("/tmp/disk", Fat("PLAN9   NVR", key, also: ("PLAN9   NVR", 9))),
            ("/tmp/disk", Fat("PLAN9   NVR", key, start: 259)),
        ];
        await WriteFileAsync("/env/nvroff", "dos"u8.ToArray());
        foreach ((string nvramfile, byte[] image) in found)
        {
            await WriteFileAsync("/tmp/disk", image);
            await WriteFileAsync("/env/nvram", Encoding.UTF8.GetBytes(nvramfile));
            Assert.Equal(0, await Authsrv.ReadnvramAsync(init, safe, 0));
        }
    }

    [Fact]
    public void AnNvrsafeIsItsBytesInOrderAndClearedIsNone()
    {
        byte[] bytes = [.. Enumerable.Range(1, Nvrsafe.Size).Select(i => (byte)i)];
        Nvrsafe safe = Nvrsafe.FromBytes(bytes);
        Assert.Equal(bytes, safe.ToBytes());
        var copy = new Nvrsafe();
        safe.CopyTo(copy);
        Assert.Equal(bytes, copy.ToBytes());
        Assert.Equal([.. bytes[..10], .. new byte[Nvrsafe.Size - 10]], Nvrsafe.FromBytes(bytes[..10]).ToBytes());
        Assert.Equal(bytes, Nvrsafe.FromBytes([.. bytes, 9, 9]).ToBytes());
        copy.Clear();
        Assert.Equal(new byte[Nvrsafe.Size], copy.ToBytes());
    }

    [Fact]
    public void Dec16TakesHexDigitsOfEitherCasePassingOverAnythingElseAsManyAsFit()
    {
        byte[] output = new byte[8];
        Assert.Equal(6, Authsrv.Dec16(output, 8, "09:AF/af@g`G 1234567"));
        Assert.Equal(new byte[] { 0x09, 0xaf, 0xaf, 0x12, 0x34, 0x56, 0, 0 }, output);
        output = new byte[4];
        Assert.Equal(2, Authsrv.Dec16(output, 2, "0123456789"));
        Assert.Equal(new byte[] { 0x01, 0x23, 0, 0 }, output);
        Assert.Equal(1, Authsrv.Dec16(output, 4, "fe5"));
        Assert.Equal(0xfe, output[0]);
        Assert.Equal(0, Authsrv.Dec16(output, 4, "/:@G`g"));
    }

    private static Authkey Key(string password, string user)
    {
        Authkey k = Authkey.Passtokey(password);
        k.Hash(user);
        return k;
    }

    // A key whose PAK key an exchange with another of the same password made.
    private static Authkey Paked(string password, string user)
    {
        Authkey k = Key(password, user), other = Key(password, user);
        byte[] y = Authsrv.AuthpakNew(out Dp9ik.AuthPakState p, k, true);
        Authsrv.AuthpakFinish(p, k, Authsrv.AuthpakNew(out Dp9ik.AuthPakState q, other, false));
        Authsrv.AuthpakFinish(q, other, y);
        return k;
    }

    private static Ticket Ticket(byte num, byte form)
    {
        var t = new Ticket { Num = num, Form = form };
        "tchallen"u8.CopyTo(t.Chal);
        Authsrv.Safecpy(t.Cuid, "glenda");
        Authsrv.Safecpy(t.Suid, "none");
        for (int i = 0; i < t.Key.Length; i++)
        {
            t.Key[i] = (byte)(i + 1);
        }

        return t;
    }

    private static byte[] Pad(string s, int n) => [.. Encoding.UTF8.GetBytes(s), .. new byte[n - Encoding.UTF8.GetByteCount(s)]];

    private static byte[] Aes(byte b) => Enumerable.Repeat(b, Authsrv.Aeskeylen).ToArray();

    // An Nvrsafe of the fields given, each with its sum.
    private static Nvrsafe Safe(string authid, string authdom, byte[] aes, string config = "", byte[]? machkey = null)
    {
        var safe = new Nvrsafe();
        Authsrv.Safecpy(safe.Authid, authid);
        Authsrv.Safecpy(safe.Authdom, authdom);
        Authsrv.Safecpy(safe.Config, config);
        aes.CopyTo(safe.Aesmachkey, 0);
        (machkey ?? []).CopyTo(safe.Machkey, 0);
        safe.Machsum = Authsrv.Nvcsum(safe.Machkey);
        safe.Configsum = Authsrv.Nvcsum(safe.Config);
        safe.Authidsum = Authsrv.Nvcsum(safe.Authid);
        safe.Authdomsum = Authsrv.Nvcsum(safe.Authdom);
        safe.Aesmachsum = Authsrv.Nvcsum(safe.Aesmachkey);
        return safe;
    }

    // A FAT image of one reserved sector and its FATs, whose root holds the file, a dos name of
    // eleven, starting at the cluster given, with what is given at its start, and maybe another
    // entry after it.
    private static byte[] Fat(string name, byte[] contents, byte magic1 = 0x3c, byte magic2 = 0x90, int sectsize = 512, int nroot = 16, byte clust = 1, byte nfats = 1, byte fatsize = 1, int start = 3, (string Name, int Start)? also = null)
    {
        int rootoff = (1 + (nfats * fatsize)) * 512, rootsects = Math.Max(1, ((nroot * 32) + 511) / 512);
        int data = rootoff + (rootsects * 512) + ((start - 2) * 512 * clust);
        byte[] image = new byte[Math.Max(data, rootoff + (rootsects * 512)) + 1024];
        (image[0], image[1], image[2]) = (0xeb, magic1, magic2);
        (image[11], image[12]) = ((byte)sectsize, (byte)(sectsize >> 8));
        (image[13], image[14], image[16], image[22]) = (clust, 1, nfats, fatsize);
        (image[17], image[18]) = ((byte)nroot, (byte)(nroot >> 8));
        Encoding.ASCII.GetBytes(name).CopyTo(image, rootoff + 32);
        (image[rootoff + 32 + 26], image[rootoff + 32 + 27]) = ((byte)start, (byte)(start >> 8));
        if (also is { } other)
        {
            Encoding.ASCII.GetBytes(other.Name).CopyTo(image, rootoff + 64);
            image[rootoff + 64 + 26] = (byte)other.Start;
        }

        contents.CopyTo(image, data);
        return image;
    }

    private static async Task<int> ReadAsZeroAsync(Process c)
    {
        await c.CloseAsync(0);
        var safe = new Nvrsafe();
        Assert.Equal(0, await Authsrv.ReadnvramAsync(c, safe, 0));
        Assert.Equal("bootes", Authsrv.Str(safe.Authid));
        Assert.Equal(0, await Authsrv.ReadnvramAsync(c, Safe("adm", "fog", Aes(3)), Authsrv.NVwritemem));
        int env = await c.CreateAsync("/env/nvram", NinePConstants.OWRITE, 0b110_110_110);
        await c.WriteAsync(env, "/tmp/nvr"u8.ToArray());
        await c.CloseAsync(env);
        Assert.Equal(0, await Authsrv.ReadnvramAsync(c, safe, 0));
        Assert.Equal("glenda", Authsrv.Str(safe.Authid));
        env = await c.CreateAsync("/env/nvram", NinePConstants.OWRITE, 0b110_110_110);
        await c.WriteAsync(env, "/tmp/9fat"u8.ToArray());
        await c.CloseAsync(env);
        Assert.Equal(0, await Authsrv.ReadnvramAsync(c, safe, 0));
        Assert.Equal("dos", Authsrv.Str(safe.Authid));
        await Assert.ThrowsAsync<SyscallException>(() => c.FStatAsync(0).AsTask());
        return 0;
    }

    private static (int R, string Errstr) Botch((int R, byte[] Tbuf, byte[] Request, string Errstr) got) => (got.R, got.Errstr);

    private async Task<(int N, string Buf)> RdrespAsync(byte[] reply, int len)
    {
        (int r, int w) = await init.PipeAsync();
        if (reply.Length != 0)
        {
            await init.WriteAsync(w, reply);
        }

        await init.CloseAsync(w);
        (int n, byte[] buf) = await Authsrv.AsrdrespAsync(init, r, len);
        await init.CloseAsync(r);
        return (n, n < 0 ? Errstr.Get(init) : Encoding.UTF8.GetString(buf));
    }

    // _asgetticket on one end of a pipe, the test the auth server on the other: the request read,
    // the replies written in pieces, and the pipe then closed.
    private async Task<(int R, byte[] Tbuf, byte[] Request, string Errstr)> GetticketAsync(byte[][] replies, int tbuflen)
    {
        (int mine, int theirs) = await init.PipeAsync();
        var tr = new Ticketreq { Type = Authsrv.AuthTreq };
        Task<(int R, byte[] Tbuf)> got = Authsrv.AsgetticketAsync(init, mine, tr, tbuflen);
        byte[] request = (await init.ReadAsync(theirs, 1000)).ToArray();
        foreach (byte[] reply in replies)
        {
            await init.WriteAsync(theirs, reply);
        }

        await init.CloseAsync(theirs);
        (int r, byte[] tbuf) = await got.WaitAsync(TimeSpan.FromSeconds(5));
        await init.CloseAsync(mine);
        return (r, tbuf, request, Errstr.Get(init));
    }

    private async Task<T> InNamespaceAsync<T>(Func<Process, Task<T>> body)
    {
        var done = new TaskCompletionSource<T>();
        init.Fork(RforkFlags.Proc | RforkFlags.Nameg | RforkFlags.Fdg, async c =>
        {
            try
            {
                done.SetResult(await body(c));
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        });
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // What /proc says the process has open.
    private async Task<string> FdsAsync(Process p)
    {
        int f = await p.OpenAsync($"/proc/{p.Pid}/fd", NinePConstants.OREAD);
        var text = new StringBuilder();
        for (ReadOnlyMemory<byte> b; !(b = await p.ReadAsync(f, 8192)).IsEmpty;)
        {
            text.Append(Encoding.UTF8.GetString(b.Span));
        }

        await p.CloseAsync(f);

        // Each descriptor and its name, without the offset each has reached.
        return string.Join('\n', text.ToString().Split('\n').Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length > 1).Select(w => $"{w[0]} {w[^1]}"));
    }

    private async Task WriteFileAsync(string path, byte[] contents)
    {
        int f = await init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(f, contents);
        await init.CloseAsync(f);
    }

    // The next n writes on init's error output.
    private async Task<string> ErrorsAsync(int n)
    {
        var text = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            text.Append(Encoding.UTF8.GetString((await init.ReadAsync(errors, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Span));
        }

        return text.ToString();
    }
}
