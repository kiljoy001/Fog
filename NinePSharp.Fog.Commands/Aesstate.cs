using System.Security.Cryptography;

namespace NinePSharp.Fog.Commands;

// libsec's AESstate with setupAESstate, aesCBCencrypt and aesCBCdecrypt: CBC over whole blocks,
// and a last part of a block exclusive-ored with the encrypted ivec, both ways.
public sealed class Aesstate : IDisposable
{
    public const int Aesbsize = 16;

    private readonly Aes aes = Aes.Create();
    private readonly byte[] ivec = new byte[Aesbsize];

    public Aesstate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        aes.Key = key.ToArray();
        iv[..Aesbsize].CopyTo(ivec);
    }

    public void CbcEncrypt(Span<byte> p)
    {
        for (; p.Length >= Aesbsize; p = p[Aesbsize..])
        {
            for (int i = 0; i < Aesbsize; i++)
            {
                ivec[i] ^= p[i];
            }

            Encrypt(ivec);
            ivec.CopyTo(p);
        }

        Tail(p);
    }

    public void CbcDecrypt(Span<byte> p)
    {
        byte[] t = new byte[Aesbsize];
        for (; p.Length >= Aesbsize; p = p[Aesbsize..])
        {
            p[..Aesbsize].CopyTo(t);
            aes.DecryptEcb(t, p[..Aesbsize], PaddingMode.None);
            for (int i = 0; i < Aesbsize; i++)
            {
                p[i] ^= ivec[i];
            }

            t.CopyTo(ivec, 0);
        }

        Tail(p);
    }

    public void Dispose() => aes.Dispose();

    private void Tail(Span<byte> p)
    {
        if (p.Length > 0)
        {
            Encrypt(ivec);
            for (int i = 0; i < p.Length; i++)
            {
                p[i] ^= ivec[i];
            }
        }
    }

    private void Encrypt(byte[] block) => aes.EncryptEcb(block, block, PaddingMode.None);
}
