using System.Net;
using System.Net.Sockets;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Auth;
using NinePSharp.Fog.Client;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Host;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Namespaces.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Fog.Wasm;
using NinePSharp.Namespaces;
using Reqnroll;
using Xunit;
using static NinePSharp.Fog.Rc.Tests.Support.FogWords;

namespace NinePSharp.Fog.Rc.Tests.Steps;

[Binding]
[Scope(Feature = "A first session on a Fog host booted from its gefs store")]
public sealed class FirstSessionSteps
{
    // The host owner's password, which the auth server knows and the host's nvram holds the key of.
    private const string OwnerPassword = "bootespw";

    // glenda's secstore password, which secuser sets and the session's console gives.
    private const string SecstorePassword = "secstorepw";
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"fog-session-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> passwords = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly Recorded fogOutput = new();
    private readonly Recorded fogErrors = new();
    private readonly Recorded fogTerminal = new();
    private readonly FakeHostConsole console = new();
    private readonly FakeHostNetwork network = new();
    private FogHost? host;
    private TcpListener? authsrv;
    private string owner = "bootes";
    private string domain = "fog";
    private string sealedPassword = OwnerPassword;
    private string nineFrontOutput = string.Empty;
    private string nineFrontErrors = string.Empty;
    private string? fogDirectory;
    private int? fogExit;

    private string StoreFile => Path.Combine(directory, "fog.gefs");

    private Process Init => host!.Init;

    [AfterScenario]
    public async Task StopAllAsync()
    {
        await StopAsync();
        await stopping.CancelAsync();
        authsrv?.Stop();
        stopping.Dispose();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }

        if (fogDirectory is not null)
        {
            Directory.Delete(fogDirectory, true);
        }
    }

    [Given(@"^a host whose store file is in a new directory, with the host owner ""(.*)"" of the domain ""(.*)""$")]
    public void GivenHost(string user, string dom)
    {
        Directory.CreateDirectory(directory);
        (owner, domain) = (user, dom);
        passwords[owner] = OwnerPassword;
    }

    [Given(@"^the auth server for ""(.*)"" knows ""(.*)"" with the password ""(.*)""$")]
    public void GivenAuthUser(string dom, string user, string password)
    {
        Assert.Equal(domain, dom);
        passwords[user] = password;
        authsrv ??= ServeAuth();
    }

    [Given("the host has booted")]
    public async Task GivenBootedAsync() => await BootAsync();

    [When("the host stops and boots again, its sealed key made from a password the auth server does not know")]
    public async Task WhenRestartedWithAnotherKeyAsync()
    {
        sealedPassword = "otherpw";
        await StopAsync();
        await BootAsync();
    }

    [When("the host stops and boots again on the same store")]
    public async Task WhenRestartedAsync()
    {
        await StopAsync();
        await BootAsync();
    }

    // As gefs(8) adds a user: a line in /adm/users, which the store reads again, and /usr/$user
    // made theirs, its owner and group the user.
    [Given(@"^""([^""]*)"" has been added as gefs adds a user$")]
    public Task GivenUserAsync(string user) => AddUserAsync(user, null);

    [Given(@"^""([^""]*)"" has been added as gefs adds a user, a member of ""([^""]*)""$")]
    public Task GivenUserInGroupAsync(string user, string group) => AddUserAsync(user, group);

    [Given(@"^""(.*)"" on the store holds the application ""(.*)""$")]
    public async Task GivenApplicationAsync(string dir, string name)
    {
        int f = await Init.CreateAsync($"{dir}/{name}", NinePConstants.OWRITE, 0b111_111_101);
        await Init.WriteAsync(f, Encoding.UTF8.GetBytes("an application"));
        await Init.CloseAsync(f);
    }

    [Given(@"^fog's local directory holds ""(.*)"" with ""(.*)""$")]
    public void GivenLocalFile(string name, string text)
    {
        fogDirectory ??= Directory.CreateTempSubdirectory("fog-local-").FullName;
        File.WriteAllText(Path.Combine(fogDirectory, name), Unescape(text));
    }

    [Given(@"^fog's local directory holds the WASM applications ""(.*)"" and ""(.*)""$")]
    public void GivenLocalApplications(string a, string b)
    {
        fogDirectory ??= Directory.CreateTempSubdirectory("fog-local-").FullName;
        foreach (string name in new[] { a, b })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", "wasm", name), Path.Combine(fogDirectory, name));
        }
    }

    [Given(@"^fog has run ""([^""]*)""$")]
    [When(@"^fog runs ""([^""]*)""$")]
    public Task WhenFogRunsAsync(string args) => RunFogAsync(args, null, []);

    [Given(@"^fog has run ""([^""]*)"" in that directory$")]
    [When(@"^fog runs ""([^""]*)"" in that directory$")]
    public Task WhenFogRunsInDirectoryAsync(string args) => RunFogAsync(args, null, []);

    [When(@"^fog runs ""([^""]*)"" in that directory with \$PASS ""([^""]*)""$")]
    public Task WhenFogRunsWithPassAsync(string args, string pass) => RunFogAsync(args, pass, []);

    // secuser makes glenda's account as the host owner, answering its prompts on the host's
    // console, and secstore puts the file in it through the secstored cpurc started.
    [Given(@"^glenda's secstore on the host holds the file ""(.*)"" with ""(.*)""$")]
    public async Task GivenSecstoreFileAsync(string file, string text)
    {
        console.Type($"{SecstorePassword}\n{SecstorePassword}\n\n\n\n\n");
        await RunAsOwnerAsync($"""
            auth/secuser glenda </dev/cons >/dev/null
            cd /tmp
            echo '{Unescape(text)}' >{file}
            echo {SecstorePassword} | auth/secstore -i -s tcp!127.0.0.1!5356 -u glenda -p {file}
            while(! test -s /adm/secstore/store/glenda/{file}) sleep 0.1
            test -s /adm/secstore/store/glenda/{file}

            """);
    }

    // The session's console gives glenda's secstore password to the first read of it.
    [When(@"^fog runs ""([^""]*)"" and is given glenda's secstore password$")]
    public Task WhenFogRunsWithSecstorePasswordAsync(string args) => RunFogAsync(args, null, [SecstorePassword]);

    [Then(@"^drawterm prints ""(.*)""$")]
    [Then(@"^fog prints ""(.*)""$")]
    public void ThenFogPrints(string text) => Assert.True(fogOutput.Text.TrimEnd('\n') == Unescape(text), FogFailure());

    // drawterm without graphics calls the host as a terminal with no window system does, its auth
    // server and secstore the host's forwarded 567 and 5356, and its console given glenda's
    // secstore password a line for each time it is asked.
    [When(@"^drawterm runs ""([^""]*)"" and is given glenda's secstore password twice$")]
    public async Task WhenDrawtermRunsAsync(string args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("drawterm") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string[] words =
        [
            "-G",
            "-h", $"tcp!127.0.0.1!{network.HostPort(17019)}",
            "-a", $"tcp!127.0.0.1!{network.HostPort(567)}",
            "-s", $"tcp!127.0.0.1!{network.HostPort(5356)}",
            .. Words(args),
        ];
        foreach (string word in words)
        {
            start.ArgumentList.Add(word);
        }

        // An empty namespace directory, so that drawterm finds no plan9port factotum of the machine's.
        fogDirectory ??= Directory.CreateTempSubdirectory("fog-local-").FullName;
        start.Environment["NAMESPACE"] = fogDirectory;
        using System.Diagnostics.Process drawterm = System.Diagnostics.Process.Start(start)!;
        await drawterm.StandardInput.WriteAsync($"{SecstorePassword}\n{SecstorePassword}\n");
        await drawterm.StandardInput.FlushAsync();
        Task output = drawterm.StandardOutput.BaseStream.CopyToAsync(fogOutput);
        Task errors = drawterm.StandardError.BaseStream.CopyToAsync(fogErrors);
        try
        {
            await drawterm.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await Task.WhenAll(output, errors);
            fogExit = drawterm.ExitCode;
        }
        catch (TimeoutException error)
        {
            drawterm.Kill();
            throw new TimeoutException(FogFailure(), error);
        }
    }

    [Then(@"^fog's terminal shows ""(.*)""$")]
    public void ThenTerminalShows(string text) => Assert.True(fogTerminal.Text == Unescape(text), FogFailure());

    [When(@"^9front runs ""(.*)""$")]
    public async Task WhenNineFrontRunsAsync(string command)
    {
        NineFrontTerminal t = await NineFront.TerminalAsync(network);
        await NineFront.KeyAsync(t, "glenda", passwords["glenda"]);
        string line = command.Replace("tcp!fog!17019", $"tcp!10.0.2.2!{network.HostPort(17019)}", StringComparison.Ordinal);
        string all = await t.RunAsync($"@{{{line}}} >/tmp/rout >[2]/tmp/rerr; cat /tmp/rout; echo '#errors'; cat /tmp/rerr", TimeSpan.FromSeconds(60));
        int e = all.LastIndexOf("#errors\n", StringComparison.Ordinal);
        Assert.True(e >= 0, all);
        (nineFrontOutput, nineFrontErrors) = (all[..e], all[(e + "#errors\n".Length)..]);
    }

    [Then(@"^9front prints ""(.*)""$")]
    public void ThenNineFrontPrints(string text) => Assert.True(nineFrontOutput.TrimEnd('\n') == Unescape(text), NineFrontFailure());

    [Then(@"^9front prints ""(.*)"" on its standard error$")]
    public void ThenNineFrontPrintsError(string text) => Assert.True(nineFrontErrors.Contains(Unescape(text), StringComparison.Ordinal), NineFrontFailure());

    // The host owner reads the log of the factotum the boot posted until it holds the line, each
    // read giving a line and waiting for the next.
    [Then(@"^the host owner's factotum logged ""(.*)""$")]
    public async Task ThenFactotumLoggedAsync(string text)
    {
        string log = string.Empty;
        Init.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg, async child =>
        {
            await child.MountAsync(await child.OpenAsync("/srv/factotum", NinePConstants.ORDWR), -1, "/mnt", MountFlags.Before, string.Empty);
            int f = await child.OpenAsync("/mnt/factotum/log", NinePConstants.OREAD);
            while (!log.Contains(Unescape(text), StringComparison.Ordinal))
            {
                log += Encoding.UTF8.GetString((await child.ReadAsync(f, 8192)).Span);
            }

            child.Exits(null);
        });
        string status;
        try
        {
            status = (await Init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10))).Message;
        }
        catch (TimeoutException)
        {
            status = "timed out";
        }

        Assert.True(status.Length == 0, $"{status}; the log holds:\n{log}");
    }

    [Then(@"^fog prints ""(.*)"" on its standard error$")]
    public void ThenFogPrintsError(string text) => Assert.True(fogErrors.Text.Contains(Unescape(text), StringComparison.Ordinal), FogFailure());

    [Then(@"^fog exits (\d+)$")]
    public void ThenFogExits(int code) => Assert.True(fogExit == code, FogFailure());

    [Then(@"^the host's processes include ""(.*)"", ""(.*)"", ""(.*)"" and ""(.*)""$")]
    public void ThenProcesses(string a, string b, string c, string d)
    {
        string[] running = [.. host!.Kernel.Processes.Values.Select(p => p.Text)];
        foreach (string name in new[] { a, b, c, d })
        {
            Assert.True(running.Contains(name), $"{name} is not running: {string.Join(' ', running)}");
        }
    }

    // The host as Fog boots it: the commands and rc, the files 9front's installer would copy, and
    // the end of bootrc for a cpu server, with the host owner's key in the nvram.
    private async Task BootAsync()
    {
        var programs = new Dictionary<string, ProgramMain>(Commands.Commands.All) { ["rc"] = RcProgram.MainAsync };
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rc/lib/rcmain"] = RcProgram.Rcmain,
            ["rc/bin/cpurc"] = Services.Cpurc,
            ["rc/bin/service/tcp17019"] = Services.Tcp17019,
            ["sys/lib/newuser"] = Services.Newuser,
            ["lib/ndb/local"] = $"ipnet={domain} ip=127.0.0.0 ipmask=255.0.0.0\n\tauth=127.0.0.1\n\tauthdom={domain}\n",
        };
        host = await FogHost.BootAsync(new FogHostSettings(StoreFile, owner)
        {
            StoreBlocks = 4096,
            Programs = programs,
            Files = files,
            Boot = ["rc", "-c", Services.Bootrc],
            Host = network,
            Console = console,
            Nvram = new HostNvram(Nvram()),
            Loaders = [new WasmLoader(TimeProvider.System).LoadAsync],
        });
        Assert.True(host.BootStatus.Length == 0, host.BootStatus);

        // listen announces tcp17019 in a process of its own after cpurc has ended.
        for (int i = 0; i < 100 && !await AnsweredAsync(); i++)
        {
            await Task.Delay(100);
        }
    }

    private async Task<bool> AnsweredAsync()
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, network.HostPort(17019));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // Every process killed and waited for, and the store committed and let go, as the host halts.
    private async Task StopAsync()
    {
        if (host is null)
        {
            return;
        }

        foreach (Process p in host.Kernel.Processes.Values)
        {
            p.PostNote("kill");
        }

        for (int i = 0; i < 100 && host.Kernel.Processes.Count > 1; i++)
        {
            await Task.Delay(50);
        }

        host.Dispose();
        host = null;
    }

    private async Task AddUserAsync(string user, string? group)
    {
        string users = await ReadAsync("/adm/users");
        var lines = users.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        int id = lines.Select(l => int.Parse(l.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture)).Max() + 1;
        lines.Add($"{id}:{user}:{user}:");
        if (group is not null)
        {
            int at = lines.FindIndex(l => l.Split(':')[1] == group);
            string members = lines[at].Split(':')[3];
            lines[at] = $"{lines[at]}{(members.Length == 0 ? string.Empty : ",")}{user}";
        }

        int f = await Init.OpenAsync("/adm/users", NinePConstants.OWRITE | NinePConstants.OTRUNC);
        await Init.WriteAsync(f, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        await Init.CloseAsync(f);
        Assert.Equal("refreshed users\n", await host!.RefreshUsersAsync());

        // gefs(8)'s recipe, as the host owner: the store mounted with the permit, and the home
        // made and given away.
        string script = $"""
            mount /srv/boot /n/u %main
            mkdir /n/u/usr/{user}
            chgrp -u {user} /n/u/usr/{user}
            chgrp {user} /n/u/usr/{user}

            """;
        await RunAsOwnerAsync(script);
    }

    // An rc script run by the host owner in a namespace of its own, which must succeed within a
    // minute, as fog must: a mount whose authentication never ends fails the scenario.
    private async Task RunAsOwnerAsync(string script)
    {
        Init.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg, child => child.ExecAsync("/bin/rc", ["rc", "-c", script]));
        string status;
        try
        {
            status = (await Init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(60))).Message;
        }
        catch (TimeoutException)
        {
            status = "timed out";
        }

        Assert.True(status.Length == 0, $"{status}\n{console.Text}");
    }

    private async Task<string> ReadAsync(string path)
    {
        int f = await Init.OpenAsync(path, NinePConstants.OREAD);
        var all = new StringBuilder();
        for (ReadOnlyMemory<byte> got; (got = await Init.ReadAsync(f, 8192)).Length > 0;)
        {
            all.Append(Encoding.UTF8.GetString(got.Span));
        }

        await Init.CloseAsync(f);
        return all.ToString();
    }

    // fog as a user runs it, its $cpu and $auth the host's forwarded 17019 and 567, and $PASS the
    // password of the user -u names, unless the scenario gives another; its console gives the
    // lines typed and then its end.
    private async Task RunFogAsync(string args, string? pass, string[] typed)
    {
        var lines = new Queue<string>(typed);
        string[] words = Words(args);
        string user = words[Array.IndexOf(words, "-u") + 1];
        var local = new Local
        {
            Input = new MemoryStream(),
            Output = fogOutput,
            Error = fogErrors,
            Directory = fogDirectory ?? "/",
            Environment = name => name switch
            {
                "cpu" => $"tcp!127.0.0.1!{network.HostPort(17019)}",
                "auth" => $"tcp!127.0.0.1!{network.HostPort(567)}",
                "PASS" => pass ?? passwords[user],
                _ => null,
            },
            LoginName = user,
            ReadPassword = (_, _) => ValueTask.FromResult<string?>(null),
            ReadConsole = _ => ValueTask.FromResult(lines.TryDequeue(out string? line) ? line + "\n" : null),
            WriteConsole = (data, ct) => fogTerminal.WriteAsync(data, ct),
            Drives = new Dictionary<string, string>(StringComparer.Ordinal),
        };
        fogExit = await Rcpu.RunAsync(words, local, stopping.Token).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private string NineFrontFailure()
        => $"9front printed:\n{nineFrontOutput}\nits errors:\n{nineFrontErrors}\nthe host's console:\n{console.Text}\n9front:\n{NineFront.Terminal?.Transcript(2000)}";

    private string FogFailure()
        => $"fog exited {fogExit}; its output:\n{fogOutput.Text}\nits errors:\n{fogErrors.Text}\nits terminal:\n{fogTerminal.Text}\nthe host's console:\n{console.Text}\nits processes: {string.Join(' ', host?.Kernel.Processes.Values.Select(p => p.Text) ?? [])}";

    // The host owner's Nvrsafe, its dp9ik key from the password the auth server knows, unless the
    // scenario sealed another.
    private byte[] Nvram()
    {
        var safe = new Nvrsafe();
        Authsrv.Safecpy(safe.Authid, owner);
        Authsrv.Safecpy(safe.Authdom, domain);
        Authkey.Passtokey(sealedPassword).K.AesKey.CopyTo(safe.Aesmachkey, 0);
        safe.Machsum = Authsrv.Nvcsum(safe.Machkey);
        safe.Configsum = Authsrv.Nvcsum(safe.Config);
        safe.Authidsum = Authsrv.Nvcsum(safe.Authid);
        safe.Authdomsum = Authsrv.Nvcsum(safe.Authdom);
        safe.Aesmachsum = Authsrv.Nvcsum(safe.Aesmachkey);
        return safe.ToBytes();
    }

    // authsrv on the port 567 is on the host, with the users' passwords.
    private TcpListener ServeAuth()
    {
        var listener = new TcpListener(IPAddress.Loopback, network.HostPort(567));
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
                        await new AuthServerConnection(stream, new PasswordDatabase(passwords), "127.0.0.1", []).ServeAsync(stopping.Token);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or EndOfStreamException)
                    {
                    }
                });
            }
        });
        return listener;
    }
}
