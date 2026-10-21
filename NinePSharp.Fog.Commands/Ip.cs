using System.Globalization;
using System.Net.Sockets;
using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// libip's addresses, as 9front's parseip.c, ipaux.c, classmask.c and eipfmt.c: 16 bytes, a v4
// address held in the last 4 after the v4 prefix.
public static class Ip
{
    public const int AddrLen = 16;
    public const int V4Off = 12;

    private static readonly byte[] V4Prefix = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 0, 0, 0, 0];
    private static readonly byte[] V6LinkLocal = [0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] V6LinkLocalMask = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] V6SolicitedNode = [0xff, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0xff, 0, 0, 0];
    private static readonly byte[] V6SolicitedNodeMask = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0, 0];
    private static readonly byte[] V6MulticastMask = [0xff, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static readonly byte[][] ClassMask =
    [
        [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0, 0],
        [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0, 0],
        [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0],
        [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0],
    ];

    public static byte[] AllBits { get; } = [.. Enumerable.Repeat((byte)0xff, AddrLen)];

    public static byte[] NoAddr { get; } = new byte[AddrLen];

    public static bool IsV4(ReadOnlySpan<byte> ip) => ip[..V4Off].SequenceEqual(V4Prefix.AsSpan(0, V4Off));

    public static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a[..AddrLen].SequenceCompareTo(b[..AddrLen]);

    // parseip: the address, its v4 value, 6 for v6, or -1 with the address zeroed for one that
    // does not parse. What follows the address need only not continue it.
    public static long Parse(byte[] to, string from)
    {
        Array.Clear(to, 0, AddrLen);
        int p = 0, i, elipsis = 0;
        bool v4 = true;
        for (i = 0; i < AddrLen && Ipcharok(At(from, p)); i += 2)
        {
            int op = p;
            uint x = Libc.Strtoul(from[p..], 16, out int end);
            p += end;

            // Ends with v4?
            if (At(from, p) == '.' || (At(from, p) == '\0' && i == 0))
            {
                if (i > AddrLen - 4)
                {
                    return Fail(to);
                }

                p = V4Parse(to, i, from, op);
                i += 4;
                break;
            }

            // v6: at most 4 hex digits, followed by a colon or a delimiter.
            if (x > 0xffff || (At(from, p) != ':' && !Delimchar(At(from, p))))
            {
                return Fail(to);
            }

            to[i] = (byte)(x / 256);
            to[i + 1] = (byte)x;
            if (At(from, p) == ':')
            {
                v4 = false;
                if (At(from, ++p) == ':')
                {
                    // :: elides zero shorts, once.
                    if (elipsis != 0)
                    {
                        return Fail(to);
                    }

                    elipsis = i + 2;
                    p++;
                }
            }

            // parseip.c also stops where strtoul made no progress, but a character ipcharok takes
            // and strtoul does not is a '.' or a ':', each handled above.
        }

        if (p == 0 || !Delimchar(At(from, p)))
        {
            return Fail(to);
        }

        // The shorts after the elision moved to the end; with all 16 bytes given this moves nothing.
        Array.Copy(to, elipsis, to, elipsis + AddrLen - i, i - elipsis);
        Array.Clear(to, elipsis, AddrLen - i);

        if (v4)
        {
            to[10] = to[11] = 0xff;
            return (long)(((uint)to[12] << 24) | ((uint)to[13] << 16) | ((uint)to[14] << 8) | to[15]);
        }

        return 6;
    }

    // v4parseip: dotted parts, each as strtoul reads them, with the parts a class's net leaves out
    // moved to the end.
    public static int V4Parse(byte[] to, int at, string from, int p)
    {
        int i;
        for (i = 0; i < 4 && At(from, p) != '\0'; i++)
        {
            to[at + i] = (byte)Libc.Strtoul(from[p..], 0, out int end);
            p += end;
            if (At(from, p) == '.')
            {
                p++;
            }
        }

        switch (to[at] / 64)
        {
            case 0 or 1:
                if (i == 3)
                {
                    (to[at + 3], to[at + 2], to[at + 1]) = (to[at + 2], to[at + 1], 0);
                }
                else if (i == 2)
                {
                    (to[at + 3], to[at + 1]) = (to[at + 1], 0);
                }

                break;
            case 2:
                if (i == 3)
                {
                    (to[at + 3], to[at + 2]) = (to[at + 2], 0);
                }

                break;
        }

        return p;
    }

    // parseipmask: a mask as /bits, or as an address; -1 for a v4 mask that cannot be one.
    public static long ParseMask(byte[] to, string from, bool v4)
    {
        long x;
        if (from.StartsWith('/'))
        {
            int i = Math.Max(0, Libc.Strtol(from[1..], 10, out _));
            if (i <= 32 && v4)
            {
                i += 96;
            }

            i = Math.Min(i, 128);

            // Each byte holds what is left of the prefix, up to 8 bits of it.
            for (int b = 0; b < AddrLen; b++)
            {
                to[b] = (byte)~(0xff / (1 << Math.Clamp(i - (8 * b), 0, 8)));
            }

            // A mask with too few bits to be a v4 one is a v6 one.
            if (i < 96)
            {
                return v4 ? -1 : 6;
            }

            x = (long)(((uint)to[12] << 24) | ((uint)to[13] << 16) | ((uint)to[14] << 8) | to[15]);
        }
        else
        {
            x = Parse(to, from);
            if (to.AsSpan(0, V4Off).SequenceEqual(V4Prefix.AsSpan(0, V4Off)))
            {
                to.AsSpan(0, V4Off).Fill(0xff);
            }
            else if (v4 && !to.AsSpan(0, V4Off).SequenceEqual(AllBits.AsSpan(0, V4Off)))
            {
                x = -1;
            }
        }

        return x;
    }

    // defmask: a v4 address's class mask, or what a v6 address's kind gives.
    public static byte[] DefMask(ReadOnlySpan<byte> ip)
    {
        if (IsV4(ip))
        {
            return ClassMask[ip[V4Off] / 64];
        }

        if (ip[..8].SequenceEqual(V6LinkLocal.AsSpan(0, 8)))
        {
            return V6LinkLocalMask;
        }

        if (ip[..13].SequenceEqual(V6SolicitedNode.AsSpan(0, 13)))
        {
            return V6SolicitedNodeMask;
        }

        return ip[0] == 0xff ? V6MulticastMask : AllBits;
    }

    public static bool IsV6LinkLocal(ReadOnlySpan<byte> ip) => ip[0] == 0xfe && (ip[1] & 0xc0) == 0x80;

    // maskip: the address with only the mask's bits.
    public static byte[] MaskIp(ReadOnlySpan<byte> from, ReadOnlySpan<byte> mask)
    {
        byte[] to = new byte[AddrLen];
        for (int i = 0; i < AddrLen; i++)
        {
            to[i] = (byte)(from[i] & mask[i]);
        }

        return to;
    }

    // readipifc: the interfaces and their addresses. Fog's #I has no ipifc, since the host keeps
    // the addresses, so they are the host's.
    public static IReadOnlyList<Ipifc> ReadIpifc(HostNetwork host)
    {
        var ifcs = new List<Ipifc>();
        foreach (HostInterface ni in host.Interfaces())
        {
            var lifcs = new List<Iplifc>();
            foreach (HostAddress a in ni.Addresses)
            {
                byte[] ip = new byte[AddrLen];
                byte[] mask = new byte[AddrLen];

                // The host's addresses are v4 or v6.
                bool v4 = a.Address.AddressFamily == AddressFamily.InterNetwork;
                if (v4)
                {
                    V4Prefix.CopyTo(ip, 0);
                    a.Address.GetAddressBytes().CopyTo(ip, V4Off);
                }
                else
                {
                    a.Address.GetAddressBytes().CopyTo(ip, 0);
                }

                ParseMask(mask, $"/{a.PrefixLength}", v4);
                lifcs.Add(new Iplifc(ip, mask, MaskIp(ip, mask)));
            }

            ifcs.Add(new Ipifc(ni.Name, lifcs));
        }

        return ifcs;
    }

    // %I: dotted for v4, else v6 with its longest run of zero shorts elided.
    public static string Format(ReadOnlySpan<byte> p)
    {
        if (p[..12].SequenceEqual(V4Prefix.AsSpan(0, 12)))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{p[12]}.{p[13]}.{p[14]}.{p[15]}");
        }

        // The longest run of zero shorts, the first of the longest; none until one is found, so eli
        // is past every short.
        int eln = 0, eli = AddrLen;
        foreach (int i in Enumerable.Range(0, 8).Select(k => 2 * k))
        {
            int j = i;
            while (j < AddrLen && p[j] == 0 && p[j + 1] == 0)
            {
                j += 2;
            }

            if (j - i > eln)
            {
                (eli, eln) = (i, j - i);
            }
        }

        var buf = new StringBuilder();
        for (int i = 0; i < 16; i += 2)
        {
            if (i == eli)
            {
                buf.Append("::");
                i += eln;
                if (i >= 16)
                {
                    break;
                }
            }
            else if (i != 0)
            {
                buf.Append(':');
            }

            buf.Append(((p[i] << 8) + p[i + 1]).ToString("x", CultureInfo.InvariantCulture));
        }

        return buf.ToString();
    }

    // %M: /bits for a prefix mask, else as an address.
    public static string FormatMask(ReadOnlySpan<byte> p)
    {
        int i = 0;
        while (i < 16 && p[i] == 0xff)
        {
            i++;
        }

        if (i == 16)
        {
            return "/128";
        }

        int bits = System.Numerics.BitOperations.LeadingZeroCount((uint)(byte)~p[i]) - 24;
        if ((byte)(0xff << (8 - bits)) != p[i] || p[(i + 1)..16].IndexOfAnyExcept((byte)0) >= 0)
        {
            return Format(p);
        }

        return string.Create(CultureInfo.InvariantCulture, $"/{(8 * i) + bits}");
    }

    private static long Fail(byte[] to)
    {
        Array.Clear(to, 0, AddrLen);
        return -1;
    }

    private static char At(string s, int p) => p < s.Length ? s[p] : '\0';

    private static bool Ipcharok(char c) => c is '.' or ':' || char.IsAsciiHexDigit(c);

    private static bool Delimchar(char c) => c == '\0' || !(c is '.' or ':' || char.IsAsciiLetterOrDigit(c));
}
