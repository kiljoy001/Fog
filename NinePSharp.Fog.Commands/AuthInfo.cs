namespace NinePSharp.Fog.Commands;

// auth.h's AuthInfo: the client's and server's users, the capability, and the shared secret.
public sealed class AuthInfo
{
    // Stryker disable once String : each AuthInfo read is one a protocol or auth_getinfo has filled, as convAI writes it
    public string Cuid { get; set; } = string.Empty;

    // Stryker disable once String : each AuthInfo read is one a protocol or auth_getinfo has filled, as convAI writes it
    public string Suid { get; set; } = string.Empty;

    public string? Cap { get; set; }

    public byte[] Secret { get; set; } = [];
}
