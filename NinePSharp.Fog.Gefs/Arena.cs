using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// blk.c's arenas: a pair of headers, then blocks allocated from free ranges, with every allocation
// and free appended to a log that grows through blocks the arena allocates for it. Unlike gefs, a
// header names its log by address alone, since log blocks carry their own hashes, the arena uses
// all of its data, and the log may use the reserve, so blocks can still be freed when it is full.
internal sealed class Arena
{
    private const long B = Format.BlockSize;
    private const int LogSpace = Format.BlockSize - Blk.LogHeaderSize;

    // A compressed log block holds this many ranges, leaving room for a barrier and a chaining word.
    private const int RangesPerBlock = (LogSpace - 16) / 16;

    private static readonly Bptr None = new(-1, default, default);

    private readonly Device device;
    private readonly long header;
    private Blk tail;
    private byte[]? packed;

    private Arena(Device device, long header, long size, Blk tail)
    {
        this.device = device;
        this.header = header;
        this.tail = tail;
        Size = size;
    }

    // The first header's address and hash, as the superblock records it.
    public Bptr Pointer { get; private set; }

    public long Start => header + (2 * B);

    public long Size { get; }

    public long Used { get; private set; }

    // gefs keeps a thousandth of the arena, between 512 KiB and 8 MiB, for deleting when it is full.
    public long Reserve => Math.Clamp(Size / 1024, 512 * 1024, 8 * 1024 * 1024);

    public FreeRanges Free { get; } = new();

    public long LogHead { get; private set; }

    public int LogBlocks { get; private set; }

    // How long the log was after it was last compressed; it is compressed again once it doubles.
    public int CompressedBlocks { get; private set; }

    // initarena: all the data free, the first block taken for the log, and a barrier for generation 0.
    public static Arena Ream(Device device, long header, long size)
    {
        long start = header + (2 * B);
        var a = new Arena(device, header, size, NewLog(start)) { LogHead = start, LogBlocks = 1, CompressedBlocks = 1, Used = B };
        a.Free.Free(start, size);
        a.Append(start, size, LogOp.Free);
        a.Free.Take(start, B);
        a.Append(start, B, LogOp.Alloc1);
        a.Barrier(0);
        a.FlushLog();
        a.WriteHeader(0);
        a.WriteHeader(1);
        return a;
    }

    // loadarena and loadlog: the header, or its twin if it is damaged or stale, then the log replayed
    // up to the barrier of the generation the superblock committed.
    public static Arena Load(Device device, Bptr pointer, long syncGen)
    {
        Blk h = ReadHeader(device, pointer) ?? ReadHeader(device, pointer with { Addr = pointer.Addr + B }) ?? throw new GefsException("internal error");
        ReadOnlySpan<byte> p = h.Data;
        long logHead = BinaryPrimitives.ReadInt64BigEndian(p);
        var a = new Arena(device, pointer.Addr, BinaryPrimitives.ReadInt64BigEndian(p[8..]), NewLog(logHead)) { Pointer = pointer, LogHead = logHead };
        a.Replay(syncGen);
        a.CompressedBlocks = a.LogBlocks;
        a.Used = a.Size - a.Free.Sum(r => r.Length);
        return a;
    }

    // blkalloc_lk and logappend: a block from the top of the free space, or the bottom for sequential
    // data, unless only the reserve is left.
    public long? Allocate(bool sequential = false, bool useReserve = false)
    {
        if (!useReserve && Size - Used <= Reserve)
        {
            return null;
        }

        long b = (sequential ? Free.TakeLowest() : Free.TakeHighest()) ?? throw new GefsException("emergency blocks exhausted");
        Used += B;
        Append(b, B, LogOp.Alloc1);
        return b;
    }

    // blkdealloc_lk
    public void Deallocate(long b)
    {
        Append(b, B, LogOp.Free1);
        Free.Free(b, B);
        Used -= B;
    }

    // logbarrier: the generation in place of an offset.
    public void Barrier(long gen) => Append(gen * 256, 0, LogOp.Sync);

    // flushlog
    public void FlushLog() => Write(tail);

    // compresslog: the log rewritten as the free ranges, into blocks taken from the bottom until they
    // can hold the ranges left; taking one can only shrink the ranges, so the last may end up empty.
    // A full arena keeps its log. The
    // old log's blocks stay allocated until the sync that stops naming them has committed; they are
    // returned for freeing then. Its tail may never have been written, so it is not read back.
    public List<long>? Compress()
    {
        if (Free.TakeLowest() is not { } first)
        {
            return null;
        }

        var blocks = new List<long> { first };
        while (blocks.Count * RangesPerBlock < Free.Count)
        {
            blocks.Add(Free.TakeLowest()!.Value);
        }

        Used += blocks.Count * B;
        Blk oldTail = tail;
        var ranges = Free.ToList();
        for (int k = 0; k < blocks.Count; k++)
        {
            Blk log = NewLog(blocks[k]);
            foreach (var (offset, length) in ranges.Skip(k * RangesPerBlock).Take(RangesPerBlock))
            {
                Put(log, offset, length, LogOp.Free);
            }

            if (k + 1 < blocks.Count)
            {
                log.LogNext = new Bptr(blocks[k + 1], default, default);
                Write(log);
            }
            else
            {
                tail = log;
            }
        }

        var old = new List<long>();
        for (long at = LogHead; at != -1; at = (at == oldTail.Address ? oldTail : Read(device, at)).LogNext.Addr)
        {
            old.Add(at);
        }

        (LogHead, LogBlocks, CompressedBlocks) = (blocks[0], blocks.Count, blocks.Count);
        return old;
    }

    // The blocks of a log no longer named by any committed header.
    public void FreeLog(List<long> blocks)
    {
        foreach (long at in blocks)
        {
            Deallocate(at);
        }
    }

    // packarena into the first header, or the same bytes into the second.
    public Bptr WriteHeader(int which)
    {
        if (which == 0)
        {
            Blk h = Blk.New(BlockType.Arena, header, default);
            Span<byte> p = h.Data;
            BinaryPrimitives.WriteInt64BigEndian(p, LogHead);
            BinaryPrimitives.WriteInt64BigEndian(p[8..], Size);
            h.Seal();
            packed = h.Buffer;
            Pointer = new Bptr(header, h.Hash!.Value, default);
        }

        device.Write(header + (which * B), packed);
        return Pointer;
    }

    public IEnumerable<LogEntry> ReadLog()
    {
        for (long at = LogHead; at != -1;)
        {
            Blk b = at == tail.Address ? tail : Read(device, at);
            foreach (LogEntry e in Entries(b))
            {
                yield return e;
            }

            at = b.LogNext.Addr;
        }
    }

    private static Blk NewLog(long at)
    {
        Blk b = Blk.New(BlockType.Log, at, default);
        b.LogNext = None;
        return b;
    }

    private static Blk Read(Device device, long at)
    {
        var bytes = new byte[B];
        device.Read(at, bytes);
        return Blk.Read(bytes, new Bptr(at, default, default));
    }

    private static Blk? ReadHeader(Device device, Bptr pointer)
    {
        try
        {
            var bytes = new byte[B];
            device.Read(pointer.Addr, bytes);
            return Blk.Read(bytes, pointer);
        }
        catch (GefsException)
        {
            return null;
        }
    }

    private static int Width(LogOp op) => op == LogOp.Free ? 16 : 8;

    private static IEnumerable<LogEntry> Entries(Blk b)
    {
        for (int i = 0; i < b.LogSize;)
        {
            ulong ent = BinaryPrimitives.ReadUInt64BigEndian(b.Data[i..]);
            var op = (LogOp)(ent & 0xff);
            long offset = (long)(ent & ~0xffUL);
            yield return op switch
            {
                LogOp.Free => new LogEntry(op, offset, BinaryPrimitives.ReadInt64BigEndian(b.Data[(i + 8)..])),
                LogOp.Sync => new LogEntry(op, offset / 256, 0),
                _ => new LogEntry(op, offset, B),
            };
            i += Width(op);
        }
    }

    private static void Put(Blk b, long offset, long length, LogOp op)
    {
        Span<byte> p = b.Data[b.LogSize..];
        BinaryPrimitives.WriteUInt64BigEndian(p, (ulong)offset | (byte)op);
        if (op == LogOp.Free)
        {
            BinaryPrimitives.WriteInt64BigEndian(p[8..], length);
        }

        b.LogSize += Width(op);
    }

    // logappend: when an entry and the word chaining to the next block would not both fit, a block
    // from the top of the arena continues the log, its allocation the old block's last entry. The old
    // block is written then; the new one is the tail, written at the next sync.
    private void Append(long offset, long length, LogOp op)
    {
        if (op == LogOp.Free1 && length != B)
        {
            op = LogOp.Free;
        }

        if (tail.LogSize + Width(op) + 8 > LogSpace)
        {
            long o = Free.TakeHighest() ?? throw new GefsException("file system full");
            Used += B;
            Put(tail, o, B, LogOp.Alloc1);
            tail.LogNext = new Bptr(o, default, default);
            Write(tail);
            tail = NewLog(o);
            LogBlocks++;
        }

        Put(tail, offset, length, op);
    }

    private void Replay(long syncGen)
    {
        for (long at = LogHead; at != -1;)
        {
            Blk b = Read(device, at);
            LogBlocks++;
            int i = 0;
            foreach (LogEntry e in Entries(b))
            {
                switch (e.Op)
                {
                    // The log goes on after the committed barrier, which it keeps; gefs drops it, so
                    // entries logged after a reopen would replay as if committed with it.
                    case LogOp.Sync when e.Offset >= syncGen:
                        (b.LogSize, b.LogNext, tail) = (i + Width(e.Op), None, b);
                        return;
                    case LogOp.Alloc1:
                        Free.Take(e.Offset, e.Length);
                        break;
                    case LogOp.Free1 or LogOp.Free:
                        Free.Free(e.Offset, e.Length);
                        break;
                }

                i += Width(e.Op);
            }

            at = b.LogNext.Addr;
        }

        // A committed superblock's barrier is always written before it, so a log without one is damaged.
        throw new GefsException("internal error");
    }

    private void Write(Blk b)
    {
        b.Seal();
        device.Write(b.Address, b.Buffer);
    }
}
