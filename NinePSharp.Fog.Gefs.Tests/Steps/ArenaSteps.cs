using System.Buffers.Binary;
using NinePSharp.Fog.Gefs.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class ArenaSteps : IDisposable
{
    private const long B = Format.BlockSize;

    private readonly Dictionary<long, Bptr[]> committed = [];
    private readonly Dictionary<long, List<HashSet<long>>> takenAt = [];
    private readonly Dictionary<long, List<(long Offset, long Length)>> freeAt = [];
    private readonly Dictionary<byte[], byte[]> given = new(ByteArrayComparer.Instance);
    private long lastGen;
    private Tree? writer;
    private Bptr bornThere;
    private MemoryDevice? device;
    private Allocator? allocator;
    private FreeRanges? ranges;
    private Tree? tree;
    private List<long> gave = [];
    private Bptr bornNow;
    private Bptr bornBefore;
    private string? failure;
    private string? path;
    private FileDevice? file;
    private byte[]? written;
    private byte[][]? formatHeader;

    public void Dispose()
    {
        file?.Dispose();
        if (path is not null)
        {
            File.Delete(path);
        }
    }

    [Given(@"^a device of (\d+) blocks formatted with (\d+) arenas?$")]
    public void GivenFormatted(long blocks, int arenas)
    {
        device = new MemoryDevice(blocks);
        allocator = Allocator.Ream(device, arenas);
        Remember(0, [.. allocator.Arenas.Select(a => a.Pointer)]);
    }

    [Given(@"^a device of (\d+) blocks formatted with (\d+) arenas, writing generation (\d+)$")]
    public void GivenWriting(long blocks, int arenas, long gen)
    {
        GivenFormatted(blocks, arenas);
        writer = new Tree(allocator!, default, 1) { Gen = gen };
    }

    [Given(@"^a device of (\d+) blocks$")]
    public void GivenDevice(long blocks) => device = new MemoryDevice(blocks);

    [When(@"^it is formatted with (\d+) arenas$")]
    public void WhenFormatted(int arenas) => Try(() => allocator = Allocator.Ream(device!, arenas));

    [Then(@"^formatting (succeeds|fails with "".*"")$")]
    public void ThenFormatting(string result) => Assert.Equal(result == "succeeds" ? null : result.Split('"')[1], failure);

    [Then(@"^arena (\d+) has its headers at blocks (\d+) and (\d+) and (\d+) blocks of data from block (\d+)$")]
    public void ThenLayout(int arena, long h0, long h1, long data, long start)
    {
        Arena a = allocator!.Arenas[arena];
        Assert.Equal((h0 * B, h1 * B, data * B, start * B), (a.Pointer.Addr, a.Pointer.Addr + B, a.Size, a.Start));
    }

    [Then("each arena's headers are the same, name its log at the first block of its data, and count 1 block used")]
    public void ThenHeaders()
    {
        foreach (Arena a in allocator!.Arenas)
        {
            Assert.Equal(device!.Block(a.Pointer.Addr / B), device.Block((a.Pointer.Addr / B) + 1));
            Assert.Equal((a.Start, B), (a.LogHead, a.Used));
        }
    }

    [Then("each arena's log frees its data, takes the log's own block and ends with a barrier for generation 0")]
    public void ThenFormatLog()
    {
        foreach (Arena a in allocator!.Arenas)
        {
            long first = a.Start / B;
            Assert.Equal([$"free {first}-{first + (a.Size / B) - 1}", $"take {first}", "barrier 0"], Log(a));
        }
    }

    [Given(@"^free ranges (.*)$")]
    public void GivenRanges(string text)
    {
        ranges = new FreeRanges();
        foreach (var (first, last) in Spans(text))
        {
            ranges.Free(first * B, (last - first + 1) * B);
        }
    }

    [When(@"^blocks (\d+)-(\d+) are (freed|taken)$")]
    public void WhenRange(long first, long last, string done) => Try(() =>
    {
        if (done == "freed")
        {
            ranges!.Free(first * B, (last - first + 1) * B);
        }
        else
        {
            ranges!.Take(first * B, (last - first + 1) * B);
        }
    });

    [Then(@"^the free ranges are (.*)$")]
    public void ThenRanges(string text) => Assert.Equal(Spans(text), ranges!.Select(r => (r.Offset / B, ((r.Offset + r.Length) / B) - 1)));

    [Then(@"^the free ranges refuse it with ""(.*)""$")]
    public void ThenRefused(string message) => Assert.Equal(message, failure);

    [When(@"^arena (\d+) allocates (\d+) blocks?, and then (\d+) in sequence$")]
    public void WhenAllocatesThenSequence(int arena, int count, int sequential)
    {
        gave = [.. Enumerable.Range(0, count).Select(_ => allocator!.Arenas[arena].Allocate()!.Value)];
        gave.AddRange(Enumerable.Range(0, sequential).Select(_ => allocator!.Arenas[arena].Allocate(sequential: true)!.Value));
    }

    [When(@"^arena (\d+) allocates (\d+) blocks?$")]
    public void WhenAllocates(int arena, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.NotNull(allocator!.Arenas[arena].Allocate());
        }
    }

    [When(@"^arena (\d+) is allowed its reserve and allocates (\d+) blocks$")]
    public void WhenReserve(int arena, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.NotNull(allocator!.Arenas[arena].Allocate(useReserve: true));
        }
    }

    [When(@"^arena (\d+) allocates (\d+) blocks? and frees block (\d+)$")]
    public void WhenAllocatesAndFrees(int arena, int count, long block)
    {
        WhenAllocates(arena, count);
        allocator!.Arenas[arena].Deallocate(block * B);
    }

    [When(@"^arena (\d+) allocates (\d+) blocks and arena (\d+) allocates (\d+) block$")]
    public void WhenBothAllocate(int a, int count, int b, int other)
    {
        WhenAllocates(a, count);
        WhenAllocates(b, other);
    }

    [When(@"^arena (\d+) allocates and frees a block (\d+) times$")]
    public void WhenChurns(int arena, int times)
    {
        for (int i = 0; i < times; i++)
        {
            allocator!.Arenas[arena].Deallocate(allocator.Arenas[arena].Allocate()!.Value);
        }
    }

    [Then(@"^it gave blocks (.*)$")]
    public void ThenGave(string blocks) => Assert.Equal(Blocks(blocks), gave.Select(a => a / B));

    [Then(@"^arena (\d+) counts (\d+) blocks used$")]
    public void ThenUsed(int arena, long used) => Assert.Equal(used * B, allocator!.Arenas[arena].Used);

    [Then(@"^arena (\d+) refuses another block, keeping its reserve$")]
    public void ThenKeepsReserve(int arena) => Assert.Null(allocator!.Arenas[arena].Allocate());

    [Then(@"^allocating another block from arena (\d+) fails with ""(.*)""$")]
    public void ThenArenaExhausted(int arena, string message)
        => Assert.Equal(message, Assert.Throws<GefsException>(() => allocator!.Arenas[arena].Allocate(useReserve: true)).Message);

    [Then(@"^arena (\d+)'s log reads: (.*)$")]
    public void ThenLogReads(int arena, string entries) => Assert.Equal(entries.Split(", "), Log(allocator!.Arenas[arena]));

    [When(@"^the arenas sync generation (\d+)$")]
    public void WhenSync(long gen) => Sync(gen, false);

    [When(@"^the arenas sync generation (\d+), compressing logs that have doubled$")]
    public void WhenSyncCompressing(long gen) => Sync(gen, true);

    [When(@"^the arenas sync generation (\d+) without committing it$")]
    public void WhenSyncLost(long gen)
    {
        // The logs and first headers reach the device; the superblock and second headers do not.
        device!.CrashAfter(2 * allocator!.Arenas.Count);
        Sync(gen, false);
        device.Restart();
    }

    [When(@"^the arenas sync generation (\d+), the device crashing after (\d+) of its writes$")]
    public void WhenSyncCrashing(long gen, int writes)
    {
        device!.CrashAfter(writes);
        Sync(gen, false);
        device.Restart();
    }

    [When(@"^the arenas are reopened at generation (\d+)$")]
    public void WhenReopened(long gen) => Try(() => allocator = Allocator.Open(device!, committed[gen], gen));

    [When("the arenas are reopened at the generation of the superblock last written")]
    public void WhenReopenedFromSuperblock() => WhenReopened(BinaryPrimitives.ReadInt64BigEndian(device!.Block(0)));

    [When(@"^arena (\d+)'s (first|second|both) header is (damaged|left as it was at format)$")]
    public void WhenHeaderChanged(int arena, string which, string change)
    {
        long h0 = committed[1][arena].Addr / B;
        foreach (long n in which switch { "first" => [h0], "second" => [h0 + 1], _ => new[] { h0, h0 + 1 } })
        {
            if (change == "damaged")
            {
                device!.Damage(n);
            }
            else
            {
                device!.Put(n, formatHeader![arena]);
            }
        }
    }

    [Then(@"^arena (\d+) has blocks (.*) taken and counts (\d+) blocks used$")]
    public void ThenTakenAndUsed(int arena, string blocks, long used)
    {
        ThenTaken(arena, blocks);
        ThenUsed(arena, used);
    }

    [Then(@"^arena (\d+) has blocks (.*) taken$")]
    public void ThenTaken(int arena, string blocks)
    {
        Assert.Null(failure);
        Assert.Equal(Spans(blocks).SelectMany(s => Range(s.First, s.Last)), Taken(allocator!.Arenas[arena]).Select(a => a / B));
    }

    [Then(@"^arena (\d+)'s log is (\d+) blocks? long and arena (\d+) counts (\d+) blocks used$")]
    public void ThenLogLength(int arena, int length, int counted, long used)
    {
        Assert.Equal(length, allocator!.Arenas[arena].LogBlocks);
        ThenUsed(counted, used);
    }

    [Then(@"^arena (\d+)'s log is (\d+) blocks? long and reads: (.*)$")]
    public void ThenLogLengthReads(int arena, int length, string entries)
    {
        Assert.Equal(length, allocator!.Arenas[arena].LogBlocks);
        ThenLogReads(arena, entries);
    }

    [Then(@"^reopening fails with ""(.*)""$")]
    public void ThenReopenFails(string message) => Assert.Equal(message, failure);

    [When(@"^a tree on it is given (\d+) keys of (\d+) bytes with (\d+)-byte values, (\d+) at a time$")]
    public void WhenTree(int count, int length, int size, int batch)
    {
        tree = Tree.Create(allocator!);
        var keys = Enumerable.Range(0, count).Select(i =>
        {
            var key = new byte[length];
            (key[0], key[1], key[2]) = (0x10, (byte)(i >> 8), (byte)i);
            return key;
        });
        foreach (byte[][] chunk in keys.Chunk(batch))
        {
            tree.Upsert([.. chunk.Select(k =>
            {
                var value = new byte[size];
                k.AsSpan(0, 3).CopyTo(value);
                given[k] = value;
                return new Message(MessageOp.Insert, k, value);
            })]);
        }
    }

    [Then("every key looks up as it was given")]
    public void ThenTreeLooksUp()
    {
        foreach (var (key, value) in given)
        {
            Assert.Equal(value, tree!.Lookup(key));
        }
    }

    [Then(@"^a block of the tree read with a byte changed fails with ""(.*)""$")]
    public void ThenTreeCorrupt(string message)
    {
        device!.Damage(tree!.Root.Addr / B);
        Assert.Equal(message, Assert.Throws<GefsException>(() => tree.Lookup(given.Keys.First())).Message);
    }

    [When(@"^a block born in generation (\d+) in each arena and a block born in generation (\d+) are allocated and freed$")]
    public void WhenBornAndFreed(long now, long before)
    {
        Blk current = allocator!.New(BlockType.Leaf, now);
        Blk older = allocator.New(BlockType.Leaf, before);
        allocator.Enqueue(current);
        allocator.Enqueue(older);
        (bornNow, bornBefore) = (current.Pointer, older.Pointer);
        bornThere = new Bptr(allocator.Arenas[1].Allocate()!.Value, default, now);
        allocator.Free(writer!, bornNow);
        allocator.Free(writer!, bornThere);
        allocator.Free(writer!, bornBefore);
    }

    [Then(@"^none is free yet, and the block born in generation (\d+) is reported for its deadlist$")]
    public void ThenNoneFree(long before)
    {
        Assert.False(IsFree(bornNow.Addr));
        Assert.False(IsFree(bornThere.Addr));
        Assert.False(IsFree(bornBefore.Addr));
        Assert.Equal([(bornBefore.Addr, before)], allocator!.Killed.Select(k => (k.Addr, k.Gen)));
    }

    [When("the arenas reclaim")]
    public void WhenReclaim() => allocator!.Reclaim();

    [Then(@"^the blocks born in generation (\d+) are free again, each in its own arena, and the block born in generation (\d+) is not$")]
    public void ThenReclaimed(long now, long before)
    {
        Assert.Equal((now, now, before), (bornNow.Gen, bornThere.Gen, bornBefore.Gen));
        Assert.Contains(allocator!.Arenas[0].Free, r => r.Offset <= bornNow.Addr && bornNow.Addr < r.Offset + r.Length);
        Assert.Contains(allocator.Arenas[1].Free, r => r.Offset <= bornThere.Addr && bornThere.Addr < r.Offset + r.Length);
        Assert.False(IsFree(bornBefore.Addr));
        Assert.Equal((2 * B, B), (allocator.Arenas[0].Used, allocator.Arenas[1].Used));
    }

    [Then("the block born in generation 4 is free, and no longer reported")]
    public void ThenKilledReleased()
    {
        Assert.True(IsFree(bornBefore.Addr));
        Assert.Empty(allocator!.Killed);
    }

    [Then(@"^arena (\d+)'s log ends: (.*)$")]
    public void ThenLogEnds(int arena, string entries)
    {
        string[] expected = entries.Split(", ");
        Assert.Equal(expected, Log(allocator!.Arenas[arena]).TakeLast(expected.Length));
    }

    [Then(@"^the lowest free block is (\d+) and the highest (\d+)$")]
    public void ThenLowestHighest(long lowest, long highest)
        => Assert.Equal((lowest * B, highest * B), (ranges!.TakeLowest(), ranges.TakeHighest()));

    [Then(@"^all of them came from arena (\d+), and the next comes from arena (\d+)$")]
    public void ThenTurn(int first, int next)
    {
        Arena a = allocator!.Arenas[first];
        Assert.All(gave, g => Assert.InRange(g, a.Start, a.Start + a.Size - 1));
        Arena b = allocator.Arenas[next];
        Assert.InRange(allocator.New(BlockType.Leaf).Address, b.Start, b.Start + b.Size - 1);
    }

    [When(@"^arena (\d+) allocates all but its reserve$")]
    public void WhenAllButReserve(int arena)
    {
        while (allocator!.Arenas[arena].Allocate() is not null)
        {
        }
    }

    [Then(@"^the next tree block comes from arena (\d+)$")]
    public void ThenNextFrom(int arena)
    {
        Arena a = allocator!.Arenas[arena];
        Assert.InRange(allocator.New(BlockType.Leaf).Address, a.Start, a.Start + a.Size - 1);
    }

    [When(@"^the arenas are reopened with generation (\d+)'s headers at generation (\d+)$")]
    public void WhenReopenedAt(long headers, long gen) => Try(() => allocator = Allocator.Open(device!, committed[headers], gen));

    [Then(@"^arena (\d+)'s log holds (\d+) entries$")]
    public void ThenLogEntries(int arena, int count) => Assert.Equal(count, allocator!.Arenas[arena].ReadLog().Count());

    [When(@"^arena (\d+)'s free space is left as (\d+) single blocks$")]
    public void WhenSingles(int arena, int count)
    {
        Arena a = allocator!.Arenas[arena];
        foreach (var (offset, length) in a.Free.ToList())
        {
            a.Free.Take(offset, length);
        }

        for (int i = 0; i < count; i++)
        {
            a.Free.Free(a.Start + B + (2 * i * B), B);
        }
    }

    [When(@"^arena (\d+)'s log is compressed and the arenas sync generation (\d+)$")]
    public void WhenCompressed(int arena, long gen)
    {
        Assert.NotNull(allocator!.Arenas[arena].Compress());
        Sync(gen, false);
    }

    [Then(@"^arena (\d+)'s log is (\d+) blocks? long$")]
    public void ThenLogBlocks(int arena, int blocks) => Assert.Equal(blocks, allocator!.Arenas[arena].LogBlocks);

    [Then(@"^arena (\d+)'s free space is as it was$")]
    public void ThenFreeAsWas(int arena)
    {
        Assert.Null(failure);
        Assert.Equal(freeAt[lastGen], allocator!.Arenas[arena].Free.ToList());
    }

    [When(@"^the device's record is cleared and the arenas sync generation (\d+)$")]
    public void WhenTraced(long gen)
    {
        device!.Trace.Clear();
        Sync(gen, false);
    }

    [Then(@"^the device saw: (.*)$")]
    public void ThenTrace(string events) => Assert.Equal(events.Split(", "), device!.Trace);

    [Then(@"^reading half a block before its end fails with ""(.*)""$")]
    public void ThenReadPastEnd(string message)
        => Assert.Equal(message, Assert.Throws<GefsException>(() => file!.Read(file.Size - (B / 2), new byte[B])).Message);

    [Then("it can be opened again afterwards")]
    public void ThenOpenAgain() => File.Open(path!, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();

    [When(@"^(\d+) tree blocks are allocated$")]
    public void WhenTreeBlocks(int count) => gave = [.. Enumerable.Range(0, count).Select(_ => allocator!.New(BlockType.Leaf).Address)];

    [Then(@"^(\d+) came from each arena$")]
    public void ThenFromEach(int count)
    {
        foreach (Arena a in allocator!.Arenas)
        {
            Assert.Equal(count, gave.Count(g => g >= a.Start && g < a.Start + a.Size));
        }
    }

    [Then(@"^allocating another tree block fails with ""(.*)""$")]
    public void ThenDeviceFull(string message) => Assert.Equal(message, Assert.Throws<GefsException>(() => allocator!.New(BlockType.Leaf)).Message);

    [Then(@"^the arenas hold what generation (\d+) allocated$")]
    public void ThenHoldGeneration(long gen)
    {
        Assert.Null(failure);
        Assert.Equal(takenAt[gen], allocator!.Arenas.Select(Taken).ToList());
    }

    [Given(@"^a new device file of (\d+) blocks$")]
    public void GivenDeviceFile(long blocks)
    {
        path = Path.Combine(Path.GetTempPath(), $"fog-device-{Guid.NewGuid():N}");
        file = FileDevice.Create(path, blocks);
    }

    [Then(@"^the file is (\d+) blocks long$")]
    public void ThenFileLength(long blocks) => Assert.Equal(blocks * B, new FileInfo(path!).Length);

    [When(@"^block (\d+) of it is written, and it is closed and opened again$")]
    public void WhenFileWritten(long block)
    {
        written = new byte[B];
        Random.Shared.NextBytes(written);
        file!.Write(block * B, written);
        file.Flush();
        file.Dispose();
        file = FileDevice.Open(path!);
    }

    [Then(@"^block (\d+) reads back as written$")]
    public void ThenFileReads(long block)
    {
        var read = new byte[B];
        file!.Read(block * B, read);
        Assert.Equal(written, read);
    }

    [Then("opening it a second time while it is open fails")]
    public void ThenFileLocked() => Assert.ThrowsAny<IOException>(() => FileDevice.Open(path!).Dispose());

    [Given(@"^a file of (\d+) blocks and (\d+) bytes$")]
    public void GivenOddFile(long blocks, long bytes)
    {
        path = Path.Combine(Path.GetTempPath(), $"fog-device-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, new byte[(blocks * B) + bytes]);
    }

    [Then(@"^opening it as a device fails with ""(.*)""$")]
    public void ThenOddFileRefused(string message) => Assert.Equal(message, Assert.Throws<GefsException>(() => FileDevice.Open(path!)).Message);

    private static IEnumerable<long> Range(long first, long last)
    {
        for (long n = first; n <= last; n++)
        {
            yield return n;
        }
    }

    private static List<(long First, long Last)> Spans(string text)
        => [.. text.Replace(" and ", ", ", StringComparison.Ordinal).Split(", ").Select(item =>
        {
            string[] ends = item.Split('-');
            return (long.Parse(ends[0]), long.Parse(ends[^1]));
        })];

    private static IEnumerable<long> Blocks(string text) => Spans(text).SelectMany(s => Range(s.First, s.Last));

    private static List<string> Log(Arena a) => [.. a.ReadLog().Select(e => e.Op switch
    {
        LogOp.Alloc1 => $"take {e.Offset / B}",
        LogOp.Free1 => $"free {e.Offset / B}",
        LogOp.Free => $"free {e.Offset / B}-{((e.Offset + e.Length) / B) - 1}",
        _ => $"barrier {e.Offset}",
    })];

    private static HashSet<long> Taken(Arena a)
    {
        var taken = new HashSet<long>(Enumerable.Range(0, (int)(a.Size / B)).Select(k => a.Start + (k * B)));
        foreach (var (offset, length) in a.Free)
        {
            for (long n = offset; n < offset + length; n += B)
            {
                taken.Remove(n);
            }
        }

        return taken;
    }

    private bool IsFree(long address) => allocator!.Arenas.Any(a => a.Free.Any(r => address >= r.Offset && address < r.Offset + r.Length));

    private void Remember(long gen, Bptr[] pointers)
    {
        committed[gen] = pointers;
        lastGen = gen;
        takenAt[gen] = [.. allocator!.Arenas.Select(Taken)];
        freeAt[gen] = [.. allocator.Arenas[0].Free];
        formatHeader ??= [.. pointers.Select(p => device!.Block(p.Addr / B))];
    }

    // The superblock stand-in: the generation, then nothing more; the pointers stay with the test.
    private void Sync(long gen, bool compress) => allocator!.Sync(gen, compress, pointers =>
    {
        var block = new byte[B];
        BinaryPrimitives.WriteInt64BigEndian(block, gen);
        device!.Write(0, block);
        Remember(gen, [.. pointers]);
    });

    private void Try(Action action)
    {
        failure = null;
        try
        {
            action();
        }
        catch (GefsException error)
        {
            failure = error.Message;
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = default(HashCode);
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
