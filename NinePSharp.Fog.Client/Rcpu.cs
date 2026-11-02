using System.Net.Sockets;
using System.Text;
using Dp9ik.P9Auth;
using Dp9ik.Tls;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Server;

namespace NinePSharp.Fog.Client;

// rcpu for a .NET console, as rc/bin/rcpu and rconnect are on 9front and cpu.c's rcpu is in
// drawterm: dial the host's rcpu, authenticate with dp9ik, start TLS-PSK with the session secret,
// send rcpu's script, and serve the local namespace on the conversation until it ends.
public static class Rcpu
{
    public const string Usage = "usage: fog [-u user] [-h host] [-c cmd arg ...]\n";

    // The services rconnect dials when an address names none: rcpu's, and the auth server's.
    internal const string RcpuService = "rcpu";
    internal const string TicketService = "ticket";

    // rcpu's server function, as 9front's rc prints it with whatis.
#pragma warning disable SA1027 // the function is as 9front's rc prints it, indented with tabs
    internal const string Server = """
        fn server {
        	mount -nc /fd/0 /mnt/term || exit
        	bind -q /mnt/term/dev/cons /dev/cons
        	 </mnt/term/fd/10  >/mnt/term/fd/11  >[2]/mnt/term/fd/12 {
        		~ $#dir 0 || cd $"dir || >[1=2]echo 'can''t change directory: '^$"dir
        		switch ($#cmd) {
        			case 0; cmd=(rc -li)
        			case 1; cmd=(rc -lc $cmd)
        			case *; cmd=(rc -lc '$*' $cmd)
        		}
        		rm -f /env/^(cmd dir 'fn#server')
        		service=cpu exec $cmd
        	}&
        	if(test -d /mnt/term/mnt/cpunote){
        		rfork e
        		mainproc=$apid
        		rm -f /mnt/term/env/rfailed
        		 </mnt/term/mnt/cpunote/data  >/proc/^$mainproc^/notepg {
        			cat; echo -n hangup
        		}&
        		noteproc=$apid
        		wait $mainproc
        		 >/mnt/term/env/rstatus  >[2]/dev/null echo -n $status
        		 >/proc/^$noteproc^/notepg echo -n hangup
        	}
        }

        """;
#pragma warning restore SA1027

    // Runs fog with its arguments on the local machine, and gives its exit code: 0 when rc's status
    // was empty, 1 when it was not or fog could not run it.
    public static async Task<int> RunAsync(string[] args, Local local, CancellationToken cancellationToken)
    {
        if (Arguments(args, local) is not { } given)
        {
            await WriteAsync(local.Error, Usage, cancellationToken);
            return 1;
        }

        try
        {
            return await RunAsync(given, local, cancellationToken);
        }
        catch (Exception error) when (error is IOException or SocketException or InvalidOperationException or FormatException)
        {
            await WriteAsync(local.Error, $"fog: {error.Message}\n", cancellationToken);
            return 1;
        }
    }

    // rcpu's options: -u, -h and -c, which takes the rest; anything else, or an option without its
    // value, is a usage error.
    internal static (string User, string Host, string[] Cmd)? Arguments(string[] args, Local local)
    {
        string? user = null, host = local.Environment("cpu");
        string[] cmd = [];
        int i = 0;
        for (; i < args.Length && args[i].StartsWith('-'); i++)
        {
            if (i + 1 == args.Length)
            {
                return null;
            }

            switch (args[i])
            {
                case "-u":
                    user = args[++i];
                    break;
                case "-h":
                    host = args[++i];
                    break;
                case "-c":
                    cmd = args[(i + 1)..];

                    // Stryker disable once Arithmetic : past the last argument either way, the loop ends and none is left over
                    i = args.Length - 1;
                    break;
                default:
                    return null;
            }
        }

        return i < args.Length || host is null ? null : (user ?? local.LoginName, host, cmd);
    }

    // rconnect's address: net!host!rcpu for a bare host, host!rcpu for one without a service.
    internal static (string Host, int Port) Address(string address, string service)
    {
        string[] parts = address.Split('!');
        (string net, string host, string port) = parts.Length switch
        {
            1 => ("net", parts[0], service),
            2 => ("net", parts[0], parts[1]),
            3 => (parts[0], parts[1], parts[2]),
            _ => throw new FormatException($"bad address {address}"),
        };
        if (net is not ("net" or "tcp"))
        {
            throw new FormatException($"bad network {net}");
        }

        return (host, port switch
        {
            RcpuService => 17019,
            TicketService => 567,
            _ => int.TryParse(port, out int n) ? n : throw new FormatException($"bad service {port}"),
        });
    }

    // The script rcpu writes to /env/rscript: dir and cmd as pvar prints them, the server function,
    // and its call.
    internal static string Script(string dir, string[] cmd)
        => $"dir={Word(dir)}\ncmd={cmd.Length switch { 0 => "()", 1 => Word(cmd[0]), _ => $"({string.Join(' ', cmd.Select(Word))})" }}\n{Server}server\n";

    // rc's pwrd: a word quoted when it is empty or holds a blank, a control character or one rc
    // gives a meaning.
    internal static string Word(string word)
        => word.Length == 0 || word.Any(c => c < 0x80 && (c <= ' ' || "`^#*[]=|\\?${}()'<>&;".Contains(c, StringComparison.Ordinal)))
            ? $"'{word.Replace("'", "''", StringComparison.Ordinal)}'"
            : word;

    // The working directory as the session sees it, under /mnt/term/root: /home/glenda as
    // /mnt/term/root/home/glenda, and C:\work as /mnt/term/root/C:/work.
    internal static string Directory(Local local)
        => local.Drives.Count == 0
            ? $"/mnt/term/root{(local.Directory == "/" ? string.Empty : local.Directory)}"
            : $"/mnt/term/root/{local.Directory.Replace('\\', '/').TrimEnd('/')}";

    private static async Task<int> RunAsync((string User, string Host, string[] Cmd) given, Local local, CancellationToken cancellationToken)
    {
        (string host, int port) = Address(given.Host, RcpuService);
        (string authHost, int authPort) = Address(local.Environment("auth") ?? host, TicketService);
        string? pass = local.Environment("PASS");
        using var call = new TcpClient();
        await call.ConnectAsync(host, port, cancellationToken);
        var config = new P9AuthClientConfig(
            given.User,
            async (prompt, ct) =>
            {
                if (pass is not null)
                {
                    return prompt.Mismatch ? throw new InvalidOperationException("wrong password") : pass;
                }

                if (prompt.Mismatch)
                {
                    await WriteAsync(local.Error, "?password mismatch with auth server\n", ct);
                }

                return await local.ReadPassword($"{prompt.Text}: ", ct);
            },
            async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(authHost, authPort, ct);

                // Stryker disable once Boolean : the stream is the socket's only user; owning it only closes it sooner
                return new NetworkStream(socket, ownsSocket: true);
            });
        P9AuthClientResult authenticated = await P9AuthClient.AuthenticateAsync(call.GetStream(), config, cancellationToken);
        Stream conversation = await P9PskTls.ConnectAsync(call.GetStream(), authenticated.Secret, cancellationToken);
        byte[] script = Encoding.UTF8.GetBytes(Script(Directory(local), given.Cmd));
        await conversation.WriteAsync(Encoding.UTF8.GetBytes($"{script.Length,7}\n"), cancellationToken);
        await conversation.WriteAsync(script, cancellationToken);

        // Stryker disable once Statement : TLS's stream writes each record as it is given, so its flush has nothing to send
        await conversation.FlushAsync(cancellationToken);

        using var hangup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var export = new Export(local, hangup.Token);
        using var served = new Hangup(conversation, hangup);
        try
        {
            await new NinePConnectionProcessor(NullLogger.Instance, export).ProcessStreamAsync(served, null, new NinePConnectionProcessor.ClientSession(), cancellationToken);
        }
        catch (IOException)
        {
        }

        if (served.Lost)
        {
            await WriteAsync(local.Error, "lost connection\n", cancellationToken);
            return 1;
        }

        return export.Status.Length == 0 ? 0 : 1;
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);

        // Stryker disable once Statement : the console's error stream is unbuffered, so its flush has nothing to send
        await stream.FlushAsync(cancellationToken);
    }

    // The conversation, which cancels the reads it serves once it has ended, and was lost when it
    // ended without TLS's close, as a host that drops the call ends it. TLS's stream takes its
    // reads and writes on threads of their own, as tlssrv takes them: Stream's own asynchronous
    // reads and writes take turns, so a write would wait for a read that waits for its answer.
    private sealed class Hangup(Stream inner, CancellationTokenSource ended) : Stream
    {
        // Stryker disable Boolean : the 9P connection reads and writes the stream without asking what it can do
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        // Stryker restore Boolean
        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool Lost { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                byte[] read = new byte[buffer.Length];
                int n = await Task.Run(() => inner.Read(read, 0, read.Length), cancellationToken);
                read.AsSpan(0, n).CopyTo(buffer.Span);
                if (n == 0)
                {
                    await ended.CancelAsync();
                }

                return n;
            }
            catch (IOException)
            {
                Lost = true;
                await ended.CancelAsync();
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] written = buffer.ToArray();
            await Task.Run(() => inner.Write(written, 0, written.Length), cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.Run(inner.Flush, cancellationToken);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
