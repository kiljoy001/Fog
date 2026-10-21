using NinePSharp.Constants;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Kernel.Tests;

// What the namespace's walk always finds first, as 9front's does unless a create races it: srvcreate
// still refuses clone, a name in use, and a board that has gone.
public sealed class SrvDeviceTests
{
    private static readonly ResourceOperationContext Context = new(new ResourceOperationId("test", 1), 1, "glenda");

    [Fact]
    public async Task CreateRefusesCloneAndANameInUse()
    {
        SrvDevice srv = (await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync()).Kernel.Srv;
        ResourceOpenHandle lease = await srv.OpenAsync((await srv.WalkAsync(SrvDevice.Root, "clone", default))!, NinePConstants.OREAD, new(new ResourceOperationId("test", 2), 1, "glenda"), default);
        await srv.CreateAndOpenAsync(SrvDevice.Root, "posted", 0b110_110_110, NinePConstants.OWRITE, new(new ResourceOperationId("test", 3), 1, "glenda"), default);
        string board = System.Text.Encoding.UTF8.GetString((await srv.ReadAsync(lease, 0, 64, default)).Span);

        foreach (string name in new[] { "clone", "posted", board })
        {
            Assert.Equal(Errors.Exist, (await Assert.ThrowsAsync<IOException>(() => srv.CreateAndOpenAsync(SrvDevice.Root, name, 0b110_110_110, NinePConstants.OWRITE, Context, default).AsTask())).Message);
        }
    }

    [Fact]
    public async Task TheNamespaceMakesNothingInSrvButByOpeningIt()
    {
        SrvDevice srv = (await FogKernel.InMemory(new Dictionary<string, ProgramMain>(), "glenda").BootAsync()).Kernel.Srv;
        await Assert.ThrowsAsync<NotSupportedException>(() => srv.CreateAsync(SrvDevice.Root, "x", false, default).AsTask());
    }
}
