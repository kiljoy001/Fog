namespace NinePSharp.Fog.Rc;

// glob.c's string functions. A GLOB byte marks the character after it as a pattern character;
// GLOB GLOB is a GLOB.
internal static class RcGlob
{
    public const char Glob = RcToken.Glob;

    // deglob: the string, which holds a GLOB, without its GLOB marks.
    public static string Deglob(string s)
    {
        int r = s.IndexOf(Glob);
        var w = new System.Text.StringBuilder(s[..r]);
        for (r++; r < s.Length; r++)
        {
            if (s[r] == Glob)
            {
                r++;
                if (r == s.Length)
                {
                    break;
                }
            }

            w.Append(s[r]);
        }

        return w.ToString();
    }

    // matchfn, which keeps . and .. for patterns starting with a dot; dirread never returns them.
    public static bool MatchName(string s, string p) => Match(s, p, '/');

    // match: whether s matches the pattern p, up to the stop byte in p. Positions step over UTF-8
    // sequences.
    public static bool Match(string s, string p, char stop)
    {
        int si = 0, pi = 0;
        for (; At(p, pi) != stop && At(p, pi) != '\0'; si = NextUtf(s, si), pi = NextUtf(p, pi))
        {
            if (At(p, pi) != Glob)
            {
                if (!EqualUtf(p, pi, s, si))
                {
                    return false;
                }

                continue;
            }

            switch (At(p, ++pi))
            {
                case Glob:
                    if (At(s, si) != Glob)
                    {
                        return false;
                    }

                    break;
                case '*':
                    for (; !Match(s[si..], p[NextUtf(p, pi)..], stop); si = NextUtf(s, si))
                    {
                        if (At(s, si) == '\0')
                        {
                            return false;
                        }
                    }

                    return true;

                case '?':
                    if (At(s, si) == '\0')
                    {
                        return false;
                    }

                    break;
                case '[':
                    if (At(s, si) == '\0' || !Class(s, si, p, ref pi))
                    {
                        return false;
                    }

                    break;
            }
        }

        return At(s, si) == '\0';
    }

    // A string ends at its length, as rc's at its NUL.
    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    // [...] at p[pi], with pi at the '['; leaves pi at the ']'.
    private static bool Class(string s, int si, string p, ref int pi)
    {
        int c = Unicode(s, si);
        pi++;
        bool complement = At(p, pi) == '~';
        if (complement)
        {
            pi++;
        }

        bool hit = false;
        while (At(p, pi) != ']')
        {
            if (At(p, pi) == '\0')
            {
                return false;
            }

            int lo = Unicode(p, pi), hi = lo;
            pi = NextUtf(p, pi);
            if (At(p, pi) == '-')
            {
                pi++;
                if (At(p, pi) == '\0')
                {
                    return false;
                }

                hi = Unicode(p, pi);
                pi = NextUtf(p, pi);
                (lo, hi) = (Math.Min(lo, hi), Math.Max(lo, hi));
            }

            hit |= lo <= c && c <= hi;
        }

        return hit != complement;
    }

    // nextutf: past the UTF-8 sequence at i, not past a byte that does not continue it.
    private static int NextUtf(string s, int i)
    {
        int c = At(s, i);
        int n = (c & 0x80) == 0 ? 1 : (c & 0xE0) == 0xC0 ? 2 : (c & 0xF0) == 0xE0 ? 3 : 4;
        int k = 1;
        while (k < n && (At(s, i + k) & 0xC0) == 0x80)
        {
            k++;
        }

        return i + k;
    }

    // unicode: the code point of the UTF-8 sequence at i, or -1 when it is broken.
    private static int Unicode(string s, int i)
    {
        int c = At(s, i);
        bool X(int k) => (At(s, i + k) & 0xC0) == 0x80;
        int Bits(int k) => At(s, i + k) & 0x3F;
        if ((c & 0x80) == 0)
        {
            return c & 0xFF;
        }

        if ((c & 0xE0) == 0xC0)
        {
            return X(1) ? ((c & 0x1F) << 6) | Bits(1) : -1;
        }

        if ((c & 0xF0) == 0xE0)
        {
            return X(1) && X(2) ? ((c & 0x0F) << 12) | (Bits(1) << 6) | Bits(2) : -1;
        }

        return (c & 0xF8) == 0xF0 && X(1) && X(2) && X(3) ? ((c & 0x07) << 18) | (Bits(1) << 12) | (Bits(2) << 6) | Bits(3) : -1;
    }

    // equtf
    private static bool EqualUtf(string p, int pi, string s, int si)
        => At(p, pi) == At(s, si) && Unicode(p, pi) == Unicode(s, si);
}
