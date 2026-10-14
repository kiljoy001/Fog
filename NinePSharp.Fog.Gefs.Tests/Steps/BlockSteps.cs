using System.Buffers.Binary;
using System.Security.Cryptography;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
[Scope(Feature = "Blocks are laid out, searched, sealed and read as 9front's gefs does")]
public sealed class BlockSteps
{
    private readonly List<(string Key, string Value)> given = [];
    private readonly List<Message> buffered = [];
    private Blk? block;
    private Blk? read;
    private byte[]? bytes;
    private int found;
    private bool same;
    private string? failure;

    [Given("a new leaf")]
    public void GivenLeaf() => block = Blk.New(BlockType.Leaf, 0, 0);

    [Given("a new pivot")]
    public void GivenPivot() => block = Blk.New(BlockType.Pivot, 0, 0);

    [When(@"^it is given the value ([0-9a-f]+) at key ([0-9a-f]+) and then the value ([0-9a-f]+) at key ([0-9a-f]+)$")]
    public void WhenTwoValues(string v1, string k1, string v2, string k2)
    {
        SetValue(Convert.FromHexString(k1), Convert.FromHexString(v1));
        SetValue(Convert.FromHexString(k2), Convert.FromHexString(v2));
    }

    [Then(@"^it holds (\d+) values in (\d+) bytes$")]
    public void ThenHolds(int count, int size) => Assert.Equal((count, size), (block!.ValueCount, block.ValueSize));

    [Then(@"^its first offset points (\d+) bytes before the end of its space, and its second (\d+)$")]
    public void ThenOffsets(int first, int second)
    {
        Assert.Equal(Blk.LeafSpace - first, BinaryPrimitives.ReadUInt16BigEndian(block!.Data));
        Assert.Equal(Blk.LeafSpace - second, BinaryPrimitives.ReadUInt16BigEndian(block.Data[2..]));
    }

    [Then("its values read back in the order they were given")]
    public void ThenValuesInOrder() => Assert.Equal(given, Values(block!));

    [Given(@"^a new leaf holding keys (.*)$")]
    public void GivenLeafKeys(string keys)
    {
        GivenLeaf();
        foreach (string key in Keys(keys))
        {
            SetValue(Convert.FromHexString(key), [1]);
        }
    }

    [When(@"^it is searched for ([0-9a-f]+)$")]
    public void WhenSearched(string key) => found = block!.BlockSearch(Convert.FromHexString(key), out same);

    [Then(@"^the search lands on (-?\d+) and (finds|misses) the key$")]
    public void ThenLands(int index, string whether) => Assert.Equal((index, whether == "finds"), (found, same));

    [When(@"^it points key ([0-9a-f]+) at block (\d+) of generation (\d+) holding (\d+) bytes$")]
    public void WhenPoints(string key, long address, long gen, int fill)
        => block!.SetPointer(Convert.FromHexString(key), new Bptr(address, BlockHash.Of([2]), gen), fill);

    [When(@"^it buffers an insert for key ([0-9a-f]+) of value ([0-9a-f]+) and then a delete for key ([0-9a-f]+)$")]
    public void WhenBuffers(string key, string value, string deleted)
    {
        Buffer(new Message(MessageOp.Insert, Convert.FromHexString(key), Convert.FromHexString(value)));
        Buffer(new Message(MessageOp.Delete, Convert.FromHexString(deleted), []));
    }

    [Then(@"^it holds (\d+) pointers? and (\d+) messages$")]
    public void ThenPivotCounts(int pointers, int messages) => Assert.Equal((pointers, messages), (block!.ValueCount, block.MessageCount));

    [Then(@"^the pointer reads back as block (\d+) of generation (\d+) holding (\d+) bytes$")]
    public void ThenPointer(long address, long gen, int fill)
    {
        (Bptr bp, int read) = Blk.GetPointer(block!.GetValue(0).Value);
        Assert.Equal((address, gen, BlockHash.Of([2]), fill), (bp.Addr, bp.Gen, bp.Hash, read));
    }

    [Then(@"^the messages read back in the order they were buffered, the first packed (\d+) bytes before the end of its second half$")]
    public void ThenMessages(int packed)
    {
        Assert.Equal(buffered.Select(Text), Enumerable.Range(0, block!.MessageCount).Select(i => Text(block.GetMessage(i))));
        Assert.Equal(Blk.BufferSpace - packed, BinaryPrimitives.ReadUInt16BigEndian(block.Data[Blk.PivotSpace..]));
    }

    [Given(@"^a new pivot buffering messages for keys (.*)$")]
    public void GivenBuffering(string keys)
    {
        GivenPivot();
        foreach (string key in Keys(keys))
        {
            Buffer(new Message(MessageOp.Insert, Convert.FromHexString(key), [1]));
        }
    }

    [When(@"^its buffer is searched for ([0-9a-f]+)$")]
    public void WhenBufferSearched(string key) => found = block!.BufferSearch(Convert.FromHexString(key), out same);

    [Given(@"^a new leaf filled to (\d+) bytes of its space with (\d+) values$")]
    public void GivenFilledLeaf(int used, int count)
    {
        GivenLeaf();
        (block!.ValueSize, block.ValueCount) = (used, count);
    }

    [Then(@"^it (is|is not) full for a value needing (\d+) bytes$")]
    public void ThenLeafFull(string answer, int needed) => Assert.Equal(answer == "is", block!.LeafFull(needed));

    [Given(@"^a new pivot filled to (\d+) bytes of pointer space with (\d+) pointers$")]
    public void GivenFilledPivot(int used, int count)
    {
        GivenPivot();
        (block!.ValueSize, block.ValueCount, block.MessageSize, block.MessageCount) = (used, count, 0, 0);
    }

    [Then(@"^its pointers (are|are not) full when (\d+) pointers must still fit$")]
    public void ThenPivotFull(string answer, int reserve) => Assert.Equal(answer == "are", block!.PivotFull(reserve));

    [Then(@"^its fill counts (\d+) bytes$")]
    public void ThenFill(int fill) => Assert.Equal(fill, block!.Fill);

    [Given(@"^a new pivot buffering (\d+) messages in (\d+) bytes$")]
    public void GivenFilledBuffer(int count, int used)
    {
        GivenPivot();
        (block!.MessageCount, block.MessageSize) = (count, used);
    }

    [Then(@"^its buffer (is|is not) full for (\d+) more messages needing (\d+) bytes$")]
    public void ThenBufferFull(string answer, int more, int needed) => Assert.Equal(answer == "is", block!.BufferFull(more, needed));

    [Given(@"^a new (leaf|pivot) with no room left$")]
    public void GivenNoRoom(string kind)
    {
        block = Blk.New(kind == "leaf" ? BlockType.Leaf : BlockType.Pivot, 0, 0);
        (block.ValueCount, block.ValueSize) = (1, (kind == "leaf" ? Blk.LeafSpace : Blk.PivotSpace) - 4);
        (block.MessageCount, block.MessageSize) = (1, Blk.BufferSpace - 4);
    }

    [When(@"^it is given one more (value|pointer|message)$")]
    public void WhenOneMore(string entry) => Try(() =>
    {
        switch (entry)
        {
            case "value":
                block!.SetValue([1], [1]);
                break;
            case "pointer":
                block!.SetPointer([1], new Bptr(0, default, 0), 0);
                break;
            default:
                block!.SetMessage(new Message(MessageOp.Insert, [1], [1]));
                break;
        }
    });

    [When(@"^it is given a (value|message) of (\d+) bytes, its key included$")]
    public void WhenGivenSized(string entry, int size) => Try(() =>
    {
        if (entry == "value")
        {
            block!.SetValue([1], new byte[size - 2 - 1 - 2]);
        }
        else
        {
            block!.SetMessage(new Message(MessageOp.Insert, [1], new byte[size - 1 - 2 - 1 - 2]));
        }
    });

    [Then("it takes it")]
    public void ThenTakes() => Assert.Equal((null, 101), (failure, block!.Type == BlockType.Leaf ? block.ValueCount : block.MessageCount));

    [Then(@"^(?:it|reading) fails with ""(.*)""$")]
    public void ThenFails(string message) => Assert.Equal(message, failure);

    [Given(@"^a new (leaf|pivot) holding entries$")]
    public void GivenEntries(string kind)
    {
        if (kind == "leaf")
        {
            GivenLeaf();
            SetValue([1], [10, 11]);
            SetValue([2, 3], [12]);
        }
        else
        {
            GivenPivot();
            block!.SetPointer([1], new Bptr(49152, BlockHash.Of([3]), 2), 40);
            Buffer(new Message(MessageOp.Insert, [5], [6]));
        }
    }

    [When(@"^it is sealed at address (\d+) in generation (\d+)$")]
    public void WhenSealed(long address, long gen)
    {
        Blk sealedBlock = Blk.New(block!.Type, address, gen);
        block.Buffer.CopyTo(sealedBlock.Buffer, 0);
        (sealedBlock.ValueCount, sealedBlock.ValueSize, sealedBlock.MessageCount, sealedBlock.MessageSize) = (block.ValueCount, block.ValueSize, block.MessageCount, block.MessageSize);
        block = sealedBlock;
        block.Seal();
        bytes = block.Buffer.ToArray();
    }

    [Then("its hash is the SHA-256 of all 16 KiB of it")]
    public void ThenSealedHash() => Assert.Equal(BlockHash.Read(SHA256.HashData(block!.Buffer)), block.Hash);

    [Then(@"^reading its bytes with its pointer gives a (leaf|pivot) with the same counts and entries$")]
    public void ThenReadsBack(string kind)
    {
        read = Blk.Read(bytes, Pointer());
        Assert.Equal(kind == "leaf" ? BlockType.Leaf : BlockType.Pivot, read.Type);
        AssertSameEntries(read);
    }

    [When(@"^its bytes are read with (byte \d+ changed|another block's hash|its type changed to \d+)$")]
    public void WhenReadWith(string change)
    {
        Bptr pointer = Pointer();
        string[] words = change.Split(' ');
        if (words[0] == "byte")
        {
            bytes![int.Parse(words[1])] ^= 0xff;
        }
        else if (words[0] == "another")
        {
            pointer = pointer with { Hash = BlockHash.Of([9]) };
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes, ushort.Parse(words[^1]));
        }

        Try(() => read = Blk.Read(bytes, pointer));
    }

    [When(@"^its bytes are read with byte (\d+) changed, without checking$")]
    public void WhenReadUnchecked(int offset)
    {
        bytes![offset] ^= 0xff;
        read = Blk.Read(bytes, Pointer(), ReadFlags.NoCheck);
    }

    [When(@"^its bytes are read as raw data (as they are|with byte \d+ changed)$")]
    public void WhenReadRaw(string change)
    {
        if (change.StartsWith("with", StringComparison.Ordinal))
        {
            bytes![int.Parse(change.Split(' ')[2])] ^= 0xff;
        }

        Try(() => read = Blk.Read(bytes, Pointer(), ReadFlags.Raw));
    }

    [Then(@"^reading (?:gives )?an? (leaf|data block)$")]
    public void ThenReadGives(string kind) => Assert.Equal(kind == "leaf" ? BlockType.Leaf : BlockType.Data, read!.Type);

    [Given(@"^a new (allocation|deadlist) log chained to block (\d+) of generation (\d+)$")]
    public void GivenLog(string kind, long address, long gen)
    {
        block = Blk.New(kind == "allocation" ? BlockType.Log : BlockType.Deadlist, 0, 0);
        block.LogNext = new Bptr(address, BlockHash.Of([4]), gen);
    }

    [When(@"^it is given (\d+) bytes of log and sealed at address (\d+) in generation (\d+)$")]
    public void WhenLogSealed(int size, long address, long gen)
    {
        Blk log = Blk.New(block!.Type, address, gen);
        log.LogNext = block.LogNext;
        for (int i = 0; i < size; i++)
        {
            log.Data[i] = (byte)(i + 1);
        }

        log.LogSize = size;
        log.Seal();
        block = log;
        bytes = log.Buffer.ToArray();
    }

    [Then(@"^its log hash is the SHA-256 of those (\d+) bytes$")]
    public void ThenLogHash(int size) => Assert.Equal(BlockHash.Of(block!.Data[..size]), block.LogHash);

    [Then(@"^reading its bytes gives an? (allocation|deadlist) log of (\d+) bytes chained to block (\d+) of generation (\d+)$")]
    public void ThenLogReads(string kind, int size, long address, long gen)
    {
        read = Blk.Read(bytes, Pointer());
        Assert.Equal(kind == "allocation" ? BlockType.Log : BlockType.Deadlist, read.Type);
        Assert.Equal((size, address, gen, BlockHash.Of([4])), (read.LogSize, read.LogNext.Addr, read.LogNext.Gen, read.LogNext.Hash));
    }

    [Then(@"^reading it with a changed byte past its (\d+) bytes still succeeds, but with a changed byte among them fails with ""(.*)""$")]
    public void ThenLogChecks(int size, string message)
    {
        byte[] past = bytes!.ToArray();
        past[Blk.LogHeaderSize + size] ^= 0xff;
        Assert.Equal(size, Blk.Read(past, Pointer()).LogSize);
        byte[] among = bytes!.ToArray();
        among[Blk.LogHeaderSize + size - 1] ^= 0xff;
        Assert.Equal(message, Assert.Throws<GefsException>(() => Blk.Read(among, Pointer())).Message);
        byte[] oversized = bytes!.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(oversized.AsSpan(2), 20000);
        Assert.Equal(message, Assert.Throws<GefsException>(() => Blk.Read(oversized, Pointer())).Message);
    }

    [Then(@"^reading it with its header claiming (\d+) bytes fails with ""(.*)""$")]
    public void ThenLogClaims(int size, string message)
    {
        byte[] claiming = bytes!.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(claiming.AsSpan(2), (ushort)size);
        Assert.Equal(message, Assert.Throws<GefsException>(() => Blk.Read(claiming, Pointer())).Message);
    }

    [When(@"^it is copied to address (\d+) in generation (\d+)$")]
    public void WhenCopied(long address, long gen) => read = block!.Copy(address, gen);

    [Then("the copy holds the same counts and entries")]
    public void ThenCopySame() => AssertSameEntries(read!);

    [Then(@"^the copy is at address (\d+) in generation (\d+) and has no hash until it is sealed$")]
    public void ThenCopyPlace(long address, long gen)
    {
        Assert.Equal((address, gen, (BlockHash?)null), (read!.Address, read.Gen, read.Hash));
        read.Seal();
        Assert.NotNull(read.Hash);
    }

    private static IEnumerable<string> Keys(string keys) => keys.Replace(" and ", ", ", StringComparison.Ordinal).Split(", ");

    private static List<(string Key, string Value)> Values(Blk b)
        => [.. Enumerable.Range(0, b.ValueCount).Select(b.GetValue).Select(kv => (Convert.ToHexString(kv.Key), Convert.ToHexString(kv.Value)))];

    private static string Text(Message m) => $"{m.Op} {Convert.ToHexString(m.Key)} {Convert.ToHexString(m.Value)}";

    private void AssertSameEntries(Blk other)
    {
        Assert.Equal((block!.ValueCount, block.ValueSize, block.MessageCount, block.MessageSize), (other.ValueCount, other.ValueSize, other.MessageCount, other.MessageSize));
        Assert.Equal(Values(block), Values(other));
        Assert.Equal(
            Enumerable.Range(0, block.MessageCount).Select(i => Text(block.GetMessage(i))),
            Enumerable.Range(0, other.MessageCount).Select(i => Text(other.GetMessage(i))));
    }

    private Bptr Pointer() => new(block!.Address, block.Hash ?? default, block.Gen);

    private void SetValue(byte[] key, byte[] value)
    {
        block!.SetValue(key, value);
        given.Add((Convert.ToHexString(key), Convert.ToHexString(value)));
    }

    private void Buffer(Message message)
    {
        block!.SetMessage(message);
        buffered.Add(message);
    }

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
