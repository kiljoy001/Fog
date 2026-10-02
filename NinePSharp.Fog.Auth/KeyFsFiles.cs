namespace NinePSharp.Fog.Auth;

/// <summary>The keyfs's file access: whole-file reads and replacements that never leave a partial file.</summary>
internal interface IKeyFsFiles
{
    /// <summary>Reads a file, or returns null when it does not exist.</summary>
    byte[]? Read(string path);

    /// <summary>Replaces a file's contents so that a failure leaves either the old or the new contents.</summary>
    void Replace(string path, ReadOnlySpan<byte> contents);
}

/// <summary>
/// The host file system. A replacement is written to an owner-only file beside the target, flushed
/// to disk, then renamed over it, so a crash leaves the previous file intact.
/// </summary>
internal sealed class KeyFsFiles : IKeyFsFiles
{
    internal static readonly KeyFsFiles Default = new();

    public byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    public void Replace(string path, ReadOnlySpan<byte> contents)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Options = FileOptions.WriteThrough,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using (var stream = new FileStream(temporary, options)) stream.Write(contents);

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
