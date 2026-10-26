namespace NinePSharp.Fog.Commands;

// authsrv.h's Ticket: its number, the server's challenge, the users on the client and server, the
// key, and the form it came in, 0 for DES and 1 for ccpoly.
public sealed class Ticket
{
    public byte Num { get; set; }

    public byte[] Chal { get; } = new byte[Authsrv.Challen];

    public byte[] Cuid { get; } = new byte[Authsrv.Anamelen];

    public byte[] Suid { get; } = new byte[Authsrv.Anamelen];

    public byte[] Key { get; } = new byte[Authsrv.Noncelen];

    public byte Form { get; set; }
}
