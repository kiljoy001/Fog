using NinePSharp.Namespaces.Authorization;

namespace NinePSharp.Fog.Namespaces;

/// <summary>
/// The host's current authorization policy. Replacing it moves to a strictly higher generation,
/// which revokes every view and handle built under an older one.
/// </summary>
public sealed class FogAuthorizationAuthority
{
    private readonly object gate = new();
    private AuthorizationPolicy current;

    /// <summary>Starts with the given policy.</summary>
    public FogAuthorizationAuthority(AuthorizationPolicy initial)
        => current = initial ?? throw new ArgumentNullException(nameof(initial));

    /// <summary>Gets the current policy.</summary>
    public AuthorizationPolicy Current
    {
        get { lock (gate) return current; }
    }

    /// <summary>Gets the current policy generation.</summary>
    public ulong Generation => Current.Generation;

    /// <summary>Activates a policy with a higher generation.</summary>
    public void Replace(AuthorizationPolicy next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (gate)
        {
            if (next.Generation <= current.Generation) throw new ArgumentOutOfRangeException(nameof(next));
            current = next;
        }
    }
}
