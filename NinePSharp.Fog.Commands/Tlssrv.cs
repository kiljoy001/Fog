using System.Text;
using Dp9ik.Tls;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// tlssrv(8), as 9front's tlssrv.c: the call on 0 and 1 authenticated through factotum with -a, which
// also becomes the user, or -A, then TLS as the server keyed by the session secret, and the command
// run with the clear conversation as its 0 and 1.
//
// Fog has no devtls: the TLS is Dp9ik.Tls's, and two processes of tlssrv's, in a note group and a
// namespace of their own, relay between the call and a pipe whose other end the command holds, one
// each way; when either way ends, both do. Holding no namespace the command mounts in, they do not
// keep a mount of the pipe, such as rcpu's of /fd/0, from being let go. Fog has no certificates, so -c is refused, and no handshake trace, so
// -D reports only the handshake's end. #I has no wstat, so the call is not given to the user.
public static class Tlssrv
{
    private const string Usage = "usage: tlssrv [-D] -[aA] [-k keyspec]] [-c cert] [-l logfile] [-r remotesys] cmd [args...]\n";
    private const int IoUnit = 32768;

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var s = new State(process, argv[0]);
        var arguments = new Arguments(argv);
        string keyspec = string.Empty;
        string? cert = null;
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'D':
                    s.Debug = true;
                    break;
                case 'a':
                    s.Auth = 1;
                    break;
                case 'A':
                    s.Auth = -1;
                    break;
                case 'k':
                    keyspec = await arguments.EargfAsync(() => UsageAsync(process));
                    break;
                case 'c':
                    cert = await arguments.EargfAsync(() => UsageAsync(process));
                    break;
                case 'l':
                    s.Logfile = await arguments.EargfAsync(() => UsageAsync(process));
                    break;
                case 'r':
                    s.Remotesys = await arguments.EargfAsync(() => UsageAsync(process));
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count == 0)
        {
            await UsageAsync(process);
        }

        byte[] psk = s.Auth != 0 ? await AuthenticateAsync(s, keyspec) : [];
        if (cert is not null)
        {
            await SysfatalAsync(s, $"{cert}: certificates are not supported");
        }

        if (psk.Length == 0)
        {
            await SysfatalAsync(s, "no certificate or shared secret");
        }

        var transport = new Fdstream(process, 0);
        Stream tls;
        try
        {
            tls = await P9PskTls.AcceptAsync(transport, psk, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or Org.BouncyCastle.Tls.TlsException)
        {
            if (s.Debug)
            {
                await s.ReporterAsync($"failed: {error.Message}");
            }

            process.Exits(null);
            return;
        }

        if (s.Debug)
        {
            await s.ReporterAsync("open");
        }

        (int p0, int p1) = await process.PipeAsync();
        process.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg | RforkFlags.Noteg, relay => RelayAsync(relay, transport, tls, p0, p1));
        await process.DupAsync(p1, 0);
        await process.DupAsync(p1, 1);
        await process.CloseAsync(p0);
        await process.CloseAsync(p1);
        try
        {
            await process.ExecAsync(args[0], args);
        }
        catch (SyscallException error)
        {
            await s.ReporterAsync($"can't exec {args[0]}: {error.Message}");
            process.Exits("exec");
        }
    }

    // auth_proxy as the server through factotum, and with -a, auth_chuid: the session secret.
    private static async Task<byte[]> AuthenticateAsync(State s, string keyspec)
    {
        AuthInfo ai = null!;
        try
        {
            ai = await Libauth.AuthProxyAsync(s.Process, 0, null, $"proto=p9any role=server {keyspec}");
        }
        catch (SyscallException error)
        {
            await SysfatalAsync(s, $"auth_proxy: {error.Message}");
        }

        if (s.Auth == 1)
        {
            try
            {
                await Libauth.AuthChuidAsync(s.Process, ai, null);
            }
            catch (SyscallException error)
            {
                await SysfatalAsync(s, $"auth_chuid: {error.Message}");
            }
        }

        return ai.Secret;
    }

    // The relay, holding the call and the pipe's end: this process takes what the client sends to
    // the command, and one it forks takes what the command sends back.
    private static async Task RelayAsync(Process relay, Fdstream transport, Stream tls, int p0, int p1)
    {
        await relay.CloseAsync(p1);
        transport.Reader = relay;
        relay.Fork(RforkFlags.Proc | RforkFlags.Fdg, async back =>
        {
            transport.Writer = back;
            await ToClientAsync(back, tls, p0);
            await HangupAsync(back);
        });
        await FromClientAsync(relay, tls, p0);
        await HangupAsync(relay);
    }

    private static async Task FromClientAsync(Process process, Stream tls, int fd)
    {
        byte[] buf = new byte[IoUnit];
        try
        {
            for (int n; (n = await Task.Run(() => tls.Read(buf, 0, buf.Length))) > 0;)
            {
                await process.WriteAsync(fd, buf.AsMemory(0, n));
            }
        }
        catch (Exception error) when (error is IOException or SyscallException or Org.BouncyCastle.Tls.TlsException)
        {
            // The client hung up, or the command did.
        }
    }

    // What the command writes, until it is done with the pipe, and then TLS's close.
    private static async Task ToClientAsync(Process process, Stream tls, int fd)
    {
        try
        {
            for (ReadOnlyMemory<byte> buf; !(buf = await process.ReadAsync(fd, IoUnit)).IsEmpty;)
            {
                byte[] b = buf.ToArray();
                await Task.Run(() => tls.Write(b, 0, b.Length));
            }

            await Task.Run(tls.Close);
        }
        catch (Exception error) when (error is IOException or SyscallException or Org.BouncyCastle.Tls.TlsException)
        {
            // The client hung up.
        }
    }

    // Either way's end is both ways'.
    private static async Task HangupAsync(Process process)
    {
        try
        {
            int fd = await process.OpenAsync($"/proc/{process.Pid}/notepg", NinePConstants.OWRITE);
            await process.WriteAsync(fd, "hangup"u8.ToArray());
        }
        catch (SyscallException)
        {
            // A relay whose other half has gone has no one else to tell.
        }

        process.Exits(null);
    }

    private static async Task SysfatalAsync(State s, string message)
    {
        await s.Process.WriteAsync(2, Encoding.UTF8.GetBytes($"{s.Argv0}: {message}\n"));
        s.Process.Exits(message);
    }

    private static async Task<string> UsageAsync(Process process)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes(Usage));
        process.Exits("usage");
        return string.Empty;
    }

    private sealed class State(Process process, string argv0)
    {
        public Process Process => process;

        public string Argv0 => argv0;

        public bool Debug { get; set; }

        public int Auth { get; set; }

        public string Remotesys { get; set; } = string.Empty;

        public string? Logfile { get; set; }

        // reporter: to the log file named, or standard error.
        public async Task ReporterAsync(string message)
        {
            if (Logfile is not null)
            {
                await Syslog.WriteAsync(process, false, Logfile, $"{Remotesys} tls reports {message}");
            }
            else
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{argv0}: {Remotesys} tls reports {message}\n"));
            }
        }
    }
}
