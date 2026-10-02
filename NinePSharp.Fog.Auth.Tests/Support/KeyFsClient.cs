using System.Net.Sockets;
using System.Text;
using NinePSharp.Client;
using NinePSharp.Constants;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Auth.Tests.Support;

/// <summary>A 9P2000 client on the keyfs admin socket, with path-level helpers.</summary>
internal sealed class KeyFsClient : IDisposable
{
    internal const uint RootFid = 0;
    private const byte OREAD = 0;
    private const byte OWRITE = 1;
    private const uint DMDIR = 0x80000000;
    private readonly Socket socket;
    private readonly NinePClient client;
    private uint nextFid = 1;

    private KeyFsClient(Socket socket, NinePClient client)
    {
        this.socket = socket;
        this.client = client;
    }

    /// <summary>Connects and negotiates 9P2000, without attaching.</summary>
    internal static async Task<KeyFsClient> ConnectAsync(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        var client = new NinePClient(new NetworkStream(socket, ownsSocket: false));
        await client.VersionAsync(8192, "9P2000");
        return new KeyFsClient(socket, client);
    }

    /// <summary>Connects and attaches the root fid with afid NOFID.</summary>
    internal static async Task<KeyFsClient> AttachAsync(string socketPath)
    {
        KeyFsClient keyfs = await ConnectAsync(socketPath);
        await keyfs.Raw.AttachAsync(RootFid, NinePConstants.NoFid, Environment.UserName, "");
        return keyfs;
    }

    internal NinePClient Raw => client;

    /// <summary>Walks from the root to a slash-separated path into a new fid.</summary>
    internal async Task<uint> WalkAsync(string path)
    {
        uint fid = nextFid++;
        string[] names = path.Length == 0 ? [] : path.Split('/');
        Rwalk walked = await client.WalkAsync(RootFid, fid, names);
        if (walked.Wqid.Length != names.Length) throw new NinePException("file not found");
        return fid;
    }

    internal async Task<byte[]> ReadAsync(string path)
    {
        uint fid = await WalkAsync(path);
        try
        {
            return await ReadFidAsync(fid);
        }
        finally
        {
            await ClunkQuietlyAsync(fid);
        }
    }

    internal async Task<byte[]> ReadFidAsync(uint fid)
    {
        await client.OpenAsync(fid, OREAD);
        var data = new List<byte>();
        while (true)
        {
            Rread read = await client.ReadAsync(fid, (ulong)data.Count, 4096);
            if (read.Count == 0) return data.ToArray();
            data.AddRange(read.Data.ToArray());
        }
    }

    internal async Task<string> ReadTextAsync(string path) => Encoding.UTF8.GetString(await ReadAsync(path)).TrimEnd('\n');

    internal async Task WriteAsync(string path, byte[] data)
    {
        uint fid = await WalkAsync(path);
        try
        {
            await client.OpenAsync(fid, OWRITE);
            await client.WriteAsync(fid, 0, data);
        }
        finally
        {
            await ClunkQuietlyAsync(fid);
        }
    }

    /// <summary>Writes through a walked fid without opening it, reaching the server's own write checks.</summary>
    internal async Task WriteRawAsync(string path, byte[] data)
    {
        uint fid = await WalkAsync(path);
        try
        {
            await client.WriteAsync(fid, 0, data);
        }
        finally
        {
            await ClunkQuietlyAsync(fid);
        }
    }

    internal Task WriteTextAsync(string path, string text) => WriteAsync(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Creates an entry in a directory; directories are made with DMDIR|0777.</summary>
    internal async Task CreateAsync(string directory, string name, bool isDirectory)
    {
        uint fid = await WalkAsync(directory);
        try
        {
            await client.CreateAsync(fid, name, isDirectory ? DMDIR | 0x1FF : 0x1B6, OREAD);
        }
        finally
        {
            await ClunkQuietlyAsync(fid);
        }
    }

    internal async Task RemoveAsync(string path) => await client.RemoveAsync(await WalkAsync(path));

    /// <summary>Renames an entry with a wstat that changes only its name.</summary>
    internal async Task RenameAsync(string path, string name)
    {
        uint fid = await WalkAsync(path);
        try
        {
            await client.WstatAsync(fid, RenameStat(name));
        }
        finally
        {
            await ClunkQuietlyAsync(fid);
        }
    }

    /// <summary>A wstat that changes only the name; every other field is "don't touch".</summary>
    internal static Stat RenameStat(string name)
        => new(0, ushort.MaxValue, uint.MaxValue, new Qid((QidType)0xFF, uint.MaxValue, ulong.MaxValue),
            uint.MaxValue, uint.MaxValue, uint.MaxValue, ulong.MaxValue, name, "", "", "");

    /// <summary>Lists a directory's entries.</summary>
    internal async Task<IReadOnlyList<Stat>> ListAsync(string path)
    {
        byte[] data = await ReadAsync(path);
        var entries = new List<Stat>();
        int offset = 0;
        while (offset < data.Length) entries.Add(new Stat(data, ref offset));
        return entries;
    }

    internal async Task<Stat> StatAsync(string path)
    {
        uint fid = await WalkAsync(path);
        try
        {
            return (await client.StatAsync(fid)).Stat;
        }
        finally
        {
            await ClunkQuietlyAsync(fid);
        }
    }

    public void Dispose()
    {
        client.Dispose();
        socket.Dispose();
    }

    private async Task ClunkQuietlyAsync(uint fid)
    {
        try
        {
            await client.ClunkAsync(fid);
        }
        catch (NinePException)
        {
        }
    }
}
