namespace NinePSharp.Fog.Commands;

// A local address of an interface, with its mask and the network they give.
public sealed record Iplifc(byte[] Ip, byte[] Mask, byte[] Net);
