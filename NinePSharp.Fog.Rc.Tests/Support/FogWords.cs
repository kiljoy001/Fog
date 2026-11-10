using System.Text;

namespace NinePSharp.Fog.Rc.Tests.Support;

// A scenario's words for fog: \n for a newline, and rc's quoting of a command line.
public static class FogWords
{
    public static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.Ordinal);

    // rc's words: blanks separate them, and single quotes hold blanks, '' a quote.
    public static string[] Words(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' && quoted && i + 1 < line.Length && line[i + 1] == '\'')
            {
                word.Append('\'');
                i++;
            }
            else if (c == '\'')
            {
                (quoted, any) = (!quoted, true);
            }
            else if (c == ' ' && !quoted)
            {
                if (any || word.Length > 0)
                {
                    words.Add(word.ToString());
                }

                (word, any) = (new StringBuilder(), false);
            }
            else
            {
                word.Append(c);
            }
        }

        if (any || word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return [.. words];
    }
}
