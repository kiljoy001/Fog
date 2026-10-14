namespace NinePSharp.Fog.Gefs;

// A device: a store of 16 KiB blocks, read and written whole at their offsets.
internal abstract class Device
{
    public abstract long Size { get; }

    // How long the store waits on the device, for room in its write queue or for a commit's blocks,
    // before taking it as failed.
    public virtual TimeSpan Patience => TimeSpan.FromMinutes(1);

    public abstract void Read(long offset, Span<byte> block);

    public abstract void Write(long offset, ReadOnlySpan<byte> block);

    // Returns once everything written has reached stable storage.
    public abstract void Flush();
}
