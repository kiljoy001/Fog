namespace NinePSharp.Fog.Gefs;

// main.c and fs.c's attach: a store's trees served by attach name, all through the store's one
// writer, committed every five seconds when changed, as gefs's task process syncs.
public sealed class GefsServer : IDisposable
{
    private static readonly TimeSpan SyncPeriod = TimeSpan.FromSeconds(5);

    private readonly Device device;
    private readonly Store store;
    private readonly Func<string, string, bool> inGroup;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, GefsFs> served = [];
    private readonly ITimer timer;

    internal GefsServer(Device device, Store store, Func<string, string, bool> inGroup, TimeProvider clock)
    {
        this.device = device;
        this.store = store;
        this.inGroup = inGroup;
        this.clock = clock;
        timer = clock.CreateTimer(_ => Tick(), null, SyncPeriod, SyncPeriod);
    }

    // Why the store became read only, if a commit or a queued write failed.
    public Exception? Failure => store.Failure;

    // A new store in a file of the given size, its root owned by owner.
    public static GefsServer Create(string path, long blocks, int arenas, string owner, Func<string, string, bool> inGroup, TimeProvider clock)
    {
        FileDevice file = FileDevice.Create(path, blocks);
        return new GefsServer(file, Store.Ream(file, arenas, owner, clock), inGroup, clock);
    }

    public static GefsServer Open(string path, Func<string, string, bool> inGroup, TimeProvider clock)
    {
        FileDevice file = FileDevice.Open(path);
        return new GefsServer(file, Store.Open(file), inGroup, clock);
    }

    // fsattach: a leading % dropped, and no name meaning main. Dump is reserved, so never a label.
    public GefsFs Attach(string aname)
    {
        string label = aname.StartsWith('%') ? aname[1..] : aname;
        label = label.Length == 0 ? "main" : label;
        lock (store.Gate)
        {
            if (!served.TryGetValue(label, out GefsFs? fs))
            {
                if (store.FindLabel(label) is null)
                {
                    throw new GefsException("attach -- bad specifier");
                }

                fs = new GefsFs(store, label, $"gefs/{label}", inGroup, clock);
                served[label] = fs;
            }

            return fs;
        }
    }

    // The console's snap: a new name for the snapshot a label names, or with mutable a tree forked
    // from it.
    public void Snap(string label, string name, bool mutable)
    {
        lock (store.Gate)
        {
            store.Tag(label, name, mutable);
        }
    }

    // A commit now, as the console's sync asks. The commit takes the writer's lock itself, and
    // releases it while it waits for the device.
    public void Sync() => store.Commit();

    public void Dispose()
    {
        timer.Dispose();
        if (Changed())
        {
            store.Commit();
        }

        (device as IDisposable)?.Dispose();
    }

    // A failed commit leaves the store read only, holding why, and the next tick finds it so.
    private void Tick()
    {
        if (Changed())
        {
            try
            {
                store.Commit();
            }
            catch (Exception failed) when (failed is GefsException or IOException)
            {
                // The store keeps why, and takes no more changes.
            }
        }
    }

    private bool Changed()
    {
        lock (store.Gate)
        {
            return store.Changed && !store.Broken;
        }
    }
}
