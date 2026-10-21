using System.Runtime.InteropServices;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using Plan9.Utf;

namespace NinePSharp.Fog.Commands;

// read(1), as 9front's read.c: a line, n lines, every line, n bytes or n runes, of the input or
// of each file; eof when the first read gives nothing.
public static class Read
{
    private const int Buf = 8 * 1024;

    // runes reads up to UTFmax short of its buffer, leaving room to finish a rune cut short.
    private const int RuneRead = Buf - Utf.UtfMax;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var reader = new Reader(process);
        Func<int, string, Task> proc = reader.LinesAsync;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'c':
                    reader.Count = Libc.Strtoll(await arguments.EargfAsync(() => UsageAsync(process)), 10, out _);
                    proc = reader.CharsAsync;
                    break;
                case 'r':
                    reader.Count = Libc.Strtoll(await arguments.EargfAsync(() => UsageAsync(process)), 10, out _);
                    proc = reader.RunesAsync;
                    break;
                case 'n':
                    reader.Count = Libc.Strtol(await arguments.EargfAsync(() => UsageAsync(process)), 10, out _);
                    break;
                case 'm':
                    reader.Multi = true;
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        IReadOnlyList<string> files = arguments.Rest();
        if (files.Count == 0)
        {
            await proc(0, "<stdin>");
        }

        foreach (string file in files)
        {
            int fd;
            try
            {
                fd = await process.OpenAsync(file, NinePConstants.OREAD);
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"read: can't open {file}: {error.Message}\n"));
                process.Exits("open");
                return;
            }

            await proc(fd, file);
            await process.CloseAsync(fd);
        }

        process.Exits(reader.Status);
    }

    private static async Task<string> UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: read [ -m | -n nlines | -c nbytes | -r nrunes ] [ file ... ]\n"u8.ToArray());
        process.Exits("usage");
        return default!;
    }

    private sealed class Reader(Process process)
    {
        public long Count { get; set; }

        public bool Multi { get; set; }

        public string? Status { get; private set; }

        public async Task LinesAsync(int fd, string file)
        {
            do
            {
                if (await LineAsync(fd, file) == 0)
                {
                    break;
                }
            }
            while (Multi || --Count > 0);
        }

        public async Task CharsAsync(int fd, string file)
        {
            for (long m = 0; m < Count;)
            {
                ReadOnlyMemory<byte> block = await ReadAsync(fd, (int)Math.Min(Buf, Count - m), file);
                if (block.IsEmpty)
                {
                    if (m == 0)
                    {
                        Status = "eof";
                    }

                    break;
                }

                await process.WriteAsync(1, block);
                m += block.Length;
            }
        }

        // runes: a read's whole runes are counted, and one cut short is finished a byte at a time.
        public async Task RunesAsync(int fd, string file)
        {
            while (Count > 0)
            {
                var buf = new List<byte>((await ReadAsync(fd, (int)Math.Min(Count, RuneRead), file)).ToArray());
                if (buf.Count == 0)
                {
                    break;
                }

                int s = 0;
                for (; Full(buf, s); s += Width(buf, s))
                {
                    Count--;
                }

                if (s < buf.Count)
                {
                    while (!Full(buf, s))
                    {
                        ReadOnlyMemory<byte> one = await ReadAsync(fd, 1, file);
                        if (one.IsEmpty)
                        {
                            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"warning: partial rune at end of {file}: \n"));
                            await process.WriteAsync(1, buf.ToArray());
                            return;
                        }

                        buf.Add(one.Span[0]);
                    }

                    Count--;
                }

                await process.WriteAsync(1, buf.ToArray());
            }
        }

        private static bool Full(List<byte> buf, int s) => Utf.FullRune(CollectionsMarshal.AsSpan(buf)[s..]);

        private static int Width(List<byte> buf, int s) => Utf.CharToRune(out _, CollectionsMarshal.AsSpan(buf)[s..]);

        // line: up to and including a newline, a byte at a time.
        private async Task<int> LineAsync(int fd, string file)
        {
            var buf = new List<byte>();
            while (true)
            {
                ReadOnlyMemory<byte> c = await ReadAsync(fd, 1, file);
                if (c.IsEmpty)
                {
                    if (buf.Count == 0)
                    {
                        Status = "eof";
                    }

                    break;
                }

                buf.Add(c.Span[0]);
                if (c.Span[0] == '\n')
                {
                    break;
                }
            }

            if (buf.Count > 0)
            {
                await process.WriteAsync(1, buf.ToArray());
            }

            return buf.Count;
        }

        private async Task<ReadOnlyMemory<byte>> ReadAsync(int fd, int count, string file)
        {
            try
            {
                return await process.ReadAsync(fd, count);
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"read: error reading {file}: {error.Message}\n"));
                process.Exits("read error");
                return ReadOnlyMemory<byte>.Empty;
            }
        }
    }
}
