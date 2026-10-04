using System.Text;
using NinePSharp.Fog.Kernel;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// bind(1), as 9front's bind.c.
public static class Bind
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        MountFlags flag = MountFlags.Replace;
        bool qflag = false;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'a':
                    flag |= MountFlags.After;
                    break;
                case 'b':
                    flag |= MountFlags.Before;
                    break;
                case 'c':
                    flag |= MountFlags.Create;
                    break;
                case 'q':
                    qflag = true;
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        IReadOnlyList<string> names = arguments.Rest();
        if (names.Count != 2 || (flag & (MountFlags.After | MountFlags.Before)) == (MountFlags.After | MountFlags.Before))
        {
            await UsageAsync(process);
        }

        try
        {
            await process.BindAsync(names[0], names[1], flag);
        }
        catch (SyscallException error)
        {
            if (qflag)
            {
                process.Exits(null);
            }

            // A less confusing error than the default.
            string message = await MissingAsync(process, names[0]) is { } first ? $"bind: {names[0]}: {first}"
                : await MissingAsync(process, names[1]) is { } second ? $"bind: {names[1]}: {second}"
                : $"bind {names[0]} {names[1]}: {error.Message}";
            await process.WriteAsync(2, Encoding.UTF8.GetBytes(message + "\n"));
            process.Exits("bind");
        }

        process.Exits(null);
    }

    // access(name, 0) failing, and why.
    private static async Task<string?> MissingAsync(Process process, string name)
    {
        try
        {
            await process.StatAsync(name);
            return null;
        }
        catch (SyscallException error)
        {
            return error.Message;
        }
    }

    private static async Task UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: bind [-b|-a|-c|-bc|-ac] new old\n"u8.ToArray());
        process.Exits("usage");
    }
}
