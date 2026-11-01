using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// cleanname(1), as 9front's cleanname.c, and libc's cleanname(2) it runs.
public static class Cleanname
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        string? dir = null;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            if (c != 'd' || (dir = arguments.Value()) is null)
            {
                await UsageAsync(process);
            }
        }

        IReadOnlyList<string> names = arguments.Rest();
        if (names.Count < 1)
        {
            await UsageAsync(process);
        }

        var output = new StringBuilder();
        foreach (string name in names)
        {
            output.Append(Clean(dir is null || name.StartsWith('/') ? name : $"{dir}/{name}")).Append('\n');
        }

        await process.WriteAsync(1, Encoding.UTF8.GetBytes(output.ToString()));
        process.Exits(null);
    }

    // cleanname: multiple slashes compressed, . elements removed and .. taken back over the
    // element before it, as far as one can be; the empty name is ".".
    public static string Clean(string name)
    {
        bool rooted = name.StartsWith('/');
        bool erasedprefix = false;
        var q = new StringBuilder(rooted ? "/" : string.Empty);
        int start = q.Length, dotdot = q.Length;
        for (int p = start; p < name.Length;)
        {
            int end = name.IndexOf('/', p) is int s and >= 0 ? s : name.Length;
            string elem = name[p..end];
            if (elem.Length == 0)
            {
                p++;
                continue;
            }

            erasedprefix |= elem == "." && p == 0;
            p = end;
            if (elem == "..")
            {
                if (q.Length > dotdot)
                {
                    q.Length = Math.Max(dotdot, q.ToString().LastIndexOf('/'));
                }
                else if (!rooted)
                {
                    q.Append(q.Length != 0 ? "/.." : "..");
                    dotdot = q.Length;
                }

                erasedprefix |= q.Length == 0;
            }
            else if (elem != ".")
            {
                q.Append(q.Length != start ? "/" : string.Empty).Append(elem);
            }
        }

        string cleaned = q.Length == 0 ? "." : q.ToString();
        return erasedprefix && cleaned.StartsWith('#') ? "./" + cleaned : cleaned;
    }

    private static async Task UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: cleanname [-d pwd] name...\n"u8.ToArray());
        process.Exits("usage");
    }
}
