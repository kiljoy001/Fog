using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// secstore's SConn.c: a delimited connection on a descriptor, each record counted as SSL counts
// one, which once the PAK's session secret is set is authenticated with SHA1 and encrypted with
// RC4. An error is written in band, as a message starting with '!'.
public sealed class SConn(Process process, int fd)
{
    // Messages longer than Maxmsg bytes are cut short.
    public const int Maxmsg = 4096;
    public const int Sha1dlen = 20;

    private readonly ConnState @in = new();
    private readonly ConnState @out = new();
    private bool alg;

    public int Secretlen => Sha1dlen;

    // werrstr's text for the last write that failed.
    public string Errstr { get; private set; } = string.Empty;

    // SC_secret: the keys of each direction from sigma, the client's direction 0 and the server's 1.
    public int Secret(ReadOnlySpan<byte> sigma, int direction)
    {
        byte[] one = HMACSHA1.HashData("one"u8, sigma);
        byte[] two = HMACSHA1.HashData("two"u8, sigma);
        (@out.Secret, @in.Secret) = direction != 0 ? (one, two) : (two, one);

        // Restricted to 128 bits.
        @in.Rc4 = new Rc4(@in.Secret.AsSpan(0, 16));
        @out.Rc4 = new Rc4(@out.Secret.AsSpan(0, 16));
        alg = true;
        return 0;
    }

    // SC_read: a record's data in buf, or -1 with why in buf.
    public async Task<int> ReadAsync(byte[] buf, int n)
    {
        byte[] count = await ReadSomeAsync(2);
        if (count.Length != 2 || (count[0] & 0x80) == 0)
        {
            return Fail(buf, n, "!SC_read invalid count");
        }

        // An SSL-style count, with no pad.
        int len = (count[0] & 0x7f) << 8 | count[1];
        if (alg)
        {
            len -= Sha1dlen;
            if (len <= 0)
            {
                return Fail(buf, n, "!SC_read missing sha1");
            }

            byte[] digest = await ReadnAsync(Sha1dlen);
            if (digest.Length != Sha1dlen)
            {
                return Fail(buf, n, "!SC_read missing sha1");
            }

            if (len > n)
            {
                return Fail(buf, n, "!SC_read missing data");
            }

            byte[] data = await ReadnAsync(len);
            if (data.Length != len)
            {
                return Fail(buf, n, "!SC_read missing data");
            }

            @in.Rc4!.Crypt(digest);
            @in.Rc4.Crypt(data);
            data.CopyTo(buf, 0);
            if (!CryptographicOperations.FixedTimeEquals(digest, Hash(@in.Secret, data, @in.Seqno)))
            {
                return Fail(buf, n, "!SC_read integrity check failed");
            }
        }
        else
        {
            if (len <= 0 || len > n)
            {
                return Fail(buf, n, "!SC_read implausible record length");
            }

            byte[] data = await ReadnAsync(len);
            if (data.Length != len)
            {
                return Fail(buf, n, $"!SC_read expected {len} bytes, but got {data.Length}");
            }

            data.CopyTo(buf, 0);
        }

        @in.Seqno++;
        return len;
    }

    // SC_write: the n bytes of buf as a record, or -1.
    public async Task<int> WriteAsync(byte[] buf, int n)
    {
        if (n <= 0 || n > Maxmsg + 1)
        {
            Errstr = $"!SC_write invalid n {n}";
            return -1;
        }

        int len = alg ? n + Sha1dlen : n;
        byte[] count = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(count, (ushort)(0x8000 | len));
        if (!await SendAsync(count))
        {
            Errstr = "!SC_write invalid count";
            return -1;
        }

        if (alg)
        {
            byte[] digest = Hash(@out.Secret, buf.AsSpan(0, n), @out.Seqno);
            @out.Rc4!.Crypt(digest);
            byte[] enc = buf[..n];
            @out.Rc4.Crypt(enc);
            if (!await SendAsync(digest) || !await SendAsync(enc))
            {
                Errstr = "!SC_write error on send";
                return -1;
            }
        }
        else if (!await SendAsync(buf[..n]))
        {
            Errstr = "!SC_write error on send";
            return -1;
        }

        @out.Seqno++;
        return n;
    }

    public Task<int> WriteAsync(string s) => WriteAsync(Encoding.UTF8.GetBytes(s), Encoding.UTF8.GetByteCount(s));

    // SC_free, which closes the descriptor.
    public async Task FreeAsync()
    {
        try
        {
            await process.CloseAsync(fd);
        }
        catch (SyscallException)
        {
            // close's failure is not reported.
        }
    }

    // writerr: the message in band, after a '!'.
    public async Task WriterrAsync(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes($"!{s}");
        await WriteAsync(b, Math.Min(b.Length, Maxmsg - 1));
    }

    // readstr: a record as a string, or -1 with why: one sent in band without its '!', or the
    // connection's failure.
    public async Task<(int N, string S)> ReadstrAsync()
    {
        byte[] buf = new byte[Maxmsg + 1];
        int n = await ReadAsync(buf, Maxmsg);
        if (n == -1)
        {
            return (-1, "connection read error");
        }

        string s = Encoding.UTF8.GetString(buf, 0, n);
        return s.StartsWith('!') ? (-1, s[1..]) : (n, s);
    }

    // hash: SHA1 of the direction's secret, the data, and the record's sequence number.
    private static byte[] Hash(byte[] secret, ReadOnlySpan<byte> data, uint seqno)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(secret);
        sha.AppendData(data);
        byte[] seq = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(seq, seqno);
        sha.AppendData(seq);
        return sha.GetHashAndReset();
    }

    // snprint of the error into buf, as much of it as n bytes hold with its NUL.
    private static int Fail(byte[] buf, int n, string message)
    {
        byte[] b = Encoding.UTF8.GetBytes(message);
        int m = Math.Min(b.Length, n - 1);
        b.AsSpan(0, m).CopyTo(buf);
        buf[m] = 0;
        return -1;
    }

    private async Task<byte[]> ReadSomeAsync(int n)
    {
        try
        {
            return (await process.ReadAsync(fd, n)).ToArray();
        }
        catch (SyscallException)
        {
            return [];
        }
    }

    private async Task<byte[]> ReadnAsync(int n)
    {
        try
        {
            return await Authsrv.ReadnAsync(process, fd, n);
        }
        catch (SyscallException)
        {
            return [];
        }
    }

    private async Task<bool> SendAsync(byte[] b)
    {
        try
        {
            return await process.WriteAsync(fd, b) == b.Length;
        }
        catch (SyscallException)
        {
            return false;
        }
    }

    private sealed class ConnState
    {
        public byte[] Secret { get; set; } = new byte[Sha1dlen];

        public uint Seqno { get; set; }

        public Rc4? Rc4 { get; set; }
    }
}
