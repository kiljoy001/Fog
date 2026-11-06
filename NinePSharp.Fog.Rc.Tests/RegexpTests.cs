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

    // As above, with newlines in the expression and subject, and rregsub given fewer
    // subexpressions than were matched, none, or no match at all (-1).
    public static TheoryData<string, string, string, string, int, string> MoreVectors => new()
    {
        { "c", "a.c", "a\nc", "&", 10, "0" },
        { "n", "a.c", "a\nc", "&", 10, "1 0:0,3 [a\nc]" },
        { "c", "a[^x]c", "a\nc", "&", 10, "0" },
        { "n", "a[^x]c", "a\nc", "&", 10, "0" },
        { "l", "a.c", "a\nc", "&", 10, "0" },
        { "l", "a.c", "a.c", "&", 10, "1 0:0,3 [a.c]" },
        { "c", "a(b)c", "abc", @"<\2|\1|&>", 2, "1 0:0,3 1:1,2 [<|b|abc>]" },
        { "c", "a(b)c", "abc", @"<\1|&>", 0, "1 0:0,3 1:1,2 [<|>]" },
        { "c", "a(b)c", "abc", @"<\1|&>", -1, "1 0:0,3 1:1,2 [<|>]" },
        { "c", "a|", "a", "&", 10, "err regexp a|: parsing error|nil" },
        { "c", "(", "a", "&", 10, "err regexp (: parsing error|nil" },
        { "c", new string('a', 120) + "|*", "a", "&", 10, $"err regexp {new string('a', 120)}|nil" },
        { "c", new string('a', 121) + "|*", "a", "&", 10, $"err regexp {new string('a', 120)}|nil" },
        { "c", new string('é', 60) + "|*", "a", "&", 10, $"err regexp {new string('é', 60)}|nil" },
        { "c", "[a-c-e]", "d", "&", 10, "err Malformed class|nil" },
        { "c", $"[{string.Concat(Enumerable.Repeat("a-b", 150))}]", "b", "&", 10, "1 0:0,1 [b]" },
        { "c", $"[{string.Concat(Enumerable.Repeat("a-b", 101))}]", "b", "&", 10, "1 0:0,1 [b]" },
        { "c", "[a-a]", "a", "&", 10, "1 0:0,1 [a]" },
        { "c", "[a-bc-d]", "c", "&", 10, "1 0:0,1 [c]" },
        { "c", "[^a-bc-d]", "ce", "&", 10, "1 0:1,2 [e]" },
        { "c", "[^a-cc-d]", "ce", "&", 10, "1 0:1,2 [e]" },
        { "c", "[^a-cb-d]", "be", "&", 10, "1 0:1,2 [e]" },
        { "c", "[ac]", "b", "&", 10, "0" },
        { "c", "[^ac]", "ab", "&", 10, "1 0:1,2 [b]" },
        { "c", "(a)(b)(c)", "abc", @"<\2>", 2, "1 0:0,3 1:0,1 2:1,2 3:2,3 [<>]" },
        { "c", "[a]", "\U0010FFFFa", "&", 10, "1 0:1,2 [a]" },
        { "c", "[a]", "\U0010FFFEa", "&", 10, "1 0:1,2 [a]" },
        { "c", "[^a]", "\U0010FFFFa", "&", 10, "1 0:0,1 [\U0010FFFF]" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void MatchesAndSubstitutesAs9frontDoes(string mode, string pattern, string subject, string template, string expected)
    {
        Assert.Equal(expected, Run(mode, pattern, subject, template));
    }

    [Theory]
    [MemberData(nameof(MoreVectors))]
    public void MatchesAndSubstitutesAs9frontDoesAcrossLinesAndWithFewerSubexpressions(string mode, string pattern, string subject, string template, int msize, string expected)
    {
        Assert.Equal(expected, Run(mode, pattern, subject, template, msize));
    }

    // regcomp of bytes is regcomp: its . is no newline, as regcompnl's is.
    [Fact]
    public void RegcompOfBytesMatchesNoNewlineWithItsDot()
    {
        Assert.Equal(0, Regcomp("a.c"u8.ToArray()).Regexec("a\nc"u8.ToArray(), null, 0));
        Assert.Equal(1, Regcompnl("a.c").Regexec("a\nc"u8.ToArray(), null, 0));
    }

    // A template whose runes end without a NUL ends at its last, a backslash there with it.
    [Fact]
    public void ATemplateWithoutItsNulEndsAtItsLastRune()
    {
        Assert.Equal("ab", string.Concat(Rregsub(['a', 'b'], 16, [0], null, 0).Select(c => (char)c)));
        Assert.Equal("a", string.Concat(Rregsub(['a', '\\'], 16, [0], null, 0).Select(c => (char)c)));
    }

    // regerror: the message on standard error, and exits "regerr".
    [Fact]
    public async Task RegerrorSaysWhatAndExitsRegerr()
    {
        NinePSharp.Fog.Kernel.Process init = await NinePSharp.Fog.Kernel.FogKernel.InMemory(new Dictionary<string, NinePSharp.Fog.Kernel.ProgramMain>()).BootAsync();
        (int r, int w) = await init.PipeAsync();
        await init.DupAsync(w, 2);
        var exit = await Assert.ThrowsAsync<NinePSharp.Fog.Kernel.ExitException>(() => RegerrorAsync(init, "bad"));
        Assert.Equal("regerr", exit.Message);

        // Within a bound, so a regerror that writes nothing fails the test rather than holding up the run.
        Assert.Equal("regerror: bad\n", Encoding.UTF8.GetString((await init.ReadAsync(r, 100).AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Span));
    }

    private static string Run(string mode, string pattern, string subject, string template, int msize = 10)
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
            foreach (uint c in Rregsub(Runes(template), 16, s, msize < 0 ? null : m, msize < 0 ? 10 : msize))
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
