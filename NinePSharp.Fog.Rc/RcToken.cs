namespace NinePSharp.Fog.Rc;

// rc's token and tree types: the grammar's terminals as yacc numbers them, and the single
// characters the lexer returns as themselves.
internal static class RcToken
{
    internal const int EndOfFile = -1;
    internal const int For = 57346;
    internal const int In = 57347;
    internal const int While = 57348;
    internal const int If = 57349;
    internal const int Not = 57350;
    internal const int Twiddle = 57351;
    internal const int Bang = 57352;
    internal const int Subshell = 57353;
    internal const int Switch = 57354;
    internal const int Fn = 57355;
    internal const int Word = 57356;
    internal const int Redir = 57357;
    internal const int Dup = 57358;
    internal const int Pipe = 57359;
    internal const int Sub = 57360;
    internal const int Simple = 57361;
    internal const int ArgList = 57362;
    internal const int Words = 57363;
    internal const int Brace = 57364;
    internal const int Paren = 57365;
    internal const int Pcmd = 57366;
    internal const int PipeFd = 57367;
    internal const int AndAnd = 57368;
    internal const int OrOr = 57369;
    internal const int Count = 57370;

    // Redirection kinds, a tree's RType.
    internal const int Append = 1;
    internal const int Write = 2;
    internal const int Read = 3;
    internal const int Here = 4;
    internal const int DupFd = 5;
    internal const int Close = 6;
    internal const int ReadWrite = 7;

    // In a word, Glob before *, ? or [ marks it as a pattern character.
    internal const char Glob = '\x01';
}
