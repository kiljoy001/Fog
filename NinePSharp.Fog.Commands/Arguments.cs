namespace NinePSharp.Fog.Commands;

// libc's ARGBEGIN, ARGF and ARGEND: the flags before the first argument that does not start with
// '-', up to a "--".
public sealed class Arguments
{
    private readonly IReadOnlyList<string> argv;
    private int next = 1;
    private string? rest;

    public Arguments(IReadOnlyList<string> argv) => this.argv = argv;

    // The flag letters in order; the arguments left are Rest once they run out.
    public IEnumerable<char> Flags()
    {
        for (; next < argv.Count && argv[next].Length > 1 && argv[next][0] == '-'; next++)
        {
            if (argv[next] == "--")
            {
                next++;
                yield break;
            }

            rest = argv[next][1..];
            while (!string.IsNullOrEmpty(rest))
            {
                char c = rest[0];
                rest = rest[1..];
                yield return c;
            }
        }
    }

    // ARGF: the rest of this argument, or the next one, or null.
    public string? Value()
    {
        if (!string.IsNullOrEmpty(rest))
        {
            string value = rest;
            rest = null;
            return value;
        }

        return next + 1 < argv.Count ? argv[++next] : null;
    }

    public IReadOnlyList<string> Rest() => argv.Skip(next).ToArray();
}
