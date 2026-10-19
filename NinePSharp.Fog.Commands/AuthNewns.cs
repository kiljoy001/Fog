using System.Text;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Commands;

// auth/newns, as 9front's newns.c: the command, rc -i by default, run in the namespace newns builds
// for the user from a namespace file, or with -a in the one addns adds to.
public static class AuthNewns
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        string file = "/lib/namespace";
        bool add = false;
        bool debug = false;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'a':
                    add = true;
                    break;
                case 'd':
                    debug = true;
                    break;
                case 'n':
                    file = arguments.Value() ?? await UsageAsync(process, argv[0]);
                    break;
                default:
                    await UsageAsync(process, argv[0]);
                    break;
            }
        }

        string user = await Newns.GetuserAsync(process);
        try
        {
            if (add)
            {
                await process.RforkAsync(RforkFlags.Nameg);
                await Newns.AddnsAsync(process, user, file, debug);
            }
            else
            {
                await Newns.NewnsAsync(process, user, file, debug);
            }
        }
        catch (SyscallException)
        {
            // newns.c runs the command whether or not the namespace was made.
        }

        IReadOnlyList<string> command = arguments.Rest() is { Count: > 0 } rest ? rest : ["/bin/rc", "-i"];
        await RunAsync(process, command);
    }

    // run: the command as named, then from /bin unless its name is rooted or starts at . or ..
    private static async Task RunAsync(Process process, IReadOnlyList<string> a)
    {
        string error;
        try
        {
            await process.ExecAsync(a[0], a);
            return;
        }
        catch (SyscallException first)
        {
            error = first.Message;
        }

        if (!(a[0].StartsWith('/') || a[0].StartsWith('#') || a[0].StartsWith("./", StringComparison.Ordinal) || a[0].StartsWith("../", StringComparison.Ordinal)))
        {
            try
            {
                await process.ExecAsync($"/bin/{a[0]}", a);
                return;
            }
            catch (SyscallException second)
            {
                error = second.Message;
            }
        }

        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"{process.Text}: exec: {a[0]}: {error}\n"));
        process.Exits($"exec: {a[0]}: {error}");
    }

    private static async Task<string> UsageAsync(Process process, string argv0)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"usage: {argv0} [-ad] [-n namespace] [cmd [args...]]\n"));
        process.Exits("usage");
        return string.Empty;
    }
}
