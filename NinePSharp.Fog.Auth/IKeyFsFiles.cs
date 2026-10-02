namespace NinePSharp.Fog.Auth;

/// <summary>The keyfs's file access: whole-file reads and replacements that never leave a partial file.</summary>
internal interface IKeyFsFiles
{
    byte[]? Read(string path);

    void Replace(string path, ReadOnlySpan<byte> contents);
}
