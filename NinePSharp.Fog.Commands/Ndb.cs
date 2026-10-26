using System.Net;
using System.Net.Sockets;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// libndb, as 9front's: a database of files of entries, searched for attr=val in file order. Fog
// has no ndb/mkhash, so no file has a hash file and each is searched from its start, as libndb
// searches one without; nor is a search's answer cached, which only saves reading the file again.
// Only what Fog's programs call is here: ndbipinfo leaves an @ attribute's names to its caller, as
// cs resolves them itself.
public sealed class Ndb
{
    public const int Alen = 32;
    public const string DefaultFile = "/lib/ndb/local";

    private const ulong Ffound = 1;
    private const ulong Fignore = 2;

    private byte[] contents = [];
    private Qid qid;

    private Ndb(Process process, string file)
    {
        Process = process;
        File = file;
    }

    // The process the files are read in; a child that goes on with the database once its parent
    // has gone takes it over, as a forked process keeps its parent's descriptors.
    public Process Process { get; set; }

    public Ndb? Next { get; set; }

    public string File { get; }

    // ndbopen: the file and each file its database entry names, in order; the default file may
    // be moved to where its own entry puts it. Null, and why, when the first cannot be opened.
    public static async Task<(Ndb? Db, string? Error)> OpenAsync(Process process, string? file)
    {
        file ??= DefaultFile;
        var db = new Ndb(process, file);
        if (await db.ReopenAsync() is { } error)
        {
            return (null, error);
        }

        Ndb first = db, last = db;
        var s = new Ndbs();
        NdbTuple? t = await db.SearchAsync(s, "database", string.Empty);
        if (t is null)
        {
            return (db, null);
        }

        foreach (NdbTuple nt in t.Entries())
        {
            if (nt.Attr != "file")
            {
                continue;
            }

            if (nt.Val == file)
            {
                // The default file may be reordered in the list.
                if (first.Next is not null && first.File == file)
                {
                    db = first;
                    first = first.Next;
                    last.Next = db;
                    db.Next = null;
                    last = db;
                }

                continue;
            }

            var opened = new Ndb(process, nt.Val);
            if (await opened.ReopenAsync() is null)
            {
                last.Next = opened;
                last = opened;
            }
        }

        return (first, null);
    }

    // ndbcat: b after the last of a.
    public static Ndb Cat(Ndb a, Ndb? b)
    {
        Ndb last = a;
        while (last.Next is not null)
        {
            last = last.Next;
        }

        last.Next = b;
        return a;
    }

    // ndbfindattr: the tuple of the attribute, first on the line, then anywhere in the entry.
    public static NdbTuple? FindAttr(NdbTuple? entry, NdbTuple? line, string attr)
    {
        for (NdbTuple? nt = line; nt is not null;)
        {
            if (nt.Attr == attr)
            {
                return nt;
            }

            nt = nt.Line;
            if (nt == line)
            {
                break;
            }
        }

        return entry?.Entries().FirstOrDefault(nt => nt.Attr == attr);
    }

    // ndbconcatenate: b after the last tuple of a.
    public static NdbTuple? Concatenate(NdbTuple? a, NdbTuple? b)
    {
        if (a is null)
        {
            return b;
        }

        a.Entries().Last().Entry = b;
        return a;
    }

    // ndbreorder: x's line made first in the entry, and x first on its line.
    public static NdbTuple Reorder(NdbTuple t, NdbTuple x)
    {
        // The end of x's line.
        NdbTuple last = x;
        while (last.Line == last.Entry)
        {
            last = last.Line!;
        }

        // Rotate to make this line first.
        if (last.Line != t)
        {
            NdbTuple nt = t;
            while (nt.Entry != last.Line)
            {
                nt = nt.Entry!;
            }

            nt.Entry = null;
            nt = last;
            while (nt.Entry is not null)
            {
                nt = nt.Entry;
            }

            nt.Entry = t;
        }

        // Rotate the line to make x first.
        if (x != last.Line)
        {
            NdbTuple prev = last;
            while (prev.Line != x)
            {
                prev = prev.Line!;
            }

            NdbTuple? detached = last.Entry;
            last.Entry = last.Line;
            prev.Entry = detached;
        }

        return x;
    }

    // ndbdedup: the second and later of each attr=val taken out.
    public static NdbTuple? Dedup(NdbTuple? t)
    {
        for (NdbTuple? nt = t; nt is not null; nt = nt.Entry)
        {
            NdbTuple last = nt;
            for (NdbTuple? tt = nt.Entry; tt is not null; tt = last.Entry)
            {
                if (nt.Attr != tt.Attr || nt.Val != tt.Val)
                {
                    last = tt;
                    continue;
                }

                if (last.Line == tt)
                {
                    last.Line = tt.Line;
                }

                last.Entry = tt.Entry;
                tt.Entry = null;
            }
        }

        return t;
    }

    // ndbdiscard: a taken out of t.
    public static NdbTuple? Discard(NdbTuple? t, NdbTuple a)
    {
        for (NdbTuple? nt = t; nt is not null; nt = nt.Entry)
        {
            if (nt.Line == a)
            {
                nt.Line = a.Line;
            }

            if (nt.Entry == a)
            {
                nt.Entry = a.Entry;
            }
        }

        if (t == a)
        {
            t = a.Entry;
        }

        a.Entry = null;
        return t;
    }

    // ndbsubstitute: a in t replaced by b, whose line structure is lost.
    public static NdbTuple? Substitute(NdbTuple? t, NdbTuple a, NdbTuple? b)
    {
        if (a == b)
        {
            return t;
        }

        if (b is null)
        {
            return Discard(t, a);
        }

        for (NdbTuple? nt = t; nt is not null; nt = nt.Entry)
        {
            if (nt.Line == a)
            {
                nt.Line = b;
            }

            if (nt.Entry == a)
            {
                nt.Entry = b;
            }
        }

        NdbTuple end = b;
        while (end.Entry is not null)
        {
            end.Line = end.Entry;
            end = end.Entry;
        }

        end.Line = a.Line;
        end.Entry = a.Entry;
        a.Entry = null;
        return a == t ? b : t;
    }

    // ipattr: the attribute an ip name is an address of: ip, dom or sys.
    public static string IpAttr(string name)
    {
        bool dot = false, alpha = false, colon = false, hex = false;
        foreach (char c in name)
        {
            if (char.IsAsciiDigit(c))
            {
                continue;
            }

            if (char.IsAsciiHexDigit(c))
            {
                hex = true;
            }
            else if (char.IsAsciiLetter(c) || c == '-')
            {
                alpha = true;
            }
            else if (c == '.')
            {
                dot = true;
            }
            else if (c == ':')
            {
                colon = true;
            }
            else
            {
                return "sys";
            }
        }

        if (alpha)
        {
            return dot ? "dom" : "sys";
        }

        return colon || (dot && !hex) ? "ip" : "sys";
    }

    // %$: a value quoted when it holds white space or a #.
    public static string ValFmt(string? s)
    {
        s ??= string.Empty;
        return s.Any(c => c is ' ' or '\t' or '\r' or '#') ? $"\"{s}\"" : s;
    }

    // _ndbparseline: the tuples of a line ending in a newline, joined as an entry and a ring.
    public static NdbTuple? ParseLine(string line)
    {
        NdbTuple? first = null, last = null;
        int cp = 0;
        while (line[cp] is not ('#' or '\n'))
        {
            if (ParseTuple(line, ref cp) is not { } t)
            {
                break;
            }

            if (first is null)
            {
                first = t;
            }
            else
            {
                last!.Line = t;
                last.Entry = t;
            }

            last = t;
        }

        if (last is not null)
        {
            last.Line = first;
        }

        return first;
    }

    // dnsquery: the tuples of dns's answers of the name and type, or null and the error dns gives.
    // Fog has no ndb/dns, so the host's resolver answers, as dns would: dom=name ip=address for each
    // address of type ip or ipv6, and ptr=name dom=name for a ptr.
    public static async Task<(NdbTuple? Answer, string? Error)> DnsQueryAsync(Process process, string val, string type)
    {
        // For a null address dnsquery gives up early, setting no error.
        if (val is "::" or "0.0.0.0")
        {
            return (null, null);
        }

        try
        {
            var lines = new List<string>();
            if (type == "ptr")
            {
                // As mkptrname does, an address that does not parse is ::.
                byte[] a = new byte[Ip.AddrLen];
                Ip.Parse(a, val);
                lines.Add($"ptr={MkPtrName(val)} dom={await process.HostAsync(host => host.ReverseAsync(new IPAddress(Ip.IsV4(a) ? a[Ip.V4Off..] : a)))}");
            }
            else if (type is "ip" or "ipv6")
            {
                AddressFamily family = type == "ip" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                foreach (IPAddress address in await process.HostAsync(host => host.ResolveAsync(val, family)))
                {
                    byte[] ip = new byte[Ip.AddrLen];
                    Ip.Parse(ip, address.ToString());
                    lines.Add($"dom={val} ip={Ip.Format(ip)}");
                }
            }

            if (lines.Count == 0)
            {
                return (null, "dns: resource does not exist; negrcode 0 (ok)");
            }

            NdbTuple? first = null;
            foreach (string line in lines.Distinct())
            {
                first = Concatenate(first, ParseLine(line + "\n"));
            }

            return (first, null);
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.HostNotFound)
        {
            return (null, "dns: name does not exist");
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.NoData)
        {
            return (null, "dns: resource does not exist; negrcode 0 (ok)");
        }
        catch (Exception error) when (error is SocketException or ArgumentException)
        {
            return (null, "dns: dns failure");
        }
    }

    // csgetvalue: the value of rattr in what cs gives for the entries holding attr=val, or null.
    public static async Task<string?> CsGetValueAsync(Process process, string netroot, string attr, string val, string rattr)
        => (await CsGetValuesAsync(process, netroot, attr, val, rattr)).Value;

    // csgetvalue with its tuples: the first value of rattr, and every tuple of every line cs gave,
    // joined as one entry.
    public static async Task<(string? Value, NdbTuple? Tuples)> CsGetValuesAsync(Process process, string? netroot, string attr, string val, string rattr)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync(netroot is null ? "/net/cs" : $"{netroot}/cs", NinePConstants.ORDWR | NinePConstants.OCEXEC);
        }
        catch (SyscallException)
        {
            return (null, null);
        }

        try
        {
            try
            {
                await process.WriteAsync(fd, Encoding.UTF8.GetBytes($"!{attr}={Quote(val)} {rattr}=*"));
            }
            catch (SyscallException)
            {
                return (null, null);
            }

            await process.SeekAsync(fd, 0, 0);
            string? rv = null;
            NdbTuple? first = null, last = null;
            for (ReadOnlyMemory<byte> line; !(line = await process.ReadAsync(fd, 1022)).IsEmpty;)
            {
                if (ParseLine(Encoding.UTF8.GetString(line.Span) + "\n") is not { } t)
                {
                    continue;
                }

                if (last is null)
                {
                    first = t;
                }
                else
                {
                    last.Entry = t;
                }

                foreach (NdbTuple e in t.Entries())
                {
                    last = e;
                    rv ??= e.Attr == rattr ? e.Val : null;
                }
            }

            return (rv, first);
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }

    // mkptrname: the in-addr.arpa or ip6.arpa name of an address.
    public static string MkPtrName(string ip)
    {
        byte[] a = new byte[Ip.AddrLen];
        Ip.Parse(a, ip);
        if (Ip.IsV4(a))
        {
            return $"{a[15]}.{a[14]}.{a[13]}.{a[12]}.in-addr.arpa";
        }

        var buf = new StringBuilder();
        for (int i = Ip.AddrLen - 1; i >= 0; i--)
        {
            buf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{a[i] % 16:x}.{a[i] / 16:x}.");
        }

        return buf + "ip6.arpa";
    }

    // ndbsearch: the first entry of the database holding attr=val.
    public async Task<NdbTuple?> SearchAsync(Ndbs s, string attr, string val)
    {
        await RefreshAsync();
        s.Db = this;
        s.Ptr = 0;
        s.T = null;
        return await SnextAsync(s, attr, val);
    }

    // ndbsnext: the next entry holding attr=val, through the files that follow.
    public async Task<NdbTuple?> SnextAsync(Ndbs s, string attr, string val)
    {
        Ndb db = s.Db!;
        while (s.Ptr >= 0)
        {
            (NdbTuple? t, long next) = db.Parse(s.Ptr);
            s.Ptr = next;
            if (t is null)
            {
                break;
            }

            if ((s.T = t.Entries().FirstOrDefault(nt => nt.Attr == attr && nt.Val == val)) is not null)
            {
                return t;
            }
        }

        return db.Next is null ? null : await db.Next.SearchAsync(s, attr, val);
    }

    // ndbgetvalue: the value of rattr in the first entry holding attr=val that has one.
    public async Task<(string? Value, NdbTuple? Entry)> GetValueAsync(Ndbs? s, string attr, string val, string rattr)
    {
        s ??= new Ndbs();
        for (NdbTuple? t = await SearchAsync(s, attr, val); t is not null; t = await s.Db!.SnextAsync(s, attr, val))
        {
            if (FindAttr(t, s.T, rattr) is { } nt)
            {
                return (nt.Val, t);
            }
        }

        return (null, null);
    }

    // ndbreopen: the file read again, or why it cannot be.
    public async Task<string?> ReopenAsync()
    {
        try
        {
            int fd = await Process.OpenAsync(File, NinePConstants.OREAD);
            try
            {
                qid = (await Process.FStatAsync(fd)).Qid;
                var all = new List<byte>();
                for (ReadOnlyMemory<byte> block; !(block = await Process.ReadAsync(fd, 8192)).IsEmpty;)
                {
                    all.AddRange(block.Span);
                }

                contents = [.. all];
                return null;
            }
            finally
            {
                await Process.CloseAsync(fd);
            }
        }
        catch (SyscallException error)
        {
            return error.Message;
        }
    }

    // ndbipinfo: each wanted attribute for the system, from its entries and then from each subnet
    // containing it, the closest first; an ip attribute with no entry is taken as the address.
    public async Task<NdbTuple?> IpInfoAsync(string attr, string val, IReadOnlyList<string> alist)
    {
        var s = new Ndbs();
        (string? ip, NdbTuple? nt) = await GetValueAsync(s, attr, val, "ip");
        NdbTuple? t = null;
        if (ip is null)
        {
            if (attr != "ip")
            {
                return null;
            }

            nt = new NdbTuple("ip", val);
            nt.Line = nt;
            t = await NetinfoAsync(nt, alist);
        }
        else
        {
            do
            {
                nt = Reorder(nt!, s.T!);
                t = Concatenate(t, await NetinfoAsync(nt, alist));
            }
            while ((nt = await s.Db!.SnextAsync(s, attr, val)) is not null);
        }

        return Dedup(t);
    }

    // The length of a mask's prefix: where its last bit is.
    internal static int PrefixLen(byte[] ip)
    {
        for (int y = Ip.AddrLen - 1; y >= 0; y--)
        {
            if (ip[y] != 0)
            {
                return (y * 8) + 8 - System.Numerics.BitOperations.TrailingZeroCount(ip[y]);
            }
        }

        return 0;
    }

    // %q: a value tokenize takes as one word. %q quotes only a value that needs it, but cs takes the
    // query apart with tokenize and logs none that starts with !, so quoting every one asks the same.
    private static string Quote(string s) => $"'{s.Replace("'", "''", StringComparison.Ordinal)}'";

    private static bool IsWhite(char c) => c is ' ' or '\t' or '\r';

    // _ndbparsetuple: attr, or attr=value, or attr="value with white space"; null at a comment or
    // the line's end.
    private static NdbTuple? ParseTuple(string line, ref int cp)
    {
        while (IsWhite(line[cp]))
        {
            cp++;
        }

        if (line[cp] is '#' or '\n')
        {
            return null;
        }

        int p = cp;
        while (line[cp] is not ('=' or '\n') && !IsWhite(line[cp]))
        {
            cp++;
        }

        var t = new NdbTuple(line[p..Math.Min(cp, p + Alen - 1)], string.Empty);
        while (IsWhite(line[cp]))
        {
            cp++;
        }

        if (line[cp] == '=')
        {
            cp++;
            t.Val = ParseValue(line, ref cp);
        }

        return t;
    }

    // A tuple's value after its =: in quotes, to the closing one, or to white space; none at a
    // comment.
    private static string ParseValue(string line, ref int cp)
    {
        int p;
        if (line[cp] == '"')
        {
            p = ++cp;
            while (line[cp] is not ('\n' or '"'))
            {
                cp++;
            }

            string quoted = line[p..cp];
            if (line[cp] == '"')
            {
                cp++;
            }

            return quoted;
        }

        if (line[cp] == '#')
        {
            return string.Empty;
        }

        p = cp;
        while (line[cp] != '\n' && !IsWhite(line[cp]))
        {
            cp++;
        }

        return line[p..cp];
    }

    // filter: only the tuples the filter wants.
    private static NdbTuple? Filter(NdbTuple? t, NdbTuple f)
    {
        for (NdbTuple? nt = t, next; nt is not null; nt = next)
        {
            next = nt.Entry;
            NdbTuple? nf = f.Entries().FirstOrDefault(x => (x.Ptr & Fignore) == 0 && x.Attr == nt.Attr);
            if (nf is null)
            {
                t = Discard(t, nt);
                continue;
            }

            nf.Ptr |= Ffound;
        }

        foreach (NdbTuple nf in f.Entries())
        {
            if ((nf.Ptr & Ffound) != 0)
            {
                nf.Ptr = Fignore;
            }
        }

        return t;
    }

    private static NdbTuple? MkFilter(IReadOnlyList<string> alist)
    {
        NdbTuple? first = null;
        foreach (string a in alist)
        {
            var t = new NdbTuple(a, default!);
            first = Concatenate(first, t);
        }

        return first;
    }

    private static bool FilterComplete(NdbTuple f) => f.Entries().All(x => (x.Ptr & Fignore) != 0);

    // ndbparse: the entry at the offset, its lines up to one that starts at the left margin; a
    // last line with no newline is not read. The offset after it is returned.
    private (NdbTuple? Entry, long Next) Parse(long offset)
    {
        NdbTuple? first = null;
        long at = offset;
        for (int end; (end = Array.IndexOf(contents, (byte)'\n', (int)at)) >= 0;)
        {
            string line = Encoding.UTF8.GetString(contents, (int)at, end - (int)at + 1);
            if (first is not null && !IsWhite(line[0]) && line[0] != '#')
            {
                break;
            }

            at = end + 1;
            first = Concatenate(first, ParseLine(line));
        }

        return (first, at);
    }

    // Stryker disable once Block : a stat that fails has the file read again, as one that changed would, and an unchanged file reads as it did
    private async Task<Stat?> StatAsync()
    {
        try
        {
            return await Process.StatAsync(File);
        }
        catch (SyscallException)
        {
            // A file gone is read again, which fails, so its contents are kept.
        }

        return null;
    }

    // hfopen's check: a file changed since it was read is read again.
    private async Task RefreshAsync()
    {
        if (await StatAsync() is not { } d || d.Qid.Path != qid.Path || d.Qid.Version != qid.Version)
        {
            await ReopenAsync();
        }
    }

    // netinfo: the wanted attributes of the entry, then of each containing subnet, closest first.
    private async Task<NdbTuple?> NetinfoAsync(NdbTuple t, IReadOnlyList<string> alist)
    {
        byte[] ip = new byte[Ip.AddrLen];
        if (FindAttr(t, t, "ip") is not { } nt || Ip.Parse(ip, nt.Val) == -1)
        {
            return null;
        }

        // Each caller wants at least one attribute.
        NdbTuple f = MkFilter(alist)!;
        NdbTuple? result = Filter(t, f);
        byte[] net = [.. ip];
        int prefix, smallest;
        if (Ip.IsV4(ip))
        {
            (prefix, smallest) = (127, 100);
        }
        else
        {
            // In v6 the last 8 bytes have no structure, so the /64 of the address is looked for
            // first, whether or not its last bit is set.
            (prefix, smallest) = (63, 2);
            Array.Clear(net, 8, 8);
            if (!FilterComplete(f))
            {
                result = Concatenate(result, await SubnetAsync(net, f, 64));
            }
        }

        // For a containing network, turn off each lower bit in turn and look for a network of that
        // address with a shorter mask.
        for (; prefix >= smallest && !FilterComplete(f); prefix--)
        {
            byte bit = (byte)(1 << (7 - (prefix % 8)));

            // Stryker disable once Bitwise : a network looked up at a bit already clear was looked up before, at a longer mask, which found all it would and more
            if ((net[prefix / 8] & bit) != 0)
            {
                net[prefix / 8] &= (byte)~bit;
                result = Concatenate(result, await SubnetAsync(net, f, prefix));
            }
        }

        // An ipmask still wanted is made up.
        if (FindAttr(f, f, "ipmask") is { } mask && (mask.Ptr & Fignore) == 0)
        {
            var made = new NdbTuple("ipmask", Ip.FormatMask(Ip.DefMask(ip)));
            made.Line = made;
            result = Concatenate(result, made);
        }

        // ndbline: each tuple a line of its own ring.
        for (NdbTuple? x = result; x is not null; x = x.Entry)
        {
            x.Line = x.Entry;

            // Stryker disable once Statement,Block : the last tuple's line closes the ring, which only printing the entry reads, and that ends the line at the entry's end either way
            if (x.Entry is null)
            {
                x.Line = result;
            }
        }

        return result;
    }

    // subnet: the entries of the network that contain it, longest prefix first, filtered.
    private async Task<NdbTuple?> SubnetAsync(byte[] net, NdbTuple f, int prefix)
    {
        var at = new NdbTuple?[129];
        string netstr = Ip.Format(net);
        var s = new Ndbs();
        for (NdbTuple? nt = await SearchAsync(s, "ip", netstr); nt is not null; nt = await s.Db!.SnextAsync(s, "ip", netstr))
        {
            if (FindAttr(nt, nt, "ipnet") is null)
            {
                continue;
            }

            byte[] mask = new byte[Ip.AddrLen];
            if (FindAttr(nt, nt, "ipmask") is not { } xt || Ip.ParseMask(mask, xt.Val, Ip.IsV4(net)) == -1)
            {
                Ip.DefMask(net).CopyTo(mask, 0);
            }

            int nprefix = PrefixLen(mask);
            if (nprefix <= prefix && at[nprefix] is null)
            {
                at[nprefix] = nt;
            }
        }

        NdbTuple? t = null;
        for (; prefix >= 0; prefix--)
        {
            if (at[prefix] is { } found)
            {
                t = Concatenate(t, Filter(found, f));
            }
        }

        return t;
    }
}
