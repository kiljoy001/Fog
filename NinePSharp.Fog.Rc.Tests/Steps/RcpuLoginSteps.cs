using System.Net;
using System.Net.Sockets;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Auth;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Namespaces.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using Reqnroll;
using Reqnroll.UnitTestProvider;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests.Steps;

[Binding]
[Scope(Feature = "Stock 9front rcpu runs rc on Fog through tcp17019")]
public sealed class RcpuLoginSteps(IUnitTestRuntimeProvider runtime)
{
    private const string Domain = "fog";
    private static readonly SemaphoreSlim Booting = new(1, 1);
    private static NineFrontTerminal? terminal;
    private readonly FakeHostNetwork host = new();
    private readonly FakeHostConsole console = new();
    private readonly Dictionary<string, string> passwords = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private FogKernel kernel = null!;
    private Process init = null!;
    private TcpListener? authsrv;
    private string printed = string.Empty;
    private string errors = string.Empty;
    private string status = string.Empty;
    private string closed = string.Empty;

    private int Port => host.HostPort(17019);

    [AfterTestRun]
    public static async Task StopTerminalAsync()
    {
        if (terminal is not null)
        {
            await terminal.DisposeAsync();
            terminal = null;
        }
    }

    [Given(@"^a Fog host listening on tcp port 17019, and the auth server for the domain ""fog"" on 567$")]
    public async Task GivenHostAsync()
    {
        var programs = new Dictionary<string, ProgramMain>(Commands.Commands.All) { ["rc"] = RcProgram.MainAsync };
        kernel = FogKernel.InMemory(programs, "glenda", TimeProvider.System, host, console);
        init = await kernel.BootAsync();
        for (int i = 0; i < 3; i++)
        {
            await init.OpenAsync("/dev/null", NinePConstants.ORDWR);
        }

        await MkdirAsync("/root/rc/lib");
        await WriteAsync("/root/rc/lib/rcmain", RcProgram.Rcmain);
        await MkdirAsync("/root/usr/glenda");
        await MkdirAsync("/tmp/svc");
        await WriteAsync("/tmp/svc/tcp17019", Services.Tcp17019);
        ServeAuth();
    }

    [Given(@"^the user ""(.*)"" with the password ""(.*)""$")]
    public async Task GivenUserAsync(string user, string password)
    {
        passwords[user] = password;
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, child => child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n", "-p", "-s", "factotum"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        int ctl = await init.OpenAsync("/mnt/factotum/ctl", NinePConstants.OWRITE);
        await init.WriteAsync(ctl, Encoding.UTF8.GetBytes($"key proto=dp9ik user={user} dom={Domain} !password={password}"));
        await init.CloseAsync(ctl);
        init.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/aux/listen", ["aux/listen", "-q", "-d", "/tmp/svc", "-a", "127.0.0.1", "tcp"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        for (int i = 0; i < 100 && !await AnsweredAsync(); i++)
        {
            await Task.Delay(100);
        }
    }

    [Given(@"^9front's ""(.*)"" holds ""(.*)""$")]
    public async Task GivenNineFrontFileAsync(string path, string text)
    {
        NineFrontTerminal t = await TerminalAsync();
        await t.RunAsync($"echo '{text}' >{path}");
    }

    [When(@"^9front runs ""(.*)""$")]
    public async Task WhenNineFrontRunsAsync(string command)
    {
        NineFrontTerminal t = await TerminalAsync();
        await KeyAsync(t, "glenda", passwords["glenda"]);
        string line = command.Replace("tcp!fog!17019", $"tcp!10.0.2.2!{Port}", StringComparison.Ordinal);
        string all;
        try
        {
            all = await t.RunAsync($"@{{{line}}} >/tmp/rout >[2]/tmp/rerr; st=$status; cat /tmp/rout; echo '#errors'; cat /tmp/rerr; echo '#status' $st", TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException error)
        {
            string fds = string.Empty;
            foreach (Process p in kernel.Processes.Values)
            {
                try
                {
                    int f = await init.OpenAsync($"/proc/{p.Pid}/fd", NinePConstants.OREAD);
                    fds += $"{p.Pid} {p.Text}:\n";
                    for (ReadOnlyMemory<byte> b; !(b = await init.ReadAsync(f, 8192)).IsEmpty;)
                    {
                        fds += Encoding.UTF8.GetString(b.Span);
                    }

                    await init.CloseAsync(f);
                }
                catch (SyscallException)
                {
                }
            }

            throw new TimeoutException($"{fds}\n{Failure()}", error);
        }

        int e = all.LastIndexOf("#errors\n", StringComparison.Ordinal);
        int s = all.LastIndexOf("#status", StringComparison.Ordinal);
        Assert.True(e >= 0 && s > e, all);
        printed = all[..e];
        errors = all[(e + "#errors\n".Length)..s];
        status = all[(s + "#status".Length)..].Trim();
    }

    [When(@"^9front runs rcpu as ""(.*)"" with the password ""(.*)""$")]
    public async Task WhenNineFrontRunsWithPasswordAsync(string user, string password)
    {
        NineFrontTerminal t = await TerminalAsync();
        await KeyAsync(t, user, password);
        await t.SendLineAsync($"rcpu -h tcp!10.0.2.2!{Port} -u {user} -c 'echo in'");
        printed = await t.ExpectAsync("!Adding key: ", TimeSpan.FromSeconds(60));
    }

    [When(@"^a client connects to 17019 and sends ""(.*)""$")]
    public async Task WhenClientSendsAsync(string text)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text.Replace("\\0", "\0", StringComparison.Ordinal)));
        var all = new List<byte>();
        byte[] buf = new byte[8192];
        for (int n; (n = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10))) > 0;)
        {
            all.AddRange(buf.AsSpan(0, n));
        }

        closed = Encoding.UTF8.GetString([.. all]);
    }

    [Then(@"^it prints ""(.*)""$")]
    public void ThenPrints(string text) => Assert.True(printed.TrimEnd('\n') == Unescape(text), Failure());

    [Then(@"^it prints ""(.*)"" on its standard error$")]
    public void ThenPrintsError(string text) => Assert.True(errors.Contains(Unescape(text), StringComparison.Ordinal), Failure());

    [Then(@"^its status is empty$")]
    public void ThenStatusEmpty() => Assert.True(status.Length == 0, Failure());

    // Without the name and pid each wait message puts before the status it passes on.
    [Then(@"^its status is ""(.*)""$")]
    public void ThenStatus(string text) => Assert.True(System.Text.RegularExpressions.Regex.Replace(status, @"^(\S+ \d+: )+", string.Empty) == text, Failure());

    [Then(@"^the host closes the call$")]
    public void ThenClosed() => Assert.Equal($"dp9ik@{Domain}\0", closed);

    [Then(@"^no process is started for the call$")]
    public async Task ThenNoProcessAsync()
    {
        await Task.Delay(500);

        // tlssrv may still be waiting for p9any, as none; nothing has become the user or run rc.
        Assert.DoesNotContain(kernel.Processes.Values, p => p.Text == "rc" || (p.Text == "tlssrv" && p.User != "none"));
    }

    [Then(@"^factotum asks for the key again$")]
    public async Task ThenAsksAgainAsync()
    {
        Assert.Contains($"dom={Domain}", await terminal!.ExpectAsync("\n", TimeSpan.FromSeconds(10)), StringComparison.Ordinal);
        await terminal.ExpectAsync("password: ", TimeSpan.FromSeconds(10));
        await terminal.SendLineAsync(passwords["glenda"]);
        Assert.Contains("in", await terminal.ExpectAsync(NineFrontTerminal.Prompt, TimeSpan.FromSeconds(60)), StringComparison.Ordinal);
    }

    [AfterScenario]
    public void CleanUp()
    {
        stopping.Cancel();
        authsrv?.Stop();
        foreach (Process process in kernel?.Processes.Values ?? [])
        {
            process.PostNote("kill");
        }
    }

    // The terminal's factotum holding only this key for the domain.
    private static async Task KeyAsync(NineFrontTerminal t, string user, string password)
    {
        await t.RunAsync($"echo 'delkey proto=dp9ik dom={Domain}' >/mnt/factotum/ctl >[2]/dev/null");
        await t.RunAsync($"echo 'key proto=dp9ik dom={Domain} user={user} !password={password}' >/mnt/factotum/ctl");
    }

    private static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.Ordinal);

    // The terminal, booted once for the run and put in Fog's auth domain: the live CD does not
    // configure its interface and its /lib/ndb is read-only, and cs and factotum are started again
    // so that both see the auth domain.
    private async Task<NineFrontTerminal> TerminalAsync()
    {
        if (NineFrontTerminal.Unavailable is string reason)
        {
            runtime.TestIgnore(reason);
        }

        await Booting.WaitAsync();
        try
        {
            if (terminal is null)
            {
                NineFrontTerminal t = await NineFrontTerminal.BootAsync();
                await t.RunAsync("ip/ipconfig");
                await t.RunAsync("cp /lib/ndb/local /tmp/ndblocal");
                await t.RunAsync($"echo 'authdom={Domain} auth=tcp!10.0.2.2!{host.HostPort(567)}' >>/tmp/ndblocal");
                await t.RunAsync("bind /tmp/ndblocal /lib/ndb/local");
                await t.RunAsync("ndb/cs");
                await t.RunAsync("auth/factotum");
                terminal = t;
            }

            return terminal;
        }
        finally
        {
            Booting.Release();
        }
    }

    private string Failure()
        => $"printed:\n{printed}\nerrors:\n{errors}\nstatus: {status}\nconsole: {console.Text}\nprocesses: {string.Join(" | ", kernel.Processes.Values.Select(p => $"{p.Pid}:{p.Text}:{p.Args}:{p.User}"))}\n9front:\n{terminal?.Transcript(2000)}";

    private async Task<bool> AnsweredAsync()
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, Port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // authsrv on the port 567 is on the host, with the users' passwords.
    private void ServeAuth()
    {
        authsrv = new TcpListener(IPAddress.Loopback, host.HostPort(567));
        authsrv.Start();
        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                Socket socket = await authsrv.AcceptSocketAsync(stopping.Token);
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

    private async Task WriteAsync(string path, string text)
    {
        int f = await init.CreateAsync(path, NinePConstants.OWRITE, 0b111_111_101);
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
