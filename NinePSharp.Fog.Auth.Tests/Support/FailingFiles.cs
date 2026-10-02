namespace NinePSharp.Fog.Auth.Tests.Support;

/// <summary>Host files whose replacements fail after writing part of the new contents to a scratch file.</summary>
internal sealed class FailingFiles : IKeyFsFiles
{
    public byte[]? Read(string path) => KeyFsFiles.Default.Read(path);

    public void Replace(string path, ReadOnlySpan<byte> contents)
    {
        // A crash midway through writing: half the new bytes reach a file beside the database.
        File.WriteAllBytes(path + ".partial", contents[..(contents.Length / 2)].ToArray());
        throw new IOException("injected failure while writing " + path);
    }
}
