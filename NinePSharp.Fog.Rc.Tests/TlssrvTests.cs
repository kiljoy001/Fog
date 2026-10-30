using System.Net;
using System.Net.Sockets;
using System.Text;
using Dp9ik.Tls;
using Microsoft.Extensions.Time.Testing;
using NinePSharp.Constants;
using NinePSharp.Fog.Auth;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// tlssrv under aux/listen, as tcp17019 runs it: a call authenticated with p9any through the host
// owner's factotum, encrypted with TLS keyed by the session secret, and the script the client sends
// run by rc as the user the call authenticated.
public sealed class TlssrvTests : IAsyncLifetime
{
    private const int Port = 17150;
    private readonly FakeHostNetwork host = new();
    private readonly FakeHostConsole console = new();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string> passwords = new() { ["glenda"] = "glendapw", ["bob"] = "bobpassw" };
    private readonly CancellationTokenSource stopping = new();
    private FogKernel kernel = null!;
    private Process init = null!;
    private TcpListener? listener;

    private string AuthAddr => "tcp!127.0.0.1!17567";

    public async Task InitializeAsync()
    {
        var programs = new Dictionary<string, ProgramMain>(Commands.Commands.All) { ["rc"] = RcProgram.MainAsync };
        kernel = FogKernel.InMemory(programs, "glenda", clock, host, console);
        init = await kernel.BootAsync();
        for (int i = 0; i < 3; i++)
        {
            await init.OpenAsync("/dev/null", NinePConstants.ORDWR);
        }

        await MkdirAsync("/root/rc/lib");
        await WriteAsync("/root/rc/lib/rcmain", RcProgram.Rcmain);
        await MkdirAsync("/root/usr/glenda");
        await MkdirAsync("/root/usr/bob");
        await MkdirAsync("/tmp/svc");
        await WriteAsync($"/tmp/svc/tcp{Port}", Services.Tcp17019);
    }

    public Task DisposeAsync()
    {
        stopping.Cancel();
        listener?.Stop();
        foreach (Process process in kernel.Processes.Values)
        {
            process.PostNote("kill");
        }

        return Task.CompletedTask;
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task TheScriptACallSendsRunsAsTheUserItAuthenticated()
    {
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        (Stream tls, _) = await ConnectAsync(client);
        await SendScriptAsync(tls, "echo hello; cat /dev/user");
        Assert.Equal("hello\nbob", await ReadAllAsync(tls));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ADescriptorAsAStreamReadsAndWritesButDoesNotSeekAndEndsWhenItsReadFails()
    {
        var stream = new Fdstream(init, 99);
        Assert.Equal((true, false, true), (stream.CanRead, stream.CanSeek, stream.CanWrite));
        Assert.Equal(0, await stream.ReadAsync(new byte[8]));
        IOException error = await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(new byte[1]).AsTask());
        Assert.IsType<SyscallException>(error.InnerException);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task WithMinusAAuthenticationLeavesTheUserAsItWas()
    {
        await ServiceAsync("exec tlssrv -A /bin/rc -c server");
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        (Stream tls, _) = await ConnectAsync(client);
        await SendScriptAsync(tls, "cat /dev/user");
        Assert.Equal("none", await ReadAllAsync(tls));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ACommandThatCannotRunIsReportedAndTheCallHungUp()
    {
        await ServiceAsync("exec tlssrv -a -r far /bin/nonesuch >[2]/tmp/err");
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        (Stream tls, _) = await ConnectAsync(client);
        Assert.Equal(string.Empty, await ReadAllAsync(tls));
        Assert.StartsWith("tlssrv: far tls reports can't exec /bin/nonesuch: ", await WaitForTextAsync("/tmp/err"), StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AUserItCannotBecomeIsFatalAndNothingRuns()
    {
        await ServiceAsync("bind /tmp/svc /dev; exec tlssrv -a /bin/rc -c server >[2]/tmp/err");
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        int data = await DialAsync(client);
        await Libauth.AuthProxyAsync(client, data, null, "proto=p9any role=client").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("tlssrv: auth_chuid: opening /dev/capuse: ", await WaitForTextAsync("/tmp/err"), StringComparison.Ordinal);
        Assert.DoesNotContain(kernel.Processes.Values, p => p.Text == "rc" && p.User == "bob");
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task WithMinusDTheHandshakeIsReportedToTheLogOrStandardError()
    {
        await MkdirAsync("#R/sys");
        await MkdirAsync("#R/sys/log");
        await WriteAsync("/sys/log/tls", string.Empty, 0b110_110_110);
        await ServiceAsync("exec tlssrv -D -a -l tls -r far /bin/rc -c server");
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        (Stream tls, _) = await ConnectAsync(client);
        await SendScriptAsync(tls, "echo hi");
        Assert.Equal("hi", await ReadAllAsync(tls));
        Assert.EndsWith(" far tls reports open\n", await WaitForTextAsync("/sys/log/tls"), StringComparison.Ordinal);
        Assert.DoesNotContain("tls reports", console.Text, StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AClientThatHangsUpEndsTheCommandsInput()
    {
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        (Stream tls, _, _) = await ConnectWithCallAsync(client);
        await SendScriptAsync(tls, "cat");
        await UntilAsync(() => kernel.Processes.Values.Any(p => p.Text == "cat"));
        await Task.Run(tls.Dispose);
        await UntilAsync(() => !kernel.Processes.Values.Any(p => p.Text is "cat" or "rc" or "tlssrv"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task AClientThatSendsARecordThatFailsItsCheckEndsTheCommandsInput()
    {
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        (Stream tls, _, int data) = await ConnectWithCallAsync(client);
        await SendScriptAsync(tls, "cat");
        await UntilAsync(() => kernel.Processes.Values.Any(p => p.Text == "cat"));
        await client.WriteAsync(data, (byte[])[0x17, 0x03, 0x03, 0x00, 0x30, .. new byte[0x30]]);
        await UntilAsync(() => !kernel.Processes.Values.Any(p => p.Text is "cat" or "rc" or "tlssrv"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ACommandThatEndsClosesTlsAndEndsTheRelaysThoughTheClientStays()
    {
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        (Stream tls, _, _) = await ConnectWithCallAsync(client);
        await SendScriptAsync(tls, "echo done");
        var all = new List<byte>();
        byte[] buf = new byte[100];
        for (int n; (n = await Task.Run(() => tls.Read(buf, 0, buf.Length)).WaitAsync(TimeSpan.FromSeconds(10))) > 0;)
        {
            all.AddRange(buf.AsSpan(0, n));
        }

        Assert.Equal("done\n", Encoding.UTF8.GetString([.. all]));
        await UntilAsync(() => !kernel.Processes.Values.Any(p => p.Text is "rc" or "tlssrv"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ACommandThatEndsEndsTheRelaysThoughTheClientNeitherReadsNorHangsUp()
    {
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        (Stream tls, _, _) = await ConnectWithCallAsync(client);
        await SendScriptAsync(tls, "echo done");
        await UntilAsync(() => !kernel.Processes.Values.Any(p => p.Text is "rc" or "tlssrv"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ARelayEndsWithAnEmptyStatusForTheCommandToWaitFor()
    {
        await ServiceAsync("exec tlssrv -a /bin/rc -c 'wait; echo -n s$status >/tmp/st'");
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        (Stream tls, _, _) = await ConnectWithCallAsync(client);
        await UntilAsync(() => kernel.Processes.Values.Any(p => p.Text == "rc"));
        await Task.Run(tls.Dispose);
        Assert.Equal("s", await WaitForTextAsync("/tmp/st"));
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task WithMinusDAHandshakeThatFailsIsReportedAndNothingRuns()
    {
        await ServiceAsync("exec tlssrv -D -a /bin/rc -c server >[2]/tmp/err");
        await HostAsync();
        Process client = await ClientAsync("bob", "bobpassw");
        int data = await DialAsync(client);
        await Libauth.AuthProxyAsync(client, data, null, "proto=p9any role=client").WaitAsync(TimeSpan.FromSeconds(10));
        await client.WriteAsync(data, new byte[64]);
        Assert.StartsWith("tlssrv:  tls reports failed: ", await WaitForTextAsync("/tmp/err"), StringComparison.Ordinal);
        Assert.DoesNotContain(kernel.Processes.Values, p => p.Text == "rc");
    }

    [Theory(Timeout = SecstoreHarness.TestTimeout)]
    [InlineData(new string[0], "usage", "usage: tlssrv [-D] -[aA] [-k keyspec]] [-c cert] [-l logfile] [-r remotesys] cmd [args...]\n")]
    [InlineData(new[] { "-x", "rc" }, "usage", "usage: tlssrv [-D] -[aA] [-k keyspec]] [-c cert] [-l logfile] [-r remotesys] cmd [args...]\n")]
    [InlineData(new[] { "-k" }, "usage", "usage: tlssrv [-D] -[aA] [-k keyspec]] [-c cert] [-l logfile] [-r remotesys] cmd [args...]\n")]
    [InlineData(new[] { "-c" }, "usage", "usage: tlssrv [-D] -[aA] [-k keyspec]] [-c cert] [-l logfile] [-r remotesys] cmd [args...]\n")]
    [InlineData(new[] { "-l" }, "usage", "usage: tlssrv [-D] -[aA] [-k keyspec]] [-c cert] [-l logfile] [-r remotesys] cmd [args...]\n")]
    [InlineData(new[] { "-r" }, "usage", "usage: tlssrv [-D] -[aA] [-k keyspec]] [-c cert] [-l logfile] [-r remotesys] cmd [args...]\n")]
    [InlineData(new[] { "rc" }, "no certificate or shared secret", "tlssrv: no certificate or shared secret\n")]
    [InlineData(new[] { "-c", "/tmp/cert", "rc" }, "/tmp/cert: certificates are not supported", "tlssrv: /tmp/cert: certificates are not supported\n")]
    [InlineData(new[] { "-a", "-k", "dom=fog", "rc" }, "auth_proxy: opening /mnt/factotum/rpc: ", "tlssrv: auth_proxy: opening /mnt/factotum/rpc: ")]
    [InlineData(new[] { "-A", "rc" }, "auth_proxy: opening /mnt/factotum/rpc: ", "tlssrv: auth_proxy: opening /mnt/factotum/rpc: ")]
    public async Task WhatTlssrvCannotDoIsSaidBeforeAnythingIsRead(string[] arguments, string status, string error)
    {
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async c =>
        {
            await c.DupAsync(await c.CreateAsync("/tmp/err", NinePConstants.OWRITE, 0b110_110_110), 2);
            await c.ExecAsync("/bin/tlssrv", ["tlssrv", .. arguments]);
        });
        Waitmsg w = await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith(status, w.Message[(w.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..], StringComparison.Ordinal);
        Assert.StartsWith(error, await TextAsync("/tmp/err"), StringComparison.Ordinal);
    }

    [Fact(Timeout = SecstoreHarness.TestTimeout)]
    public async Task ACallThatDoesNotCompleteP9anyIsClosedWithoutTls()
    {
        await HostAsync();
        int data = await DialAsync(init);
        await init.WriteAsync(data, "nonsense\0"u8.ToArray());
        var all = new List<byte>();
        for (ReadOnlyMemory<byte> b; !(b = await init.ReadAsync(data, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsEmpty;)
        {
            all.AddRange(b.Span);
        }

        // Only p9any's offer is sent before the call is closed.
        Assert.Equal("dp9ik@fog\0", Encoding.UTF8.GetString([.. all]));
        Assert.DoesNotContain(kernel.Processes.Values, p => p.Text is "rc" or "tlssrv");
    }

    // The script as rconnect's sendscript sends it: its length on a line, as wc -c prints it, then it.
    private static async Task SendScriptAsync(Stream tls, string script)
    {
        byte[] s = Encoding.UTF8.GetBytes(script + "\n");
        await Task.Run(() => tls.Write([.. Encoding.UTF8.GetBytes($"{s.Length,7}\n"), .. s]));
    }

    private static async Task<string> ReadAllAsync(Stream tls)
    {
        var all = new List<byte>();
        byte[] buf = new byte[8192];
        try
        {
            for (int n; (n = await Task.Run(() => tls.Read(buf, 0, buf.Length)).WaitAsync(TimeSpan.FromSeconds(10))) > 0;)
            {
                all.AddRange(buf.AsSpan(0, n));
            }
        }
        catch (IOException)
        {
            // The host hung up without TLS's close.
        }

        return Encoding.UTF8.GetString([.. all]).TrimEnd('\n');
    }

    private static async Task<int> DialAsync(Process p)
    {
        int ctl = await p.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR);
        string n = Encoding.UTF8.GetString((await p.ReadAsync(ctl, 32)).Span);
        await p.WriteAsync(ctl, Encoding.UTF8.GetBytes($"connect 127.0.0.1!{Port}"));
        return await p.OpenAsync($"/net/tcp/{n}/data", NinePConstants.ORDWR);
    }

    private static async Task CtlAsync(Process p, string key)
    {
        int ctl = await p.OpenAsync("/mnt/factotum/ctl", NinePConstants.OWRITE);
        await p.WriteAsync(ctl, Encoding.UTF8.GetBytes(key)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await p.CloseAsync(ctl);
    }

    private async Task UntilAsync(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(done(), string.Join(" | ", kernel.Processes.Values.Select(p => $"{p.Pid}:{p.Text}:{p.User}")));
    }

    // The service's script, with its last line in place of tcp17019's.
    private Task ServiceAsync(string exec)
        => WriteAsync($"/tmp/svc/tcp{Port}", Services.Tcp17019.Replace("exec tlssrv -a /bin/rc -c server", exec, StringComparison.Ordinal));

    private async Task<string> TextAsync(string path)
    {
        int f = await init.OpenAsync(path, NinePConstants.OREAD);
        var all = new List<byte>();
        for (ReadOnlyMemory<byte> b; !(b = await init.ReadAsync(f, 8192)).IsEmpty;)
        {
            all.AddRange(b.Span);
        }

        await init.CloseAsync(f);
        return Encoding.UTF8.GetString([.. all]);
    }

    // The file's text once something is in it.
    private async Task<string> WaitForTextAsync(string path)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                if (await TextAsync(path) is { Length: > 0 } text)
                {
                    return text;
                }
            }
            catch (SyscallException)
            {
                // Not made yet.
            }

            await Task.Delay(50);
        }

        return string.Empty;
    }

    // The host: its owner's factotum with the owner's key, the auth server, and aux/listen with
    // tcp17019 for the port.
    private async Task HostAsync()
    {
        Serve();
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, child => child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n", "-p", "-s", "factotum"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        await CtlAsync(init, "key proto=dp9ik user=glenda dom=fog !password=glendapw");
        init.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/aux/listen", ["aux/listen", "-q", "-d", "/tmp/svc", "-a", "127.0.0.1", "tcp"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        for (int i = 0; i < 100 && !await AnsweredAsync(); i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(50);
        }
    }

    // A process in a namespace of its own, with a factotum of its own holding the user's key, which
    // gets tickets from the auth server.
    private async Task<Process> ClientAsync(string user, string password)
    {
        var made = new TaskCompletionSource<Process>();
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg, async c =>
        {
            try
            {
                c.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg, f => f.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n", "-p", "-s", $"factotum.{user}", "-a", AuthAddr]));
                await c.WaitAsync();
                await c.MountAsync(await c.OpenAsync($"/srv/factotum.{user}", NinePConstants.ORDWR), -1, "/mnt", MountFlags.Replace, string.Empty);
                await CtlAsync(c, $"key proto=dp9ik user={user} dom=fog !password={password}");
                made.SetResult(c);
                await Task.Delay(Timeout.Infinite, stopping.Token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                made.TrySetException(error);
            }
        });
        return await made.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // The call, p9any as the client, and TLS keyed by the secret.
    private async Task<(Stream Tls, AuthInfo Ai)> ConnectAsync(Process client)
    {
        (Stream tls, AuthInfo ai, _) = await ConnectWithCallAsync(client);
        return (tls, ai);
    }

    // As ConnectAsync, with the call's data descriptor under TLS.
    private async Task<(Stream Tls, AuthInfo Ai, int Data)> ConnectWithCallAsync(Process client)
    {
        int data = await DialAsync(client);
        AuthInfo ai = await Libauth.AuthProxyAsync(client, data, null, "proto=p9any role=client").WaitAsync(TimeSpan.FromSeconds(10));
        Stream tls = await P9PskTls.ConnectAsync(new Fdstream(client, data), ai.Secret, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        return (tls, ai, data);
    }

    private async Task<bool> AnsweredAsync()
    {
        int ctl = await init.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR);
        try
        {
            await init.WriteAsync(ctl, Encoding.UTF8.GetBytes($"connect 127.0.0.1!{Port}"));
            return true;
        }
        catch (SyscallException)
        {
            return false;
        }
        finally
        {
            await init.CloseAsync(ctl);
        }
    }

    // authsrv on the port the -a address names.
    private void Serve()
    {
        listener = new TcpListener(IPAddress.Loopback, host.HostPort(17567));
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                Socket socket = await listener.AcceptSocketAsync(stopping.Token);
                _ = Task.Run(async () =>
                {
                    using var stream = new NetworkStream(socket, ownsSocket: true);
                    try
                    {
                        await new AuthServerConnection(stream, new Database(passwords), "127.0.0.1", []).ServeAsync(stopping.Token);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or EndOfStreamException)
                    {
                    }
                });
            }
        });
    }

    private async Task MkdirAsync(string path)
        => await init.CloseAsync(await init.CreateAsync(path, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));

    private async Task WriteAsync(string path, string text, uint mode = 0b111_111_101)
    {
        int f = await init.CreateAsync(path, NinePConstants.OWRITE, mode);
        await init.WriteAsync(f, Encoding.UTF8.GetBytes(text));
        await init.CloseAsync(f);
    }

    // keyfs as authsrv sees it: each user's key from a password, hashed for the user.
    private sealed class Database(Dictionary<string, string> passwords) : IAuthDatabase
    {
        public Dp9ik.AuthKey? FindKey(string user)
        {
            if (!passwords.TryGetValue(user, out string? password))
            {
                return null;
            }

            Dp9ik.AuthKey key = Dp9ik.AuthKey.FromPassword(password);
            key.ApplyAuthPakHash(user);
            return key;
        }

        public bool SetKey(string user, Dp9ik.AuthKey key) => false;

        public bool SetSecret(string user, string secret) => false;

        public void Succeed(string user)
        {
        }
    }
}
