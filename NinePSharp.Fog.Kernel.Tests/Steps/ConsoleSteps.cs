using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests.Steps;

[Binding]
[Scope(Feature = "/dev holds devcons's bintime, null, zero, pid, ppid and user, as 9front binds #c there")]
public sealed class ConsoleSteps(KernelDriver driver)
{
    private readonly List<string> childRead = new();
    private long child;
    private string listed = string.Empty;
    private string? failure;
    private bool opened;

    [Given(@"^a kernel booted for the host owner ""(.*)""$")]
    public async Task GivenKernel(string user)
        => driver.Init = await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), user, driver.Clock).BootAsync();

    [Given(@"^the clock reads (\d+) seconds since the epoch$")]
    public void GivenClock(long seconds) => driver.Clock.Now = DateTimeOffset.FromUnixTimeSeconds(seconds);

    [Then(@"^reading 32 bytes of ""(.*)"" gives (\d+) nanoseconds, then ticks, their frequency and the nanoseconds since boot$")]
    public async Task ThenBintime(string path, long nanoseconds)
    {
        byte[] bytes = await PReadAsync(path, 32, 0);
        long[] numbers = [.. Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(i * 8)))];
        Assert.Equal((nanoseconds, driver.Clock.TimestampFrequency), (numbers[0], numbers[2]));
        Assert.InRange(numbers[1], 1, driver.Clock.GetTimestamp());
        Assert.InRange(numbers[3], 0, (long)TimeSpan.FromMinutes(1).TotalNanoseconds);
    }

    [Then(@"^reading (\d+) bytes of ""(.*)"" at offset (\d+) gives (\d+) numbers$")]
    public async Task ThenBintimeNumbers(int count, string path, long offset, int numbers)
    {
        byte[] bytes = await PReadAsync(path, count, offset);
        Assert.Equal(numbers * 8, bytes.Length);
        if (numbers > 0)
        {
            Assert.Equal(1_000_000_000_000, BinaryPrimitives.ReadInt64BigEndian(bytes));
        }
    }

    [When(@"^the process lists ""(.*)""$")]
    public async Task WhenLists(string path)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        listed = string.Join(' ', (await driver.Init.DirReadAsync(fd)).Select(entry => entry.Name));
    }

    [When(@"^the process forks a child that reads ""(.*)"" and ""(.*)""$")]
    public async Task WhenChildReads(string first, string second)
    {
        child = driver.Init.Fork(RforkFlags.Fdg, async process =>
        {
            childRead.Add(await ReadAsync(process, first, 100));
            childRead.Add(await ReadAsync(process, second, 100));
        });
        await driver.Bounded(new ValueTask<Waitmsg>(driver.Init.WaitAsync()));
    }

    [When(@"^the process writes ""(.*)"" to ""(.*)""$")]
    public async Task WhenWrites(string text, string path)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OWRITE);
        try
        {
            await driver.Init.WriteAsync(fd, Encoding.UTF8.GetBytes(text));
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [Given(@"^""(.*)"" belongs to the host owner with the permissions (\d+)$")]
    public async Task GivenOwned(string path, string permissions)
        => await driver.Init.CloseAsync(await driver.Init.CreateAsync(path, NinePConstants.OWRITE, Convert.ToUInt32(permissions, 8)));

    [Then(@"^""(.*)"" belongs to the host owner with the mode (\d+)$")]
    public async Task ThenOwned(string path, string mode)
    {
        var stat = await driver.Init.StatAsync(path);
        Assert.Equal((driver.Init.User, Convert.ToUInt32(mode, 8)), (stat.Uid, stat.Mode));
    }

    [When(@"^the process opens ""(.*)"" for (reading|writing)$")]
    public async Task WhenOpens(string path, string access)
    {
        try
        {
            await driver.Init.OpenAsync(path, access == "reading" ? NinePConstants.OREAD : NinePConstants.OWRITE);
            opened = true;
        }
        catch (SyscallException error)
        {
            failure = error.Message;
        }
    }

    [Then(@"^it sees ""(.*)""$")]
    public void ThenSees(string names) => Assert.Equal(names, listed);

    [Then(@"^reading ""(.*)"" gives ""(.*)""$")]
    public async Task ThenReadingGives(string path, string text) => Assert.Equal(text, await ReadAsync(driver.Init, path, 100));

    [Then(@"^writing ""(.*)"" to ""(.*)"" writes (\d+) bytes$")]
    public async Task ThenWritingWrites(string text, string path, int count)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OWRITE);
        Assert.Equal(count, await driver.Init.WriteAsync(fd, Encoding.UTF8.GetBytes(text)));
    }

    [Then(@"^reading (\d+) bytes of ""(.*)"" gives (\d+) zero bytes$")]
    public async Task ThenReadingZeros(int count, string path, int zeros)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        Assert.Equal(new byte[zeros], (await driver.Init.ReadAsync(fd, count)).ToArray());
    }

    [Then("the child read its own number and then the parent's, each in twelve bytes")]
    public void ThenChildReadNumbers()
    {
        string Number(long pid) => pid.ToString(CultureInfo.InvariantCulture).PadLeft(11) + " ";
        Assert.Equal([Number(child), Number(driver.Init.Pid)], childRead);
    }

    [Then(@"^reading ""(.*)"" (\d+) bytes at a time gives (\d+) bytes and then nothing$")]
    public async Task ThenReadingInPieces(string path, int piece, int total)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        var read = new List<byte>();
        for (ReadOnlyMemory<byte> block; !(block = await driver.Init.ReadAsync(fd, piece)).IsEmpty;)
        {
            read.AddRange(block.ToArray());
        }

        Assert.Equal(driver.Init.Pid.ToString(CultureInfo.InvariantCulture).PadLeft(11) + " ", Encoding.UTF8.GetString(read.ToArray()));
        Assert.Equal(total, read.Count);
    }

    [Then(@"^a child it forks reads ""(.*)"" from ""(.*)""$")]
    public async Task ThenChildReads(string text, string path)
    {
        driver.Init.Fork(RforkFlags.Fdg, async process => childRead.Add(await ReadAsync(process, path, 100)));
        await driver.Bounded(new ValueTask<Waitmsg>(driver.Init.WaitAsync()));
        Assert.Equal([text], childRead);
    }

    [Then(@"^the write fails with ""(.*)""$")]
    public void ThenWriteFails(string message) => Assert.Equal(message, failure);

    [Then("the open succeeds")]
    public void ThenOpenSucceeds() => Assert.True(opened);

    [Then(@"^the open fails with ""(.*)""$")]
    public void ThenOpenFails(string message) => Assert.Equal(message, failure);

    private static async Task<string> ReadAsync(Process process, string path, int count)
    {
        int fd = await process.OpenAsync(path, NinePConstants.OREAD);
        string text = Encoding.UTF8.GetString((await process.ReadAsync(fd, count)).Span);
        await process.CloseAsync(fd);
        return text;
    }

    private async Task<byte[]> PReadAsync(string path, int count, long offset)
    {
        int fd = await driver.Init.OpenAsync(path, NinePConstants.OREAD);
        try
        {
            return (await driver.Init.PReadAsync(fd, count, offset)).ToArray();
        }
        finally
        {
            await driver.Init.CloseAsync(fd);
        }
    }
}
