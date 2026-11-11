using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Dp9ik.P9Auth;
using Dp9ik.Tls;
using NinePSharp.Constants;
using NinePSharp.Fog.Auth;
using NinePSharp.Fog.Client;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Namespaces.Tests.Support;
using NinePSharp.Fog.Rc.Tests.Support;
using Reqnroll;
using Reqnroll.UnitTestProvider;
using Xunit;
using static NinePSharp.Fog.Rc.Tests.Support.FogWords;

namespace NinePSharp.Fog.Rc.Tests.Steps;

[Binding]
[Scope(Feature = "Stock 9front rcpu runs rc on Fog through tcp17019")]
public sealed class RcpuLoginSteps
{
    // rc's prompt on Fog, as rcmain sets it when the profile sets none.
    private const string FogPrompt = "% ";

    // secstored's port on Fog, and the host's port forwarded to 9front's 5356.
    private const int FogSecstore = 17380;
    private const string SecstorePassword = "secstorepw";
    private static bool nineFrontSecstored;
    private readonly FakeHostNetwork host = new();
    private readonly FakeHostConsole console = new();
    private readonly Dictionary<string, string> passwords = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly HashSet<long> session = [];
    private readonly List<PasswordPrompt> prompts = [];
    private readonly Recorded fogOutput = new();
    private readonly Recorded fogErrors = new();
    private readonly Channel<string> fogInterrupts = Channel.CreateUnbounded<string>();
    private readonly Queue<string> fogTyped = new();
    private readonly Queue<string> fogConsole = new();
    private readonly List<string> fogAsked = [];

    // raw or cooked, for each time the session made fog's terminal one.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> fogRaw = new();
    private readonly Dictionary<string, string> fogDrives = new(StringComparer.Ordinal);
    private FogKernel kernel = null!;
    private Process init = null!;
    private TcpListener? authsrv;
    private string printed = string.Empty;
    private string errors = string.Empty;
    private string status = string.Empty;
    private string closed = string.Empty;
    private bool interactive;
    private TcpClient? call;
    private P9AuthClientResult? authenticated;
    private Task<int>? fog;
    private string? fogDirectory;
    private string? fogPass;
    private bool fogWithoutPass;
    private AnonymousPipeServerStream? fogTyping;
    private (string Output, int Code)? program;

    private int Port => host.HostPort(17019);

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
        await init.WriteAsync(ctl, Encoding.UTF8.GetBytes($"key proto=dp9ik user={user} dom={NineFront.Domain} !password={password}"));
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
        NineFrontTerminal t = await NineFront.TerminalAsync(host);
        await t.RunAsync($"echo '{text}' >{path}");
    }

    [When(@"^9front runs ""(.*)""$")]
    public async Task WhenNineFrontRunsAsync(string command)
    {
        NineFrontTerminal t = await NineFront.TerminalAsync(host);
        await NineFront.KeyAsync(t, "glenda", passwords["glenda"]);
        string line = Dialed(command);
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

        Collect(all);
    }

    // The command's output, in a terminal it asks for glenda's secstore password, as readcons
    // asks, typed after its prompt and not echoed.
    [When(@"^9front runs ""(.*)"" and gives glenda's secstore password$")]
    public async Task WhenNineFrontRunsWithSecstorePasswordAsync(string command)
    {
        NineFrontTerminal t = await NineFront.TerminalAsync(host);
        string line = command.Replace("tcp!fog!5356", $"tcp!10.0.2.2!{host.HostPort(FogSecstore)}", StringComparison.Ordinal);
        await t.SendLineAsync($"@{{{line}}} >/tmp/rout >[2]/tmp/rerr; st=$status; cat /tmp/rout; echo '#errors'; cat /tmp/rerr; echo '#status' $st");
        await t.ExpectAsync("secstore password: ", TimeSpan.FromSeconds(60));
        await t.SendLineAsync(SecstorePassword);
        Collect((await t.ExpectAsync(NineFrontTerminal.Prompt, TimeSpan.FromSeconds(60))).TrimStart('\n'));
    }

    // secstore's client does not wait for its put to be stored.
    [When(@"^glenda's secstore on Fog holds ""(.*)""$")]
    public async Task WhenFogSecstoreHoldsAsync(string file)
        => Assert.Equal("held", (await RunOnFogAsync($"while(! test -s /adm/secstore/store/glenda/{file}) sleep 0.1; echo held")).TrimEnd('\n'));

    [When(@"^Fog runs ""(.*)"" with glenda's secstore password$")]
    public async Task WhenFogRunsWithSecstorePasswordAsync(string command)
    {
        string line = command
            .Replace("tcp!fog!5356", $"tcp!127.0.0.1!{FogSecstore}", StringComparison.Ordinal)
            .Replace("tcp!9front!5356", $"tcp!127.0.0.1!{NineFront.Secstore}", StringComparison.Ordinal);
        Collect(await RunOnFogAsync($"@{{echo {SecstorePassword} | {line}}} >/tmp/rout >[2]/tmp/rerr; st=$status; cat /tmp/rout; echo '#errors'; cat /tmp/rerr; echo '#status' $st"));
    }

    // secuser's account for glenda on Fog, and secstored serving it.
    [Given(@"^glenda has a secstore account on Fog$")]
    public async Task GivenFogSecstoreAsync()
    {
        console.Type($"{SecstorePassword}\n{SecstorePassword}\n\n\n\n\n");
        string ready = await RunOnFogAsync(
            $"auth/secuser glenda </dev/cons >/dev/null; auth/secstored -s tcp!127.0.0.1!{FogSecstore} >/dev/null >[2=1]; " +
            $"while(! ~ `{{cat /net/tcp/*/local >[2]/dev/null}} *!{FogSecstore}*) sleep 0.1; echo ready");
        Assert.True(ready.TrimEnd('\n') == "ready", ready);
    }

    [Given(@"^glenda's secstore on Fog holds the file ""(.*)"" with ""(.*)""$")]
    public async Task GivenFogSecstoreFileAsync(string file, string text)
    {
        await GivenFogSecstoreAsync();
        string saved = await RunOnFogAsync($"cd /tmp; echo '{text}' >{file}; echo {SecstorePassword} | auth/secstore -i -s tcp!127.0.0.1!{FogSecstore} -u glenda -p {file}; while(! test -s /adm/secstore/store/glenda/{file}) sleep 0.1");
        Assert.Contains("secstore: saved", saved, StringComparison.Ordinal);
    }

    // On 9front, secuser makes glenda's account, answering its prompts, and secstored serves it,
    // once for the run; then 9front's own client stores the file.
    [Given(@"^glenda's secstore on 9front holds the file ""(.*)"" with ""(.*)""$")]
    public async Task GivenNineFrontSecstoreFileAsync(string file, string text)
    {
        NineFrontTerminal t = await NineFront.TerminalAsync(host);
        if (!nineFrontSecstored)
        {
            // The live CD's /adm is the CD's; secuser makes the store in a directory before it.
            await t.RunAsync("mkdir -p /tmp/adm; bind -bc /tmp/adm /adm");
            await t.SendLineAsync("auth/secuser glenda");
            foreach (string prompt in new[] { "glenda password: ", "retype password: " })
            {
                await t.ExpectAsync(prompt, TimeSpan.FromSeconds(30));
                await t.SendLineAsync(SecstorePassword);
            }

            foreach (string prompt in new[] { "]: ", "]: ", "]: ", "]: " })
            {
                await t.ExpectAsync(prompt, TimeSpan.FromSeconds(30));
                await t.SendLineAsync(string.Empty);
            }

            Assert.Contains("change written", await t.ExpectAsync(NineFrontTerminal.Prompt, TimeSpan.FromSeconds(30)), StringComparison.Ordinal);
            await t.RunAsync("auth/secstored");
            nineFrontSecstored = true;
        }

        string saved = await t.RunAsync($"@{{cd /tmp; echo '{text}' >{file}; echo {SecstorePassword} | auth/secstore -i -s tcp!10.0.2.15!5356 -u glenda -p {file}; while(! test -s /adm/secstore/store/glenda/{file}) sleep 0.1}} >[2=1]");
        Assert.True(saved.Contains("secstore: saved", StringComparison.Ordinal), saved);
    }

    [When(@"^9front runs rcpu as ""(.*)"" with the password ""(.*)""$")]
    public async Task WhenNineFrontRunsWithPasswordAsync(string user, string password)
    {
        NineFrontTerminal t = await NineFront.TerminalAsync(host);
        await NineFront.KeyAsync(t, user, password);
        await t.SendLineAsync($"rcpu -h tcp!10.0.2.2!{Port} -u {user} -c 'echo in'");
        printed = await t.ExpectAsync("!Adding key: ", TimeSpan.FromSeconds(60));
    }

    [When(@"^9front runs ""(.*)"" interactively$")]
    public async Task WhenNineFrontRunsInteractivelyAsync(string command)
    {
        NineFrontTerminal t = await NineFront.TerminalAsync(host);
        await NineFront.KeyAsync(t, "glenda", passwords["glenda"]);
        await t.SendLineAsync(Dialed(command));
        interactive = true;
        printed = await t.ExpectAsync(FogPrompt, TimeSpan.FromSeconds(60));
    }

    // Del once the command is running on Fog, as kbdfs reads it from the serial console.
    [When(@"^it types ""(.*)"" and then Del$")]
    public async Task WhenTypesThenDelAsync(string line)
    {
        await NineFront.Terminal!.SendLineAsync(line);
        string name = line.Split(' ')[0];
        await UntilAsync(() => kernel.Processes.Values.Any(p => p.Text == name), $"{name} did not start on Fog");
        await NineFront.Terminal.TypeAsync("\x7f");
    }

    [When(@"^9front runs ""(.*)"" in the background$")]
    public async Task WhenNineFrontRunsInBackgroundAsync(string command)
    {
        NineFrontTerminal t = await NineFront.TerminalAsync(host);
        await NineFront.KeyAsync(t, "glenda", passwords["glenda"]);
        await t.RunAsync($"{Dialed(command)} >/tmp/rout >[2]/tmp/rerr &");
        await UntilAsync(() => kernel.Processes.Values.Any(p => p.Text == "sleep"), "sleep did not start on Fog");
        string group = await ReadFileAsync($"/proc/{kernel.Processes.Values.First(p => p.Text == "sleep").Pid}/noteid");
        foreach (Process p in kernel.Processes.Values)
        {
            try
            {
                if (await ReadFileAsync($"/proc/{p.Pid}/noteid") == group)
                {
                    session.Add(p.Pid);
                }
            }
            catch (SyscallException)
            {
                // It exited as the group was read.
            }
        }
    }

    // tcp(3)'s hangup of the call to Fog, as the call ends when its client is killed: tlsclient and
    // exportfs each make a note group of their own, so no one note reaches all of the client.
    [When(@"^9front hangs up the client's call$")]
    public Task WhenCallHungUpAsync() => NineFront.Terminal!.RunAsync(
        $"for(c in /net/tcp/[0-9]*) if(~ `{{cat $c/remote}} 10.0.2.2!{Port}) echo hangup >$c/ctl");

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

    [When(@"^dp9ik\.net's client calls 17019 as ""([^""]*)"" with the password ""([^""]*)"", getting its tickets from 567$")]
    public Task WhenClientCallsAsync(string user, string password) => CallAsync(user, password);

    [When(@"^dp9ik\.net's client calls 17019 as ""([^""]*)"" with the passwords ""([^""]*)"" and then ""([^""]*)"", getting its tickets from 567$")]
    public Task WhenClientCallsTwiceAsync(string user, string first, string second) => CallAsync(user, first, second);

    [Then(@"^its ticket names ""(.*)"" as the client's and the server's user$")]
    public void ThenTicketNames(string user)
    {
        Assert.Equal(user, authenticated!.ClientUser);
        Assert.Equal(user, authenticated.ServerUser);
    }

    [Then(@"^the password was asked for as ""(.*)"", and again after a mismatch$")]
    public void ThenAskedAgain(string text)
    {
        Assert.Equal([text, text], prompts.Select(prompt => prompt.Text));
        Assert.Equal([false, true], prompts.Select(prompt => prompt.Mismatch));
    }

    // rcpu's way of sending its script, its length on a line of seven and then the script, over
    // TLS-PSK; what rc prints comes back until the session ends.
    [Then(@"^over TLS-PSK keyed by its secret, the script ""(.*)"" prints ""(.*)""$")]
    public async Task ThenScriptPrintsAsync(string script, string text)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Stream tls = await P9PskTls.ConnectAsync(call!.GetStream(), authenticated!.Secret, bound.Token);
        byte[] body = Encoding.UTF8.GetBytes(script + "\n");
        await tls.WriteAsync(Encoding.UTF8.GetBytes($"{body.Length,7}\n"), bound.Token);
        await tls.WriteAsync(body, bound.Token);
        await tls.FlushAsync(bound.Token);
        var all = new List<byte>();
        byte[] buf = new byte[8192];
        for (int n; (n = await tls.ReadAsync(buf, bound.Token)) > 0;)
        {
            all.AddRange(buf.AsSpan(0, n));
        }

        Assert.Equal(Unescape(text), Encoding.UTF8.GetString([.. all]).TrimEnd('\n'));
    }

    // tcp17019 whose server function keeps the length line and the script a call sends, in a file
    // of its own under /tmp/scripts, and then ends the call.
    [Given(@"^Fog's tcp17019 keeps the script each call sends and runs none$")]
    public async Task GivenKeepingScriptsAsync()
    {
        await MkdirAsync("/tmp/scripts");
        await WriteAsync("/tmp/svc/tcp17019", Services.Tcp17019.Replace(
            ". <{n=`{read} && ! ~ $#n 0 && read -c $n} >[2=1]",
            "read -c 8 >/tmp/scripts/$pid; n=`{cat /tmp/scripts/$pid}; read -c $n >>/tmp/scripts/$pid",
            StringComparison.Ordinal));
    }

    // 9front's /mnt/term holds the directory, so rcpu's pwd names it as fog's does.
    [When(@"^9front's rcpu calls from the directory ""(.*)"" with the command ""(.*)""$")]
    public async Task WhenNineFrontCallsFromAsync(string directory, string command)
    {
        NineFrontTerminal t = await NineFront.TerminalAsync(host);
        await NineFront.KeyAsync(t, "glenda", passwords["glenda"]);
        await t.RunAsync($"mkdir -p /tmp/term{directory["/mnt/term".Length..]}; bind -c /tmp/term /mnt/term");
        printed = await t.RunAsync($"@{{cd {directory}; rcpu -h tcp!10.0.2.2!{Port} -u glenda -c '{command}'}} >[2=1]", TimeSpan.FromSeconds(60));
    }

    [When(@"^fog calls from the local directory ""(.*)"" with the command ""(.*)""$")]
    public async Task WhenFogCallsFromAsync(string directory, string command)
    {
        fogDirectory = directory;
        await RunFogAsync(["-u", "glenda", "-c", command], new MemoryStream());
    }

    [Then(@"^the two scripts are the same$")]
    public async Task ThenScriptsSameAsync()
    {
        string[] kept = (await RunOnFogAsync("echo /tmp/scripts/*")).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(kept.Length == 2, $"{string.Join(' ', kept)}\n{FogFailure()}");
        string first = await ReadFileAsync(kept[0]), second = await ReadFileAsync(kept[1]);
        Assert.StartsWith("    7", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    [Given(@"^fog's local directory holds ""(.*)"" with ""(.*)""$")]
    public void GivenLocalFile(string name, string text)
    {
        fogDirectory ??= System.IO.Directory.CreateTempSubdirectory("fog-local-").FullName;
        File.WriteAllText(Path.Combine(fogDirectory, name), Unescape(text));
    }

    [Given(@"^a Windows machine whose drives are ""(.*)"" and ""(.*)""$")]
    public void GivenDrives(string first, string second)
    {
        foreach (string drive in new[] { first, second })
        {
            fogDrives[drive] = System.IO.Directory.CreateTempSubdirectory("fog-drive-").FullName;
        }

        System.IO.Directory.CreateDirectory(Path.Combine(fogDrives[first], "work"));
    }

    [Given(@"^fog has no \$PASS, and the terminal gives ""(.*)"" and then ""(.*)""$")]
    public void GivenTerminalPasswords(string first, string second)
    {
        fogWithoutPass = true;
        fogTyped.Enqueue(first);
        fogTyped.Enqueue(second);
    }

    [Given(@"^fog has no \$PASS, and the terminal gives ""([^""]*)""$")]
    public void GivenTerminalPassword(string password)
    {
        fogWithoutPass = true;
        fogTyped.Enqueue(password);
    }

    // The lines the terminal gives /dev/cons, and then its end.
    [Given(@"^fog's terminal gives the lines ""(.*)""$")]
    public void GivenConsoleLines(string lines)
    {
        foreach (string line in Unescape(lines).Split('\n'))
        {
            fogConsole.Enqueue(line);
        }
    }

    [Given(@"^fog's \$PASS is ""(.*)""$")]
    public void GivenPass(string pass) => fogPass = pass;

    [When(@"^fog runs ""([^""]*)""$")]
    public Task WhenFogRunsAsync(string args) => RunFogAsync(Words(args), new MemoryStream());

    [When(@"^fog runs ""([^""]*)"" with the standard input ""([^""]*)""$")]
    public Task WhenFogRunsWithInputAsync(string args, string input) => RunFogAsync(Words(args), new MemoryStream(Encoding.UTF8.GetBytes(Unescape(input))));

    [When(@"^fog runs ""([^""]*)"" in that directory$")]
    public Task WhenFogRunsInDirectoryAsync(string args) => RunFogAsync(Words(args), new MemoryStream());

    [When(@"^fog runs ""([^""]*)"" from ""([^""]*)""$")]
    public Task WhenFogRunsFromAsync(string args, string directory)
    {
        fogDirectory = directory;
        return RunFogAsync(Words(args), new MemoryStream());
    }

    // rc reads the user's lines from fog's standard input, a pipe the test types into.
    [Given(@"^fog runs ""([^""]*)"" interactively$")]
    public async Task GivenFogInteractiveAsync(string args)
    {
        fogTyping = new AnonymousPipeServerStream(PipeDirection.Out);
        fog = Task.Run(() => FogAsync(Words(args), new AnonymousPipeClientStream(PipeDirection.In, fogTyping.ClientSafePipeHandle)));
        await UntilAsync(() => Prompts() == 1, "rc did not prompt");
    }

    [When(@"^the user types ""(.*)"" and then Ctrl-C$")]
    public async Task WhenUserTypesThenCtrlCAsync(string line)
    {
        await TypeAsync(line);
        string name = line.Split(' ')[0];
        await UntilAsync(() => kernel.Processes.Values.Any(p => p.Text == name), $"{name} did not start on Fog");
        fogInterrupts.Writer.TryWrite("interrupt");
    }

    [Then(@"^rc on Fog is interrupted, and typing ""(.*)"" prints ""(.*)""$")]
    public async Task ThenFogInterruptedAsync(string line, string text)
    {
        await UntilAsync(() => Prompts() == 2, "rc did not prompt again");
        await TypeAsync(line);
        await UntilAsync(() => fogOutput.Text.Contains(Unescape(text) + "\n", StringComparison.Ordinal), $"{line} printed nothing");
    }

    [Given(@"^fog runs ""([^""]*)"" until rc is running$")]
    public async Task GivenFogRunningAsync(string args)
    {
        fog = Task.Run(() => FogAsync(Words(args), new MemoryStream()));
        await UntilAsync(() => kernel.Processes.Values.Any(p => p.Text == "sleep"), "sleep did not start on Fog");
    }

    // tlssrv relays the call for the session; killing it hangs the call up.
    [When(@"^the host drops fog's call$")]
    public async Task WhenHostDropsAsync()
    {
        foreach (Process p in kernel.Processes.Values.Where(p => p.Text == "tlssrv"))
        {
            p.PostNote("kill");
        }

        await fog!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Then(@"^fog prints ""(.*)""$")]
    public void ThenFogPrints(string text) => Assert.True(fogOutput.Text.TrimEnd('\n') == Unescape(text), FogFailure());

    [Then(@"^fog prints ""(.*)"" on its standard error$")]
    public void ThenFogPrintsError(string text) => Assert.True(fogErrors.Text.Contains(Unescape(text), StringComparison.Ordinal), FogFailure());

    [Then(@"^fog does not print ""(.*)"" on its standard error$")]
    public void ThenFogDoesNotPrintError(string text) => Assert.False(fogErrors.Text.Contains(Unescape(text), StringComparison.Ordinal), FogFailure());

    [Then(@"^fog prints that directory under ""(.*)"", and then ""(.*)""$")]
    public void ThenFogPrintsDirectory(string under, string text)
        => Assert.True(fogOutput.Text.TrimEnd('\n') == $"{under}{fogDirectory}\n{Unescape(text)}", FogFailure());

    [Then(@"^fog exits (\d+)$")]
    public async Task ThenFogExitsAsync(int code) => Assert.True(await fog!.WaitAsync(TimeSpan.FromSeconds(60)) == code, FogFailure());

    [Then(@"^fog's local directory holds ""(.*)"" with ""(.*)""$")]
    public void ThenLocalFile(string name, string text) => Assert.Equal(Unescape(text), File.ReadAllText(Path.Combine(fogDirectory!, name)));

    // As "raw, cooked and raw": each time, in turn, the session made the terminal raw or cooked.
    [Then(@"^fog's terminal was made (.*)$")]
    public async Task ThenTerminalMadeAsync(string made)
    {
        string[] expected = made.Replace(" and ", ", ", StringComparison.Ordinal).Split(", ");
        await UntilAsync(() => fogRaw.Count >= expected.Length, $"the terminal was made only {string.Join(' ', fogRaw)}");
        Assert.Equal(expected, fogRaw);
    }

    [Then(@"^the terminal was asked ""(.*)"" twice$")]
    public void ThenAskedTwice(string prompt) => Assert.Equal([prompt, prompt], fogAsked);

    // The built fog, run by dotnet as a shell runs it, its environment naming the host.
    [When(@"^the shell runs fog ""([^""]*)"" with the standard input ""([^""]*)""$")]
    public async Task WhenShellRunsFogAsync(string args, string input)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "NinePSharp.Fog.Client.dll"));
        foreach (string word in Words(args))
        {
            start.ArgumentList.Add(word);
        }

        start.Environment["cpu"] = $"tcp!127.0.0.1!{Port}";
        start.Environment["auth"] = $"tcp!127.0.0.1!{host.HostPort(567)}";
        start.Environment["PASS"] = passwords["glenda"];
        using System.Diagnostics.Process shell = System.Diagnostics.Process.Start(start)!;
        await shell.StandardInput.WriteAsync(Unescape(input));
        shell.StandardInput.Close();
        Task<string> output = shell.StandardOutput.ReadToEndAsync();
        Task<string> error = shell.StandardError.ReadToEndAsync();
        try
        {
            await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException hung)
        {
            shell.Kill();
            throw new TimeoutException($"{await DescriptorsAsync()}\n{await output}\n{await error}\n{Failure()}", hung);
        }

        program = (await output + await error, shell.ExitCode);
    }

    [Then(@"^the program prints ""(.*)""$")]
    public void ThenProgramPrints(string text) => Assert.True(program!.Value.Output.TrimEnd('\n') == Unescape(text), program.Value.Output);

    [Then(@"^the program exits (\d+)$")]
    public void ThenProgramExits(int code) => Assert.True(program!.Value.Code == code, program.Value.Output);

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
    public void ThenClosed() => Assert.Equal($"dp9ik@{NineFront.Domain}\0", closed);

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
        Assert.Contains($"dom={NineFront.Domain}", await NineFront.Terminal!.ExpectAsync("\n", TimeSpan.FromSeconds(10)), StringComparison.Ordinal);
        await NineFront.Terminal.ExpectAsync("password: ", TimeSpan.FromSeconds(10));
        await NineFront.Terminal.SendLineAsync(passwords["glenda"]);
        Assert.Contains("in", await NineFront.Terminal.ExpectAsync(NineFrontTerminal.Prompt, TimeSpan.FromSeconds(60)), StringComparison.Ordinal);
    }

    [Then(@"^rc on Fog is interrupted and prompts again$")]
    public async Task ThenPromptsAgainAsync()
    {
        printed = await NineFront.Terminal!.ExpectAsync(FogPrompt, TimeSpan.FromSeconds(30));
        await UntilAsync(() => !kernel.Processes.Values.Any(p => p.Text == "sleep"), "sleep was not interrupted");
    }

    // What the line prints after its echo.
    [Then(@"^typing ""(.*)"" prints ""(.*)""$")]
    public async Task ThenTypingPrintsAsync(string line, string text)
    {
        await NineFront.Terminal!.SendLineAsync(line);
        printed = await NineFront.Terminal.ExpectAsync(FogPrompt, TimeSpan.FromSeconds(30));
        Assert.True(printed[(printed.IndexOf('\n', StringComparison.Ordinal) + 1)..].TrimEnd('\n') == Unescape(text), Failure());
    }

    [Then(@"^every process of the session's note group on Fog has exited$")]
    public async Task ThenSessionExitedAsync()
    {
        Assert.NotEmpty(session);
        await UntilAsync(() => !kernel.Processes.Values.Any(p => session.Contains(p.Pid)), "the session's processes are still running");
    }

    // The host's processes killed, which hangs up any session still open, as after Del, where
    // rcpu's note copier holds the mount of the client's files open, as 9front's does; the
    // terminal is waited for until its rcpu has ended.
    [AfterScenario]
    public async Task CleanUpAsync()
    {
        stopping.Cancel();
        authsrv?.Stop();
        call?.Dispose();
        fogTyping?.Dispose();
        foreach (string made in fogDrives.Values.Append(fogDirectory ?? string.Empty).Where(d => d.StartsWith(Path.GetTempPath(), StringComparison.Ordinal)))
        {
            System.IO.Directory.Delete(made, recursive: true);
        }

        foreach (Process process in kernel?.Processes.Values ?? [])
        {
            process.PostNote("kill");
        }

        if (interactive)
        {
            await NineFront.Terminal!.ExpectAsync(NineFrontTerminal.Prompt, TimeSpan.FromSeconds(30));
        }
    }

    // What the command left after #errors and #status, split.
    private void Collect(string all)
    {
        int e = all.LastIndexOf("#errors\n", StringComparison.Ordinal);
        int s = all.LastIndexOf("#status", StringComparison.Ordinal);
        Assert.True(e >= 0 && s > e, all);
        printed = all[..e];
        errors = all[(e + "#errors\n".Length)..s];
        status = all[(s + "#status".Length)..].Trim();
    }

    // rc on the Fog host runs the script, and what it prints, to standard output and error both,
    // is given back once it is done.
    private async Task<string> RunOnFogAsync(string script)
    {
        var (read, write) = await init.PipeAsync();
        long pid = init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            await child.DupAsync(write, 1);
            await child.DupAsync(write, 2);
            await child.CloseAsync(read);
            await child.CloseAsync(write);
            await child.ExecAsync("/bin/rc", ["rc", "-c", script]);
        });
        await init.CloseAsync(write);
        var bytes = new List<byte>();
        for (ReadOnlyMemory<byte> b; !(b = await init.ReadAsync(read, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(60))).IsEmpty;)
        {
            bytes.AddRange(b.Span);
        }

        await init.CloseAsync(read);
        while ((await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10))).Pid != pid)
        {
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private string Dialed(string command) => command.Replace("tcp!fog!17019", $"tcp!10.0.2.2!{Port}", StringComparison.Ordinal);

    private async Task UntilAsync(Func<bool> done, string why)
    {
        for (int i = 0; i < 300 && !done(); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(done(), $"{why}\n{Failure()}");
    }

    private async Task<string> ReadFileAsync(string path)
    {
        int f = await init.OpenAsync(path, NinePConstants.OREAD);
        try
        {
            return Encoding.UTF8.GetString((await init.ReadAsync(f, 8192)).Span);
        }
        finally
        {
            await init.CloseAsync(f);
        }
    }

    private string Failure()
        => $"printed:\n{printed}\nerrors:\n{errors}\nstatus: {status}\nconsole: {console.Text}\nprocesses: {string.Join(" | ", kernel.Processes.Values.Select(p => $"{p.Pid}:{p.Text}:{p.Args}:{p.User}"))}\n9front:\n{NineFront.Terminal?.Transcript(2000)}";

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

    // dp9ik.net's client on a call to 17019, its tickets got from the auth server on 567, given
    // each password in turn as it asks.
    private async Task CallAsync(string user, params string[] given)
    {
        var left = new Queue<string>(given);
        call = new TcpClient();
        await call.ConnectAsync(IPAddress.Loopback, Port);
        var config = new P9AuthClientConfig(
            user,
            (prompt, _) =>
            {
                prompts.Add(prompt);
                return ValueTask.FromResult(left.Count > 0 ? left.Dequeue() : null);
            },
            async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, host.HostPort(567), token);
                return new NetworkStream(socket, ownsSocket: true);
            });
        authenticated = await P9AuthClient.AuthenticateAsync(call.GetStream(), config, stopping.Token).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async Task RunFogAsync(string[] args, Stream input)
    {
        Task<int> running = FogAsync(args, input);
        try
        {
            fog = Task.FromResult(await running.WaitAsync(TimeSpan.FromSeconds(60)));
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"{await DescriptorsAsync()}\n{FogFailure()}", error);
        }
    }

    // fog with the host's forwarded ports as $cpu and $auth, glenda's password as $PASS unless the
    // scenario gives another or none, and a terminal that gives what the scenario typed.
    private Task<int> FogAsync(string[] args, Stream input)
    {
        var local = new Local
        {
            Input = input,
            Output = fogOutput,
            Error = fogErrors,
            Directory = fogDirectory ?? "/",
            Environment = name => name switch
            {
                "cpu" => $"tcp!127.0.0.1!{Port}",
                "auth" => $"tcp!127.0.0.1!{host.HostPort(567)}",
                "PASS" => fogWithoutPass ? null : fogPass ?? passwords["glenda"],
                _ => null,
            },
            LoginName = "glenda",
            ReadPassword = (prompt, _) =>
            {
                fogAsked.Add(prompt);
                return ValueTask.FromResult(fogTyped.Count > 0 ? fogTyped.Dequeue() : null);
            },
            ReadConsole = _ => ValueTask.FromResult(fogConsole.Count > 0 ? fogConsole.Dequeue() + "\n" : null),
            Raw = on => fogRaw.Enqueue(on ? "raw" : "cooked"),
            WriteConsole = (data, ct) => fogOutput.WriteAsync(data, ct),
            Interrupts = fogInterrupts.Reader,
            Drives = fogDrives,
        };
        return Rcpu.RunAsync(args, local, stopping.Token);
    }

    private async Task TypeAsync(string line)
    {
        await fogTyping!.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await fogTyping.FlushAsync();
    }

    // rc's prompts so far, on fog's standard output or error.
    private int Prompts() => (fogOutput.Text + fogErrors.Text).Split(FogPrompt).Length - 1;

    // Each process's descriptors, as /proc/n/fd gives them.
    private async Task<string> DescriptorsAsync()
    {
        var all = new StringBuilder();
        foreach (Process p in kernel.Processes.Values)
        {
            try
            {
                all.Append(CultureInfo.InvariantCulture, $"{p.Pid} {p.Text}:\n{await ReadFileAsync($"/proc/{p.Pid}/fd")}");
            }
            catch (SyscallException)
            {
            }
        }

        return all.ToString();
    }

    private string FogFailure() => $"fog's output:\n{fogOutput.Text}\nfog's errors:\n{fogErrors.Text}\n{Failure()}";

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
                        await new AuthServerConnection(stream, new PasswordDatabase(passwords), "127.0.0.1", []).ServeAsync(stopping.Token);
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
}
