using System.Text;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class PackingSteps
{
    private int comparison;
    private byte[]? key;
    private byte[]? value;
    private List<byte[]> sorted = [];
    private Dir? entry;
    private Bptr pointer;
    private byte[]? block;
    private TreeEntry? tree;
    private Superblock? superblock;
    private string? failure;

    [When(@"^key ([0-9a-f]+) is compared with key ([0-9a-f]+)$")]
    public void WhenCompared(string a, string b) => comparison = Keys.Compare(Convert.FromHexString(a), Convert.FromHexString(b));

    [Then(@"^it sorts (before|after|level)$")]
    public void ThenSorts(string order) => Assert.Equal(order switch { "before" => -1, "after" => 1, _ => 0 }, comparison);

    [When(@"^the entry key for ""(.*)"" in directory (\d+) is packed$")]
    public void WhenEntryKey(string name, long parent) => key = Keys.Entry(parent, name);

    [When(@"^the entry key for a name of (\d+) bytes in directory (\d+) is packed$")]
    public void WhenLongEntryKey(int length, long parent) => Try(() => key = Keys.Entry(parent, new string('x', length)));

    [When(@"^the snapshot key for snapshot (\d+) is packed$")]
    public void WhenSnapKey(long id) => key = Keys.Snap(id);

    [Then("packing succeeds")]
    public void ThenPackingSucceeds() => Assert.Null(failure);

    [Then(@"^its bytes are (.*)$")]
    public void ThenBytes(string hex) => Assert.Equal(hex.Replace(" ", string.Empty, StringComparison.Ordinal), Convert.ToHexStringLower(key!));

    [Then(@"^it unpacks to directory (\d+) and the name ""(.*)""$")]
    public void ThenUnpacksToEntry(long parent, string name)
    {
        Assert.Equal(name, Keys.ReadEntry(key, out long up));
        Assert.Equal(parent, up);
    }

    [When(@"^the entry keys for ""(.*)"" in directory (\d+), ""(.*)"" in directory (\d+) and ""(.*)"" in directory (\d+) are sorted$")]
    public void WhenSorted(string a, long pa, string b, long pb, string c, long pc)
    {
        sorted = [Keys.Entry(pa, a), Keys.Entry(pb, b), Keys.Entry(pc, c)];
        sorted.Sort((x, y) => Keys.Compare(x, y));
    }

    [Then(@"^they are in the order ""(.*)"" in directory (\d+), ""(.*)"" in directory (\d+), ""(.*)"" in directory (\d+)$")]
    public void ThenOrder(string a, long pa, string b, long pb, string c, long pc)
        => Assert.Equal([Keys.Entry(pa, a), Keys.Entry(pb, b), Keys.Entry(pc, c)], sorted);

    [Given(@"^an entry ""(.*)"" with qid (\d+) version (\d+), mode (\d+), length (\d+), atime (\d+), mtime (\d+), user (\d+), group (\d+) and muid (\d+), flagged (\d+)$")]
    public void GivenFlaggedEntry(string name, long path, uint version, string mode, long length, long atime, long mtime, int uid, int gid, int muid, long flag)
    {
        GivenEntry(name, path, version, mode, length, atime, mtime, uid, gid, muid);
        entry = entry! with { Flag = flag };
    }

    [Given(@"^an entry ""(.*)"" with qid (\d+) version (\d+), mode (\d+), length (\d+), atime (\d+), mtime (\d+), user (\d+), group (\d+) and muid (\d+)$")]
    public void GivenEntry(string name, long path, uint version, string mode, long length, long atime, long mtime, int uid, int gid, int muid)
    {
        uint permissions = Convert.ToUInt32(mode, 8);
        entry = new Dir(name, new Qid(path, version, (byte)(permissions >> 24)), permissions, atime, mtime, length, uid, gid, muid);
    }

    [When("its key and value are packed")]
    public void WhenEntryPacked()
    {
        key = entry!.Key(1);
        value = entry.Value();
    }

    [Then(@"^the value is (\d+) bytes long$")]
    public void ThenValueLength(int length) => Assert.Equal(length, value!.Length);

    [Then("they unpack to the same entry")]
    public void ThenSameEntry() => Assert.Equal(entry, Dir.Read(key, value));

    [Given(@"^a block holding ""(.*)"" at address (\d+) born in generation (\d+)$")]
    public void GivenBlock(string contents, long address, long generation)
        => pointer = new Bptr(address, BlockHash.Of(Encoding.UTF8.GetBytes(contents)), generation);

    [When("its pointer is packed")]
    public void WhenPointerPacked()
    {
        block = new byte[Format.PointerSize + 3];
        Assert.Equal(Format.PointerSize, pointer.Write(block));
    }

    [Then(@"^the pointer is (\d+) bytes long$")]
    public void ThenPointerLength(int length) => Assert.Equal(length, Format.PointerSize);

    [Then(@"^its hash is ([0-9a-f]+)$")]
    public void ThenHash(string hex) => Assert.Equal(hex, Convert.ToHexStringLower(block.AsSpan(8, 32)));

    [Then(@"^it unpacks to address (\d+) and generation (\d+) with the same hash$")]
    public void ThenPointerUnpacks(long address, long generation)
    {
        Bptr read = Bptr.Read(block);
        Assert.Equal((address, generation, pointer.Hash), (read.Addr, read.Gen, read.Hash));
    }

    [Given(@"^a tree with (\d+) references?, (\d+) labels, height (\d+), flags (\d+), generation (\d+), predecessor (\d+), successor (\d+), base (\d+) and its root at address (\d+)$")]
    public void GivenTree(int refs, int labels, int height, uint flags, long generation, long pred, long succ, long @base, long root)
        => tree = new TreeEntry(refs, labels, height, flags, generation, pred, succ, @base, new Bptr(root, BlockHash.Of([1]), generation));

    [When("its entry is packed")]
    public void WhenTreePacked() => value = tree!.Value();

    [Then("it unpacks to the same tree")]
    public void ThenSameTree() => Assert.Equal(tree, TreeEntry.Read(value));

    [Given(@"^the value ""(.*)"" at key ([0-9a-f]+)$")]
    public void GivenValue(string text, string hex)
    {
        key = Convert.FromHexString(hex);
        value = Encoding.UTF8.GetBytes(text);
    }

    [When(@"^an? (insert|delete|clear block|clobber) message for key ([0-9a-f]+) with value ""(.*)"" is applied$")]
    public void WhenMessage(string op, string hex, string text)
    {
        var kind = op switch { "insert" => MessageOp.Insert, "delete" => MessageOp.Delete, "clear block" => MessageOp.ClearBlock, _ => MessageOp.Clobber };
        value = Messages.Apply(value!, new Message(kind, Convert.FromHexString(hex), Encoding.UTF8.GetBytes(text)));
    }

    [When(@"^a message of kind (\d+) for key ([0-9a-f]+) with value ""(.*)"" is applied$")]
    public void WhenUnknownMessage(byte kind, string hex, string text)
        => Try(() => value = Messages.Apply(value!, new Message((MessageOp)kind, Convert.FromHexString(hex), Encoding.UTF8.GetBytes(text))));

    [Then(@"^the key holds ""(.*)""$")]
    public void ThenHolds(string text) => Assert.Equal(text, Encoding.UTF8.GetString(value!));

    [Then("the key holds no value")]
    public void ThenNoValue() => Assert.Null(value);

    [When(@"^a wstat setting (.*) is applied to it$")]
    public void WhenWstat(string fields) => ApplyWstat(Wstat(fields).Pack());

    [When(@"^a wstat setting (.*) is applied to it, with (\d+) bytes too many$")]
    public void WhenOverlongWstat(string fields, int extra) => ApplyWstat([.. Wstat(fields).Pack(), .. new byte[extra]]);

    [Then(@"^the entry has (.*), qid version (\d+), and every other field as it was$")]
    public void ThenWstatApplied(string changed, uint version)
    {
        Dir original = new("notes", new Qid(7, 3, 0), Convert.ToUInt32("0644", 8), 5, 6, 11, 1, 2, 3);
        WstatChange change = Wstat(changed.Replace(" and qid type 80", string.Empty, StringComparison.Ordinal));
        uint mode = change.Mode ?? original.Mode;
        Dir expected = original with
        {
            Qid = original.Qid with { Version = version, Type = change.Mode is null ? original.Qid.Type : (byte)(mode >> 24) },
            Mode = mode,
            Length = change.Length ?? original.Length,
            Mtime = change.Mtime ?? original.Mtime,
            Atime = change.Atime ?? original.Atime,
            Uid = change.Uid ?? original.Uid,
            Gid = change.Gid ?? original.Gid,
            Muid = change.Muid ?? original.Muid,
        };
        Assert.Equal(expected, entry);
    }

    [When(@"^an? (relink|reprev|incref) message linking snapshot (\d+), changing labels by (-?\d+) and references by (-?\d+), is applied to it$")]
    public void WhenSnapshotMessage(string op, long link, sbyte labels, sbyte refs)
    {
        var kind = op switch { "relink" => MessageOp.Relink, "reprev" => MessageOp.Reprev, _ => MessageOp.Incref };
        Try(() => tree = TreeEntry.Read(Messages.Apply(tree!.Value(), new Message(kind, Keys.Snap(tree.Gen), SnapshotChange.Pack(link, labels, refs)))));
    }

    [Then(@"^the tree has (successor \d+|predecessor \d+|no new link), 2 labels changed by (-?\d+) and 1 reference changed by (-?\d+)$")]
    public void ThenTreeChanged(string link, int labels, int refs)
    {
        string[] words = link.Split(' ');
        Assert.Equal(words[0] == "successor" ? long.Parse(words[1]) : 6, tree!.Succ);
        Assert.Equal(words[0] == "predecessor" ? long.Parse(words[1]) : 5, tree.Pred);
        Assert.Equal((2 + labels, 1 + refs), (tree.Labels, tree.Refs));
    }

    [Given(@"^a file system with (\d+) arenas, flags (\d+), next qid (\d+), next generation (\d+) and sync generation (\d+)$")]
    public void GivenFileSystem(int arenas, long flags, long nextQid, long nextGen, long syncGen)
    {
        Bptr Pointer(long addr) => new(addr, BlockHash.Of(BitConverter.GetBytes(addr)), -1);
        superblock = new Superblock(
            Format.BlockSize,
            Format.BufferSpace,
            new TreeRoot(2, Pointer(16384)),
            Pointer(32768),
            Pointer(49152),
            flags,
            nextQid,
            nextGen,
            syncGen,
            [.. Enumerable.Range(1, arenas).Select(i => Pointer(i * 1048576L))]);
    }

    [When("its superblock is packed into a block")]
    public void WhenSuperblockPacked()
    {
        block = new byte[Format.BlockSize];
        superblock!.Write(block);
    }

    [When(@"^its superblock is packed into a block and (byte \d+ is changed|its first 8 bytes are replaced by gefs9.00|its arena count is changed to -?\d+)$")]
    public void WhenSuperblockDamaged(string damage)
    {
        WhenSuperblockPacked();
        string[] words = damage.Split(' ');
        if (words[0] == "byte")
        {
            block![int.Parse(words[1])] ^= 0xff;
        }
        else if (words[1] == "arena")
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(16), int.Parse(words[^1]));
        }
        else
        {
            Encoding.ASCII.GetBytes("gefs9.00").CopyTo(block!, 0);
        }

        Try(() => Superblock.Read(block));
    }

    [Then(@"^the block starts with ""(.*)""$")]
    public void ThenBlockStarts(string text) => Assert.Equal(text, Encoding.ASCII.GetString(block!, 0, text.Length));

    [Then("it unpacks to the same file system state")]
    public void ThenSameSuperblock()
    {
        Superblock read = Superblock.Read(block);
        Assert.Equal(superblock!.Arenas, read.Arenas);
        Assert.Equal(superblock with { Arenas = [] }, read with { Arenas = [] });
    }

    [Then(@"^(?:packing|applying|unpacking) fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);

    private static WstatChange Wstat(string fields)
    {
        var change = new WstatChange();
        foreach (string part in fields.Replace(", ", " and ", StringComparison.Ordinal).Split(" and "))
        {
            string[] words = part.Split(' ');
            change = words[0] switch
            {
                "length" => change with { Length = long.Parse(words[1]) },
                "mode" => change with { Mode = words[1][0] == 'd' ? 0x80000000u | Convert.ToUInt32(words[1][1..], 8) : Convert.ToUInt32(words[1], 8) },
                "mtime" => change with { Mtime = long.Parse(words[1]) },
                "atime" => change with { Atime = long.Parse(words[1]) },
                "user" => change with { Uid = int.Parse(words[1]) },
                "group" => change with { Gid = int.Parse(words[1]) },
                "muid" => change with { Muid = int.Parse(words[1]) },
                _ => change,
            };
        }

        return change;
    }

    private void ApplyWstat(byte[] change)
        => Try(() => entry = Dir.Read(entry!.Key(1), Messages.Apply(entry.Value(), new Message(MessageOp.Wstat, entry.Key(1), change))));

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (GefsException error)
        {
            failure = error.Message;
        }
    }
}
