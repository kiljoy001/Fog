using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Gefs;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Host;

// The host's boot: its root is a gefs store in a file, opened as gefs opens its device, or reamed
// as gefs -r reams one when the file does not exist. A file that exists is never reamed. The
// store's users are read from its /adm/users, as gefs loads them, and then init runs the boot.
public sealed class FogHost : IDisposable
{
    // /adm/users is read as gefs reads it, and refused from 1 MiB.
    private const int UsersMax = 1 << 20;

    private readonly GefsServer store;
    private readonly Table table;
    private readonly TextWriter log;

    private FogHost(GefsServer store, Table table, FogKernel kernel, Process init, TextWriter log)
    {
        this.store = store;
        this.table = table;
        this.log = log;
        Kernel = kernel;
        Init = init;
    }

    public FogKernel Kernel { get; }

    public Process Init { get; }

    // How the boot's program ended: empty when it succeeded, or when there was none to run.
    public string BootStatus { get; private set; } = string.Empty;

    public static async Task<FogHost> BootAsync(FogHostSettings settings)
    {
        var table = new Table();
        bool ream = !File.Exists(settings.StorePath);
        GefsServer store = ream
            ? GefsServer.Create(settings.StorePath, settings.StoreBlocks, GefsServer.Arenas(settings.StoreBlocks), settings.Owner, table.InGroup, settings.Clock)
            : GefsServer.Open(settings.StorePath, table.InGroup, settings.Clock);
        try
        {
            GefsFs main = store.Attach(string.Empty, settings.Owner);
            var kernel = new FogKernel(settings.Programs, main, main.Root, settings.Owner, settings.Loaders)
            {
                Clock = settings.Clock,
                Host = settings.Host,
                Console = settings.Console,
                Nvram = settings.Nvram,
            };
            var host = new FogHost(store, table, kernel, await kernel.BootAsync(), settings.Log);
            if (ream)
            {
                await host.ReamAsync(settings.Owner);
            }
            else
            {
                await host.LoadUsersAsync();
            }

            foreach ((string path, string text) in settings.Files)
            {
                await host.InstallAsync(path, text);
            }

            if (settings.Boot.Count > 0)
            {
                await host.RunBootAsync(settings.Boot);
            }

            return host;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    // refreshusers, the console's users: /adm/users read again, and what gefs's console prints.
    public async Task<string> RefreshUsersAsync()
    {
        var console = new StringWriter();
        try
        {
            await LoadUsersAsync(console);
            console.Write("refreshed users\n");
        }
        catch (Exception error) when (error is GefsException or SyscallException)
        {
            console.Write($"load users: {error.Message}\n");
        }

        return console.ToString();
    }

    // Commits what the store holds, as gefs does when it halts.
    public void Dispose() => store.Dispose();

    // What gefs reams for its user, with sys, whose members may write the store's /bin.
    private async Task ReamAsync(string owner)
    {
        int f = await Init.CreateAsync("/root/adm/users", NinePConstants.OWRITE, 0b110_110_100);
        await Init.WriteAsync(f, Encoding.UTF8.GetBytes($"{Users.Ream(owner)}2:sys::{owner}\n"));
        await Init.CloseAsync(f);
        await LoadUsersAsync();
        NinePSharp.Messages.Stat d = Process.NullDir;
        await Init.WStatAsync("/root/bin", new NinePSharp.Messages.Stat(0, d.Type, d.Dev, d.Qid, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101, d.Atime, d.Mtime, d.Length, d.Name, d.Uid, "sys", d.Muid));
    }

    // loadusers, at boot or when the console asks: the table read from /adm/users and put in place
    // of the one there was, or refused, keeping it. A store reamed before Fog read its users has no
    // /adm/users, and each user is the only member of its own group until it has one.
    private async Task LoadUsersAsync(TextWriter? console = null)
    {
        string text;
        try
        {
            text = await ReadUsersAsync();
        }
        catch (SyscallException) when (console is null && table.Current is null)
        {
            return;
        }

        try
        {
            table.Current = Users.Parse(text, console ?? log);
        }
        catch (GefsException error)
        {
            log.Write(table.Current is null
                ? $"user table broken: {error.Message}\n\tnot permissive: bailing\n"
                : $"load users: {error.Message}\nkeeping old table\n");
            throw;
        }
    }

    private async Task<string> ReadUsersAsync()
    {
        int f = await Init.OpenAsync("/root/adm/users", NinePConstants.OREAD);
        try
        {
            var all = new List<byte>();
            for (ReadOnlyMemory<byte> got; (got = await Init.ReadAsync(f, 8192)).Length > 0;)
            {
                all.AddRange(got.Span);
                if (all.Count >= UsersMax)
                {
                    throw new GefsException("file too big");
                }
            }

            return Encoding.UTF8.GetString([.. all]);
        }
        finally
        {
            await Init.CloseAsync(f);
        }
    }

    // A file the installer copies, in directories made where missing, executable as a script is.
    private async Task InstallAsync(string path, string text)
    {
        string[] elements = path.Split('/');
        string at = "/root";
        foreach (string element in elements[..^1])
        {
            at += $"/{element}";
            if (!await ExistsAsync(at))
            {
                await Init.CloseAsync(await Init.CreateAsync(at, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_101));
            }
        }

        int f = await ExistsAsync($"{at}/{elements[^1]}")
            ? await Init.OpenAsync($"{at}/{elements[^1]}", NinePConstants.OWRITE | NinePConstants.OTRUNC)
            : await Init.CreateAsync($"{at}/{elements[^1]}", NinePConstants.OWRITE, 0b111_111_101);
        await Init.WriteAsync(f, Encoding.UTF8.GetBytes(text));
        await Init.CloseAsync(f);
    }

    private async Task<bool> ExistsAsync(string path)
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

    // init's console on its descriptors 0, 1 and 2, as 9front's init opens #c/cons, and the boot
    // run to its end.
    private async Task RunBootAsync(IReadOnlyList<string> argv)
    {
        for (int i = 0; i < 3; i++)
        {
            await Init.OpenAsync("/dev/cons", NinePConstants.ORDWR);
        }

        Init.Fork(RforkFlags.Proc | RforkFlags.Fdg, child => child.ExecAsync($"/bin/{argv[0]}", argv));
        BootStatus = (await Init.WaitAsync()).Message;
    }

    // The table the store asks whether a user is in a group, replaced as it is read again.
    private sealed class Table
    {
        public Users? Current { get; set; }

        public bool InGroup(string user, string group) => Current?.InGroup(user, group) ?? user == group;
    }
}
