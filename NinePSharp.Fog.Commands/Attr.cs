using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// libauth's attributes, as attr.c keeps them: a list of name=val, name? and, inside factotum, the
// defaults matching uses; a list is the first of its attributes, or null for none.
public sealed class Attr(AttrType type, string name, string val, Attr? next)
{
    public AttrType Type { get; set; } = type;

    public string Name { get; } = name;

    public string Val { get; set; } = val;

    public Attr? Next { get; set; } = next;

    public IEnumerable<Attr> All
    {
        get
        {
            for (Attr? a = this; a is not null; a = a.Next)
            {
                yield return a;
            }
        }
    }

    // _attrfmt, %A: name=val for a value, name? for a query, each quoted as %q quotes.
    public static string Format(Attr? a)
    {
        var text = new StringBuilder();
        for (; a is not null; a = a.Next)
        {
            text.Append(text.Length == 0 ? string.Empty : " ").Append(a.Type == AttrType.Query ? $"{Libc.Quote(a.Name)}?" : $"{Libc.Quote(a.Name)}={Libc.Quote(a.Val)}");
        }

        return text.ToString();
    }

    public static Attr? Copy(Attr? a)
    {
        Attr? first = null, last = null;
        for (; a is not null; a = a.Next)
        {
            var copy = new Attr(a.Type, a.Name, a.Val, null);
            (first, last) = last is null ? (copy, copy) : (first, last.Next = copy);
        }

        return first;
    }

    // _delattr: every attribute of the name taken out.
    public static Attr? Delete(Attr? a, string name)
    {
        while (a is not null && a.Name == name)
        {
            a = a.Next;
        }

        for (Attr? l = a; l?.Next is not null;)
        {
            if (l.Next.Name == name)
            {
                l.Next = l.Next.Next;
            }
            else
            {
                l = l.Next;
            }
        }

        return a;
    }

    // _findattr: the first of the name with a value; a query is not one.
    public static Attr? Find(Attr? a, string name)
    {
        for (; a is not null; a = a.Next)
        {
            if (a.Name == name && a.Type != AttrType.Query)
            {
                return a;
            }
        }

        return null;
    }

    public static string? StrFind(Attr? a, string name) => Find(a, name)?.Val;

    // _parseattr: the tokens of s, name=val, name? or a bare name with an empty value, and then a
    // query dropped where the name has a value too.
    public static Attr? Parse(string s)
    {
        Attr? a = null;
        List<string> tokens = Process.Tokenize(s, 256);
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            string t = tokens[i];
            int eq = t.IndexOf('=', StringComparison.Ordinal);
            a = eq >= 0 ? new Attr(AttrType.Nameval, t[..eq], t[(eq + 1)..], a)
                : t.EndsWith('?') ? new Attr(AttrType.Query, t[..^1], string.Empty, a)
                : new Attr(AttrType.Nameval, t, string.Empty, a);
        }

        return Clean(a);
    }

    private static Attr? Clean(Attr? a)
    {
        Attr? all = a;
        while (a is not null && a.Type == AttrType.Query && Find(all, a.Name) is not null)
        {
            a = a.Next;
        }

        for (Attr? l = a; l?.Next is not null;)
        {
            if (l.Next.Type == AttrType.Query && Find(all, l.Next.Name) is not null)
            {
                l.Next = l.Next.Next;
            }
            else
            {
                l = l.Next;
            }
        }

        return a;
    }
}
