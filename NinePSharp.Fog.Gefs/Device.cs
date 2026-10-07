namespace NinePSharp.Fog.Gefs;

// A device: a store of 16 KiB blocks, read and written whole at their offsets.
internal abstract class Device
{
    public abstract long Size { get; }

    public abstract void Read(long offset, Span<byte> block);

    public abstract void Write(long offset, ReadOnlySpan<byte> block);

    // Returns once everything written has reached stable storage.
    public abstract void Flush();
}
