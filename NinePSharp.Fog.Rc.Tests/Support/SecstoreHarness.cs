using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Kernel.Tests.Support;

namespace NinePSharp.Fog.Rc.Tests.Support;

// A kernel for secstore's programs: each run with its input given and its output and status
// taken, secstored started as the daemon it makes itself, and accounts and files put in place.
internal sealed class SecstoreHarness : IAsyncDisposable
{
    // H(secretpw)^-1 mod p for glenda.
    public const string GlendaHi = "hOaEPUy9zkWLWGWGLvdWIueeQw49POlvKUdOe4LlGVdNwwcdCh3LdRnAMnUf4y+FOosIGyfTSjJpJCTQOQVfsSgNWkXXGlChgCakzJPctlRDjOCjB1RKvkeRZfwqMxMjlrRX2g41QGl6wR2C1EiaGgBqgJ8TORplC1GsLnyJi6E=";

    // The standard input that is the console, as typed.
    public const string Cons = "\0cons";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private SecstoreHarness(FogKernel kernel, Process init) => (Kernel, Init) = (kernel, init);

    public ManualClock Clock { get; } = new() { Now = DateTimeOffset.FromUnixTimeSeconds(1700000000) };

    public FakeHostConsole Console { get; } = new();

    public FogKernel Kernel { get; private set; }

    public Process Init { get; private set; }

    public static async Task<SecstoreHarness> StartAsync(byte[]? nvram = null)
    {
        var harness = new SecstoreHarness(null!, null!);
        harness.Kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", harness.Clock, new FakeHostNetwork(), harness.Console, nvram is null ? null : new HostNvram(nvram));
        harness.Init = await harness.Kernel.BootAsync();
        for (int i = 0; i < 3; i++)
        {
            await harness.Init.OpenAsync("/dev/null", NinePConstants.ORDWR);
        }

        return harness;
    }

    // An nvram image whose config, the secret secstore and aescbc take with -n, is given.
    public static byte[] Nvram(string config)
    {
        var safe = new Nvrsafe();
        Authsrv.Safecpy(safe.Authid, "glenda");
        Authsrv.Safecpy(safe.Authdom, "fog");
        Authsrv.Safecpy(safe.Config, config);
        safe.Machsum = Authsrv.Nvcsum(safe.Machkey);
        safe.Configsum = Authsrv.Nvcsum(safe.Config);
        safe.Authidsum = Authsrv.Nvcsum(safe.Authid);
        safe.Authdomsum = Authsrv.Nvcsum(safe.Authdom);
        safe.Aesmachsum = Authsrv.Nvcsum(safe.Aesmachkey);
        return safe.ToBytes();
    }

    public ValueTask DisposeAsync()
    {
        foreach (Process process in Kernel.Processes.Values)
        {
            process.PostNote("kill");
        }

        return ValueTask.CompletedTask;
    }

    // The program run with stdin as its input, its standard output and error as it printed them
    // into one, and the status it exited with.
    public Task<(string Out, string Status)> RunAsync(string stdin, params string[] argv) => RunInAsync(null, stdin, argv);

    // As RunAsync, in the directory given.
    public async Task<(string Out, string Status)> RunInAsync(string? dir, string stdin, params string[] argv)
    {
        bool cons = stdin == Cons;
        (int inr, int inw) = await Init.PipeAsync();
        (int outr, int outw) = await Init.PipeAsync();
        long pid = Init.Fork(RforkFlags.Proc | RforkFlags.Fdg, async child =>
        {
            await child.DupAsync(cons ? await child.OpenAsync("/dev/cons", NinePConstants.OREAD) : inr, 0);
            await child.DupAsync(outw, 1);
            await child.DupAsync(outw, 2);
            foreach (int fd in new[] { inr, inw, outr, outw })
            {
                await child.CloseAsync(fd);
            }

            if (dir is not null)
            {
                await child.ChdirAsync(dir);
            }

            await child.ExecAsync($"/bin/{argv[0]}", argv);
        });
        await Init.CloseAsync(inr);
        await Init.CloseAsync(outw);
        if (!cons && stdin.Length > 0)
        {
            await Init.WriteAsync(inw, Encoding.UTF8.GetBytes(stdin));
        }

        await Init.CloseAsync(inw);
        var bytes = new List<byte>();
        for (ReadOnlyMemory<byte> b; !(b = await Init.ReadAsync(outr, 8192).AsTask().WaitAsync(Bound)).IsEmpty;)
        {
            bytes.AddRange(b.Span);
        }

        await Init.CloseAsync(outr);
        return (Encoding.UTF8.GetString([.. bytes]), await WaitForAsync(pid));
    }

    // A daemon started with no output, once the process that started it has exited; its status.
    public async Task<string> StartDaemonAsync(params string[] argv)
    {
        long pid = Init.Fork(RforkFlags.Proc | RforkFlags.Fdg, child => child.ExecAsync($"/bin/{argv[0]}", argv));
        return await WaitForAsync(pid);
    }

    // secstored on the port, once it answers.
    public async Task SecstoredAsync(int port, params string[] flags)
    {
        await StartDaemonAsync(["auth/secstored", .. flags, "-s", $"tcp!127.0.0.1!{port}"]);
        for (int i = 0; ; i++)
        {
            try
            {
                await Init.CloseAsync(await Net.DialAsync(Init, $"tcp!127.0.0.1!{port}"));
                return;
            }
            catch (SyscallException) when (i < 300)
            {
                await Task.Delay(10);
            }
        }
    }

    // A connection to secstored on the port, logged in with PAK as glenda, or as the user given.
    public async Task<SConn> LoginAsync(int port, string user = "glenda", string password = "secretpw")
    {
        var conn = new SConn(Init, await Net.DialAsync(Init, $"tcp!127.0.0.1!{port}"));
        (int rc, _) = await Pak.ClientAsync(Init, "secstore", false, conn, user, password);
        if (rc < 0)
        {
            throw new InvalidOperationException("PAK failed");
        }

        return conn;
    }

    public async Task AccountAsync(string id, string text, bool store = true)
    {
        foreach (string dir in new[] { "/adm/secstore", "/adm/secstore/who", "/adm/secstore/store" })
        {
            await MkdirAsync(dir);
        }

        if (store)
        {
            await MkdirAsync($"/adm/secstore/store/{id}");
        }

        await WriteFileAsync($"/adm/secstore/who/{id}", Encoding.UTF8.GetBytes(text));
    }

    public Task GlendaAsync() => AccountAsync("glenda", $"exp\t0\nPAK-Hi\t{GlendaHi}\n");

    public async Task MkdirAsync(string dir)
    {
        try
        {
            await Init.StatAsync(dir);
        }
        catch (SyscallException)
        {
            await Init.CloseAsync(await Init.CreateAsync(dir, NinePConstants.OREAD, 0x80000000 | 0b111_111_111));
        }
    }

    public async Task WriteFileAsync(string path, byte[] data)
    {
        int fd = await Init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        await Init.WriteAsync(fd, data);
        await Init.CloseAsync(fd);
    }

    public async Task<string> FileAsync(string path) => Encoding.UTF8.GetString(await BytesAsync(path));

    public async Task<byte[]> BytesAsync(string path)
    {
        int fd = await Init.OpenAsync(path, NinePConstants.OREAD);
        var bytes = new List<byte>();
        for (ReadOnlyMemory<byte> b; !(b = await Init.ReadAsync(fd, 8192)).IsEmpty;)
        {
            bytes.AddRange(b.Span);
        }

        await Init.CloseAsync(fd);
        return [.. bytes];
    }

    public async Task<bool> ExistsAsync(string path)
    {
        try
        {
            await Init.StatAsync(path);
            return true;
        }
        catch (SyscallException)
        {
            return false;
        }
    }

    // The status of the child, after any others that end first.
    private async Task<string> WaitForAsync(long pid)
    {
        while (true)
        {
            Waitmsg w = await Init.WaitAsync().WaitAsync(Bound);
            if (w.Pid == pid)
            {
                // Without the name and pid the message starts with.
                return System.Text.RegularExpressions.Regex.Replace(w.Message, @"^\S+ \d+: ", string.Empty);
            }
        }
    }
}
