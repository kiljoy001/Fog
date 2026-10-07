using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NinePSharp.Fog.Gefs;

// A 16 KiB block: blk.c's header handling and tree.c's layout of leaves and pivots.
internal sealed class Blk
{
    public const int LeafHeaderSize = 6;
    public const int PivotHeaderSize = Format.PivotHeaderSize;

    // type, size, 4 bytes of padding so a log's 8-byte entries fill its space exactly, hash, chain.
    public const int LogHeaderSize = 2 + 2 + 4 + Format.HashSize + Format.PointerSize;
    public const int LeafSpace = Format.BlockSize - LeafHeaderSize;
    public const int BufferSpace = Format.BufferSpace;
    public const int PivotSpace = Format.BlockSize - PivotHeaderSize - BufferSpace;

    private int dataOffset;

    private Blk(BlockType type, long address, long gen)
    {
        Type = type;
        Address = address;
        Gen = gen;
        dataOffset = Offset(type);
    }

    public BlockType Type { get; private set; }

    public long Address { get; }

    public long Gen { get; }

    // The hash of the sealed block; none until it is sealed.
    public BlockHash? Hash { get; private set; }

    public Bptr Pointer => new(Address, Hash ?? default, Gen);

    public byte[] Buffer { get; } = new byte[Format.BlockSize];

    public Span<byte> Data => Buffer.AsSpan(dataOffset);

    public int ValueCount { get; set; }

    public int ValueSize { get; set; }

    public int MessageCount { get; set; }

    public int MessageSize { get; set; }

    public int LogSize { get; set; }

    public BlockHash LogHash { get; private set; }

    public Bptr LogNext { get; set; }

    // blkfill: the bytes a leaf or pivot uses, offsets included.
    public int Fill => (2 * MessageCount) + MessageSize + (2 * ValueCount) + ValueSize;

    public static Blk New(BlockType type, long address, long gen) => new(type, address, gen);

    // readblk, from bytes already read: the header, then the hash its pointer or log header gives.
    public static Blk Read(ReadOnlySpan<byte> bytes, Bptr bp, ReadFlags flags = ReadFlags.None)
    {
        var type = (flags & ReadFlags.Raw) != 0 ? BlockType.Data : (BlockType)BinaryPrimitives.ReadUInt16BigEndian(bytes);
        if (!Enum.IsDefined(type))
        {
            throw new GefsException($"invalid block type {(ushort)type}");
        }

        var b = new Blk(type, bp.Addr, bp.Gen);
        bytes[..Format.BlockSize].CopyTo(b.Buffer);
        ReadOnlySpan<byte> p = b.Buffer.AsSpan(2);
        switch (type)
        {
            case BlockType.Log or BlockType.Deadlist:
                b.LogSize = BinaryPrimitives.ReadUInt16BigEndian(p);
                b.LogHash = BlockHash.Read(p[6..]);
                b.LogNext = Bptr.Read(p[(6 + Format.HashSize)..]);
                break;
            case BlockType.Pivot:
                b.ValueCount = BinaryPrimitives.ReadUInt16BigEndian(p);
                b.ValueSize = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
                b.MessageCount = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
                b.MessageSize = BinaryPrimitives.ReadUInt16BigEndian(p[6..]);
                break;
            case BlockType.Leaf:
                b.ValueCount = BinaryPrimitives.ReadUInt16BigEndian(p);
                b.ValueSize = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
                break;
        }

        if ((flags & ReadFlags.NoCheck) == 0 && !b.Intact(bp))
        {
            throw new GefsException("block contents corrupted");
        }

        return b;
    }

    public static (Bptr Pointer, int Fill) GetPointer(ReadOnlySpan<byte> value)
        => (Bptr.Read(value), BinaryPrimitives.ReadUInt16BigEndian(value[Format.PointerSize..]));

    // dupblk: the same contents at a new address, to be sealed there.
    public Blk Copy(long address, long gen)
    {
        var copy = new Blk(Type, address, gen)
        {
            ValueCount = ValueCount,
            ValueSize = ValueSize,
            MessageCount = MessageCount,
            MessageSize = MessageSize,
            LogSize = LogSize,
        };
        Buffer.CopyTo(copy.Buffer, 0);
        return copy;
    }

    // finalize: write the header and hash the whole block.
    public void Seal()
    {
        if (Type != BlockType.Data)
        {
            BinaryPrimitives.WriteUInt16BigEndian(Buffer, (ushort)Type);
        }

        Span<byte> p = Buffer.AsSpan(2);
        switch (Type)
        {
            case BlockType.Pivot:
                BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)ValueCount);
                BinaryPrimitives.WriteUInt16BigEndian(p[2..], (ushort)ValueSize);
                BinaryPrimitives.WriteUInt16BigEndian(p[4..], (ushort)MessageCount);
                BinaryPrimitives.WriteUInt16BigEndian(p[6..], (ushort)MessageSize);
                break;
            case BlockType.Leaf:
                BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)ValueCount);
                BinaryPrimitives.WriteUInt16BigEndian(p[2..], (ushort)ValueSize);
                break;
            case BlockType.Log or BlockType.Deadlist:
                LogHash = BlockHash.Of(Data[..LogSize]);
                BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)LogSize);
                LogHash.Write(p[6..]);
                LogNext.Write(p[(6 + Format.HashSize)..]);
                break;
        }

        Hash = BlockHash.Of(Buffer);
    }

    // getval: the i'th key and value of a leaf, or key and pointer of a pivot.
    public (byte[] Key, byte[] Value) GetValue(int i)
    {
        ReadOnlySpan<byte> data = Data;
        ReadOnlySpan<byte> p = data[BinaryPrimitives.ReadUInt16BigEndian(data[(2 * i)..])..];
        int keySize = BinaryPrimitives.ReadUInt16BigEndian(p);
        int valueSize = BinaryPrimitives.ReadUInt16BigEndian(p[(2 + keySize)..]);
        return (p.Slice(2, keySize).ToArray(), p.Slice(2 + keySize + 2, valueSize).ToArray());
    }

    // setval: the offset table grows from the start of the space, the values from its end.
    public void SetValue(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        int space = Type == BlockType.Leaf ? LeafSpace : PivotSpace;
        int size = 2 + key.Length + 2 + value.Length;
        if ((2 * (ValueCount + 1)) + ValueSize + size > space)
        {
            throw new GefsException("block full");
        }

        ValueSize += size;
        int offset = space - ValueSize;
        Span<byte> data = Data;
        BinaryPrimitives.WriteUInt16BigEndian(data[(2 * ValueCount)..], (ushort)offset);
        Span<byte> p = data[offset..];
        BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)key.Length);
        key.CopyTo(p[2..]);
        BinaryPrimitives.WriteUInt16BigEndian(p[(2 + key.Length)..], (ushort)value.Length);
        value.CopyTo(p[(4 + key.Length)..]);
        ValueCount++;
    }

    // setptr: a pivot's child pointer is a value holding the pointer and the child's fill.
    public void SetPointer(ReadOnlySpan<byte> key, Bptr bp, int fill)
    {
        Span<byte> value = stackalloc byte[Format.PointerSize + 2];
        bp.Write(value);
        BinaryPrimitives.WriteUInt16BigEndian(value[Format.PointerSize..], (ushort)fill);
        SetValue(key, value);
    }

    // setmsg: messages live in a pivot's second half, laid out as values are in its first.
    public void SetMessage(Message m)
    {
        int size = 1 + 2 + m.Key.Length + 2 + m.Value.Length;
        if ((2 * (MessageCount + 1)) + MessageSize + size > BufferSpace)
        {
            throw new GefsException("block full");
        }

        MessageSize += size;
        int offset = BufferSpace - MessageSize;
        Span<byte> buffer = Data[PivotSpace..];
        BinaryPrimitives.WriteUInt16BigEndian(buffer[(2 * MessageCount)..], (ushort)offset);
        Span<byte> p = buffer[offset..];
        p[0] = (byte)m.Op;
        BinaryPrimitives.WriteUInt16BigEndian(p[1..], (ushort)m.Key.Length);
        m.Key.CopyTo(p[3..]);
        BinaryPrimitives.WriteUInt16BigEndian(p[(3 + m.Key.Length)..], (ushort)m.Value.Length);
        m.Value.CopyTo(p[(5 + m.Key.Length)..]);
        MessageCount++;
    }

    // fastupsert's shuffle: the i'th buffered message's offset moved to slot at, those between moving up.
    public void MoveMessage(int i, int at)
    {
        Span<byte> table = Data[PivotSpace..];
        ushort offset = BinaryPrimitives.ReadUInt16BigEndian(table[(2 * i)..]);
        table[(2 * at)..(2 * i)].CopyTo(table[((2 * at) + 2)..]);
        BinaryPrimitives.WriteUInt16BigEndian(table[(2 * at)..], offset);
    }

    public Message GetMessage(int i)
    {
        ReadOnlySpan<byte> buffer = Data[PivotSpace..];
        ReadOnlySpan<byte> p = buffer[BinaryPrimitives.ReadUInt16BigEndian(buffer[(2 * i)..])..];
        int keySize = BinaryPrimitives.ReadUInt16BigEndian(p[1..]);
        int valueSize = BinaryPrimitives.ReadUInt16BigEndian(p[(3 + keySize)..]);
        return new Message((MessageOp)p[0], p.Slice(3, keySize).ToArray(), p.Slice(5 + keySize, valueSize).ToArray());
    }

    // blksearch: the first value at the key, or the last before it, -1 if none is.
    public int BlockSearch(ReadOnlySpan<byte> key, out bool same) => Search(key, ValueCount, i => GetValue(i).Key, out same);

    // bufsearch: the same over the pivot's buffered messages.
    public int BufferSearch(ReadOnlySpan<byte> key, out bool same) => Search(key, MessageCount, i => GetMessage(i).Key, out same);

    // filledleaf
    public bool LeafFull(int needed) => (2 * (ValueCount + 1)) + ValueSize + needed > LeafSpace;

    // filledpiv: room is kept for the pointers splits along a path may bring up, each with 8 bytes
    // of offsets, lengths and fill.
    public bool PivotFull(int reserve) => (2 * (ValueCount + 1)) + ValueSize + (reserve * (8 + Format.MaxEntry + Format.PointerSize)) > PivotSpace;

    // filledbuf
    public bool BufferFull(int count, int needed) => (2 * (MessageCount + count)) + MessageSize + needed > BufferSpace;

    private static int Offset(BlockType type) => type switch
    {
        BlockType.Arena => 2,
        BlockType.Log or BlockType.Deadlist => LogHeaderSize,
        BlockType.Pivot => PivotHeaderSize,
        BlockType.Leaf => LeafHeaderSize,
        _ => 0,
    };

    private static int Search(ReadOnlySpan<byte> key, int count, Func<int, byte[]> keyAt, out bool same)
    {
        int lo = 0, hi = count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int r = Keys.Compare(key, keyAt(mid));
            if (r <= 0)
            {
                found = r == 0 ? mid : found;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        same = found != -1;
        return same ? found : lo - 1;
    }

    // A log's hash covers only the log bytes in use; every other block's covers all of it.
    private bool Intact(Bptr bp)
        => Type is BlockType.Log or BlockType.Deadlist
            ? LogSize <= Format.BlockSize - LogHeaderSize && BlockHash.Of(Data[..LogSize]) == LogHash
            : BlockHash.Of(Buffer) == bp.Hash;
}
