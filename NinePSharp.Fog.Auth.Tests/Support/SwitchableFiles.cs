namespace NinePSharp.Fog.Auth.Tests.Support;

/// <summary>Host files whose replacements can be made to fail from a chosen moment, without restarting anything.</summary>
internal sealed class SwitchableFiles : IKeyFsFiles
{
    internal bool Failing { get; set; }

    public byte[]? Read(string path) => KeyFsFiles.Default.Read(path);

    public void Replace(string path, ReadOnlySpan<byte> contents)
    {
        if (Failing)
        {
            throw new IOException("injected failure while writing " + path);
        }

        KeyFsFiles.Default.Replace(path, contents);
    }
}
