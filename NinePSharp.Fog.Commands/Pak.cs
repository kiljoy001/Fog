using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// secstore's pak.c: PAK, the password-authenticated key exchange secstore logs in with, over an
// SConn, with libmp's mptoa and strtomp for the base-64 numbers it sends.
public static class Pak
{
    public const string Version = "secstore";

    private static readonly string[] Feedback = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"];

    // From seed EB7B6E35F7CD37B511D96C67D6688CC4DD440E1E.
    private static readonly BigInteger Q = Hex("E0F0EF284E10796C5A2A511E94748BA03C795C13");
    private static readonly BigInteger P = Hex(
        "C41CFBE4D4846F67A3DF7DE9921A49D3B42DC33728427AB159CEC8CBB" +
        "DB12B5F0C244F1A734AEB9840804EA3C25036AD1B61AFF3ABBC247CD4B384224567A86" +
        "3A6F020E7EE9795554BCD08ABAD7321AF27E1E92E3DB1C6E7E94FAAE590AE9C48F96D9" +
        "3D178E809401ABE8A534A1EC44359733475A36A70C7B425125062B1142D");

    private static readonly BigInteger R = Hex(
        "DF310F4E54A5FEC5D86D3E14863921E834113E060F90052AD332B3241" +
        "CEF2497EFA0303D6344F7C819691A0F9C4A773815AF8EAECFB7EC1D98F039F17A32A7E" +
        "887D97251A927D093F44A55577F4D70444AEBD06B9B45695EC23962B175F266895C67D" +
        "21C4656848614D888A4");

    private static readonly BigInteger G = Hex(
        "2F1C308DC46B9A44B52DF7DACCE1208CCEF72F69C743ADD4D23271734" +
        "44ED6E65E074694246E07F9FD4AE26E0FDDD9F54F813C40CB9BCD4338EA6F242AB94CD" +
        "410E676C290368A16B1A3594877437E516C53A6EEE5493A038A017E955E218E7819734" +
        "E3E2A6E0BAE08B14258F8C03CC1B30E0DDADFCF7CEDF0727684D3D255F1");

    // mptoa(b, 64): the base 64 of its big-endian bytes, at least one.
    public static string Mptoa64(BigInteger b)
    {
        byte[] be = BigInteger.Abs(b).ToByteArray(isUnsigned: true, isBigEndian: true);
        return (b.Sign < 0 ? "-" : string.Empty) + Convert.ToBase64String(be.Length == 0 ? [0] : be);
    }

    // strtomp(a, nil, 64, nil): the number in base 64 after any blanks and signs, or null when no
    // digit of it is there; dec64 drops a last group too short to make a byte.
    public static BigInteger? Strtomp64(string a)
    {
        int i = 0;
        while (i < a.Length && a[i] is ' ' or '\t')
        {
            i++;
        }

        int sign = 1;
        for (; i < a.Length && a[i] == '-'; i++)
        {
            sign = -sign;
        }

        int start = i;
        while (i < a.Length && Dec64chr(a[i]) >= 0)
        {
            i++;
        }

        if (i == start)
        {
            return null;
        }

        return sign * new BigInteger(Dec64(a[start..i]), isUnsigned: true, isBigEndian: true);
    }

    // PAK_Hi: H(C, passphrase) and its inverse mod p, printed in base 64.
    public static (BigInteger H, BigInteger Hi, string HexHi) Hi(string c, string passphrase)
    {
        BigInteger h = Longhash(Version, c, SHA1.HashData(Encoding.UTF8.GetBytes(passphrase)));
        BigInteger hi = Invert(h, P);
        return (h, hi, Mptoa64(hi));
    }

    // PAKclient: the session's secret set in conn once the server has proved it holds the
    // passphrase's verifier, and the name the server gives itself; or -1.
    public static async Task<(int Rc, string? S)> ClientAsync(Process process, string argv0, bool verbose, SConn conn, string c, string pass)
    {
        (BigInteger h, _, string hexHi) = Hi(c, pass);
        if (verbose)
        {
            // A clue to catch typos.
            await PrintAsync(process, $"{Feedback[(int)(h & 7)]}\n");
        }

        // Random 1<=x<=q-1; send C, m=g**x H.
        BigInteger x = Rand();
        BigInteger m = (BigInteger.ModPow(G, x, P) * h) % P;
        string hexm = Mptoa64(m);
        await conn.WriteAsync($"{Version}\tPAK\nC={c}\nm={hexm}\n");

        // Receive g**y and S, and check hash1(g**xy).
        (int n, string mess) = await conn.ReadstrAsync();
        if (n < 0)
        {
            await PrintAsync(process, $"{argv0}: error: {mess}\n");
            await conn.WriterrAsync("couldn't read g**y");
            return (-1, null);
        }

        int eol = mess.IndexOf('\n', StringComparison.Ordinal);
        if (!mess.StartsWith("mu=", StringComparison.Ordinal) || eol < 0 || !mess.AsSpan(eol).StartsWith("\nk="))
        {
            await conn.WriterrAsync("verifier syntax error");
            return (-1, null);
        }

        string hexmu = mess[3..eol];
        string rest = mess[(eol + 3)..];
        eol = rest.IndexOf('\n', StringComparison.Ordinal);
        if (eol < 0 || !rest.AsSpan(eol).StartsWith("\nS="))
        {
            await conn.WriterrAsync("verifier syntax error for secstore 1.0");
            return (-1, null);
        }

        string ks = rest[..eol];
        string s = rest[(eol + 3)..];
        eol = s.IndexOf('\n', StringComparison.Ordinal);
        if (eol < 0)
        {
            await conn.WriterrAsync("verifier syntax error for secstore 1.0");
            return (-1, null);
        }

        s = s[..eol];
        BigInteger mu = Strtomp64(hexmu) ?? BigInteger.Zero;
        string hexsigma = Mptoa64(BigInteger.ModPow(mu, x, P));
        if (ks != Enc64(Shorthash("server", c, s, hexm, hexmu, hexsigma, hexHi)))
        {
            await conn.WriterrAsync("verifier didn't match");
            return (-1, s);
        }

        // Send hash2(g**xy).
        await conn.WriteAsync($"k'={Enc64(Shorthash("client", c, s, hexm, hexmu, hexsigma, hexHi))}\n");

        // Set the session key.
        conn.Secret(Shorthash("session", c, s, hexm, hexmu, hexsigma, hexHi), 0);
        return (0, s);
    }

    // PAKserver: the session's secret set in conn once the client named in mess, its first
    // message, has proved it knows the passphrase; -2 when it has not, -1 on any other failure.
    // The account's failures are counted, and cleared by a success.
    public static async Task<(int Rc, Pw? Pw)> ServerAsync(Process process, string argv0, SConn conn, string s, string mess)
    {
        // secstore version and algorithm.
        string mess2 = $"{Version}\tPAK\n";
        if (!mess.StartsWith(mess2, StringComparison.Ordinal))
        {
            await conn.WriterrAsync("protocol should start with ver alg");
            return (-1, null);
        }

        (int rc, Pw? pw) = await ExchangeAsync(process, argv0, conn, s, mess[mess2.Length..]);
        if (rc < 0 && pw is not null)
        {
            pw.Failed++;
            await Password.PutAsync(process, pw);
        }

        if (rc == 0 && pw is { Failed: > 0 })
        {
            pw.Failed = 0;
            await Password.PutAsync(process, pw);
        }

        return (rc, pw);
    }

    private static async Task<(int Rc, Pw? Pw)> ExchangeAsync(Process process, string argv0, SConn conn, string s, string mess)
    {
        // Parse the first message into C and m.
        int eol = mess.IndexOf('\n', StringComparison.Ordinal);
        if (!mess.StartsWith("C=", StringComparison.Ordinal) || eol < 0)
        {
            await PrintAsync(process, $"{argv0}: mess[1]={mess}\n");
            await conn.WriterrAsync("PAK version mismatch");
            return (-1, null);
        }

        string c = mess[2..eol];
        string hexm = eol + 3 <= mess.Length ? mess[(eol + 3)..] : string.Empty;
        int end = hexm.IndexOf('\n', StringComparison.Ordinal);
        if (!mess.AsSpan(eol + 1).StartsWith("m=") || end < 0)
        {
            await conn.WriterrAsync("PAK version mismatch");
            return (-1, null);
        }

        hexm = hexm[..end];
        BigInteger m = Mod(Strtomp64(hexm) ?? BigInteger.Zero, P);

        // Look up the client.
        (Pw? pw, string? why) = await Password.GetAsync(process, c, false);
        if (pw is null)
        {
            await conn.WriterrAsync(why!);
            return (-1, null);
        }

        if (m.IsZero)
        {
            await conn.WriterrAsync("account exists");
            return (-1, null);
        }

        string hexHi = Mptoa64(pw.Hi);

        // Random y, mu=g**y, sigma=g**xy.
        BigInteger y = Rand();
        BigInteger mu = BigInteger.ModPow(G, y, P);
        BigInteger sigma = BigInteger.ModPow((m * pw.Hi) % P, y, P);

        // Send g**y and hash1(g**xy).
        string hexmu = Mptoa64(mu);
        string hexsigma = Mptoa64(sigma);
        string ks = Enc64(Shorthash("server", c, s, hexm, hexmu, hexsigma, hexHi));
        await conn.WriteAsync($"mu={hexmu}\nk={ks}\nS={s}\n");

        // Receive hash2(g**xy).
        (int n, string reply) = await conn.ReadstrAsync();
        if (n < 0)
        {
            await conn.WriterrAsync("couldn't read verifier");
            return (-1, pw);
        }

        eol = reply.IndexOf('\n', StringComparison.Ordinal);
        if (!reply.StartsWith("k'=", StringComparison.Ordinal) || eol < 0)
        {
            await conn.WriterrAsync("verifier syntax error");
            return (-1, pw);
        }

        if (reply[3..eol] != Enc64(Shorthash("client", c, s, hexm, hexmu, hexsigma, hexHi)))
        {
            return (-2, pw);
        }

        // Set the session key.
        conn.Secret(Shorthash("session", c, s, hexm, hexmu, hexsigma, hexHi), 1);
        return (0, pw);
    }

    // longhash: H = (sha(ver,C,sha(passphrase)))^r mod p, a hash expensive to attack by brute force.
    private static BigInteger Longhash(string ver, string c, byte[] passwd)
    {
        byte[] cp = [.. Encoding.UTF8.GetBytes(ver), .. Encoding.UTF8.GetBytes(c), .. passwd];
        byte[] buf = new byte[140];
        for (int i = 0; i < 7; i++)
        {
            HMACSHA1.HashData(new[] { (byte)('A' + i) }, cp).CopyTo(buf, i * SHA1.HashSizeInBytes);
        }

        CryptographicOperations.ZeroMemory(cp);
        BigInteger h = new BigInteger(buf, isUnsigned: true, isBigEndian: true) % P;
        return BigInteger.ModPow(h, R, P);
    }

    // shorthash: a faster hash for each party to confirm the other has the right secrets.
    private static byte[] Shorthash(string mess, string c, string s, string m, string mu, string sigma, string hi)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        string[] parts = [mess, c, s, m, mu, sigma, hi];
        foreach (string part in parts.Concat(parts))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(part));
        }

        return sha.GetHashAndReset();
    }

    // mprand(240) mod q, and 1 for 0.
    private static BigInteger Rand()
    {
        byte[] b = new byte[30];
        Authsrv.Genrandom(b);
        BigInteger x = new BigInteger(b, isUnsigned: true, isBigEndian: true) % Q;
        return x.IsZero ? BigInteger.One : x;
    }

    // mpinvert: the inverse of b mod m, by the extended gcd.
    private static BigInteger Invert(BigInteger b, BigInteger m)
    {
        (BigInteger r0, BigInteger r1, BigInteger t0, BigInteger t1) = (m, Mod(b, m), BigInteger.Zero, BigInteger.One);
        while (!r1.IsZero)
        {
            BigInteger q = r0 / r1;
            (r0, r1) = (r1, r0 - (q * r1));
            (t0, t1) = (t1, t0 - (q * t1));
        }

        return Mod(t0, m);
    }

    private static BigInteger Mod(BigInteger a, BigInteger m) => ((a % m) + m) % m;

    private static BigInteger Hex(string s) => BigInteger.Parse("0" + s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string Enc64(byte[] digest) => Convert.ToBase64String(digest);

    // dec64chr: the value of a base-64 digit, or -1.
    private static int Dec64chr(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };

    // dec64: the bytes of the digits, four to three, a last two or three giving one or two.
    private static byte[] Dec64(string digits)
    {
        var o = new List<byte>();
        int b24 = 0, i = 0;
        foreach (char ch in digits)
        {
            b24 |= Dec64chr(ch) << (18 - (6 * i));
            if (++i == 4)
            {
                o.AddRange([(byte)(b24 >> 16), (byte)(b24 >> 8), (byte)b24]);
                (b24, i) = (0, 0);
            }
        }

        if (i >= 2)
        {
            o.Add((byte)(b24 >> 16));
        }

        if (i == 3)
        {
            o.Add((byte)(b24 >> 8));
        }

        return [.. o];
    }

    private static Task PrintAsync(Process process, string text) => process.WriteAsync(2, Encoding.UTF8.GetBytes(text)).AsTask();
}
