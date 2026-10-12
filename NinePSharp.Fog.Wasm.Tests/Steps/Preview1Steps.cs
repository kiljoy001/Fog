using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Wasm.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Wasm.Tests.Steps;

[Binding]
public sealed class Preview1Steps(WasmDriver driver)
{
    private static readonly TimeSpan Bound = WasmDriver.Bound;
    private Waitmsg? waited;
    private long child;
    private string? failure;
    private string? childName;
    private string? childFile;
    private string piped = string.Empty;

    [Given("a kernel that runs WASM applications")]
    public async Task GivenAKernel()
    {
        FogKernel kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(), clock: driver.Clock, loaders: [new WasmLoader(driver.Clock).LoadAsync]);
        driver.Init = await kernel.BootAsync();
    }

    [Given(@"^""(.*)"" holds the application ""(.*)""$")]
    public Task GivenApplication(string path, string fixture)
        => WriteAsync(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".wasm")));

    [Given(@"^""(.*)"" holds the bytes ([0-9a-f ]+)$")]
    public Task GivenBytes(string path, string bytes)
        => WriteAsync(path, [.. bytes.Split(' ').Select(b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture))]);

    [Given(@"^""(.*)"" holds ""(.*)""$")]
    public Task GivenText(string path, string text) => WriteAsync(path, Encoding.UTF8.GetBytes(text.Replace("\\n", "\n", StringComparison.Ordinal)));

    [Given(@"^the process's environment holds ""(.*)"" as ""(.*)""$")]
    public Task GivenEnvironment(string name, string value) => WriteAsync($"/env/{name}", Encoding.UTF8.GetBytes(value));

    [When(@"^a child with a pipe as descriptor 1 execs ""([^""]*)""$")]
    public Task WhenPipedExec(string path) => PipedExecAsync(path, [path]);

    [When(@"^a child with ""(.*)"" open for reading as descriptor 3 and a pipe as descriptor 1 execs ""(.*)""$")]
    public Task WhenPipedExecWithFile(string file, string path) => PipedExecAsync(path, [path], file);

    [When(@"^a child with a pipe as descriptor 1 execs ""(.*)"" with the arguments ""(.*)""$")]
    public Task WhenPipedExecWithArguments(string path, string arguments) => PipedExecAsync(path, arguments.Split(' '));

    [When(@"^a child with a pipe whose other end is closed as descriptor 1 execs ""(.*)""$")]
    public async Task WhenClosedPipeExec(string path)
    {
        var (first, second) = await driver.Init.PipeAsync();
        await driver.Init.CloseAsync(second);
        child = driver.Init.Fork(RforkFlags.Fdg, async process =>
        {
            await process.DupAsync(first, 1);
            await process.ExecAsync(path, [path]);
        });
        waited = await driver.Init.WaitAsync().WaitAsync(Bound);
    }

    [When(@"^a child with ""(.*)"" open as descriptor 1 execs ""(.*)""$")]
    public Task WhenExecWithOutput(string file, string path) => ExecWithOutputAsync(file, NinePConstants.OWRITE, path);

    [When(@"^a child with ""(.*)"" open for reading as descriptor 1 execs ""(.*)""$")]
    public async Task WhenExecWithInput(string file, string path)
    {
        await WriteAsync(file, "d"u8.ToArray());
        await ExecWithOutputAsync(file, NinePConstants.OREAD, path);
    }

    [When(@"^a child with ""(.*)"" open as descriptor 3 execs ""(.*)""$")]
    public async Task WhenExecWithDescriptor(string file, string path)
    {
        await WriteAsync(file, "d"u8.ToArray());
        child = driver.Init.Fork(RforkFlags.Fdg, async process =>
        {
            await process.DupAsync(await process.OpenAsync(file, NinePConstants.OREAD), 3);
            try
            {
                await process.ExecAsync(path, [path]);
            }
            catch (SyscallException error)
            {
                failure = error.Message;
                childFile = Encoding.UTF8.GetString((await process.PReadAsync(3, 1, 0)).Span);
                childName = process.Text;
                process.Exits(error.Message);
            }
        });
        waited = await driver.Init.WaitAsync().WaitAsync(Bound);
    }

    [Then(@"^exec fails with ""(.*)""$")]
    public void ThenExecFails(string message) => Assert.Equal(message, failure);

    [Then("the child still has descriptor 3 open and its own name")]
    public void ThenUnchanged()
    {
        Assert.Equal("d", childFile);
        Assert.Equal("*init*", childName);
    }

    [Then(@"^the pipe holds ""(.*)""$")]
    public void ThenPipeHolds(string contents)
        => Assert.Equal(contents.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\0", "\0", StringComparison.Ordinal), piped);

    [Then("the parent's wait message for it is empty")]
    public void ThenWaitEmpty() => ThenWaitMessage(string.Empty);

    [Then(@"^the parent's wait message for it is ""(.*)""$")]
    public void ThenWaitMessage(string message)
    {
        Assert.Equal(child, waited!.Pid);
        Assert.Equal(message.Replace("<pid>", child.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), waited.Message);
    }

    private async Task WriteAsync(string path, byte[] contents)
    {
        int fd = await driver.Init.CreateAsync(path, NinePConstants.OWRITE, 0b111_111_101);
        await driver.Init.WriteAsync(fd, contents);
        await driver.Init.CloseAsync(fd);
    }

    private async Task ExecWithOutputAsync(string file, int mode, string path)
    {
        child = driver.Init.Fork(RforkFlags.Fdg, async process =>
        {
            await process.DupAsync(await process.OpenAsync(file, mode), 1);
            await process.ExecAsync(path, [path]);
        });
        waited = await driver.Init.WaitAsync().WaitAsync(Bound);
    }

    // The child writes to the pipe's first end as descriptor 1; once it and the parent's copy of that
    // end are closed, the second end reads to its end. The ends may themselves be 0 and 1.
    private async Task PipedExecAsync(string path, string[] argv, string? file = null)
    {
        var (first, second) = await driver.Init.PipeAsync();
        child = driver.Init.Fork(RforkFlags.Fdg, async process =>
        {
            await process.DupAsync(first, 1);
            foreach (int end in new[] { first, second }.Where(end => end != 1))
            {
                await process.CloseAsync(end);
            }

            if (file is not null)
            {
                await process.RenumberAsync(await process.OpenAsync(file, NinePConstants.OREAD), 3);
            }

            await process.ExecAsync(path, argv);
        });
        await driver.Init.CloseAsync(first);
        var output = new StringBuilder();
        Task<Waitmsg> wait = driver.Init.WaitAsync().WaitAsync(Bound);
        for (ReadOnlyMemory<byte> block; !(block = await driver.Init.ReadAsync(second, 8192).AsTask().WaitAsync(Bound)).IsEmpty;)
        {
            output.Append(Encoding.UTF8.GetString(block.Span));
        }

        await driver.Init.CloseAsync(second);
        waited = await wait;
        piped = output.ToString();
    }
}
