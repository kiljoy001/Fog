using System.Text;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "Pipes carry bytes between processes as 9front's devpipe does")]
public sealed class PipeSteps(KernelDriver driver)
{
    private static readonly TimeSpan Bound = KernelDriver.Bound;
    private readonly CancellationTokenSource cancel = new();
    private Task<ReadOnlyMemory<byte>>? read;
    private Task<int>? write;
    private Waitmsg? waited;
    private long child;

    [When(@"^it writes ""(.*)"" to the (first|second) end$")]
    public async Task WhenWrites(string text, string end)
        => Assert.Equal(text.Length, await driver.Init.WriteAsync(End(end), Encoding.UTF8.GetBytes(text)).AsTask().WaitAsync(Bound));

    [When(@"^it writes (\d+) bytes to the first end$")]
    public async Task WhenWritesBytes(int count) => Assert.Equal(count, await driver.Init.WriteAsync(driver.Pipe[0], new byte[count]).AsTask().WaitAsync(Bound));

    [When(@"^it starts writing (\d+) bytes to the first end$")]
    public void WhenStartsWriting(int count) => write = driver.Init.WriteAsync(driver.Pipe[0], new byte[count]).AsTask();

    [When(@"^it reads (\d+) bytes from the second end$")]
    public Task WhenReadsBytes(int count) => ReadAsync(driver.Pipe[1], count);

    [When("it starts reading the second end")]
    public void WhenStartsReading() => read = driver.Init.ReadAsync(driver.Pipe[1], 8192, cancel.Token).AsTask();

    [When("the read is cancelled")]
    public void WhenCancelled() => cancel.Cancel();

    [When(@"^it closes the (first|second) end$")]
    public Task WhenCloses(string end) => driver.Init.CloseAsync(End(end)).AsTask();

    [When(@"^it duplicates the first end as descriptor (\d+)$")]
    public async Task WhenDuplicates(int fd) => Assert.Equal(fd, await driver.Init.DupAsync(driver.Pipe[0], fd));

    [When(@"^it closes descriptor (\d+)$")]
    public Task WhenClosesDescriptor(int fd) => driver.Init.CloseAsync(fd).AsTask();

    [When(@"^it forks a child that writes ""(.*)"" to the first end$")]
    public async Task WhenChildWrites(string text)
    {
        child = driver.Init.Fork(RforkFlags.Fdg, async process =>
        {
            await process.WriteAsync(driver.Pipe[0], Encoding.UTF8.GetBytes(text));
            process.Exits("wrote");
        });
        waited = await driver.Init.WaitAsync().WaitAsync(Bound);
    }

    [Then(@"^reading the (first|second) end gives ""(.*)""$")]
    public async Task ThenReadingGives(string end, string text)
        => Assert.Equal(text, Encoding.UTF8.GetString((await ReadAsync(End(end), 8192)).Span));

    [Then(@"^reading (\d+) bytes from the second end gives ""(.*)""$")]
    public async Task ThenReadingCountGives(int count, string text)
        => Assert.Equal(text, Encoding.UTF8.GetString((await ReadAsync(driver.Pipe[1], count)).Span));

    [Then(@"^reading (\d+) bytes from the second end gives (\d+) bytes$")]
    public async Task ThenReadingCountGivesLength(int count, int length)
        => Assert.Equal(length, (await ReadAsync(driver.Pipe[1], count)).Length);

    [Then(@"^reading the second end fails with ""(.*)""$")]
    public async Task ThenReadingFails(string message)
        => Assert.Equal(message, (await Assert.ThrowsAsync<SyscallException>(() => ReadAsync(driver.Pipe[1], 8192))).Message);

    [Then(@"^the pipe's descriptors are (\d+) and (\d+)$")]
    public void ThenDescriptors(int first, int second) => Assert.Equal([first, second], driver.Pipe);

    [Then("the read is still waiting")]
    public void ThenReadWaiting() => Assert.False(read!.IsCompleted);

    [Then(@"^the read gives ""(.*)""$")]
    public async Task ThenReadGives(string text) => Assert.Equal(text, Encoding.UTF8.GetString((await read!.WaitAsync(Bound)).Span));

    [Then("the read was cancelled")]
    public Task ThenReadCancelled() => Assert.ThrowsAnyAsync<OperationCanceledException>(() => read!.WaitAsync(Bound));

    [Then("the write is still waiting")]
    public void ThenWriteWaiting() => Assert.False(write!.IsCompleted);

    [Then(@"^the write finishes having written (\d+) bytes$")]
    public async Task ThenWriteFinishes(int count) => Assert.Equal(count, await write!.WaitAsync(Bound));

    [Then(@"^the child's wait message is ""(.*)""$")]
    public void ThenChildWaitMessage(string message)
    {
        Assert.Equal(child, waited!.Pid);
        Assert.Equal(message.Replace("<pid>", child.ToString(), StringComparison.Ordinal), waited.Message);
    }

    private int End(string end) => end == "first" ? driver.Pipe[0] : driver.Pipe[1];

    private Task<ReadOnlyMemory<byte>> ReadAsync(int fd, int count) => driver.Init.ReadAsync(fd, count).AsTask().WaitAsync(Bound);
}
