using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NinePSharp.Constants;
using NinePSharp.Messages;
using NinePSharp.Parser;
using NinePSharp.Server;
using NinePSharp.Server.FileSystem;
using NinePSharp.Server.Identity;

namespace NinePSharp.Fog.Client;

// The tree fog serves on the conversation, as rcpu's client serves its namespace with exportfs:
// the terminal at /dev/cons, the standard streams at /fd/10, 11 and 12, interrupts at
// /mnt/cpunote/data, rc's status at /env/rstatus, and the local files at /root.
internal sealed class Export : INinePFSDispatcher, INinePSessionLifecycle
{
    private const uint ReadWrite = 0b110_110_110;

    private readonly NinePFSDispatcher server;
    private readonly NinePFile rstatus = new("rstatus") { Mode = ReadWrite };

    public Export(Local local, CancellationToken hangup)
    {
        // Stryker disable once String : the root's own name is never served; a walk starts at it
        var root = new NinePDir("/");
        var dev = new NinePDir("dev");
        dev.AddChild(new Device("cons", ct => Read(local.ReadConsole, ct), local.WriteConsole, hangup));
        root.AddChild(dev);
        var fd = new NinePDir("fd");
        fd.AddChild(new Device("10", async ct => await ReadAsync(local.Input, ct), null, hangup));
        fd.AddChild(new Device("11", null, (data, ct) => WriteAsync(local.Output, data, ct), hangup));
        fd.AddChild(new Device("12", null, (data, ct) => WriteAsync(local.Error, data, ct), hangup));
        root.AddChild(fd);
        var cpunote = new NinePDir("cpunote");
        cpunote.AddChild(new Device("data", async ct => Encoding.UTF8.GetBytes(await local.Interrupts.ReadAsync(ct)), null, hangup));
        var mnt = new NinePDir("mnt");
        mnt.AddChild(cpunote);
        root.AddChild(mnt);
        var env = new NinePDir("env");
        env.AddChild(rstatus);

        // Stryker disable once String,Initializer : the server function only removes rfailed; what it holds, and its mode, are never read
        env.AddChild(new NinePFile("rfailed", "lost connection"u8.ToArray()) { Mode = ReadWrite });
        root.AddChild(env);
        root.AddChild(Files(local));
        server = new NinePFSDispatcher(NullLogger<NinePFSDispatcher>.Instance, new FileSystemBackend(root));
    }

    public string Status => Encoding.UTF8.GetString(rstatus.ReadAsync(0, (uint)rstatus.Length, CancellationToken.None).Result);

    public Task<object> DispatchAsync(string sessionId, NinePMessage message, NinePDialect dialect, X509Certificate2? certificate = null)
        => server.DispatchAsync(sessionId, message, dialect, certificate);

    public Task CloseSessionAsync(string sessionId) => server.CloseSessionAsync(sessionId);

    // /root: the tree from / or, on Windows, a directory of the drives.
    private static INinePNode Files(Local local)
    {
        if (local.Drives.Count == 0)
        {
            return new Named("root", new LocalDir(new DirectoryInfo(local.Root)));
        }

        var drives = new NinePDir("root");
        foreach ((string name, string path) in local.Drives)
        {
            drives.AddChild(new Named(name, new LocalDir(new DirectoryInfo(path))));
        }

        return drives;
    }

    private static async Task<byte[]> Read(Func<CancellationToken, ValueTask<string?>> read, CancellationToken ct)
        => Encoding.UTF8.GetBytes(await read(ct) ?? string.Empty);

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        int n = await stream.ReadAsync(buffer, ct);
        return buffer[..n];
    }

    private static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await stream.WriteAsync(data, ct);

        // Stryker disable once Statement : the console's streams are unbuffered, so the flush has nothing to send
        await stream.FlushAsync(ct);
    }

    // A file whose reads and writes are the machine's, each read the next of what it gives, and
    // which ends with the conversation.
    private sealed class Device(
        string name,
        Func<CancellationToken, Task<byte[]>>? read,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? write,
        CancellationToken hangup) : NinePFile(name)
    {
        // What a read gave beyond the count it was asked for, which the next read gives first, as a
        // device keeps the rest of a line for the next read of it.
        private byte[] pending = [];

        public override Stat GetStat(NinePDialect dialect)
        {
            Mode = (read is null ? 0u : 0b100_100_100) | (write is null ? 0u : 0b010_010_010);
            return base.GetStat(dialect);
        }

        public override async Task<byte[]> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            if (pending.Length == 0)
            {
                using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, hangup);

                // Stryker disable once Null coalescing,String : a file with no reader has no read in its mode, so it is never opened to read
                pending = await (read ?? throw new NotSupportedException("permission denied"))(both.Token);
            }

            byte[] data = pending.Length <= count ? pending : pending[..(int)count];
            pending = pending[data.Length..];
            return data;
        }

        public override async Task<uint> WriteAsync(ulong offset, byte[] data, CancellationToken ct)
        {
            // Stryker disable once Null coalescing,String : a file with no writer has no write in its mode, so it is never opened to write
            await (write ?? throw new NotSupportedException("permission denied"))(data, ct);
            return (uint)data.Length;
        }
    }

    // A node served under another name: / as root, and C:\ as C:.
    private sealed class Named(string name, INinePNode node) : INinePNode
    {
        public string Name => name;

        public IUser User => node.User;

        public IGroup Group => node.Group;

        public Stat GetStat(NinePDialect dialect)
        {
            Stat stat = node.GetStat(dialect);
            return new Stat(0, stat.Type, stat.Dev, stat.Qid, stat.Mode, stat.Atime, stat.Mtime, stat.Length, name, stat.Uid, stat.Gid, stat.Muid, dialect);
        }

        public Task<byte[]> ReadAsync(ulong offset, uint count, CancellationToken ct) => node.ReadAsync(offset, count, ct);

        public Task<uint> WriteAsync(ulong offset, byte[] data, CancellationToken ct) => node.WriteAsync(offset, data, ct);

        public Task<INinePNode?> WalkAsync(string name, CancellationToken ct) => node.WalkAsync(name, ct);

        public Task<IEnumerable<INinePNode>> ReaddirAsync(CancellationToken ct) => node.ReaddirAsync(ct);

        public Task<INinePNode> CreateAsync(string name, uint perm, byte mode, CancellationToken ct) => node.CreateAsync(name, perm, mode, ct);

        public Task RemoveAsync(string name, CancellationToken ct) => node.RemoveAsync(name, ct);

        public Task WstatAsync(Stat stat, CancellationToken ct) => node.WstatAsync(stat, ct);

        public Task SymlinkAsync(string name, string target, CancellationToken ct) => node.SymlinkAsync(name, target, ct);

        public Task<string> ReadlinkAsync(CancellationToken ct) => node.ReadlinkAsync(ct);

        public Task LinkAsync(string name, INinePNode target, CancellationToken ct) => node.LinkAsync(name, target, ct);

        public Task<Rlerror> LockAsync(Tlock msg, CancellationToken ct) => node.LockAsync(msg, ct);

        public Task<Rgetlock> GetlockAsync(Tgetlock msg, CancellationToken ct) => node.GetlockAsync(msg, ct);

        public Task<Rxattrwalk> XattrwalkAsync(Txattrwalk msg, CancellationToken ct) => node.XattrwalkAsync(msg, ct);

        public Task<Rxattrcreate> XattrcreateAsync(Txattrcreate msg, CancellationToken ct) => node.XattrcreateAsync(msg, ct);
    }
}
