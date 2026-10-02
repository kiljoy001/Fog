using System.Buffers.Binary;
using System.Text;
using Dp9ik;

namespace NinePSharp.Fog.Auth;

/// <summary>The user database and the record layout of 9front's AES keyfile, without its outer encryption.</summary>
internal sealed class KeyDatabase
{
    internal const int NameLength = 28;

    internal const int SecretLength = 32;

    internal const int RecordLength = NameLength + Dp9ikConstants.DesKeyLength + 1 + 1 + 4 + SecretLength + Dp9ikConstants.AesKeyLength;

    private readonly Dictionary<string, KeyUser> users = new(StringComparer.Ordinal);
    private ulong nextUniq = 1;

    internal IEnumerable<KeyUser> Users => users.Values.OrderBy(user => user.Uniq);

    internal static bool IsValidName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || Encoding.UTF8.GetByteCount(name) >= NameLength)
        {
            return false;
        }

        // Invalid UTF-8 and lone surrogates enumerate as the replacement character, keyfs's Runeerror.
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar || Rune.IsControl(rune) || rune.Value is ' ' or '/')
            {
                return false;
            }
        }

        return true;
    }

    internal static KeyDatabase Decode(ReadOnlySpan<byte> records)
    {
        if (records.Length % RecordLength != 0)
        {
            throw new InvalidDataException("The database holds a partial record.");
        }

        var database = new KeyDatabase();
        for (int start = 0; start < records.Length; start += RecordLength)
        {
            ReadOnlySpan<byte> record = records.Slice(start, RecordLength);

            // An unterminated field reads as a 28-byte name, which IsValidName refuses.
            string name = Encoding.UTF8.GetString(record[..NameLength]).Split('\0')[0];
            if (!IsValidName(name) || database.Find(name) is not null)
            {
                throw new InvalidDataException("The database holds an invalid user name.");
            }

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

    internal KeyDatabase Copy()
    {
        var copy = new KeyDatabase { nextUniq = nextUniq };
        foreach (KeyUser user in users.Values)
        {
            copy.users.Add(user.Name, user.Copy());
        }

        return copy;
    }

    internal KeyUser? FindByUniq(ulong uniq) => users.Values.FirstOrDefault(user => user.Uniq == uniq);
}
