using System.Text;

namespace NinePSharp.Fog.Rc.Tests.Support;

// Reads a script as rc's Xrdcmds does, until the interpreter exists to do it: a line at a time, each
// compiled only when it read without errors. Running a line only defines its functions and prints
// those named by whatis. Interactively, rc prompts as pprompt does and reads on after an error.
internal sealed class RcReader
{
    private readonly Dictionary<string, string> functions = new(StringComparer.Ordinal);
    private readonly StringWriter output = new();

    // rc's strings hold bytes, one per char; Output decodes them as UTF-8.
    internal string Output => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(output.ToString()));

    internal static RcReader Read(byte[] bytes, string file, bool interactive)
    {
        var reader = new RcReader();
        string prompt = "%";
        var lexer = new RcLexer(RcInput.FromBytes(bytes), file, reader.output);
        if (interactive)
        {
            lexer.Prompt = () =>
            {
                reader.output.Write(prompt);
                lexer.DoPrompt = false;
                prompt = "+";
            };
        }

        lexer.ReadLines();
        var parser = new RcParser(lexer);
        while (true)
        {
            prompt = "%";
            lexer.ErrorCount = 0;
            RcParser.Outcome outcome = parser.Parse(tree =>
            {
                if (lexer.ErrorCount != 0)
                {
                    return false;
                }

                reader.Run(tree);
                return true;
            });
            if (outcome == RcParser.Outcome.Stop && (!interactive || lexer.Eof))
            {
                return reader;
            }
        }
    }

    internal string? Function(string name) => functions.GetValueOrDefault(name);

    private void Run(RcTree? tree)
    {
        if (tree is null)
        {
            return;
        }

        if (tree.Type == ';')
        {
            Run(tree.Child[0]);
            Run(tree.Child[1]);
        }
        else if (tree.Type == RcToken.Fn && tree.Child[1] is not null)
        {
            functions[RcPrinter.Print(tree.Child[0])] = RcPrinter.Print(tree.Child[1]);
        }
        else if (tree is { Type: RcToken.Simple, Child: [{ Type: RcToken.ArgList, Child: [{ Str: "whatis" }, { Str: string name }, _] }, ..] })
        {
            output.Write("fn " + RcPrinter.Word(name) + " " + functions[name] + "\n");
        }
    }
}
