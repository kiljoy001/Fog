namespace NinePSharp.Fog.Commands;

// authsrv.h's Authenticator: its number, a challenge, and a nonce.
public sealed class Authenticator
{
    public byte Num { get; set; }

    public byte[] Chal { get; } = new byte[Authsrv.Challen];

    public byte[] Rand { get; } = new byte[Authsrv.Noncelen];
}
