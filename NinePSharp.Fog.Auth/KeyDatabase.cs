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

    /// <summary>Gets or sets the secret, without its terminator.</summary>
    internal byte[] Secret { get; set; } = [];

    internal bool Disabled { get; set; }

    internal byte Warnings { get; set; }

    /// <summary>Gets or sets the expiry in seconds since the epoch; 0 is never.</summary>
    internal uint Expire { get; set; }

    /// <summary>Gets or sets the consecutive bad attempts; not persisted, as in keyfs.</summary>
    internal ulong Bad { get; set; }

    /// <summary>Gets or sets when purgatory ends, in seconds since the epoch; not persisted.</summary>
    internal long PurgatoryEnds { get; set; }

    /// <summary>Recomputes the PAK hash from the AES key and the name, as keyfs does with authpak_hash.</summary>
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

/// <summary>The user database and the record layout of 9front's AES keyfile, without its outer encryption.</summary>
internal sealed class KeyDatabase
{
    /// <summary>ANAMELEN: a name field including its terminator.</summary>
    internal const int NameLength = 28;

    /// <summary>SECRETLEN: a secret field including its terminator.</summary>
    internal const int SecretLength = 32;

    /// <summary>name, DES key, status, warnings, expiry, secret and AES key: KEYDBLEN + AESKEYLEN.</summary>
    internal const int RecordLength = NameLength + Dp9ikConstants.DesKeyLength + 1 + 1 + 4 + SecretLength + Dp9ikConstants.AesKeyLength;

    private readonly Dictionary<string, KeyUser> users = new(StringComparer.Ordinal);
    private ulong nextUniq = 1;

    internal IEnumerable<KeyUser> Users => users.Values.OrderBy(user => user.Uniq);

    internal KeyUser? Find(string name) => users.GetValueOrDefault(name);

    internal KeyUser Add(string name)
    {
        var user = new KeyUser(name, nextUniq++);
        users.Add(name, user);
        return user;
    }

    internal void Remove(KeyUser user) => users.Remove(user.Name);

    internal void Rename(KeyUser user, string name)
    {
        users.Remove(user.Name);
        user.Name = name;
        users.Add(name, user);
        user.Rehash();
    }

    /// <summary>
    /// Whether a name can be a user: 1 to 27 bytes of UTF-8 without control characters, spaces or
    /// slashes, as keyfs's userok requires of the names it loads, and not "." or "..".
    /// </summary>
    internal static bool IsValidName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || Encoding.UTF8.GetByteCount(name) >= NameLength) return false;
        // Invalid UTF-8 and lone surrogates enumerate as the replacement character, keyfs's Runeerror.
        foreach (Rune rune in name.EnumerateRunes())
            if (rune == Rune.ReplacementChar || Rune.IsControl(rune) || rune.Value is ' ' or '/') return false;
        return true;
    }

    /// <summary>Encodes every user as consecutive records.</summary>
    internal byte[] Encode()
    {
        KeyUser[] ordered = Users.ToArray();
        var records = new byte[ordered.Length * RecordLength];
        for (int index = 0; index < ordered.Length; index++)
        {
            Span<byte> record = records.AsSpan(index * RecordLength, RecordLength);
            KeyUser user = ordered[index];
            Encoding.UTF8.GetBytes(user.Name, record[..NameLength]);
            user.DesKey.CopyTo(record[NameLength..]);
            int offset = NameLength + Dp9ikConstants.DesKeyLength;
            record[offset++] = user.Disabled ? (byte)1 : (byte)0;
            record[offset++] = user.Warnings;
            BinaryPrimitives.WriteUInt32LittleEndian(record[offset..], user.Expire);
            offset += 4;
            user.Secret.CopyTo(record[offset..]);
            offset += SecretLength;
            user.AesKey.CopyTo(record[offset..]);
        }

        return records;
    }

    /// <summary>Decodes records written by <see cref="Encode"/>, recomputing each PAK hash.</summary>
    internal static KeyDatabase Decode(ReadOnlySpan<byte> records)
    {
        if (records.Length % RecordLength != 0) throw new InvalidDataException("The database holds a partial record.");
        var database = new KeyDatabase();
        for (int start = 0; start < records.Length; start += RecordLength)
        {
            ReadOnlySpan<byte> record = records.Slice(start, RecordLength);
            // An unterminated field reads as a 28-byte name, which IsValidName refuses.
            string name = Encoding.UTF8.GetString(record[..NameLength]).Split('\0')[0];
            if (!IsValidName(name) || database.Find(name) is not null)
                throw new InvalidDataException("The database holds an invalid user name.");
            KeyUser user = database.Add(name);
            user.DesKey = record.Slice(NameLength, Dp9ikConstants.DesKeyLength).ToArray();
            int offset = NameLength + Dp9ikConstants.DesKeyLength;
            user.Disabled = record[offset++] != 0;
            user.Warnings = record[offset++];
            user.Expire = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            offset += 4;
            // keyfs terminates the secret at its last byte, so at most 31 bytes are read.
            ReadOnlySpan<byte> secret = record.Slice(offset, SecretLength - 1);
            int secretEnd = secret.IndexOf((byte)0);
            user.Secret = (secretEnd < 0 ? secret : secret[..secretEnd]).ToArray();
            offset += SecretLength;
            user.AesKey = record.Slice(offset, Dp9ikConstants.AesKeyLength).ToArray();
            user.Rehash();
        }

        return database;
    }

    /// <summary>Copies the database, including the counters kept only in memory, so a change can be saved before it takes effect.</summary>
    internal KeyDatabase Copy()
    {
        var copy = new KeyDatabase { nextUniq = nextUniq };
        foreach (KeyUser user in users.Values) copy.users.Add(user.Name, user.Copy());
        return copy;
    }

    /// <summary>Finds a user by the identity open fids hold, which survives renames and copies.</summary>
    internal KeyUser? FindByUniq(ulong uniq) => users.Values.FirstOrDefault(user => user.Uniq == uniq);
}
