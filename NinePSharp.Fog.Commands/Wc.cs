using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// wc(1), as 9front's wc.c, whose state tables are taken here by the class of each byte.
public sealed class Wc
{
    private const int IoUnit = 32768;

    // statesp, statewd, and statec2 to statec4, waiting for 1 to 3 more bytes of a rune.
    private const int Sp = 0;
    private const int Wd = 1;

    private readonly Process process;
    private readonly HashSet<char> p = [];
    private ulong nline;
    private ulong nword;
    private ulong nrune;
    private ulong nbadr;
    private ulong nchar;

    private Wc(Process process) => this.process = process;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var wc = new Wc(process);
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            if (!"lwrbc".Contains(c, StringComparison.Ordinal))
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"usage: {argv[0]} [-lwrbc] [file ...]\n"));
                process.Exits("usage");
            }

            wc.p.Add(c);
        }

        if (wc.p.Count == 0)
        {
            wc.p.UnionWith("lwc");
        }

        IReadOnlyList<string> files = arguments.Rest();
        string? status = null;
        if (files.Count == 0)
        {
            await wc.CountAsync(0, null);
        }

        ulong tnline = 0, tnword = 0, tnrune = 0, tnbadr = 0, tnchar = 0;
        foreach (string file in files)
        {
            int f;
            try
            {
                f = await process.OpenAsync(file, NinePConstants.OREAD);
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{file}: {error.Message}\n"));
                status = "can't open";
                continue;
            }

            await wc.CountAsync(f, file);
            tnline += wc.nline;
            tnword += wc.nword;
            tnrune += wc.nrune;
            tnbadr += wc.nbadr;
            tnchar += wc.nchar;
            await process.CloseAsync(f);
        }

        if (files.Count > 1)
        {
            await wc.ReportAsync(tnline, tnword, tnrune, tnbadr, tnchar, "total");
        }

        process.Exits(status);
    }

    private async Task ReportAsync(ulong line, ulong word, ulong rune, ulong badr, ulong chars, string? fname)
    {
        var counts = new List<string>();
        foreach ((char c, ulong n) in new[] { ('l', line), ('w', word), ('r', rune), ('b', badr), ('c', chars) })
        {
            if (p.Contains(c))
            {
                counts.Add(n.ToString(CultureInfo.InvariantCulture).PadLeft(7));
            }
        }

        string text = string.Join(' ', counts);
        await process.WriteAsync(1, Encoding.UTF8.GetBytes(fname is null ? $"{text}\n" : $"{text} {fname}\n"));
    }

    private async Task CountAsync(int f, string? name)
    {
        (nline, nword, nrune, nbadr, nchar) = (0, 0, 0, 0, 0);
        int state = Sp;
        try
        {
            for (ReadOnlyMemory<byte> buf; !(buf = await process.ReadAsync(f, IoUnit)).IsEmpty;)
            {
                nchar += (ulong)buf.Length;
                nrune += (ulong)buf.Length;
                foreach (byte b in buf.Span)
                {
                    state = Next(state, b);
                }
            }
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes(name is null ? $"{error.Message}\n" : $"{name}: {error.Message}\n"));
        }

        if (state > Wd)
        {
            nbadr++;
        }

        await ReportAsync(nline, nword, nrune, nbadr, nchar, name);
    }

    // The state after the byte, counting what the tables' action for it counts.
    private int Next(int state, byte b)
    {
        bool pending = state > Wd;
        switch (b)
        {
            case (byte)' ' or (byte)'\t' or (byte)'\n':
                nline += b == '\n' ? 1u : 0u;
                nbadr += pending ? 1u : 0u;
                return Sp;
            case >= 0x80 and <= 0xBF when pending:
                nrune--;
                return state - 1;
        }

        nword += state == Sp ? 1u : 0u;
        nbadr += pending || b is >= 0x80 and <= 0xBF or >= 0xF8 ? 1u : 0u;
        return b switch
        {
            >= 0xC0 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF7 => 4,
            _ => Wd,
        };
    }
}
