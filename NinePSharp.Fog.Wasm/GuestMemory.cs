using System.Buffers.Binary;
using WebAssembly.Runtime;

namespace NinePSharp.Fog.Wasm;

// The guest's linear memory, seen from a host call. A range must lie wholly inside the memory as it is
// now, which may be 4 GiB, so it is checked in 64 bits. One access is a span, which holds at most
// 2^31 - 1 bytes, so a longer one is FAULT even inside memory. Every access takes the memory's current
// base, since growing it may move it, and no slice outlives the access that took it.
internal sealed class GuestMemory(UnmanagedMemory memory)
{
    public delegate void SpanAction(Span<byte> range);

    public uint ReadUInt32(uint pointer) => BinaryPrimitives.ReadUInt32LittleEndian(Range(pointer, 4));

    public void WriteUInt32(uint pointer, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Range(pointer, 4), value);

    public void WriteUInt64(uint pointer, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Range(pointer, 8), value);

    public byte[] Read(uint pointer, uint length) => Range(pointer, length).ToArray();

    public void Write(uint pointer, byte[] source) => source.CopyTo(Range(pointer, (uint)source.Length));

    // Lets fill write the range in place, without a copy.
    public void Fill(uint pointer, uint length, SpanAction fill) => fill(Range(pointer, length));

    // Faults unless the whole range lies inside memory.
    public void Check(uint pointer, uint length)
    {
        if ((ulong)pointer + length > memory.Size)
        {
            throw new GuestFaultException();
        }
    }

    private unsafe Span<byte> Range(uint pointer, uint length)
    {
        Check(pointer, length);
        try
        {
            return new Span<byte>((byte*)memory.Start + pointer, checked((int)length));
        }
        catch (OverflowException)
        {
            throw new GuestFaultException();
        }
    }
}
