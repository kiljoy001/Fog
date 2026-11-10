using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;
using NQid = NinePSharp.Constants.Qid;

namespace NinePSharp.Fog.Gefs.Tests.Support;

// A 9P client on an in-memory connection to a GefsConn, its messages written in a short form: the
// type, then its fields in order, as "Twalk 1 2 usr notes" or "Tattach 1 -1 glenda main". A fid of
// -1 is NOFID. Replies are written back the same way.
internal sealed class NinePClient
{
    private readonly Channel<byte[]> toServer = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> toClient = Channel.CreateUnbounded<byte[]>();
    private byte[] pending = [];
    private ushort tag;

    public NinePClient(GefsServer server, Func<Task<IGefsAuth>> authnew)
    {
        Conn = new GefsConn(server, ReadnAsync, reply => Deaf ? throw new IOException("i/o on hungup channel") : toClient.Writer.WriteAsync(reply).AsTask(), authnew);
        Serving = Task.Run(Conn.ServeAsync);
    }

    public GefsConn Conn { get; }

    public Task Serving { get; }

    // What the server writes fails from now on, as a write to a hung up channel does.
    public bool Deaf { get; set; }

    public static ISerializable Message(string text, ushort tag)
    {
        string[] f = text.Split(' ');
        uint Fid(int i) => f[i] == "-1" ? NinePConstants.NoFid : uint.Parse(f[i]);
        string Rest(int i) => string.Join(' ', f[i..]);
        return f[0] switch
        {
            "Tversion" => new Tversion(tag, uint.Parse(f[1]), f[2]),
            "Tauth" => new Tauth(tag, Fid(1), f[2], f.Length > 3 ? f[3] : string.Empty),
            "Tattach" => new Tattach(tag, Fid(1), Fid(2), f[3], f.Length > 4 ? f[4] : string.Empty),
            "Twalk" => new Twalk(tag, Fid(1), Fid(2), f[3..]),
            "Topen" => new Topen(tag, Fid(1), Convert.ToByte(f[2], 8)),
            "Tcreate" => new Tcreate(tag, Fid(1), f[2], Mode(f[3]), Convert.ToByte(f[4], 8)),
            "Tread" => new Tread(tag, Fid(1), ulong.Parse(f[2]), uint.Parse(f[3])),
            "Twrite" => new Twrite(tag, Fid(1), ulong.Parse(f[2]), Encoding.UTF8.GetBytes(Rest(3))),
            "Tclunk" => new Tclunk(tag, Fid(1)),
            "Tremove" => new Tremove(tag, Fid(1)),
            "Tstat" => new Tstat(tag, Fid(1)),
            "Tflush" => new Tflush(tag, ushort.Parse(f[1])),
            "Twstat" => new Twstat(tag, Fid(1), Change(f[2], Rest(3))),
            _ => throw new ArgumentException(text),
        };
    }

    // The reply in the short form: a directory's read as the names of its entries.
    public static string Text(byte[] r)
    {
        return (MessageTypes)r[4] switch
        {
            MessageTypes.Rversion => $"Rversion {new Rversion(r).MSize} {new Rversion(r).Version}",
            MessageTypes.Rauth => $"Rauth {Type(new Rauth(r).Aqid)}",
            MessageTypes.Rattach => $"Rattach {Type(new Rattach(r).Qid)}",
            MessageTypes.Rwalk => $"Rwalk {string.Join(' ', new Rwalk(r).Wqid.Select(Type))}".TrimEnd(),
            MessageTypes.Ropen => $"Ropen {Type(new Ropen(r).Qid)} {new Ropen(r).Iounit}",
            MessageTypes.Rcreate => $"Rcreate {Type(new Rcreate(r).Qid)} {new Rcreate(r).Iounit}",
            MessageTypes.Rread => $"Rread {Encoding.UTF8.GetString(new Rread(r).Data.Span)}".TrimEnd(),
            MessageTypes.Rwrite => $"Rwrite {new Rwrite(r).Count}",
            MessageTypes.Rstat => $"Rstat {Stat(new Rstat(r).Stat)}",
            MessageTypes.Rerror => $"Rerror {new Rerror(r).Ename}",
            var type => type.ToString(),
        };
    }

    // A stat in the short form: name, owner, group, last modifier, mode in octal, length, and
    // its times; the type and device as given.
    public static string Stat(Stat s)
        => $"{(s.Name.Length == 0 ? "''" : s.Name)} {s.Uid} {s.Gid} {s.Muid} {Convert.ToString(s.Mode & 0x1FF, 8)} {s.Length} {s.Atime} {s.Mtime} {s.Type:x} {s.Dev:x}";

    // A directory read's stats, each in the short form, one to a line.
    public static string[] Stats(byte[] r)
    {
        var read = new Rread(r);
        byte[] data = read.Data.ToArray();
        var stats = new List<string>();
        for (int offset = 0; offset < data.Length;)
        {
            stats.Add(Stat(new Stat(data, ref offset)));
        }

        return [.. stats];
    }

    public async Task<byte[]> RpcAsync(string text)
    {
        await SendAsync(Message(text, ++tag));
        return await ReceiveAsync();
    }

    public async Task SendAsync(ISerializable message)
    {
        var bytes = new byte[message.Size];
        message.WriteTo(bytes);
        await SendBytesAsync(bytes);
    }

    public ValueTask SendBytesAsync(byte[] bytes) => toServer.Writer.WriteAsync(bytes);

    public async Task<byte[]> ReceiveAsync() => await toClient.Reader.ReadAsync().AsTask().WaitAsync(Bounded.Limit);

    // The client hangs up: the server reads the end of the connection.
    public void HangUp() => toServer.Writer.TryComplete();

    private static uint Mode(string mode) => mode[0] == 'd' ? 0x80000000 | Convert.ToUInt32(mode[1..], 8) : Convert.ToUInt32(mode, 8);

    private static string Type(NQid qid) => qid.Type switch
    {
        QidType.QTDIR => "dir",
        QidType.QTAUTH => "auth",
        _ => "file",
    };

    // A wstat changing one field, the rest left as they are.
    private static Stat Change(string field, string value)
    {
        var none = new NQid((QidType)0xff, uint.MaxValue, ulong.MaxValue);
        uint mode = field == "mode" ? Mode(value) : uint.MaxValue;
        uint mtime = field == "mtime" ? uint.Parse(value) : uint.MaxValue;
        ulong length = field == "length" ? ulong.Parse(value) : ulong.MaxValue;
        string Named(string name) => field == name ? value : string.Empty;
        return new Stat(0, ushort.MaxValue, uint.MaxValue, none, mode, uint.MaxValue, mtime, length, Named("name"), Named("owner"), Named("group"), Named("muid"));
    }

    // readn on the server's end: n bytes, or fewer once the client has hung up.
    private async Task<byte[]> ReadnAsync(int n)
    {
        while (pending.Length < n && await toServer.Reader.WaitToReadAsync())
        {
            pending = [.. pending, .. await toServer.Reader.ReadAsync()];
        }

        int got = Math.Min(n, pending.Length);
        byte[] taken = pending[..got];
        pending = pending[got..];
        return taken;
    }
}
