using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "/net/tcp is ip(3)'s file interface over the host's sockets, as 9front binds #I on /net")]
public sealed class NetworkSteps(KernelDriver driver) : IAsyncDisposable
{
    private readonly Dictionary<string, int> fds = [];
    private readonly TaskCompletionSource<bool> childDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Socket> fillers = [];
    private int announced = -1;
    private string announcedNumber = string.Empty;
    private int call = -1;
    private string callNumber = string.Empty;
    private long childPid;
    private int childData = -1;
    private string childLocal = string.Empty;
    private Task<string>? childWait;
    private int refusedPort;
    private int announcedPort;
    private int freePort;
    private string childNumber = string.Empty;
    private TcpListener? taken;
    private Socket? full;
    private string? failure;

    private Process Init => driver.Init;

    [Given(@"^a kernel booted for the host owner ""(.*)""$")]
    public async Task GivenKernel(string user)
        => driver.Init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), user, driver.Clock).BootAsync();

    [Given(@"^a conversation announced on ""(.*)""$")]
    public async Task GivenAnnounced(string address)
    {
        announced = await Init.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR);
        announcedNumber = await ReadAsync(Init, announced);
        await Init.WriteAsync(announced, Encoding.UTF8.GetBytes($"announce {address}"));
        announcedPort = int.Parse((await LocalAsync(announcedNumber)).TrimEnd('\n').Split('!')[1], CultureInfo.InvariantCulture);
    }

    [Given("a port of the host's loopback that the host holds for another")]
    public void GivenTakenPort()
    {
        taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
    }

    // listen(2) with no backlog holds one call waiting; the host drops the next caller's SYN, so its
    // connect waits, as one to a host that does not answer does.
    [Given("a listener of the host's loopback whose queue of calls is full")]
    public async Task GivenFullListener()
    {
        full = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        full.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        full.Listen(0);
        var filler = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        fillers.Add(filler);
        await filler.ConnectAsync(full.LocalEndPoint!).WaitAsync(KernelDriver.Bound);
    }

    [Given("a port of the host's loopback that nobody takes")]
    public void GivenRefusedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        refusedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
    }

    [Given(@"^the process has become ""none""$")]
    public async Task GivenNone()
    {
        int fd = await Init.OpenAsync("/dev/user", NinePConstants.OWRITE);
        await Init.WriteAsync(fd, "none"u8.ToArray());
        await Init.CloseAsync(fd);
    }

    [When(@"^the process opens ""([^""]*)""$")]
    public Task WhenOpens(string path) => RecordAsync(async () => { await Init.OpenAsync(path, NinePConstants.ORDWR); });

    [When(@"^the process opens ""(.*)"" as ""(.*)""$")]
    public async Task WhenOpensAs(string path, string name) => fds[name] = await Init.OpenAsync(path, NinePConstants.ORDWR);

    [When(@"^the process opens ""(.*)"" (\d+) times$")]
    public async Task WhenOpensTimes(string path, int times)
    {
        for (int i = 0; i < times; i++)
        {
            await Init.OpenAsync(path, NinePConstants.ORDWR);
        }
    }

    [When(@"^the process stats ""(.*)""$")]
    public Task WhenStats(string path) => RecordAsync(async () => { await Init.StatAsync(path); });

    [When(@"^the process closes ""(.*)""$")]
    public Task WhenCloses(string name) => Init.CloseAsync(fds[name]).AsTask();

    [When(@"^the process writes ""(.*)"" to ""(.*)""$")]
    public Task WhenWrites(string text, string name) => RecordAsync(() => Init.WriteAsync(fds[name], Encoding.UTF8.GetBytes(Expand(text))).AsTask());

    [When(@"^the process connects ""(.*)"" to that port$")]
    public Task WhenConnects(string name) => RecordAsync(() => Init.WriteAsync(fds[name], Encoding.UTF8.GetBytes($"connect 127.0.0.1!{refusedPort}")).AsTask());

    [When(@"^the process writes ""(.*)"" to the announced conversation's ctl$")]
    public Task WhenWritesAnnounced(string text) => RecordAsync(() => Init.WriteAsync(announced, Encoding.UTF8.GetBytes(text)).AsTask());

    [When(@"^a child connects to the announced port and writes ""(.*)""$")]
    public Task WhenChildConnects(string text) => WhenChildConnectsTo("127.0.0.1", text);

    [When(@"^a child connects to ""(.*)"" at the announced port and writes ""(.*)""$")]
    public async Task WhenChildConnectsTo(string address, string text)
    {
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The child shares the parent's descriptors, so the parent reads the child's end too.
        Init.Fork(RforkFlags.Proc, async child =>
        {
            int ctl = await child.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR);
            string number = await ReadAsync(child, ctl);
            await child.WriteAsync(ctl, Encoding.UTF8.GetBytes($"connect {address}!{announcedPort}"));
            childData = await child.OpenAsync($"/net/tcp/{number}/data", NinePConstants.ORDWR);
            childLocal = await ReadFileAsync(child, $"/net/tcp/{number}/local");
            await child.WriteAsync(childData, Encoding.UTF8.GetBytes(text));
            connected.SetResult(true);
            await childDone.Task;
        });
        await connected.Task.WaitAsync(KernelDriver.Bound);
    }

    [When("the process opens the announced conversation's listen")]
    public async Task WhenListens()
    {
        call = await Init.OpenAsync($"/net/tcp/{announcedNumber}/listen", NinePConstants.ORDWR).AsTask().WaitAsync(KernelDriver.Bound);
        callNumber = await ReadAsync(Init, call);
    }

    [When(@"^the process writes ""(.*)"" to the call's ctl$")]
    public Task WhenHangsUp(string message) => Init.WriteAsync(call, Encoding.UTF8.GetBytes(message)).AsTask();

    [When("the process closes the call")]
    public Task WhenClosesCall() => Init.CloseAsync(call).AsTask();

    [When("a child that continues after every note waits in the announced conversation's listen")]
    public Task WhenChildListens() => ChildWaitsAsync(async (child, begun) =>
    {
        Task listen = child.OpenAsync($"/net/tcp/{announcedNumber}/listen", NinePConstants.ORDWR).AsTask();
        begun();
        await listen;
    });

    [When("a child that continues after every note waits to read the call's data twice")]
    public Task WhenChildReads() => ChildWaitsAsync(async (child, begun) =>
    {
        int data = await child.OpenAsync($"/net/tcp/{callNumber}/data", NinePConstants.ORDWR);
        await child.ReadAsync(data, 64);
        Task read = child.ReadAsync(data, 64).AsTask();
        begun();
        await read;
    });

    [When("a child that continues after every note connects a conversation to the full listener")]
    public Task WhenChildConnectsFull() => ChildWaitsAsync(async (child, begun) =>
    {
        int ctl = await child.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR);
        childNumber = await ReadAsync(child, ctl);
        Task connect = child.WriteAsync(ctl, Encoding.UTF8.GetBytes($"connect 127.0.0.1!{((IPEndPoint)full!.LocalEndPoint!).Port}")).AsTask();
        begun();
        await connect;
    });

    [When(@"^a child that becomes ""none"" clones a conversation and opens its ctl again$")]
    public Task WhenNoneChildReopens() => NoneChildAsync(keep: false, async child =>
    {
        int ctl = await child.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR);
        await child.OpenAsync($"/net/tcp/{await ReadAsync(child, ctl)}/ctl", NinePConstants.ORDWR);
    });

    [When(@"^a child that becomes ""none"" clones a conversation and keeps it$")]
    public Task WhenNoneChildClones() => NoneChildAsync(keep: true, child => child.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR).AsTask());

    [When(@"^a child that becomes ""none"" opens ""(.*)"" and keeps it$")]
    public Task WhenNoneChildKeeps(string path) => NoneChildAsync(keep: true, child => child.OpenAsync(path, NinePConstants.ORDWR).AsTask());

    [When(@"^the process posts ""(.*)"" to the child$")]
    public async Task WhenPosts(string note)
    {
        int fd = await Init.OpenAsync($"/proc/{childPid}/note", NinePConstants.OWRITE);
        await Init.WriteAsync(fd, Encoding.UTF8.GetBytes(note));
        await Init.CloseAsync(fd);
    }

    [When(@"^a child that becomes ""none"" opens ""([^""]*)""$")]
    public Task WhenNoneChildOpens(string path) => NoneChildAsync(keep: false, child => child.OpenAsync(path, NinePConstants.ORDWR).AsTask());

    [Then(@"^the names in ""(.*)"" are ""(.*)""$")]
    public async Task ThenNames(string path, string names)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(names, string.Join(' ', (await Init.DirReadAsync(fd)).Select(entry => entry.Name).Order(StringComparer.Ordinal)));
        await Init.CloseAsync(fd);
    }

    [Then(@"^the stats of ""(.*)"", ""(.*)"", ""(.*)"" and ""(.*)"" are named ""(.*)""$")]
    public async Task ThenStatNames(string a, string b, string c, string d, string names)
    {
        var named = new List<string>();
        foreach (string path in new[] { a, b, c, d })
        {
            named.Add((await Init.StatAsync(path)).Name!);
        }

        Assert.Equal(names, string.Join(' ', named));
    }

    [Then(@"^the stats of the files in ""(.*)"" are named ""(.*)""$")]
    public async Task ThenFileStatNames(string directory, string names)
    {
        foreach (string name in names.Split(' '))
        {
            Assert.Equal(name, (await Init.StatAsync($"{directory}/{name}")).Name);
        }
    }

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReads(string name, string text) => Assert.Equal(text, await ReadAsync(Init, fds[name]));

    [Then(@"^the call's data reads ""(.*)""$")]
    public async Task ThenCallReads(string text)
    {
        int data = await Init.OpenAsync($"/net/tcp/{callNumber}/data", NinePConstants.ORDWR);
        Assert.Equal(text, Encoding.UTF8.GetString((await Init.ReadAsync(data, 64).AsTask().WaitAsync(KernelDriver.Bound)).Span));
        fds["call data"] = data;
    }

    [Then(@"^the call's local is ""(.*)"" and the announced port$")]
    public async Task ThenCallLocal(string address) => Assert.Equal($"{address}!{announcedPort}\n", await LocalAsync(callNumber));

    [Then(@"^a call from the host to ""(.*)"" at the announced port is (refused|taken)$")]
    public async Task ThenHostCall(string address, string outcome)
    {
        using var caller = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await caller.ConnectAsync(new IPEndPoint(IPAddress.Parse(address), announcedPort)).WaitAsync(KernelDriver.Bound);
            Assert.Equal("taken", outcome);
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
        {
            Assert.Equal("refused", outcome);
        }
    }

    [Then(@"^the status of the call's conversation becomes ""(.*)""$")]
    public async Task ThenCallStatusBecomes(string status)
    {
        string expected = status.Replace("\\n", "\n", StringComparison.Ordinal);
        string read = string.Empty;
        for (DateTime end = DateTime.UtcNow + KernelDriver.Bound; DateTime.UtcNow < end && (read = await ReadFileAsync(Init, $"/net/tcp/{callNumber}/status")) != expected;)
        {
            await Task.Delay(10);
        }

        Assert.Equal(expected, read);
    }

    [Then(@"^reading the call's local from offset (\d+) gives nothing$")]
    public async Task ThenLocalPastEnd(int offset)
    {
        int fd = await Init.OpenAsync($"/net/tcp/{callNumber}/local", NinePConstants.OREAD);
        Assert.True((await Init.PReadAsync(fd, 128, offset)).IsEmpty);
        await Init.CloseAsync(fd);
    }

    [Then(@"^the local of ""(.*)"" is ""(.*)""$")]
    public async Task ThenLocalOf(string name, string local)
    {
        string read = (await LocalAsync(await ReadAsync(Init, fds[name]))).TrimEnd('\n');
        string[] expected = Expand(local).Split('!');
        string[] parts = read.Split('!');
        Assert.Equal(expected[0], parts[0]);
        Assert.True(expected[1] == "*" ? int.Parse(parts[1], CultureInfo.InvariantCulture) > 0 : expected[1] == parts[1], read);
    }

    [Then(@"^the status of ""(.*)"" begins ""(.*)""$")]
    public async Task ThenStatusOf(string name, string state)
        => Assert.StartsWith(state + " ", await ReadFileAsync(Init, $"/net/tcp/{await ReadAsync(Init, fds[name])}/status"), StringComparison.Ordinal);

    [Then(@"^the status of the child's conversation begins ""(.*)""$")]
    public async Task ThenChildStatus(string state)
    {
        string read = string.Empty;
        for (DateTime end = DateTime.UtcNow + KernelDriver.Bound; DateTime.UtcNow < end && !(read = await ReadFileAsync(Init, $"/net/tcp/{childNumber}/status")).StartsWith(state + " ", StringComparison.Ordinal);)
        {
            await Task.Delay(10);
        }

        Assert.StartsWith(state + " ", read, StringComparison.Ordinal);
    }

    [Then("the child's open succeeds")]
    public void ThenChildOpened() => Assert.Equal("opened", failure);

    [Then(@"^the process can open ""(.*)"" and ""(.*)""$")]
    public async Task ThenCanOpen(string a, string b)
    {
        await Init.OpenAsync(a, NinePConstants.ORDWR);
        await Init.OpenAsync(b, NinePConstants.ORDWR);
    }

    [Then("the call's remote is the child's local")]
    public async Task ThenRemote() => Assert.Equal(childLocal, await ReadFileAsync(Init, $"/net/tcp/{callNumber}/remote"));

    [Then(@"^writing ""(.*)"" to the call's data is read by the child$")]
    public async Task ThenChildReadsBack(string text)
    {
        await Init.WriteAsync(fds["call data"], Encoding.UTF8.GetBytes(text));
        Assert.Equal(text, await ReadChildAsync());
    }

    [Then(@"^its local is ""(.*)"" and a port above 0$")]
    public async Task ThenLocal(string address)
    {
        string local = await LocalAsync(announcedNumber);
        Assert.EndsWith("\n", local, StringComparison.Ordinal);
        string[] parts = local.TrimEnd('\n').Split('!');
        Assert.Equal(address, parts[0]);
        Assert.True(int.Parse(parts[1], CultureInfo.InvariantCulture) > 0, local);
    }

    [Then(@"^its status begins ""(.*)""$")]
    public async Task ThenStatus(string state) => Assert.StartsWith(state + " ", await ReadFileAsync(Init, $"/net/tcp/{announcedNumber}/status"), StringComparison.Ordinal);

    [Then(@"^the status of the call's conversation begins ""(.*)""$")]
    public async Task ThenCallStatus(string state) => Assert.StartsWith(state + " ", await ReadFileAsync(Init, $"/net/tcp/{callNumber}/status"), StringComparison.Ordinal);

    [Then("the child reads the end of its data")]
    public async Task ThenChildEof() => Assert.Equal(string.Empty, await ReadChildAsync());

    [Then(@"^the call fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);

    [Then(@"^the child's (wait|open) fails with ""(.*)""$")]
    public async Task ThenChildFails(string what, string message)
        => Assert.Equal(message, what == "wait" ? await childWait!.WaitAsync(KernelDriver.Bound) : failure);

    [Then(@"^""(.*)"" belongs to ""(.*)"" with the mode (\d+)$")]
    public async Task ThenOwned(string path, string user, string mode)
    {
        var stat = await Init.StatAsync(path);
        Assert.Equal((user, Convert.ToUInt32(mode, 8)), (stat.Uid, stat.Mode));
    }

    public ValueTask DisposeAsync()
    {
        childDone.TrySetResult(true);
        taken?.Stop();
        full?.Dispose();
        fillers.ForEach(filler => filler.Dispose());
        return ValueTask.CompletedTask;
    }

    private static async Task<string> ReadAsync(Process process, int fd)
        => Encoding.UTF8.GetString((await process.PReadAsync(fd, 128, 0)).Span);

    private static async Task<string> ReadFileAsync(Process process, string path)
    {
        int fd = await process.OpenAsync(path, NinePConstants.OREAD);
        try
        {
            return await ReadAsync(process, fd);
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    // $port is the announced port, $free a port the host has free, and $taken the one it holds for another.
    private string Expand(string text)
    {
        if (text.Contains("$free", StringComparison.Ordinal) && freePort == 0)
        {
            using var probe = new TcpListener(IPAddress.IPv6Loopback, 0);
            probe.Start();
            freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        return text.Replace("$port", announcedPort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$free", freePort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$taken", taken is null ? string.Empty : ((IPEndPoint)taken.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    // A child made none, which runs the call and, when it is to keep what it opened, lives on.
    private async Task NoneChildAsync(bool keep, Func<Process, Task> call)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Init.Fork(RforkFlags.Fdg, async child =>
        {
            int user = await child.OpenAsync("/dev/user", NinePConstants.OWRITE);
            await child.WriteAsync(user, "none"u8.ToArray());
            try
            {
                await call(child);
                result.SetResult("opened");
            }
            catch (SyscallException error)
            {
                result.SetResult(error.Message);
            }

            if (keep)
            {
                await childDone.Task;
            }
        });
        failure = await result.Task.WaitAsync(KernelDriver.Bound);
    }

    private Task<string> LocalAsync(string number) => ReadFileAsync(Init, $"/net/tcp/{number}/local");

    private async Task RecordAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    // The child signals once the call it waits in has begun, past any note waiting for it.
    private async Task ChildWaitsAsync(Func<Process, Action, Task> wait)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waited = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        childPid = Init.Fork(RforkFlags.Fdg, async child =>
        {
            child.Notify(_ => true);
            try
            {
                await wait(child, () => started.SetResult(true));
                waited.SetResult("returned");
            }
            catch (SyscallException error)
            {
                waited.SetResult(error.Message);
            }
        });
        await started.Task.WaitAsync(KernelDriver.Bound);
        childWait = waited.Task;
    }

    private async Task<string> ReadChildAsync()
        => Encoding.UTF8.GetString((await Init.ReadAsync(childData, 64).AsTask().WaitAsync(KernelDriver.Bound)).Span);
}
