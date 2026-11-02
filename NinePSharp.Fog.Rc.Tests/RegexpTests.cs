using System.Text;
using NinePSharp.Fog.Commands;
using Xunit;
using static NinePSharp.Fog.Commands.Regexp;

namespace NinePSharp.Fog.Rc.Tests;

// libregexp against what 9front's gives: each expression compiled by regcomp (c), regcomplit (l)
// or regcompnl (n), run by rregexec on the subject with ten subexpressions, and its match put
// through rregsub with sixteen runes. A line is printed as 9front's harness printed it: the
// result, each subexpression's start and end that was set, and the substitution in brackets; or
// what regerror was told. $ on ab is as 9front gives it in a run of its own: after a$ in the
// same run it gives 0, since rregexec clears its instructions' marks only when the first's is set
// and the program malloc gives back still holds a$'s.
public sealed class RegexpTests
{
    public static TheoryData<string, string, string, string, string> Vectors => new()
    {
        { "c", "a(b)c", "xabcx", @"<\1|&|\\|\x|\0>", @"1 0:1,4 1:2,3 [<b|abc|\|x|abc>]" },
        { "c", "(a)(b)(c)(d)(e)(f)(g)(h)(i)", "abcdefghi", @"\9\8\7\1\2\3\4\5\6\0", "1 0:0,9 1:0,1 2:1,2 3:2,3 4:3,4 5:4,5 6:5,6 7:6,7 8:7,8 9:8,9 [ihgabcdefabcdef]" },
        { "c", "a", "bab", "[&&&&&&&&&&&&&&&&&&&&]", "1 0:1,2 [[aaaaaaaaaaaaaa]" },
        { "c", "(x)?b", "b", @"<\1>", "1 0:0,1 [<>]" },
        { "c", "b", "abc", @"\", "1 0:1,2 []" },
        { "l", "a.c", "a.c abc", "[&]", "1 0:0,3 [[a.c]]" },
        { "l", "a.c", "abc", "[&]", "0" },
        { "l", @"^a\", @"x^a\", "[&]", @"1 0:1,4 [[^a\]]" },
        { "n", "a.c", @"a\nc", "&", "0" },
        { "n", "a[^x]c", @"a\nc", "&", "0" },
        { "c", "a.c", @"a\nc", "&", "0" },
        { "c", "a[^x]c", @"a\nc", "&", "0" },
        { "c", "[^a-c]", "abcd", "&", "1 0:3,4 [d]" },
        { "c", "[c-a]", "abc", "&", "1 0:0,1 [a]" },
        { "c", "[a-cx-z]", "y", "&", "1 0:0,1 [y]" },
        { "c", "[x-za-c]", "b", "&", "1 0:0,1 [b]" },
        { "c", "[a-ca-e]", "d", "&", "1 0:0,1 [d]" },
        { "c", "[b-cd-e]", "e", "&", "1 0:0,1 [e]" },
        { "c", "[a-db-c]", "c", "&", "1 0:0,1 [c]" },
        { "c", "[^a-cd-f]", "ag", "&", "1 0:1,2 [g]" },
        { "c", "[^d-fa-c]", "ah", "&", "1 0:1,2 [h]" },
        { "c", "[^]", "a]", "&", "1 0:0,1 [a]" },
        { "c", "[]a]", "]", "&", "0" },
        { "c", "[^-a]", "-ab", "&", "err Malformed class|nil" },
        { "c", "[a-]", "-", "&", "err Malformed class|nil" },
        { "c", @"[\]]", "]", "&", "1 0:0,1 []]" },
        { "c", @"[\-]", "-", "&", "1 0:0,1 [-]" },
        { "c", @"[a-\z]", "m", "&", "1 0:0,1 [m]" },
        { "c", "x*", "aaa", "<&>", "1 0:0,0 [<>]" },
        { "c", "(a|ab)(c|bcd)", "abcd", @"<\1,\2>", "1 0:0,4 1:0,1 2:1,4 [<a,bcd>]" },
        { "c", "é+", "ééa", "&", "1 0:0,2 [éé]" },
        { "c", "[é-ü]+", "ñx", "&", "1 0:0,1 [ñ]" },
        { "c", "^b", @"a\nb", "&", "0" },
        { "c", "a$", "ab", "&", "0" },
        { "c", "$", "ab", "<&>", "1 0:2,2 [<>]" },
        { "c", "b$", "ab", "<&>", "1 0:1,2 [<b>]" },
        { "c", "$", "a", "<&>", "1 0:1,1 [<>]" },
        { "c", "x*$", "ab", "<&>", "1 0:2,2 [<>]" },
        { "c", "a|$", "b", "<&>", "1 0:1,1 [<>]" },
        { "c", "^", "ab", "<&>", "1 0:0,0 [<>]" },
        { "c", "^$", "x", "<&>", "0" },
        { "c", "()$", "ab", "<&>", "err regexp ()$: syntax error: )|nil" },
        { "c", ".", @"\n", "&", @"1 0:0,1 [\]" },
        { "c", "[^a]", @"\n", "&", @"1 0:0,1 [\]" },
        { "n", "[^a]", @"\n", "&", @"1 0:0,1 [\]" },
        { "c", "[a-cb-df-gh-i]+", "abcdfghie", "&", "1 0:0,8 [abcdfghi]" },
        { "c", "[^b-dg]", "g", "&", "0" },
        { "c", @"[^\x00-\x7f]", "a", "&", "0" },
        { "c", "a{", "a{", "&", "1 0:0,2 [a{]" },
        { "c", "a**", "aa", "&", "1 0:0,2 [aa]" },
        { "c", "(a*)*", "b", @"<\1>", "1 0:0,0 [<>]" },
        { "c", "(a", "a", "&", "err regexp (a: no matching parenthesis|nil" },
        { "c", "a)", "a", "&", "1 0:0,1 [a]" },
        { "c", "*a", "a", "&", "err regexp *a: syntax error: *|nil" },
        { "c", "[a", "a", "&", "err No closing ] for class|nil" },
        { "c", "[", "a", "&", "err No closing ] for class|nil" },
        { "c", "[^", "a", "&", "err No closing ] for class|nil" },
        { "c", "a|*", "a", "&", "err regexp a|*: syntax error: *|nil" },
        { "c", "()", "a", "&", "err regexp (): syntax error: )|nil" },
        { "c", "a(|b)", "a", "&", "err regexp a(|b): syntax error: ||nil" },
        { "c", $"[{new string('a', 200)}]", "a", "&", "1 0:0,1 [a]" },
        { "c", $"[{new string('a', 201)}]", "a", "&", "err Class too big\n|nil" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void MatchesAndSubstitutesAs9frontDoes(string mode, string pattern, string subject, string template, string expected)
    {
        Assert.Equal(expected, Run(mode, pattern, subject, template));
    }

    private static string Run(string mode, string pattern, string subject, string template)
    {
        Regexp p;
        try
        {
            p = mode switch
            {
                "l" => Regcomplit(pattern),
                "n" => Regcompnl(pattern),
                _ => Regcomp(pattern),
            };
        }
        catch (RegerrorException e)
        {
            return $"err {e.Message}|nil";
        }

        uint[] s = Runes(subject);
        Resub[] m = Enumerable.Repeat(Resub.None, 10).ToArray();
        int r = p.Rregexec(s, m, 10);
        var line = new StringBuilder(r.ToString());
        for (int i = 0; r != 0 && i < 10; i++)
        {
            if (m[i].Sp >= 0)
            {
                line.Append($" {i}:{m[i].Sp},{m[i].Ep}");
            }
        }

        if (r != 0)
        {
            line.Append(" [");
            foreach (uint c in Rregsub(Runes(template), 16, s, m, 10))
            {
                line.Append(char.ConvertFromUtf32((int)c));
            }

            line.Append(']');
        }

        return line.ToString();
    }

    private static uint[] Runes(string s)
    {
        byte[] b = Encoding.UTF32.GetBytes(s);
        var runes = new uint[(b.Length / 4) + 1];
        Buffer.BlockCopy(b, 0, runes, 0, b.Length);
        return runes;
    }
}
