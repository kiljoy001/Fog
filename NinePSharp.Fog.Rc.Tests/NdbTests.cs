using System.Text;
using NinePSharp.Fog.Commands;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// libndb's tuples as 9front's ndbaux.c, ndbreorder.c, ndbdedup.c, ndbdiscard.c, ndbsubstitute.c,
// ipattr.c, ndbvalfmt.c and mkptrname.c make them: an entry is its tuples in order, each line a
// ring of its own, and each test prints the entry a line at a time, as cs's %N does.
public sealed class NdbTests
{
    [Theory]
    [InlineData("a=1 b=2\n", "a=1 b=2")]
    [InlineData("\t a=\"x y\" b c=#d\n", "a=\"x y\" b= c=")]
    [InlineData("a=\"unterminated\n", "a=unterminated")]
    [InlineData("a = 1\n", "a= 1=")]
    [InlineData("a\tb\r\n", "a= b=")]
    [InlineData("a=1#comment\n", "a=\"1#comment\"")]
    [InlineData("a=1 #comment\n", "a=1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=1\n", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=1")]
    [InlineData("a=1\nb=2\n", "a=1")]
    public void ALineParsesAsNdbparselineReadsIt(string line, string printed)
    {
        Assert.Equal(printed, Print(Ndb.ParseLine(line)));
    }

    [Theory]
    [InlineData("# a comment\n")]
    [InlineData("\n")]
    [InlineData("   \n")]
    public void ALineOfNoTuplesIsNone(string line)
    {
        Assert.Null(Ndb.ParseLine(line));
    }

    [Fact]
    public void EachLineIsARingAndTheEntryRunsThroughThem()
    {
        NdbTuple a = Ndb.ParseLine("a=1 b=2 c=3\n")!;
        NdbTuple b = a.Entry!, c = b.Entry!;
        Assert.Equal((b, c, a), (a.Line, b.Line, c.Line));
        Assert.Null(c.Entry);
        NdbTuple single = Ndb.ParseLine("a=1\n")!;
        Assert.Same(single, single.Line);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("ab", "ab")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\tb", "\"a\tb\"")]
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData("a#b", "\"a#b\"")]
    [InlineData("a\"b", "a\"b")]
    public void AValueIsQuotedWhenItHoldsWhiteSpaceOrAHash(string? value, string printed)
    {
        Assert.Equal(printed, Ndb.ValFmt(value));
    }

    [Theory]
    [InlineData("1.2.3.4", "ip")]
    [InlineData("::1", "ip")]
    [InlineData("fe80::1", "ip")]
    [InlineData("1:2", "ip")]
    [InlineData("10", "sys")]
    [InlineData("abc", "sys")]
    [InlineData("ab.cd", "sys")]
    [InlineData("1.a", "sys")]
    [InlineData("g", "sys")]
    [InlineData("host-1", "sys")]
    [InlineData("1-2", "sys")]
    [InlineData("g.h", "dom")]
    [InlineData("host.example", "dom")]
    [InlineData("a-b.c", "dom")]
    [InlineData("a_b", "sys")]
    [InlineData("1.2_3", "sys")]
    [InlineData("g:h", "sys")]
    [InlineData("", "sys")]
    public void AnIpNameIsAnAddressADomainNameOrASystemName(string name, string attr)
    {
        Assert.Equal(attr, Ndb.IpAttr(name));
    }

    [Fact]
    public void ReorderPutsTheTuplesLineFirstAndItFirstOnItsLine()
    {
        NdbTuple t = Entry(out NdbTuple[] x);
        Assert.Same(t, Ndb.Reorder(t, t));
        Assert.Equal("a=1 b=2\nc=3 d=4\ne=5", Print(t));

        t = Entry(out x);
        Assert.Equal("b=2 a=1\nc=3 d=4\ne=5", Print(Ndb.Reorder(t, x[1])));
        t = Entry(out x);
        Assert.Equal("c=3 d=4\ne=5\na=1 b=2", Print(Ndb.Reorder(t, x[2])));
        t = Entry(out x);
        Assert.Equal("d=4 c=3\ne=5\na=1 b=2", Print(Ndb.Reorder(t, x[3])));
        t = Entry(out x);
        Assert.Equal("e=5\na=1 b=2\nc=3 d=4", Print(Ndb.Reorder(t, x[4])));
    }

    [Fact]
    public void DedupTakesOutTheLaterOfEachAttrAndValue()
    {
        Assert.Equal("a=1 b=2", Print(Ndb.Dedup(Ndb.ParseLine("a=1 b=2 a=1\n"))));
        Assert.Equal("a=1 b=2 c=3", Print(Ndb.Dedup(Ndb.ParseLine("a=1 b=2 a=1 c=3\n"))));
        Assert.Equal("a=1 b=2 a=2", Print(Ndb.Dedup(Ndb.ParseLine("a=1 b=2 a=2 b=2\n"))));
        Assert.Equal("a=1\nc=3", Print(Ndb.Dedup(Ndb.Concatenate(Ndb.ParseLine("a=1\n"), Ndb.ParseLine("a=1 c=3\n")))));
        Assert.Equal("a=1 b=1", Print(Ndb.Dedup(Ndb.ParseLine("a=1 b=1\n"))));
        Assert.Null(Ndb.Dedup(null));
    }

    [Fact]
    public void DiscardTakesATupleOut()
    {
        NdbTuple t = Ndb.ParseLine("a=1 b=2 c=3\n")!;
        Assert.Equal("a=1 c=3", Print(Ndb.Discard(t, t.Entry!)));
        t = Ndb.ParseLine("a=1 b=2 c=3\n")!;
        NdbTuple a = t;
        Assert.Equal("b=2 c=3", Print(Ndb.Discard(t, a)));
        Assert.Null(a.Entry);
        t = Ndb.ParseLine("a=1 b=2 c=3\n")!;
        Assert.Equal("a=1 b=2", Print(Ndb.Discard(t, t.Entry!.Entry!)));
    }

    [Fact]
    public void SubstituteReplacesATupleWithAChainOnItsLine()
    {
        NdbTuple t = Ndb.ParseLine("a=1 b=2 c=3\n")!;
        Assert.Equal("a=1 x=9 y=8 c=3", Print(Ndb.Substitute(t, t.Entry!, Ndb.ParseLine("x=9 y=8\n"))));
        t = Ndb.ParseLine("a=1 b=2 c=3\n")!;
        NdbTuple a = t;
        Assert.Equal("x=9 b=2 c=3", Print(Ndb.Substitute(t, a, Ndb.ParseLine("x=9\n"))));
        Assert.Null(a.Entry);
        t = Ndb.ParseLine("a=1 b=2 c=3\n")!;
        Assert.Same(t, Ndb.Substitute(t, t, t));
        Assert.Equal("a=1 b=2 c=3", Print(t));
        t = Ndb.ParseLine("a=1 b=2 c=3\n")!;
        Assert.Equal("a=1 c=3", Print(Ndb.Substitute(t, t.Entry!, null)));
        t = Ndb.Concatenate(Ndb.ParseLine("a=1 b=2\n"), Ndb.ParseLine("c=3\n"))!;
        Assert.Equal("a=1 x=9\nc=3", Print(Ndb.Substitute(t, t.Entry!, Ndb.ParseLine("x=9\n"))));
    }

    [Fact]
    public void FindattrLooksOnTheLineFirstThenTheEntry()
    {
        NdbTuple t = Ndb.Concatenate(Ndb.ParseLine("a=1 b=2\n"), Ndb.ParseLine("b=3 c=4\n"))!;
        NdbTuple line2 = t.Entry!.Entry!;
        Assert.Equal("3", Ndb.FindAttr(t, line2, "b")!.Val);
        Assert.Equal("2", Ndb.FindAttr(t, t, "b")!.Val);
        Assert.Equal("1", Ndb.FindAttr(t, line2, "a")!.Val);
        Assert.Equal("4", Ndb.FindAttr(t, null, "c")!.Val);
        Assert.Null(Ndb.FindAttr(t, t, "d"));
        Assert.Null(Ndb.FindAttr(null, null, "a"));
    }

    [Fact]
    public void ConcatenateJoinsEntries()
    {
        NdbTuple b = Ndb.ParseLine("b=2\n")!;
        Assert.Same(b, Ndb.Concatenate(null, b));
        Assert.Equal("a=1 c=3\nb=2", Print(Ndb.Concatenate(Ndb.ParseLine("a=1 c=3\n"), b)));
    }

    [Theory]
    [InlineData("1.2.3.4", "4.3.2.1.in-addr.arpa")]
    [InlineData("10.200.0.255", "255.0.200.10.in-addr.arpa")]
    [InlineData("2001:db8::1f", "f.1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa")]
    [InlineData("x", "0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.ip6.arpa")]
    public void ThePtrNameOfAnAddressIsItsBytesOrNibblesReversed(string address, string name)
    {
        Assert.Equal(name, Ndb.MkPtrName(address));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0)]
    [InlineData(new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 1)]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0 }, 112)]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xf0, 0, 0 }, 108)]
    [InlineData(new byte[] { 0xff, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, 128)]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 }, 127)]
    [InlineData(new byte[] { 0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 2)]
    public void APrefixLengthIsWhereTheMasksLastBitIs(byte[] mask, int length)
    {
        Assert.Equal(length, Ndb.PrefixLen(mask));
    }

    // a=1 b=2 / c=3 d=4 / e=5, and its tuples in order.
    private static NdbTuple Entry(out NdbTuple[] tuples)
    {
        NdbTuple t = Ndb.Concatenate(Ndb.Concatenate(Ndb.ParseLine("a=1 b=2\n"), Ndb.ParseLine("c=3 d=4\n")), Ndb.ParseLine("e=5\n"))!;
        tuples = [.. t.Entries()];
        return t;
    }

    // %N for each line: a tuple whose line is not its entry ends its line.
    private static string Print(NdbTuple? t)
    {
        var s = new StringBuilder();
        for (; t is not null; t = t.Entry)
        {
            s.Append(t.Attr).Append('=').Append(Ndb.ValFmt(t.Val));
            if (t.Entry is not null)
            {
                s.Append(t.Line != t.Entry ? '\n' : ' ');
            }
        }

        return s.ToString();
    }
}
