namespace NinePSharp.Fog.Auth;

/// <summary>The server's identity in its auth domain: the keyfs user whose key it authenticates with.</summary>
public sealed class AuthFidOptions
{
    public required string AuthId { get; init; }

    public required string AuthDomain { get; init; }
}
