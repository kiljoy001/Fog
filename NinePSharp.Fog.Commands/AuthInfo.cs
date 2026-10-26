namespace NinePSharp.Fog.Commands;

// auth.h's AuthInfo: the client's and server's users, the capability, and the shared secret.
public sealed class AuthInfo
{
    public string Cuid { get; set; } = string.Empty;

    public string Suid { get; set; } = string.Empty;

    public string? Cap { get; set; }

    public byte[] Secret { get; set; } = [];
}
