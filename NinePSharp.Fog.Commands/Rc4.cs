namespace NinePSharp.Fog.Commands;

// libsec's RC4state, setupRC4state and rc4.
public sealed class Rc4
{
    private readonly byte[] state = new byte[256];
    private int x;
    private int y;

    public Rc4(ReadOnlySpan<byte> start)
    {
        for (int i = 0; i < 256; i++)
        {
            state[i] = (byte)i;
        }

        int index2 = 0;
        int p = 0;
        for (int sp = 0; sp < 256; sp++)
        {
            byte t = state[sp];
            index2 = (start[p] + t + index2) & 255;
            state[sp] = state[index2];
            state[index2] = t;
            if (++p >= start.Length)
            {
                p = 0;
            }
        }
    }

    public void Crypt(Span<byte> p)
    {
        for (int i = 0; i < p.Length; i++)
        {
            x = (x + 1) & 255;
            byte tx = state[x];
            y = (y + tx) & 255;
            byte ty = state[y];
            state[x] = ty;
            state[y] = tx;
            p[i] ^= state[(tx + ty) & 255];
        }
    }
}
