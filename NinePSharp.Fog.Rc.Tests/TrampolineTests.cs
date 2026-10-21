using System.Net;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// aux/trampoline as another program starts it, with what it inherits that rc cannot give it.
public sealed class TrampolineTests : IAsyncLifetime
{
    private readonly FakeHostNetwork host = new();
    private FogKernel kernel = null!;
    private Process init = null!;

    public async Task InitializeAsync()
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", host: host);
        init = await kernel.BootAsync();
        int f = await init.CreateAsync("/tmp/ndb", NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(f, Encoding.UTF8.GetBytes("sys=fogtest ip=192.168.1.10\n"));
        await init.CloseAsync(f);
        init.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/ndb/cs", ["ndb/cs", "-f", "/tmp/ndb"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
    }

    public Task DisposeAsync()
    {
        host.Gate = null;
        foreach (Process process in kernel.Processes.Values)
        {
            process.PostNote("kill");
        }

        return Task.CompletedTask;
    }

    // Without -t trampoline leaves the alarm alone, as trampoline.c does, so one set before exec
    // still rings during its dial.
    [Fact]
    public async Task WithoutTAnAlarmItWasStartedWithStillEndsItsDial()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Gate = gate.Task;
        host.Names["slow.example"] = [IPAddress.Parse("10.9.9.9")];
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            await child.AlarmAsync(300);
            await child.ExecAsync("/bin/aux/trampoline", ["aux/trampoline", "tcp!slow.example!1"]);
        });
        Waitmsg ended = await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        gate.SetResult();

        Assert.EndsWith(": alarm", ended.Message, StringComparison.Ordinal);
    }
}
