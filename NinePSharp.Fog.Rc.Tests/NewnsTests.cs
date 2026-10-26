using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Commands;
using NinePSharp.Fog.Kernel;
using NinePSharp.Fog.Rc.Tests.Support;
using NinePSharp.Namespaces;
using Xunit;

namespace NinePSharp.Fog.Rc.Tests;

// libauth's newns and addns, run in a process of their own: what each line of a namespace file does
// to it, what -d says of each, and the descriptors, directory and notes it is left with.
public sealed class NewnsTests : IAsyncLifetime
{
    private const string Long = "abcdefghijklmnopqrstuvwxyz0123";
    private const string LongName = "abcdefghijklmnopqrstuvwxyz01";
    private readonly FakeHostConsole console = new();
    private FogKernel kernel = null!;
    private Process init = null!;

    public async Task InitializeAsync()
    {
        kernel = FogKernel.InMemory(new Dictionary<string, ProgramMain>(Commands.Commands.All), "glenda", console: console);
        init = await kernel.BootAsync();
        foreach (string dir in (string[])["/tmp/a", "/tmp/b", "/tmp/c", "/tmp/x", "/tmp/y"])
        {
            await MkdirAsync(dir);
        }

        await WriteAsync("/tmp/a/f", "a");
        await WriteAsync("/tmp/b/f", "b");
        await WriteAsync("/tmp/b/onlyb", "b");
    }

    public Task DisposeAsync()
    {
        foreach (Process process in kernel.Processes.Values)
        {
            process.PostNote("kill");
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddnsWantsAFileAndNewnsTakesLibNamespaceWithoutOne()
    {
        string r1 = await InChildAsync(async c =>
        {
            await Newns.AddnsAsync(c, "glenda", null);
            return "added";
        });
        Assert.Equal("error: no namespace file specified", r1);
        string r2 = await InChildAsync(async c =>
        {
            await Newns.NewnsAsync(c, "glenda", null);
            return $"{await EnvAsync(c, "user")} {await EnvAsync(c, "home")}";
        });
        Assert.Equal("glenda /usr/glenda", r2);
    }

    [Fact]
    public async Task DebuggingPrintsEachLineAsItsArgumentsAreExpandedAndUnquoted()
    {
        await WriteAsync("/env/v", "V");
        await WriteAsync("/env/long", Long);
        await WriteAsync("/env/nul", "\0x");
        await WriteAsync($"/env/{LongName}", "L");
        string a250 = new('a', 250), a247 = new('a', 247), a275 = new('a', 275), a277 = new('a', 277), a27 = new('a', 27);
        string[] lines =
        [
            string.Empty,
            "# a comment",
            "  \t# an indented comment",
            $"x $v/a $v.b $v!c $v'd' $v$v '$v' $/e $long $nul/z ${LongName}/f {a27}$v",
            "q a''b 'it''s' 'q r'",
            $"y {a250}$v",
            $"y {a250}a$v",
            $"z $v/{a247}$v",
            $"w {new string('a', 278)}",
            $"w {new string('a', 279)}",
            $"u $v/{a275}",
            $"u $v/{a277}",
            "mount /tmp/nonesuch a b c d e f g h i j k l m n o",
            "mount /tmp/nonesuch /tmp/c",
            "bind /tmp/nonesuch /tmp/c",
        ];
        string expected = string.Concat(
            $"x V/a V.b V!c Vd VV $v /e {Long[..27]} /z /f {a27}V \n",
            "q ab it's q r \n",
            $"y {a250}V \n",
            $"z V/{a247}V \n",
            $"w {new string('a', 278)} \n",
            $"u V/{a275} \n",
            "mount /tmp/nonesuch /tmp/c \n",
            "addns: mount: /tmp/nonesuch: file does not exist: '/tmp/nonesuch'\n",
            "bind /tmp/nonesuch /tmp/c \n",
            "addns: bind: /tmp/nonesuch /tmp/c: file does not exist: '/tmp/nonesuch'\n");
        Assert.Equal(string.Empty, await AddnsAsync(string.Join('\n', lines) + "\n", debug: true));

        // First, what it could not open: factotum's rpc and the drivers Fog has not.
        string errors = await ReadAsync("/tmp/errors");
        Assert.StartsWith("open /mnt/factotum/rpc: ", errors, StringComparison.Ordinal);
        Assert.Contains("\nopen #c/drivers: ", errors, StringComparison.Ordinal);
        Assert.Equal(expected, errors[errors.IndexOf("x V/a", StringComparison.Ordinal)..]);
    }

    [Fact]
    public async Task NewnsSaysSoInWhatItPrints()
    {
        await WriteAsync("/tmp/ns", "bind /tmp/nonesuch /tmp/c\n");
        string r3 = await InChildAsync(async c =>
        {
            await Newns.NewnsAsync(c, "glenda", "/tmp/ns", debug: true);
            return "built";
        });
        Assert.Equal("built", r3);
        Assert.Contains("\nnewns: bind: /tmp/nonesuch /tmp/c: ", await ReadAsync("/tmp/errors"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindTakesItsFlagsOnceEachAndUnmountTakesTheMountOrAll()
    {
        // -aa is -a, the union keeping what was there, and first.
        string r4 = await InChildAsync(async c =>
        {
            await Newns.AddnsAsync(c, "glenda", await FileAsync("bind -aa /tmp/a /tmp/b\n"));
            return $"{await ReadAsync(c, "/tmp/b/f")} {await ReadAsync(c, "/tmp/b/onlyb")}";
        });
        Assert.Equal("b b", r4);

        // One argument unmounts all of what is mounted there; two, the one named.
        string r5 = await InChildAsync(async c =>
        {
            await c.BindAsync("/tmp/b", "/tmp/x", MountFlags.Before);
            await c.BindAsync("/tmp/a", "/tmp/x", MountFlags.Before);
            await c.BindAsync("/tmp/a", "/tmp/b", MountFlags.Replace);
            await Newns.AddnsAsync(c, "glenda", await FileAsync("unmount /tmp/b\nunmount /tmp/a /tmp/x\n"));
            string x = (await NamesAsync(c, "/tmp/x")).Contains("onlyb") ? "x" : "-";
            return $"{await ReadAsync(c, "/tmp/b/f")} {x}";
        });
        Assert.Equal("b x", r5);
    }

    [Fact]
    public async Task MountMountsTheFileServerOnTheFileAndLeavesNothingOpen()
    {
        string r6 = await InChildAsync(async c =>
        {
            string fds = await FdsAsync(c);
            await Newns.AddnsAsync(c, "glenda", await FileAsync("mount /srv/slashn /tmp/c\nmount /srv/slashn /nonesuch\nmount /srv/slashn\n"));
            Assert.Equal(fds, await FdsAsync(c));
            await c.StatAsync("/tmp/c/made");
            return "made";
        });
        Assert.Equal("made", r6);
    }

    [Fact]
    public async Task ADotRunsTheLinesOfTheFileItNames()
    {
        await WriteAsync("/tmp/inc", "bind /tmp/a /tmp/b\n");
        string r7 = await InChildAsync(async c =>
        {
            await Newns.AddnsAsync(c, "glenda", await FileAsync(". /tmp/inc\n"));
            return await ReadAsync(c, "/tmp/b/f");
        });
        Assert.Equal("a", r7);
    }

    [Fact]
    public async Task ChdevEndsTheProcessForWantOfDrivers()
    {
        string ended = await InChildAsync(async c =>
        {
            await Newns.AddnsAsync(c, "glenda", await FileAsync("chdev -r c\n"));
            return "went on";
        });
        Assert.EndsWith("chdev requested, but could not open #c/drivers", ended, StringComparison.Ordinal);
        Assert.EndsWith(": chdev requested, but could not open #c/drivers\n", await ReadAsync("/tmp/errors"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewnsTakesTheProcessIntoTheNewNamespaceAtTheDirectoryItWasInUnlessItCdsToARootedOne()
    {
        // /tmp/b is /tmp/a's directory only in the namespace newns leaves.
        const string Root = "bind #R /root\nbind -a /root /\n";
        Assert.Equal("b", await NewnsInBAsync(Root + "bind /tmp/x /tmp/y\n"));
        Assert.Equal("b", await NewnsInBAsync(Root + ". /tmp/nonesuch\n"));
        Assert.Equal("b", await NewnsInBAsync(Root + "cd /nonesuch\n"));
        Assert.Equal("b", await NewnsInBAsync(Root + "cd tmp\n"));
        Assert.Equal("a", await NewnsInBAsync(Root + "cd /tmp/a\n"));

        // A directory the new namespace has not is left for the root.
        string r8 = await InChildAsync(async c =>
        {
            await c.ChdirAsync("/n/only");
            await Newns.NewnsAsync(c, "glenda", await FileAsync(Root));
            return await WdAsync(c);
        });
        Assert.Equal("/", r8);

        // addns leaves the process in the directory it was in, which the path now names no longer.
        await MkdirAsync("/tmp/a/sub");
        await MkdirAsync("/tmp/b/sub");
        await WriteAsync("/tmp/a/sub/f", "asub");
        await WriteAsync("/tmp/b/sub/f", "bsub");
        string r9 = await InChildAsync(async c =>
        {
            await c.ChdirAsync("/tmp/b/sub");
            await Newns.AddnsAsync(c, "glenda", await FileAsync("bind /tmp/a /tmp/b\n"));
            return await ReadAsync(c, "f");
        });
        Assert.Equal("bsub", r9);
    }

    [Fact]
    public async Task NothingIsLeftOpenNotEvenFactotumsRpcAsDescriptorZero()
    {
        init.Fork(RforkFlags.Proc, child => child.ExecAsync("/bin/auth/factotum", ["auth/factotum", "-n", "-p"]));
        Assert.Equal(string.Empty, (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message);
        await WriteAsync("/env/v", "V");
        string r10 = await InChildAsync(async c =>
        {
            await c.CloseAsync(0);
            string fds = await FdsAsync(c);
            await Newns.AddnsAsync(c, "glenda", await FileAsync("bind $v /tmp/c\n"));
            await Newns.NewnsAsync(c, "glenda", await FileAsync("bind #R /root\nbind -a /root /\nbind #p /proc\nbind #c /dev\n"));
            Assert.Equal("glenda", await Newns.GetuserAsync(c));
            return fds == await FdsAsync(c) ? "same" : await FdsAsync(c);
        });
        Assert.Equal("same", r10);
    }

    [Fact]
    public async Task AWriteOnAClosedPipeIsCaughtWhileTheFileRunsAndNotAfter()
    {
        // The server's pipe has no reader, so the mount's version is a write on a closed pipe, which
        // fails the mount and not the process; the next line is run.
        (int read, int written) = await init.PipeAsync();
        await init.CloseAsync(read);
        string ended = await InChildAsync(async c =>
        {
            await Newns.AddnsAsync(c, "glenda", await FileAsync($"mount /fd/{written} /tmp/c\nbind /tmp/a /tmp/b\n"));
            string bound = await ReadAsync(c, "/tmp/b/f");

            // After, such a write is the note it always is.
            (int r, int w) = await c.PipeAsync();
            await c.CloseAsync(r);
            await c.WriteAsync(w, "x"u8.ToArray());
            return $"{bound} lived";
        });
        Assert.EndsWith("write on closed pipe", ended, StringComparison.Ordinal);
        await init.CloseAsync(written);
    }

    [Fact]
    public async Task ANoteWhileTheFileRunsIsTheCallersToCatch()
    {
        // A note the caller catches interrupts the mount waiting for the server's version.
        Assert.Equal("a", await MountInterruptedAsync(c => c.Notify(note => note == "test")));

        // One it does not ends the process.
        Assert.StartsWith("exited: ", await MountInterruptedAsync(_ => { }), StringComparison.Ordinal);
    }

    private static async Task<string> EnvAsync(Process p, string name) => await ReadAsync(p, $"/env/{name}");

    private static async Task<string> WdAsync(Process p)
    {
        int fd = await p.OpenAsync(".", NinePConstants.OREAD);
        string wd = await p.Fd2PathAsync(fd);
        await p.CloseAsync(fd);
        return wd;
    }

    private static async Task<string> FdsAsync(Process p) => await ReadAsync(p, $"/proc/{p.Pid}/fd");

    private static async Task<List<string>> NamesAsync(Process p, string dir)
    {
        int fd = await p.OpenAsync(dir, NinePConstants.OREAD);
        IReadOnlyList<NinePSharp.Messages.Stat> entries = await p.DirReadAsync(fd);
        await p.CloseAsync(fd);
        return [.. entries.Select(e => e.Name)];
    }

    private static async Task<string> ReadAsync(Process p, string path)
    {
        int f = await p.OpenAsync(path, NinePConstants.OREAD);
        var all = new List<byte>();
        for (ReadOnlyMemory<byte> b; !(b = await p.ReadAsync(f, 8192)).IsEmpty;)
        {
            all.AddRange(b.Span);
        }

        await p.CloseAsync(f);
        return Encoding.UTF8.GetString([.. all]);
    }

    private async Task<string> MountInterruptedAsync(Action<Process> notify)
    {
        (int read, int written) = await init.PipeAsync();
        string? result = null;
        long pid = init.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg, async c =>
        {
            notify(c);
            await Newns.AddnsAsync(c, "glenda", await FileAsync($"mount /fd/{written} /tmp/c\nbind /tmp/a /tmp/b\n"));
            result = await ReadAsync(c, "/tmp/b/f");
        });
        Assert.False((await init.ReadAsync(read, 8192).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsEmpty);
        kernel.Processes[pid].PostNote("test");
        string message = (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5))).Message;
        await init.CloseAsync(read);
        await init.CloseAsync(written);
        return result ?? $"exited: {message}";
    }

    // newns of the file in a process whose /tmp/b is /tmp/a's directory, and which is in it: what
    // its f then reads.
    private Task<string> NewnsInBAsync(string file) => InChildAsync(async c =>
    {
        await c.BindAsync("/tmp/a", "/tmp/b", MountFlags.Replace);
        await c.ChdirAsync("/tmp/b");
        await Newns.NewnsAsync(c, "glenda", await FileAsync(file));
        return await ReadAsync(c, "f");
    });

    // addns of the contents, its errors left in /tmp/errors; what the body of InChildAsync says.
    private Task<string> AddnsAsync(string contents, bool debug) => InChildAsync(async c =>
    {
        await Newns.AddnsAsync(c, "glenda", await FileAsync(contents), debug);
        return string.Empty;
    });

    // The body run in a process of its own, with its errors to /tmp/errors: what it returns, what
    // error it throws, or the status of a process that ended first.
    private async Task<string> InChildAsync(Func<Process, Task<string>> body)
    {
        string? result = null;
        init.Fork(RforkFlags.Proc | RforkFlags.Fdg | RforkFlags.Nameg | RforkFlags.Envg, async c =>
        {
            await c.DupAsync(await c.CreateAsync("/tmp/errors", NinePConstants.OWRITE, 0b110_110_110), 2);
            try
            {
                result = await body(c);
            }
            catch (SyscallException error)
            {
                result = $"error: {error.Message}";
            }
        });
        string message = (await init.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10))).Message;
        return result ?? $"exited: {message}";
    }

    // A namespace file of the contents.
    private async Task<string> FileAsync(string contents)
    {
        string name = $"/tmp/ns{Guid.NewGuid():N}";
        await WriteAsync(name, contents);
        return name;
    }

    private Task<string> ReadAsync(string path) => ReadAsync(init, path);

    private async Task MkdirAsync(string path)
        => await init.CloseAsync(await init.CreateAsync(path, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));

    private async Task WriteAsync(string path, string text)
    {
        int f = await init.CreateAsync(path, NinePConstants.OWRITE, 0b110_110_110);
        await init.WriteAsync(f, Encoding.UTF8.GetBytes(text));
        await init.CloseAsync(f);
    }
}
