using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// tail(1), as 9front's tail.c: counts in its 32-bit longs, its output through a Biobuf, and fatal
// giving whatever errstr holds.
public sealed class Tail
{
    private const int Bsize = Biobuf.Bsize;
    private const string Umsg = "usage: tail [-n N] [-c N] [-f] [-r] [+-N[bc][fr]] [file]";

    private readonly Process process;
    private readonly Biobuf bout;
    private long fend;
    private int count;
    private bool anycount;
    private int follow;
    private int file;
    private bool beg;
    private bool chars;
    private bool rev;

    private Tail(Process process) => (this.process, bout) = (process, new Biobuf(process, 1));

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var tail = new Tail(process);
        int i = 1;
        for (; i < argv.Count && argv[i] is ['-' or '+', ..]; i++)
        {
            string a = argv[i];
            if (await tail.GetnumberAsync(a))
            {
                await tail.SuffixAsync(a);
                continue;
            }

            if (a[0] == '-')
            {
                switch (a.Length > 1 ? a[1] : '\0')
                {
                    case 'c' or 'n':

                        // Stryker disable once Assignment : chars set a second time is set before a second count, which is refused
                        tail.chars |= a[1] == 'c';
                        if (await tail.GetnumberAsync(a[2..]))
                        {
                            continue;
                        }

                        if (i + 1 < argv.Count && await tail.GetnumberAsync(argv[i + 1]))
                        {
                            i++;
                            continue;
                        }

                        await tail.UsageAsync();
                        break;
                    case 'r':
                        tail.rev = true;
                        continue;
                    case 'f':

                        // Stryker disable once Update : follow is only ever asked whether it is 0
                        tail.follow++;
                        continue;
                    case '-':
                        i++;
                        break;
                }
            }

            break;
        }

        if (tail.rev && (tail.chars || tail.follow != 0 || tail.beg))
        {
            await tail.FatalAsync("incompatible options");
        }

        if (!tail.anycount)
        {
            tail.count = tail.rev ? int.MaxValue : 10;
        }

        // Stryker disable once Equality : a count of -1 skips no lines, as one of 0 does
        if (tail.beg && !tail.chars && tail.count > 0)
        {
            tail.count--;
        }

        if (i + 1 < argv.Count)
        {
            await tail.UsageAsync();
        }

        if (i < argv.Count)
        {
            try
            {
                tail.file = await process.OpenAsync(argv[i], NinePConstants.OREAD);
            }
            catch (SyscallException error)
            {
                Errstr.Set(process, error.Message);
                await tail.FatalAsync(argv[i]);
            }
        }

        await tail.RunAsync();
    }

    private async Task RunAsync()
    {
        bool seekable = await IsseekableAsync();
        if (!seekable && !beg)
        {
            await KeepAsync();
        }
        else if (!seekable || !chars)
        {
            await (beg ? SkipAsync() : ReverseAsync());
        }
        else
        {
            await TseekAsync(beg ? count : -count, beg ? 0 : 2);
            await CopyAsync();
        }

        if (follow != 0 && (seekable || fend == 0))
        {
            long? sb0 = null, sb1 = null;
            for (; ;)
            {
                sb0 = await TruncAsync(sb1, sb0);
                await CopyAsync();
                sb1 = await TruncAsync(sb0, sb1);

                // Stryker disable once Statement : a follow that does not wait between its reads prints the same bytes
                await process.SleepAsync(5000);
            }
        }

        await ExitsAsync(null);
    }

    // The file's length now, or what it was when it cannot be had; a file shorter than old was is
    // read again from its start.
    private async Task<long?> TruncAsync(long? old, long? was)
    {
        Stat d;
        try
        {
            d = await process.FStatAsync(file);
        }
        catch (SyscallException error)
        {
            // Stryker disable once Statement : errstr is not read after a failed fstat
            Errstr.Set(process, error.Message);
            return was;
        }

        long length = (long)d.Length;
        return length < (old ?? 0) ? await TseekAsync(0, 0) : length;
    }

    private async Task SuffixAsync(string s)
    {
        int p = 0;
        while (p < s.Length && "0123456789+-".Contains(s[p], StringComparison.Ordinal))
        {
            p++;
        }

        char c = p < s.Length ? s[p] : '\0';
        if (c == 'b' && (count = unchecked(count * 1024)) < 0)
        {
            await FatalAsync("too big");
        }

        // Stryker disable once Assignment : chars set a second time is set before a second count, which is refused
        chars |= c is 'b' or 'c';
        p += c is 'b' or 'c' or 'l' ? 1 : 0;
        switch (p < s.Length ? s[p] : '\0')
        {
            case 'r':
                rev = true;
                return;
            case 'f':

                // Stryker disable once Update : follow is only ever asked whether it is 0
                follow++;
                return;
            case '\0':
                return;
        }

        await UsageAsync();
    }

    // Read past the head of the file to find its tail.
    private async Task SkipAsync()
    {
        byte[] buf = new byte[Bsize];
        int n;
        if (chars)
        {
            for (; count > 0; count -= n)
            {
                if ((n = await TreadAsync(buf, 0, Math.Min(count, Bsize))) == 0)
                {
                    return;
                }
            }
        }
        else
        {
            int i = n = 0;
            while (count > 0)
            {
                if ((n = await TreadAsync(buf, 0, Bsize)) == 0)
                {
                    return;
                }

                for (i = 0; i < n && count > 0; i++)
                {
                    if (buf[i] == '\n')
                    {
                        count--;
                    }
                }
            }

            await TwriteAsync(buf, i, n - i);
        }

        await CopyAsync();
    }

    private async Task CopyAsync()
    {
        byte[] buf = new byte[Bsize];
        int n;
        while ((n = await TreadAsync(buf, 0, Bsize)) > 0)
        {
            await TwriteAsync(buf, 0, n);
            await bout.BflushAsync();
        }
    }

    // Read the whole file, keeping its tail.
    private async Task KeepAsync()
    {
        int len = 0, bufsiz = 0, j;
        byte[] buf = [];
        for (int n = 1; n != 0;)
        {
            // Stryker disable once Equality : the buffer grown a read sooner holds the same
            if (len + Bsize > bufsiz)
            {
                bufsiz += 2 * Bsize;
                Array.Resize(ref buf, bufsiz + 1);
            }

            for (; n != 0 && len < bufsiz; len += n)
            {
                n = await TreadAsync(buf, len, bufsiz - len);
            }

            // Stryker disable once Equality : a count as long as what is kept keeps all of it either way
            if (count >= len)
            {
                continue;
            }

            if (chars)
            {
                j = len - count;
            }
            else
            {
                // Stryker disable once Conditional : a last byte that is no newline is passed over by the loop either way
                j = buf[len - 1] == '\n' ? len - 1 : len;
                for (int k = 0; j > 0; j--)
                {
                    if (buf[j - 1] == '\n' && ++k >= count)
                    {
                        break;
                    }
                }
            }

            len -= j;
            Array.Copy(buf, j, buf, 0, len);
        }

        if (rev)
        {
            if (len > 0 && buf[len - 1] != '\n')
            {
                buf[len++] = (byte)'\n';
            }

            for (j = len - 1; j > 0; j--)
            {
                if (buf[j - 1] == '\n')
                {
                    await TwriteAsync(buf, j, len - j);

                    // Stryker disable once Equality,Statement : what is kept is no more than count lines, so the loop ends before count does
                    if (--count <= 0)
                    {
                        return;
                    }

                    len = j;
                }
            }
        }

        if (count > 0)
        {
            await TwriteAsync(buf, 0, len);
        }
    }

    // Count back from the end and print the tail of the file.
    private async Task ReverseAsync()
    {
        int len = 0, n = 0, bufsiz = 0;
        byte[] buf = [];
        long pos = await TseekAsync(0, 2);

        // Stryker disable once Boolean : after the first block, the line kept ends in the newline the first one ended in
        for (bool first = true; pos > 0 && count > 0; first = false)
        {
            n = (int)Math.Min(pos, Bsize);
            pos -= n;

            // Stryker disable once Equality : the buffer grown a block sooner holds the same
            if (len + n > bufsiz)
            {
                bufsiz += 2 * Bsize;
                Array.Resize(ref buf, bufsiz + 1);
            }

            Array.Copy(buf, 0, buf, n, len);
            len += n;
            await TseekAsync(pos, 0);
            if (await TreadAsync(buf, 0, n) != n)
            {
                await FatalAsync("length error");
            }

            if (first && buf[len - 1] != '\n')
            {
                buf[len++] = (byte)'\n';
            }

            for (n = len - 1; n > 0 && count > 0; n--)
            {
                if (buf[n - 1] == '\n')
                {
                    count--;
                    if (rev)
                    {
                        await TwriteAsync(buf, n, len - n);
                    }

                    len = n;
                }
            }
        }

        if (!rev)
        {
            await TseekAsync(n != 0 ? pos + n + 1 : 0, 0);
            await CopyAsync();
        }
        else if (count > 0)
        {
            await TwriteAsync(buf, 0, len);
        }
    }

    private async Task<long> TseekAsync(long o, int p)
    {
        if ((o = await SeekAsync(o, p)) == -1)
        {
            await FatalAsync(string.Empty);
        }

        return o;
    }

    // seek, or -1 with errstr set.
    private async Task<long> SeekAsync(long o, int p)
    {
        try
        {
            return await process.SeekAsync(file, o, p);
        }
        catch (SyscallException error)
        {
            Errstr.Set(process, error.Message);
            return -1;
        }
    }

    private async Task<int> TreadAsync(byte[] buf, int at, int n)
    {
        try
        {
            ReadOnlyMemory<byte> r = await process.ReadAsync(file, n);
            r.CopyTo(buf.AsMemory(at));
            return r.Length;
        }
        catch (SyscallException error)
        {
            Errstr.Set(process, error.Message);
            await FatalAsync(string.Empty);
            return default;
        }
    }

    private async Task TwriteAsync(byte[] s, int at, int n)
    {
        if (await bout.BwriteAsync(s.AsMemory(at, n)) != n)
        {
            await FatalAsync(string.Empty);
        }
    }

    private async Task<bool> GetnumberAsync(string s)
    {
        int p = s is ['-' or '+', ..] ? 1 : 0;
        if (p == s.Length || !char.IsAsciiDigit(s[p]))
        {
            return false;
        }

        // Stryker disable once Assignment : a second count is refused just after
        beg |= p == 1 && s[0] == '+';
        if (anycount)
        {
            await FatalAsync("excess option");
        }

        anycount = true;

        // atol, whose 32-bit long strtol holds at its largest, so tail.c's check that the count
        // is neither negative nor too big for an int never fails.
        count = Libc.Strtol(s[p..], 10, out _);
        return true;
    }

    private async Task<bool> IsseekableAsync()
    {
        fend = await SeekAsync(0, 2);

        // Stryker disable once Equality : an empty file is as empty read through as from its end
        return fend > 0 && await SeekAsync(0, 0) == 0;
    }

    private async Task FatalAsync(string s)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"tail: {s}: {Errstr.Get(process)}\n"));
        await ExitsAsync(s);
    }

    private async Task UsageAsync()
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes(Umsg + "\n"));
        await ExitsAsync("usage");
    }

    // exits, after libbio's atexit has flushed standard output.
    private async Task ExitsAsync(string? status)
    {
        await bout.BflushAsync();
        process.Exits(status);
    }
}
