using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// The database file: "FOGKEYS1", a 12-byte nonce, then the records under ChaCha20-Poly1305 with the
/// storage key and the magic as associated data. Unlike 9front's AES-CBC keyfile, any change is detected.
/// </summary>
internal static class KeyFsStore
{
    internal const string AuthenticationFailed = "keyfs: database authentication failed";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] Magic = "FOGKEYS1"u8.ToArray();
    private static readonly SecureRandom Random = new();

    internal static byte[] Seal(ReadOnlySpan<byte> storageKey, ReadOnlySpan<byte> records)
    {
        var nonce = new byte[NonceLength];
        Random.NextBytes(nonce);
        ChaCha20Poly1305 cipher = Cipher(encrypting: true, storageKey, nonce);
        var output = new byte[Magic.Length + NonceLength + cipher.GetOutputSize(records.Length)];
        Magic.CopyTo(output, 0);
        nonce.CopyTo(output, Magic.Length);
        int written = cipher.ProcessBytes(records.ToArray(), 0, records.Length, output, Magic.Length + NonceLength);
        cipher.DoFinal(output, Magic.Length + NonceLength + written);
        return output;
    }

    /// <summary>Opens a database file, throwing <see cref="AuthenticationFailed"/> for any change or wrong key.</summary>
    internal static KeyDatabase Open(ReadOnlySpan<byte> storageKey, ReadOnlySpan<byte> file)
    {
        using var records = new SecretBuffer(Decrypt(storageKey, file));
        return KeyDatabase.Decode(records.Bytes);
    }

    /// <summary>Decrypts the records; a wrong magic, a short file or a failed tag are all the same failure.</summary>
    private static byte[] Decrypt(ReadOnlySpan<byte> storageKey, ReadOnlySpan<byte> file)
    {
        try
        {
            if (!file.StartsWith(Magic)) throw new InvalidCipherTextException();
            ChaCha20Poly1305 cipher = Cipher(encrypting: false, storageKey, file.Slice(Magic.Length, NonceLength));
            byte[] body = file[(Magic.Length + NonceLength)..].ToArray();
            var records = new byte[cipher.GetOutputSize(body.Length)];
            int written = cipher.ProcessBytes(body, 0, body.Length, records, 0);
            cipher.DoFinal(records, written);
            return records;
        }
        catch (Exception exception) when (exception is InvalidCipherTextException or ArgumentOutOfRangeException)
        {
            throw new KeyFsException(AuthenticationFailed, exception);
        }
    }

    private static ChaCha20Poly1305 Cipher(bool encrypting, ReadOnlySpan<byte> storageKey, ReadOnlySpan<byte> nonce)
    {
        var cipher = new ChaCha20Poly1305();
        cipher.Init(encrypting, new AeadParameters(new KeyParameter(storageKey.ToArray()), TagLength * 8, nonce.ToArray(), Magic));
        return cipher;
    }
}
