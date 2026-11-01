using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// basename(1), as 9front's basename.c.
public static class Basename
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        bool dflag = argv.Count > 1 && argv[1] == "-d";
        IReadOnlyList<string> args = argv.Skip(dflag ? 2 : 1).ToArray();
        if (args.Count is < 1 or > 2)
        {
            await process.WriteAsync(2, "usage: basename [-d] string [suffix]\n"u8.ToArray());
            process.Exits("usage");
        }

        int slash = args[0].LastIndexOf('/');
        string pr = args[0][(slash + 1)..];
        if (dflag)
        {
            pr = slash >= 0 ? args[0][..slash] : ".";
        }
        else if (args.Count == 2 && pr.EndsWith(args[1], StringComparison.Ordinal))
        {
            pr = pr[..^args[1].Length];
        }

        await process.WriteAsync(1, Encoding.UTF8.GetBytes(pr + "\n"));
        process.Exits(null);
    }
}
