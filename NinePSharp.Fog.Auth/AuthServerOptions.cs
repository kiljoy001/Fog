using System.Net;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// One /lib/ndb/auth speaks-for line: the host may obtain tickets for <see cref="Uid"/>. A uid of
/// "*" means any user, and "!user" excludes that user even when "*" allows everyone.
/// </summary>
public sealed record SpeaksForRule(string HostId, string Uid);

/// <summary>Where the auth server listens and which hosts may speak for other users.</summary>
public sealed class AuthServerOptions
{
    /// <summary>Gets the TCP endpoint to listen on; 9front's auth service is port 567.</summary>
    public required IPEndPoint EndPoint { get; init; }

    /// <summary>Gets the speaks-for rules; a host always speaks for itself.</summary>
    public IReadOnlyList<SpeaksForRule> SpeaksFor { get; init; } = [];

    /// <summary>Gets how long a connection may stay open in all; authsrv's alarm allows 10 minutes.</summary>
    public TimeSpan ConnectionLifetime { get; init; } = TimeSpan.FromMinutes(10);
}
