using System.Buffers.Binary;
using System.Text;
using Dp9ik;

namespace NinePSharp.Fog.Auth;

/// <summary>One user of the database, as keyfs.c's User: keys, account state and the counters it keeps only in memory.</summary>
internal sealed class KeyUser
{
    internal KeyUser(string name, ulong uniq)
    {
        Name = name;
        Uniq = uniq;
    }

    internal string Name { get; set; }

    internal ulong Uniq { get; }

    internal byte[] DesKey { get; set; } = new byte[Dp9ikConstants.DesKeyLength];

    internal byte[] AesKey { get; set; } = new byte[Dp9ikConstants.AesKeyLength];

    internal byte[] PakHash { get; set; } = new byte[Dp9ikConstants.PakHashLength];

    internal byte[] Secret { get; set; } = [];

    internal bool Disabled { get; set; }

    internal byte Warnings { get; set; }

    internal uint Expire { get; set; }

    internal ulong Bad { get; set; }

    internal long PurgatoryEnds { get; set; }

    internal void Rehash()
    {
        AuthKey key = AuthKey.FromKeys(DesKey, AesKey);
        key.ApplyAuthPakHash(Name);
        PakHash = key.PakHash;
    }

    internal KeyUser Copy() => new(Name, Uniq)
    {
        DesKey = DesKey.ToArray(), AesKey = AesKey.ToArray(), PakHash = PakHash.ToArray(), Secret = Secret.ToArray(),
        Disabled = Disabled, Warnings = Warnings, Expire = Expire, Bad = Bad, PurgatoryEnds = PurgatoryEnds,
    };
}
