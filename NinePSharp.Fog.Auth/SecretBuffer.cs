namespace NinePSharp.Fog.Auth;

/// <summary>Key material held only as long as needed and zeroed when released.</summary>
internal sealed class SecretBuffer(byte[] bytes) : IDisposable
{
    internal byte[] Bytes { get; } = bytes;

    public void Dispose() => Array.Clear(Bytes);
}
