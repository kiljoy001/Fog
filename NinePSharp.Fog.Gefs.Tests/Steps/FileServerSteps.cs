using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Gefs.Tests.Support;
using NinePSharp.Namespaces;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class FileServerSteps(StoreContext context)
{
    private const uint Directory = 0x80000000;

    private static readonly Dictionary<string, string[]> Groups = new() { ["bob"] = ["dev"], ["glenda"] = ["sys"] };

    private readonly ManualClock clock = new();
    private readonly Dictionary<string, byte[]> written = [];
    private readonly Dictionary<string, ResourceHandle> seen = [];
    private readonly Dictionary<string, ResourceOpenHandle> opened = [];
    private GefsFs? fs;
    private ResourceHandle? renamed;
    private ulong sequence;

    [Given(@"^a device of (\d+) blocks reamed with (\d+) arenas by (\w+) at time (\d+), serving main$")]
    public void GivenReamed(long blocks, int arenas, string owner, long time)
    {
        At(time);
        context.Device = new MemoryDevice(blocks);
        context.Store = Store.Ream(context.Device, arenas, owner, clock);
        Serve();
    }

    [When("the device is opened again, serving main")]
    public void WhenReopened()
    {
        context.Store = Store.Open(context.Device!);
        Serve();
    }

    [When("the store commits")]
    public void WhenCommitted() => context.Store!.Commit();

    [Then(@"^(.+) is an? (file|directory) ""(.*)"" owned by (\w+) in group (\w+), mode (d?\d+), version (\d+), modified at (\d+)$")]
    public void ThenIs(string path, string kind, string name, string user, string group, string mode, uint version, uint mtime)
    {
        ResourceStat stat = Stat(path);
        Assert.Equal((name, user, group, Mode(mode), version, mtime), (stat.Name, stat.User, stat.Group, stat.Mode, stat.Resource.Version, stat.ModificationTime));
        Assert.Equal(kind == "directory", stat.Resource.IsDirectory);
    }

    [Then(@"^(.+) is an? (file|directory) ""(.*)"" owned by (\w+) in group (\w+), mode (d?\d+), version (\d+), modified at (\d+), (\d+) bytes long, last changed by (\w+)$")]
    public void ThenIsLong(string path, string kind, string name, string user, string group, string mode, uint version, uint mtime, ulong length, string muid)
    {
        ThenIs(path, kind, name, user, group, mode, version, mtime);
        ResourceStat stat = Stat(path);
        Assert.Equal((length, muid), (stat.Length, stat.LastModifier));
    }

    [Then(@"^walking \.\. from (.+) gives (.+)$")]
    public void ThenWalksUp(string path, string up)
        => Assert.Equal(Resolve(up).Identity, Walk(Resolve(path), "..")!.Identity);

    [Then(@"^walking (\S+) from (.+) finds nothing$")]
    public void ThenWalkFails(string name, string path) => Assert.Null(Walk(Resolve(path), name));

    [Then(@"^(the root|[\w/]+) lists (.+)$")]
    public void ThenLists(string path, string names)
    {
        string[] expected = names switch
        {
            "nothing" => [],
            "the name of 244 bytes" => [new string('n', 244)],
            _ => names.Replace(" and ", ", ", StringComparison.Ordinal).Split(", "),
        };
        Assert.Equal(expected, fs!.ReadDirectoryAsync(Resolve(path), default).AsTask().Result.Select(e => e.Name));
    }

    [When(@"^(\w+) sets the root's mode to (d?\d+) at time (\d+)$")]
    public void WhenRootMode(string user, string mode, long time)
    {
        At(time);
        WStat(fs!.Root, user, ResourceWStat.Unchanged() with { Mode = Mode(mode) });
    }

    [When(@"^(\w+) creates (file|directory) (\S+) in (.+) with mode (d?\d+) at time (\d+)$")]
    public void WhenCreates(string user, string kind, string name, string path, string mode, long time)
    {
        At(time);
        Create(user, Resolve(path), name, Mode(mode) | (kind == "directory" ? Directory : 0));
    }

    [When(@"^(\w+) creates files (.+) in (.+) with mode (\d+) at time (\d+)$")]
    public void WhenCreatesMany(string user, string names, string path, string mode, long time)
    {
        foreach (string name in names.Replace(" and ", ", ", StringComparison.Ordinal).Split(", "))
        {
            WhenCreates(user, "file", name, path, mode, time);
        }
    }

    [When(@"^(\w+) creates an? (append-only|exclusive) file (\S+) in (.+) with mode (\d+) at time (\d+)$")]
    public void WhenCreatesSpecial(string user, string kind, string name, string path, string mode, long time)
    {
        At(time);
        ResourceOpenHandle open = Create(user, Resolve(path), name, Mode(mode) | (kind == "append-only" ? 0x40000000u : 0x20000000u));
        Clunk(user, open);
    }

    [When(@"^(\w+) creates a file named in (\d+) bytes in the root$")]
    public void WhenCreatesLong(string user, int length) => Create(user, fs!.Root, new string('n', length), 0b110_100_100);

    [Then(@"^(\w+) creating (.+) fails with ""(.*)""$")]
    public void ThenCreateFails(string user, string what, string error)
    {
        var (name, path, mode) = what switch
        {
            "a file with no name in the root" => (string.Empty, "the root", 0b110_100_100u),
            "a file named with a tab in the root" => ("a\tb", "the root", 0b110_100_100u),
            "a file named in 245 bytes in the root" => (new string('n', 245), "the root", 0b110_100_100u),
            "a mount point x in the root" => ("x", "the root", 0x10000000u | 0b110_100_100),
            "an authentication file x in the root" => ("x", "the root", 0x08000000u | 0b110_100_100),
            "a file x with unknown mode bits" => ("x", "the root", 0x00100000u | 0b110_100_100),
            _ => (what.Split(' ')[1], what.Split(" in ", 2)[1], 0b110_100_100u),
        };
        var refused = Assert.Throws<ResourceCreateRejectedException>(() => Create(user, Resolve(path), name, mode));
        Assert.Equal(error, refused.Message);
    }

    [When(@"^(\w+) writes ""(.*)"" to (\S+) at offset (\d+) at time (\d+)$")]
    public void WhenWritesText(string user, string text, string path, ulong offset, long time) => Write(user, path, offset, Encoding.ASCII.GetBytes(text), time);

    [When(@"^(\w+) writes (\d+) bytes to (\S+) at offset (\d+) at time (\d+)$")]
    public void WhenWritesBytes(string user, int count, string path, ulong offset, long time)
    {
        byte[] data = [.. Enumerable.Range(0, count).Select(i => (byte)((i * 7) + 3))];
        written[path] = data;
        Write(user, path, offset, data, time);
    }

    [Then(@"^reading (\d+) bytes of (\S+) at offset (\d+) gives nothing$")]
    public void ThenReadsNothing(uint count, string path, ulong offset) => Assert.Equal(0, Read(path, offset, count).Length);

    [Then(@"^reading (\d+) bytes of (\S+) at offset (\d+) gives ""(.*)""$")]
    public void ThenReadsText(uint count, string path, ulong offset, string text) => Assert.Equal(text, Encoding.ASCII.GetString(Read(path, offset, count)));

    [Then(@"^reading (\d+) bytes of (\S+) at offset (\d+) gives (\d+) zeros$")]
    public void ThenReadsZeros(uint count, string path, ulong offset, int zeros) => Assert.Equal(new byte[zeros], Read(path, offset, count));

    [Then(@"^reading (\d+) bytes of (\S+) at offset (\d+) gives the first (\d+) bytes written$")]
    public void ThenReadsWritten(uint count, string path, ulong offset, int first) => Assert.Equal(written[path][..first], Read(path, offset, count));

    [Then(@"^reading the (\d+) bytes of (\S+) at offset (\d+) gives what was written$")]
    public void ThenReadsAll(uint count, string path, ulong offset) => Assert.Equal(written[path], Read(path, offset, count));

    [Then(@"^(\w+) writing to (\S+) opened only for reading fails with ""(.*)""$")]
    public void ThenWriteRefused(string user, string path, string error)
    {
        ResourceOpenHandle open = Open(user, path, NinePConstants.OREAD);
        Assert.Equal(error, Assert.Throws<IOException>(() => fs!.WriteAsync(open, 0, new byte[1], Context(user), default).AsTask().GetAwaiter().GetResult()).Message);
    }

    [When(@"^(\w+) removes (\S+)$")]
    public void WhenRemoves(string user, string path) => fs!.RemoveAsync(Resolve(path), null, Context(user), default).AsTask().GetAwaiter().GetResult();

    [Then(@"^statting the removed (\S+) fails with ""(.*)""$")]
    public void ThenStatRemoved(string path, string error)
        => Assert.Equal(error, Assert.Throws<IOException>(() => fs!.StatAsync(seen[path], default).AsTask().GetAwaiter().GetResult()).Message);

    [Then(@"^(\w+) removing (.+) fails with ""(.*)""$")]
    public void ThenRemoveFails(string user, string path, string error)
        => Assert.Equal(error, Assert.Throws<IOException>(() => WhenRemoves(user, path)).Message);

    [When(@"^(\w+) opens (\S+) with truncation$")]
    public void WhenTruncates(string user, string path) => Clunk(user, Open(user, path, NinePConstants.OWRITE | NinePConstants.OTRUNC));

    [When(@"^(\w+) sets (\S+)'s mode to (\d+), group to (\w+) and owner to (\w+) at time (\d+)$")]
    public void WhenSetsAll(string user, string path, string mode, string group, string owner, long time)
    {
        At(time);
        WStat(Resolve(path), user, ResourceWStat.Unchanged() with { Mode = Mode(mode), Group = group, User = owner });
    }

    [Then(@"^(\w+) opening (.+) to (read|write|run) (succeeds|fails with "".*"")$")]
    public void ThenOpening(string user, string path, string how, string result)
    {
        byte mode = how switch { "read" => NinePConstants.OREAD, "write" => NinePConstants.OWRITE, _ => NinePConstants.OEXEC };
        if (result == "succeeds")
        {
            Clunk(user, Open(user, path, mode));
        }
        else
        {
            Assert.Equal(result.Split('"')[1], Assert.Throws<IOException>(() => Open(user, path, mode)).Message);
        }
    }

    [When(@"^(\w+) opens (\S+) to be removed on close, and closes it$")]
    public void WhenRemovesOnClose(string user, string path) => Clunk(user, Open(user, path, NinePConstants.OREAD | NinePConstants.ORCLOSE));

    [Then(@"^(\w+) opening (\S+) to be removed on close fails with ""(.*)""$")]
    public void ThenRemoveOnCloseFails(string user, string path, string error)
        => Assert.Equal(error, Assert.Throws<IOException>(() => Open(user, path, NinePConstants.OREAD | NinePConstants.ORCLOSE)).Message);

    [When(@"^(\w+) opens (\S+) to read$")]
    public void WhenOpensToRead(string user, string path) => opened[path] = Open(user, path, NinePConstants.OREAD);

    [When(@"^(\w+) closes (\S+)$")]
    public void WhenCloses(string user, string path) => Clunk(user, opened[path]);

    [When(@"^(\w+) renames (\S+) to (\S+) at time (\d+)$")]
    public void WhenRenames(string user, string path, string name, long time)
    {
        At(time);
        renamed = Resolve(path);
        WStat(renamed, user, ResourceWStat.Unchanged() with { Name = name });
    }

    [When(@"^(\w+) sets (the .+|a name of \d+ bytes) of (\S+) at time (\d+)$")]
    public void WhenSets(string user, string change, string path, long time)
    {
        At(time);
        WStat(Resolve(path), user, Change(change));
    }

    [When(@"^(\w+) sets nothing of (\S+)$")]
    public void WhenSetsNothing(string user, string path) => WStat(Resolve(path), user, ResourceWStat.Unchanged());

    [Then(@"^(\w+) setting (.+) of (\S+) fails with ""(.*)""$")]
    public void ThenSetFails(string user, string change, string path, string error)
        => Assert.Equal(error, Assert.Throws<ResourceWStatRejectedException>(() => WStat(Resolve(path), user, Change(change))).Message);

    [Then(@"^(\S+)'s (mode|group) is (\S+)$")]
    public void ThenField(string path, string field, string value)
    {
        ResourceStat stat = Stat(path);
        Assert.Equal(value, field == "mode" ? Convert.ToString(stat.Mode & 0b111_111_111, 8).PadLeft(4, '0') : stat.Group);
    }

    [Then("the store has committed once since it was reamed")]
    public void ThenCommittedOnce() => Assert.Equal(2, context.Store!.Generation);

    private static bool InGroup(string user, string group) => user == group || (Groups.TryGetValue(user, out string[]? groups) && groups.Contains(group));

    private static uint Mode(string mode) => mode[0] == 'd' ? Directory | Convert.ToUInt32(mode[1..], 8) : Convert.ToUInt32(mode, 8);

    private static ResourceWStat Change(string change)
    {
        string[] words = change.Split(' ');
        return change switch
        {
            "the qid" => ResourceWStat.Unchanged() with { Qid = new NinePSharp.Constants.Qid(QidType.QTFILE, 0, 0) },
            "the mode to 0664 with unknown bits" => ResourceWStat.Unchanged() with { Mode = 0x00100000u | 0b110_110_100 },
            _ when change.StartsWith("a name of", StringComparison.Ordinal) => ResourceWStat.Unchanged() with { Name = new string('n', int.Parse(words[3])) },
            _ => words[1] switch
            {
                "mode" => ResourceWStat.Unchanged() with { Mode = Mode(words[3]) },
                "mtime" => ResourceWStat.Unchanged() with { ModificationTime = uint.Parse(words[3]) },
                "owner" => ResourceWStat.Unchanged() with { User = words[3] },
                "group" => ResourceWStat.Unchanged() with { Group = words[3] },
                "length" => ResourceWStat.Unchanged() with { Length = ulong.Parse(words[3]) },
                "name" => ResourceWStat.Unchanged() with { Name = words[3] },
                _ => ResourceWStat.Unchanged() with { LastModifier = words[^1] },
            },
        };
    }

    private void Serve() => fs = new GefsFs(context.Store!, "main", "gefs/main", InGroup, clock);

    private void At(long time) => clock.Now = DateTimeOffset.FromUnixTimeSeconds(time);

    private ResourceOperationContext Context(string user) => new(new ResourceOperationId("test", ++sequence), 1, user);

    private ResourceHandle? Walk(ResourceHandle from, string name) => fs!.WalkAsync(from, name, default).AsTask().Result;

    private ResourceHandle Resolve(string path)
    {
        if (path == "the renamed handle")
        {
            return renamed!;
        }

        ResourceHandle at = fs!.Root;
        if (path != "the root")
        {
            foreach (string name in path.Split('/'))
            {
                at = Walk(at, name) ?? throw new InvalidOperationException($"{path} does not exist");
            }
        }

        seen[path] = at;
        return at;
    }

    private ResourceStat Stat(string path) => fs!.StatAsync(Resolve(path), default).AsTask().GetAwaiter().GetResult();

    private ResourceOpenHandle Create(string user, ResourceHandle directory, string name, uint mode)
        => fs!.CreateAndOpenAsync(directory, name, mode, NinePConstants.ORDWR, Context(user), default).AsTask().GetAwaiter().GetResult();

    private ResourceOpenHandle Open(string user, string path, byte mode) => fs!.OpenAsync(Resolve(path), mode, Context(user), default).AsTask().GetAwaiter().GetResult();

    private void Clunk(string user, ResourceOpenHandle open) => fs!.ClunkAsync(open, Context(user), default).AsTask().GetAwaiter().GetResult();

    private void WStat(ResourceHandle at, string user, ResourceWStat stat) => fs!.WStatAsync(at, stat, Context(user), default).AsTask().GetAwaiter().GetResult();

    private void Write(string user, string path, ulong offset, byte[] data, long time)
    {
        At(time);
        ResourceOpenHandle open = Open(user, path, NinePConstants.OWRITE);
        Assert.Equal((uint)data.Length, fs!.WriteAsync(open, offset, data, Context(user), default).AsTask().GetAwaiter().GetResult());
        Clunk(user, open);
    }

    private byte[] Read(string path, ulong offset, uint count)
    {
        ResourceOpenHandle open = Open("adm", path, NinePConstants.OREAD);
        byte[] data = fs!.ReadAsync(open, offset, count, default).AsTask().GetAwaiter().GetResult().ToArray();
        Clunk("adm", open);
        return data;
    }
}
