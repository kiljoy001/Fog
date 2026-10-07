using System.Buffers.Binary;

namespace NinePSharp.Fog.Gefs;

// blk.c's arenas: a pair of headers, then blocks allocated from free ranges, with every allocation
// and free appended to a log that grows through blocks the arena allocates for it. Unlike gefs, a
// header names its log by address alone, since log blocks carry their own hashes, and the arena
// uses all of its data.
internal sealed class Arena
{
    private const long B = Format.BlockSize;
    private const int LogSpace = Format.BlockSize - Blk.LogHeaderSize;

    // Room kept at the end of a log block for one more two-word entry and the chain to the next.
    private const int LogSlop = 16 + 16 + 8;

    private static readonly Bptr None = new(-1, default, -1);

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
        a.Append(start, B, LogOp.Alloc);
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
        Append(b, B, LogOp.Alloc);
        return b;
    }

    // blkdealloc_lk
    public void Deallocate(long b)
    {
        Append(b, B, LogOp.Free);
        Free.Free(b, B);
        Used -= B;
    }

    // logbarrier
    public void Barrier(long gen) => Append(gen << 8, 0, LogOp.Sync);

    // flushlog
    public void FlushLog() => Write(tail);

    // compresslog: the log rewritten as the free ranges, into blocks taken from the bottom. The old
    // log's blocks stay allocated until the sync that stops naming them has committed; the head
    // returned is for freeing them then.
    public long Compress()
    {
        FlushLog();
        int nr = Free.Count;
        long sz = 16L * nr;
        int nblks = (int)(((sz + LogSpace) / (LogSpace - LogSlop)) + (16L * nr / (LogSpace - LogSlop)) + 1);
        var blks = new long[nblks];
        for (int i = 0; i < nblks; i++)
        {
            blks[i] = Free.TakeLowest() ?? throw new GefsException("file system full");
            Used += B;
        }

        int k = 0;
        Blk b = NewLog(blks[k++]);
        int blocks = 1;
        foreach (var (offset, length) in Free)
        {
            if (b.LogSize >= LogSpace - LogSlop)
            {
                b.LogNext = new Bptr(blks[k], default, -1);
                Write(b);
                b = NewLog(blks[k++]);
                blocks++;
            }

            Put(b, offset, length, LogOp.Free);
        }

        Write(b);
        long old = LogHead;
        (LogHead, tail, LogBlocks, CompressedBlocks) = (blks[0], b, blocks, blocks);
        for (; k < nblks; k++)
        {
            Deallocate(blks[k]);
        }

        return old;
    }

    // The blocks of a log no longer named by any committed header.
    public void FreeLog(long head)
    {
        for (long at = head; at != -1;)
        {
            long next = Read(device, at).LogNext.Addr;
            Deallocate(at);
            at = next;
        }
    }

    // packarena into the first header, or the same bytes into the second.
    public Bptr WriteHeader(int which)
    {
        if (which == 0)
        {
            Blk h = Blk.New(BlockType.Arena, header, -1);
            Span<byte> p = h.Data;
            BinaryPrimitives.WriteInt64BigEndian(p, LogHead);
            BinaryPrimitives.WriteInt64BigEndian(p[8..], Size);
            BinaryPrimitives.WriteInt64BigEndian(p[16..], Used);
            h.Seal();
            packed = h.Buffer;
            Pointer = new Bptr(header, h.Hash!.Value, -1);
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
        Blk b = Blk.New(BlockType.Log, at, -1);
        b.LogNext = None;
        return b;
    }

    private static Blk Read(Device device, long at)
    {
        var bytes = new byte[B];
        device.Read(at, bytes);
        Blk b = Blk.Read(bytes, new Bptr(at, default, -1));
        return b.Type == BlockType.Log ? b : throw new GefsException("internal error");
    }

    private static Blk? ReadHeader(Device device, Bptr pointer)
    {
        try
        {
            var bytes = new byte[B];
            device.Read(pointer.Addr, bytes);
            Blk h = Blk.Read(bytes, pointer);
            return h.Type == BlockType.Arena ? h : null;
        }
        catch (GefsException)
        {
            return null;
        }
    }

    private static IEnumerable<LogEntry> Entries(Blk b)
    {
        for (int i = 0; i < b.LogSize;)
        {
            ulong ent = BinaryPrimitives.ReadUInt64BigEndian(b.Data[i..]);
            var op = (LogOp)(ent & 0xff);
            long offset = (long)(ent & ~0xffUL);
            yield return op switch
            {
                LogOp.Alloc or LogOp.Free => new LogEntry(op, offset, BinaryPrimitives.ReadInt64BigEndian(b.Data[(i + 8)..])),
                LogOp.Sync => new LogEntry(op, offset >> 8, 0),
                _ => new LogEntry(op, offset, B),
            };
            i += op >= LogOp.Alloc ? 16 : 8;
        }
    }

    private static void Put(Blk b, long offset, long length, LogOp op)
    {
        Span<byte> p = b.Data[b.LogSize..];
        BinaryPrimitives.WriteUInt64BigEndian(p, (ulong)offset | (byte)op);
        b.LogSize += 8;
        if (op >= LogOp.Alloc)
        {
            BinaryPrimitives.WriteInt64BigEndian(p[8..], length);
            b.LogSize += 8;
        }
    }

    // logappend: when the block is nearly full, a block from the top of the arena continues the log,
    // its allocation recorded as the old block's last entry.
    private void Append(long offset, long length, LogOp op)
    {
        Blk? old = null;
        if (tail.LogSize >= LogSpace - LogSlop)
        {
            long o = Free.TakeHighest() ?? throw new GefsException("file system full");
            Used += B;
            Put(tail, o, B, LogOp.Alloc1);
            tail.LogNext = new Bptr(o, default, -1);
            (old, tail) = (tail, NewLog(o));
            LogBlocks++;
        }

        if (length == B)
        {
            op = op == LogOp.Alloc ? LogOp.Alloc1 : LogOp.Free1;
        }

        Put(tail, offset, length, op);
        if (old is not null)
        {
            Write(tail);
            Write(old);
        }
    }

    private void Replay(long syncGen)
    {
        for (long at = LogHead; ;)
        {
            Blk b = Read(device, at);
            LogBlocks++;
            int i = 0;
            foreach (LogEntry e in Entries(b))
            {
                switch (e.Op)
                {
                    case LogOp.Sync when e.Offset >= syncGen:
                        (b.LogSize, b.LogNext, tail) = (i, None, b);
                        return;
                    case LogOp.Alloc1 or LogOp.Alloc:
                        Free.Take(e.Offset, e.Length);
                        break;
                    case LogOp.Free1 or LogOp.Free:
                        Free.Free(e.Offset, e.Length);
                        break;
                    case LogOp.Sync:
                        break;
                    default:
                        throw new GefsException("internal error");
                }

                i += e.Op >= LogOp.Alloc ? 16 : 8;
            }

            if (b.LogNext.Addr == -1)
            {
                tail = b;
                return;
            }

            at = b.LogNext.Addr;
        }
    }

    private void Write(Blk b)
    {
        b.Seal();
        device.Write(b.Address, b.Buffer);
    }
}
