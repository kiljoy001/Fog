using Microsoft.Win32.SafeHandles;

namespace NinePSharp.Fog.Gefs;

// A device kept in an ordinary file on a local disk, made at its full size and held exclusively,
// as gefs -f uses a file.
internal sealed class FileDevice : Device, IDisposable
{
    private readonly SafeFileHandle handle;

    private FileDevice(SafeFileHandle handle) => this.handle = handle;

    public override long Size => RandomAccess.GetLength(handle);

    public static FileDevice Create(string path, long blocks)
    {
        long size = blocks * Format.BlockSize;
        SafeFileHandle handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.None, size);
        RandomAccess.SetLength(handle, size);
        return new FileDevice(handle);
    }

    public static FileDevice Open(string path)
    {
        if (new FileInfo(path).Length % Format.BlockSize != 0)
        {
            throw new GefsException("device size is not a whole number of blocks");
        }

        return new FileDevice(File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
    }

    public override void Read(long offset, Span<byte> block)
    {
        while (!block.IsEmpty)
        {
            int n = RandomAccess.Read(handle, block, offset);
            if (n == 0)
            {
                throw new GefsException("i/o error");
            }

            block = block[n..];
            offset += n;
        }
    }

    public override void Write(long offset, ReadOnlySpan<byte> block) => RandomAccess.Write(handle, block, offset);

    public override void Flush() => RandomAccess.FlushToDisk(handle);

    public void Dispose() => handle.Dispose();
}
