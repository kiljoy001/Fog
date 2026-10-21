using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// ndb/csquery, as 9front's csquery.c: each address written to cs, and each of cs's answers printed
// on a line of its own, then an empty line; with no addresses, those of the standard input's lines,
// after a prompt. -s prints nothing and only sets the status.
public sealed class Csquery(string argv0)
{
    private string server = "/net/cs";
    private string? status;
    private bool statusonly;

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Csquery(argv[0]).RunAsync(process, argv);

    private async Task RunAsync(Process process, IReadOnlyList<string> argv)
    {
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            if (c == 's')
            {
                statusonly = true;
            }
            else
            {
                await process.WriteAsync(2, "usage: ndb/csquery [/net/cs [addr...]]\n"u8.ToArray());
                process.Exits("usage");
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count > 0)
        {
            server = args[0];
        }

        if (args.Count > 1)
        {
            foreach (string addr in args.Skip(1))
            {
                await QueryAsync(process, addr);
            }

            process.Exits(status);
        }

        // Brdline: each whole line; a last one without a newline is not read.
        var pending = new List<byte>();
        while (true)
        {
            await process.WriteAsync(2, "> "u8.ToArray());
            int nl;
            while ((nl = pending.IndexOf((byte)'\n')) < 0)
            {
                ReadOnlyMemory<byte> block = await process.ReadAsync(0, 8192);
                if (block.IsEmpty)
                {
                    process.Exits(null);
                }

                pending.AddRange(block.Span);
            }

            string line = Encoding.UTF8.GetString([.. pending[..nl]]);
            pending.RemoveRange(0, nl + 1);
            await QueryAsync(process, line);
        }
    }

    private async Task QueryAsync(Process process, string addr)
    {
        int fd;
        try
        {
            fd = await process.OpenAsync(server, NinePConstants.ORDWR);
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv0}: cannot open {server}: {error.Message}\n"));
            process.Exits($"cannot open {server}: {error.Message}");
            return;
        }

        try
        {
            try
            {
                await process.WriteAsync(fd, Encoding.UTF8.GetBytes(addr));
            }
            catch (SyscallException error)
            {
                if (!statusonly)
                {
                    await process.WriteAsync(2, Encoding.UTF8.GetBytes($"translating {addr}: {error.Message}\n"));
                }

                status = "errors";
                return;
            }

            if (!statusonly)
            {
                await process.SeekAsync(fd, 0, 0);
                for (ReadOnlyMemory<byte> buf; !(buf = await process.ReadAsync(fd, 8192)).IsEmpty;)
                {
                    await process.WriteAsync(1, (byte[])[.. buf.Span, (byte)'\n']);
                }

                await process.WriteAsync(1, "\n"u8.ToArray());
            }
        }
        finally
        {
            await process.CloseAsync(fd);
        }
    }
}
