namespace NinePSharp.Fog.Rc;

// A lexer reading commands from an io, with its parser: what Xrdcmds reads.
internal sealed class RcReading(RcIo input, RcLexer lexer)
{
    public RcIo Input { get; } = input;

    public RcLexer Lexer { get; } = lexer;

    public RcParser Parser { get; } = new(lexer);
}
