using System.Net;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// One /lib/ndb/auth speaks-for line: the host may obtain tickets for <see cref="Uid"/>. A uid of
/// "*" means any user, and "!user" excludes that user even when "*" allows everyone.
/// </summary>
public sealed record SpeaksForRule(string HostId, string Uid);
