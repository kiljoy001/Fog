using System.Net;

namespace NinePSharp.Fog.Auth;

/// <summary>Where the auth server listens and which hosts may speak for other users.</summary>
public sealed class AuthServerOptions
{
    public required IPEndPoint EndPoint { get; init; }

    public IReadOnlyList<SpeaksForRule> SpeaksFor { get; init; } = [];

    public TimeSpan ConnectionLifetime { get; init; } = TimeSpan.FromMinutes(10);
}
