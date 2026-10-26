using NinePSharp.Constants;
using NinePSharp.Interfaces;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// A request, as req.c keeps it: what was asked, what the answer is to carry, and what flushes wait for it.
public sealed class Req(Srv srv, ISerializable ifcall, MessageTypes type)
{
    public Srv Srv { get; } = srv;

    public ISerializable Ifcall { get; } = ifcall;

    public MessageTypes Type { get; } = type;

    public ushort Tag => Ifcall.Tag;

    public Fid? Fid { get; set; }

    public Fid? Newfid { get; set; }

    public Fid? Afid { get; set; }

    public Req? Oldreq { get; set; }

    public object? Aux { get; set; }

    public string? Error { get; set; }

    public string[] Wname => ((Twalk)Ifcall).Wname;

    public ulong Offset => Ifcall is Tread t ? t.Offset : ((Twrite)Ifcall).Offset;

    // The count asked for, cut to what one message carries.
    public uint Count { get; set; }

    // What a read gives, or what a write was given.
    public byte[] Data { get; set; } = [];

    // What a write reports it wrote.
    public uint Rcount { get; set; }

    public Qid Qid { get; set; }

    public uint Iounit { get; set; }

    public List<Qid> Wqid { get; } = [];

    public NinePSharp.Messages.Stat D { get; set; }

    internal uint Rmsize { get; set; }

    internal string Rversion { get; set; } = string.Empty;

    internal bool Responded { get; set; }

    internal bool Pooled { get; set; }

    internal int Ref { get; set; }

    internal List<Req> Flushes { get; } = [];
}
