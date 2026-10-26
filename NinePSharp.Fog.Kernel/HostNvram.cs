namespace NinePSharp.Fog.Kernel;

// The host's nvram, which #c/nvram reads and writes as devrtc's nvram is read and written: Fog's
// host keeps the host owner's Nvrsafe in its sealed key store, and gives it here, where readnvram
// finds it. A host without one has an nvram of no bytes. Each read and write reaches no further
// than its end.
public class HostNvram(byte[] contents)
{
    public static HostNvram None { get; } = new([]);

    public virtual ReadOnlyMemory<byte> Read(long offset, int count)
    {
        lock (contents)
        {
            int start = (int)Math.Min(offset, contents.Length);
            return contents.AsSpan(start, Math.Min(count, contents.Length - start)).ToArray();
        }
    }

    public virtual int Write(long offset, ReadOnlySpan<byte> data)
    {
        lock (contents)
        {
            int start = (int)Math.Min(offset, contents.Length);
            int n = Math.Min(data.Length, contents.Length - start);
            data[..n].CopyTo(contents.AsSpan(start));
            return n;
        }
    }
}
