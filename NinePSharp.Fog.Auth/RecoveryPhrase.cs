using System.Numerics;
using System.Reflection;
using Org.BouncyCastle.Crypto.Digests;

namespace NinePSharp.Fog.Auth;

/// <summary>
/// A 32-byte storage key as a 24-word BIP-39 English mnemonic: the key's 256 bits and the first 8
/// bits of its SHA-256, read as 24 indexes of 11 bits (base-2048 digits) into the BIP-39 wordlist.
/// </summary>
internal static class RecoveryPhrase
{
    private const int KeyLength = 32;
    private const int Words = 24;
    private const int Radix = 2048;
    private static readonly string[] Wordlist = LoadWordlist();
    private static readonly Dictionary<string, int> Indexes =
        Wordlist.Select((word, index) => (word, index)).ToDictionary(entry => entry.word, entry => entry.index, StringComparer.Ordinal);

    /// <summary>Encodes a 32-byte key.</summary>
    internal static string Encode(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength) throw new ArgumentException("A storage key is 32 bytes.", nameof(key));
        BigInteger value = new([.. key, Checksum(key)], isUnsigned: true, isBigEndian: true);
        var words = new string[Words];
        // The 264 bits read as 24 base-2048 digits, most significant first.
        for (int word = Words - 1; word >= 0; word--)
        {
            words[word] = Wordlist[(int)(value % Radix)];
            value /= Radix;
        }

        return string.Join(' ', words);
    }

    /// <summary>Decodes a 24-word phrase whose words and checksum are valid; case and spacing are not significant.</summary>
    internal static bool TryDecode(string phrase, out byte[] key)
    {
        key = [];
        string[] words = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != Words) return false;
        BigInteger value = BigInteger.Zero;
        foreach (string word in words)
        {
            if (!Indexes.TryGetValue(word.ToLowerInvariant(), out int index)) return false;
            value = (value * Radix) + index;
        }

        var bits = new byte[KeyLength + 1];
        value.TryWriteBytes(bits.AsSpan(bits.Length - value.GetByteCount(isUnsigned: true)), out _, isUnsigned: true, isBigEndian: true);
        byte[] decoded = bits[..KeyLength];
        if (Checksum(decoded) != bits[KeyLength]) return false;
        key = decoded;
        return true;
    }

    /// <summary>Reads the wordlist: one word per line, exactly 2048 of them.</summary>
    internal static string[] ParseWordlist(Stream? stream)
    {
        if (stream is null) throw new InvalidOperationException("The BIP-39 wordlist resource is missing.");
        using var reader = new StreamReader(stream);
        string[] words = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != Radix) throw new InvalidOperationException("The BIP-39 wordlist must hold 2048 words.");
        return words;
    }

    private static byte Checksum(ReadOnlySpan<byte> key)
    {
        var digest = new Sha256Digest();
        digest.BlockUpdate(key);
        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash);
        return hash[0];
    }

    private static string[] LoadWordlist()
        => ParseWordlist(Assembly.GetExecutingAssembly().GetManifestResourceStream("NinePSharp.Fog.Auth.bip39-english.txt"));
}
