using System.Security.Cryptography;
using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// auth/aescbc, as 9front's aescbc.c: encrypts a file as the v2 header, a 16-byte IV, AES-CBC of a
// random block and the file, and HMAC-SHA1 under md5 of the key of what was encrypted; decrypts
// that, or a file secstore stored, which ends in its check of X's.
public sealed class Aescbc(Process process, string argv0)
{
    private const int Chk = 16;
    private const int Buf = 4096;
    private const int Sha1dlen = 20;
    private const int Md5dlen = 16;
    private static readonly byte[] V2hdr = "AES CBC SHA1  2\n"u8.ToArray();
    private readonly List<byte> pending = [];
    private bool eof;

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => new Aescbc(process, argv[0]).RunAsync(argv);

    private async Task RunAsync(IReadOnlyList<string> argv)
    {
        bool encrypt = false, passStdin = false, passNvram = false;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'e':
                    encrypt = true;
                    break;
                case 'i':
                    passStdin = true;
                    break;
                case 'n':
                    passNvram = true;
                    break;
            }
        }

        if (arguments.Rest().Count != 0)
        {
            await PrintAsync($"usage: {argv0} -d < cipher.aes > clear.txt\n");
            await PrintAsync($"   or: {argv0} -e < clear.txt > cipher.aes\n");
            process.Exits("usage");
        }

        byte[] pass = await KeyAsync(passStdin, passNvram);
        if (pass.Length <= 0)
        {
            await SysfatalAsync("no key");
        }

        byte[] key = SHA1.HashData([.. "aescbc file"u8, .. pass])[..16];

        // So even if HMAC_SHA1 is broken, the encryption key is protected.
        byte[] key2 = MD5.HashData(key);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, key2[..Md5dlen]);
        if (encrypt)
        {
            await EncryptAsync(key, hmac);
        }
        else
        {
            await DecryptAsync(key, hmac);
        }

        process.Exits(null);
    }

    private async Task<byte[]> KeyAsync(bool passStdin, bool passNvram)
    {
        if (passStdin)
        {
            byte[] buf = await Authsrv.ReadnAsync(process, 3, Buf + Sha1dlen - 1);
            if (buf.Length < 1)
            {
                await SysfatalAsync("usage: echo password |[3=1] auth/aescbc -i ...");
            }

            int n = buf.Length;
            while (n > 0 && buf[n - 1] == '\n')
            {
                n--;
            }

            return buf[..n];
        }

        if (passNvram)
        {
            var nvr = new Nvrsafe();
            if (await Authsrv.ReadnvramAsync(process, nvr, 0) < 0)
            {
                await SysfatalAsync($"readnvram: {Errstr.Get(process)}");
            }

            return Encoding.UTF8.GetBytes(Authsrv.Str(nvr.Config));
        }

        string? typed = await Authsrv.ReadconsAsync(process, "aescbc key", null, true);
        if (typed is null)
        {
            await SysfatalAsync("key input aborted");
        }

        byte[] key = Encoding.UTF8.GetBytes(typed!);
        if (key.Length >= Buf)
        {
            await SysfatalAsync("key too long");
        }

        return key;
    }

    private async Task EncryptAsync(byte[] key, IncrementalHash hmac)
    {
        await SafewriteAsync(V2hdr);

        // CBC is semantically secure if the IV is unpredictable: the first block is the IV, and the
        // second the initial plaintext.
        byte[] buf = new byte[2 * Aesstate.Aesbsize];
        Authsrv.Genrandom(buf);
        using var aes = new Aesstate(key, buf);
        aes.CbcEncrypt(buf.AsSpan(Aesstate.Aesbsize));
        await SafewriteAsync(buf);
        hmac.AppendData(buf, Aesstate.Aesbsize, Aesstate.Aesbsize);
        while (true)
        {
            byte[] block = await BreadAsync(Buf);
            aes.CbcEncrypt(block);
            await SafewriteAsync(block);
            hmac.AppendData(block);
            if (block.Length < Buf)
            {
                // EOF.
                break;
            }
        }

        await SafewriteAsync(hmac.GetHashAndReset());
    }

    private async Task DecryptAsync(byte[] key, IncrementalHash hmac)
    {
        byte[] hdr = await SafereadAsync(Aesstate.Aesbsize);
        if (hdr.AsSpan().SequenceEqual(V2hdr))
        {
            // The IV and the random initial plaintext.
            byte[] iv = await SafereadAsync(2 * Aesstate.Aesbsize);
            using var aes = new Aesstate(key, iv);
            hmac.AppendData(iv, Aesstate.Aesbsize, Aesstate.Aesbsize);
            aes.CbcDecrypt(iv.AsSpan(Aesstate.Aesbsize));

            // The last SHA1dlen bytes read are not yet decrypted, as they may be the HMAC.
            byte[] buf = await SafereadAsync(Sha1dlen);
            for (byte[] more; (more = await BreadAsync(Buf)).Length > 0;)
            {
                byte[] all = [.. buf, .. more];
                byte[] chunk = all[..more.Length];
                hmac.AppendData(chunk);
                aes.CbcDecrypt(chunk);
                await SafewriteAsync(chunk);
                buf = all[more.Length..];
            }

            if (!buf.AsSpan().SequenceEqual(hmac.GetHashAndReset()))
            {
                await SysfatalAsync("decrypted file failed to authenticate");
            }

            return;
        }

        // Compatibility with a past mistake: decrypting a file secstore stored, whose IV is what was
        // taken for the header.
        using var compat = new Aesstate(key, hdr);
        byte[] carry = await SafereadAsync(Chk);
        compat.CbcDecrypt(carry);
        for (byte[] more; (more = await BreadAsync(Buf)).Length > 0;)
        {
            compat.CbcDecrypt(more);
            byte[] all = [.. carry, .. more];
            await SafewriteAsync(all[..more.Length]);
            carry = all[more.Length..];
        }

        if (!carry.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(new string('X', Chk))))
        {
            await SysfatalAsync("decrypted file failed to authenticate");
        }
    }

    // Bread: count bytes of standard input, or fewer when it ends first.
    private async Task<byte[]> BreadAsync(int count)
    {
        while (pending.Count < count && !eof)
        {
            ReadOnlyMemory<byte> b;
            try
            {
                b = await process.ReadAsync(0, Buf);
            }
            catch (SyscallException)
            {
                await SysfatalAsync("read error");
                return [];
            }

            eof = b.IsEmpty;
            pending.AddRange(b.Span);
        }

        byte[] got = [.. pending.Take(count)];
        pending.RemoveRange(0, got.Length);
        return got;
    }

    private async Task<byte[]> SafereadAsync(int n)
    {
        byte[] b = await BreadAsync(n);
        if (b.Length != n)
        {
            await SysfatalAsync("read error");
        }

        return b;
    }

    private async Task SafewriteAsync(byte[] buf)
    {
        try
        {
            if (await process.WriteAsync(1, buf) != buf.Length)
            {
                await SysfatalAsync("write error");
            }
        }
        catch (SyscallException)
        {
            await SysfatalAsync("write error");
        }
    }

    private async Task SysfatalAsync(string message)
    {
        await PrintAsync($"{argv0}: {message}\n");
        process.Exits(message);
    }

    private Task PrintAsync(string text) => process.WriteAsync(2, Encoding.UTF8.GetBytes(text)).AsTask();
}
