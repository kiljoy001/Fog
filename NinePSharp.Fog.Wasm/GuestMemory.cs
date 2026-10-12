using System.Buffers.Binary;
using WebAssembly.Runtime;

namespace NinePSharp.Fog.Wasm;

// The guest's linear memory, seen from a host call. A range is a slice of the memory as it is now, so
// the runtime refuses any part of it outside, a pointer or length past 2^31 included, since as an int
// it is negative. Every access takes the memory's current base, since growing it may move it, and no
// slice outlives the access that took it.
internal sealed class GuestMemory(UnmanagedMemory memory)
{
    public uint ReadUInt32(uint pointer) => BinaryPrimitives.ReadUInt32LittleEndian(Range(pointer, 4));

    public void WriteUInt32(uint pointer, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Range(pointer, 4), value);

    public void WriteUInt64(uint pointer, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Range(pointer, 8), value);

    public byte[] Read(uint pointer, uint length) => Range(pointer, length).ToArray();

    public void Write(uint pointer, byte[] source) => source.CopyTo(Range(pointer, (uint)source.Length));

    // Faults unless the whole range lies inside memory.
    public void Check(uint pointer, uint length) => _ = Range(pointer, length);

    private unsafe Span<byte> Range(uint pointer, uint length)
    {
        try
        {
            return new Span<byte>((void*)memory.Start, (int)memory.Size).Slice((int)pointer, (int)length);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new GuestFaultException();
        }
    }
}
