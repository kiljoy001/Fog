namespace NinePSharp.Fog.Gefs;

internal readonly record struct Message(MessageOp Op, byte[] Key, byte[] Value);
