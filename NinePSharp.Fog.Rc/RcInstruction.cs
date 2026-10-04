namespace NinePSharp.Fog.Rc;

// One word of a code vector, as rc.h's code union: an operation, a number or a string.
internal readonly record struct RcInstruction(RcOp Op, int I = 0, string? S = null);
