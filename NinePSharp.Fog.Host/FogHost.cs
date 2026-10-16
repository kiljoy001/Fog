using NinePSharp.Fog.Gefs;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Host;

// The host's boot: its root is a gefs store in a file, opened as gefs opens its device, or reamed
// as gefs -r reams one when the file does not exist. A file that exists is never reamed.
public sealed class FogHost : IDisposable
{
    private readonly GefsServer store;

    private FogHost(GefsServer store, Process init)
    {
        this.store = store;
        Init = init;
    }

    public Process Init { get; }

    public static async Task<FogHost> BootAsync(FogHostSettings settings)
    {
        GefsServer store = File.Exists(settings.StorePath)
            ? GefsServer.Open(settings.StorePath, InGroup, settings.Clock)
            : GefsServer.Create(settings.StorePath, settings.StoreBlocks, GefsServer.Arenas(settings.StoreBlocks), settings.Owner, InGroup, settings.Clock);
        try
        {
            GefsFs main = store.Attach(string.Empty);
            var kernel = new FogKernel(settings.Programs, main, main.Root, settings.Owner, settings.Loaders) { Clock = settings.Clock };
            return new FogHost(store, await kernel.BootAsync());
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    // Commits what the store holds, as gefs does when it halts.
    public void Dispose() => store.Dispose();

    // Until the store's /adm/users is read, each user is the only member of its own group.
    private static bool InGroup(string user, string group) => user == group;
}
