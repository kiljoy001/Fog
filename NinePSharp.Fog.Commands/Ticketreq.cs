namespace NinePSharp.Fog.Commands;

// authsrv.h's Ticketreq: what a ticket is asked for by, each name a NUL-ended field.
public sealed class Ticketreq
{
    public byte Type { get; set; }

    public byte[] Authid { get; } = new byte[Authsrv.Anamelen];

    public byte[] Authdom { get; } = new byte[Authsrv.Domlen];

    public byte[] Chal { get; } = new byte[Authsrv.Challen];

    public byte[] Hostid { get; } = new byte[Authsrv.Anamelen];

    public byte[] Uid { get; } = new byte[Authsrv.Anamelen];
}
