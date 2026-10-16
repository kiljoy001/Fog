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
    private int announced = -1;
    private string announcedNumber = string.Empty;
    private int call = -1;
    private string callNumber = string.Empty;
    private long childPid;
    private int childData = -1;
    private string childLocal = string.Empty;
    private Task<string>? childWait;
    private int refusedPort;
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

    [When(@"^the process closes ""(.*)""$")]
    public Task WhenCloses(string name) => Init.CloseAsync(fds[name]).AsTask();

    [When(@"^the process writes ""(.*)"" to ""(.*)""$")]
    public Task WhenWrites(string text, string name) => RecordAsync(() => Init.WriteAsync(fds[name], Encoding.UTF8.GetBytes(text)).AsTask());

    [When(@"^the process connects ""(.*)"" to that port$")]
    public Task WhenConnects(string name) => RecordAsync(() => Init.WriteAsync(fds[name], Encoding.UTF8.GetBytes($"connect 127.0.0.1!{refusedPort}")).AsTask());

    [When(@"^the process writes ""(.*)"" to the announced conversation's ctl$")]
    public Task WhenWritesAnnounced(string text) => RecordAsync(() => Init.WriteAsync(announced, Encoding.UTF8.GetBytes(text)).AsTask());

    [When(@"^a child connects to the announced port and writes ""(.*)""$")]
    public async Task WhenChildConnects(string text)
    {
        string port = (await LocalAsync(announcedNumber)).Split('!')[1];
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The child shares the parent's descriptors, so the parent reads the child's end too.
        Init.Fork(RforkFlags.Proc, async child =>
        {
            int ctl = await child.OpenAsync("/net/tcp/clone", NinePConstants.ORDWR);
            string number = await ReadAsync(child, ctl);
            await child.WriteAsync(ctl, Encoding.UTF8.GetBytes($"connect 127.0.0.1!{port}"));
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

    [When("the process writes \"hangup\" to the call's ctl")]
    public Task WhenHangsUp() => Init.WriteAsync(call, "hangup"u8.ToArray()).AsTask();

    [When("the process closes the call")]
    public Task WhenClosesCall() => Init.CloseAsync(call).AsTask();

    [When("a child that continues after every note waits in the announced conversation's listen")]
    public Task WhenChildListens() => ChildWaitsAsync(async child =>
    {
        await child.OpenAsync($"/net/tcp/{announcedNumber}/listen", NinePConstants.ORDWR);
    });

    [When("a child that continues after every note waits to read the call's data twice")]
    public Task WhenChildReads() => ChildWaitsAsync(async child =>
    {
        int data = await child.OpenAsync($"/net/tcp/{callNumber}/data", NinePConstants.ORDWR);
        await child.ReadAsync(data, 64);
        await child.ReadAsync(data, 64);
    });

    [When(@"^the process posts ""(.*)"" to the child$")]
    public async Task WhenPosts(string note)
    {
        int fd = await Init.OpenAsync($"/proc/{childPid}/note", NinePConstants.OWRITE);
        await Init.WriteAsync(fd, Encoding.UTF8.GetBytes(note));
        await Init.CloseAsync(fd);
    }

    [When(@"^a child that becomes ""none"" opens ""(.*)""$")]
    public async Task WhenNoneChildOpens(string path)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Init.Fork(RforkFlags.Fdg, async child =>
        {
            int user = await child.OpenAsync("/dev/user", NinePConstants.OWRITE);
            await child.WriteAsync(user, "none"u8.ToArray());
            try
            {
                await child.OpenAsync(path, NinePConstants.ORDWR);
                result.SetResult("opened");
            }
            catch (SyscallException error)
            {
                result.SetResult(error.Message);
            }
        });
        failure = await result.Task.WaitAsync(KernelDriver.Bound);
    }

    [Then(@"^the names in ""(.*)"" are ""(.*)""$")]
    public async Task ThenNames(string path, string names)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(names, string.Join(' ', (await Init.DirReadAsync(fd)).Select(entry => entry.Name).Order(StringComparer.Ordinal)));
        await Init.CloseAsync(fd);
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

    private async Task ChildWaitsAsync(Func<Process, Task> wait)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waited = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        childPid = Init.Fork(RforkFlags.Fdg, async child =>
        {
            child.Notify(_ => true);
            started.SetResult(true);
            try
            {
                await wait(child);
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
