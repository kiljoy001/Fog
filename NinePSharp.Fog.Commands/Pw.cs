using System.Numerics;

namespace NinePSharp.Fog.Commands;

// secstore.h's PW: an account's expiry, its status, its failed logins, a comment, and its
// H(passphrase)^-1 mod p.
public sealed class Pw(string id)
{
    public const ushort Enabled = 1 << 0;

    // An extra SecurID step.
    public const ushort Sta = 1 << 1;

    public string Id { get; } = id;

    // In epoch seconds.
    public uint Expire { get; set; }

    public ushort Status { get; set; }

    public ushort Failed { get; set; }

    public string? Other { get; set; }

    public BigInteger Hi { get; set; }
}
