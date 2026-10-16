using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// mount(1), as 9front's mount.c. Until Fog has factotum, there is no fauth to authenticate with, so
// a mount without -n goes on unauthenticated, as 9front's does to a server that needs none.
public static class Mount
{
    private const string Usage = "usage: mount [-a|-b] [-cCnNq] [-k keypattern] /srv/service dir [spec]\n";

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        MountFlags flag = MountFlags.Replace;
        bool qflag = false, asnone = false;
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
                case 'C':
                    flag |= MountFlags.Cache;
                    break;
                case 'k':
                    if (arguments.Value() is null)
                    {
                        await UsageAsync(process);
                    }

                    break;
                case 'N':
                    asnone = true;
                    break;
                case 'n':
                    break;
                case 'q':
                    qflag = true;
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        IReadOnlyList<string> args = arguments.Rest();
        string spec = string.Empty;
        if (args.Count == 3)
        {
            spec = args[2];
        }
        else if (args.Count != 2)
        {
            await UsageAsync(process);
        }

        if ((flag & MountFlags.After) != 0 && (flag & MountFlags.Before) != 0)
        {
            await UsageAsync(process);
        }

        int fd;
        try
        {
            fd = await process.OpenAsync(args[0], NinePConstants.ORDWR);
        }
        catch (SyscallException error)
        {
            await FailAsync(process, qflag, $"can't open {args[0]}: {error.Message}", "open");
            return;
        }

        if (asnone)
        {
            try
            {
                // procsetuser writes #c/user, which Fog, naming no devices by #, binds on /dev.
                int user = await process.OpenAsync("/dev/user", NinePConstants.OWRITE);
                await process.WriteAsync(user, "none"u8.ToArray());
                await process.CloseAsync(user);
            }
            catch (SyscallException error)
            {
                await FailAsync(process, qflag, $"can't become none: {error.Message}", "becomenone");
            }
        }

        process.Notify(note =>
        {
            process.WriteAsync(2, Encoding.UTF8.GetBytes($"mount: {note}\n")).AsTask().GetAwaiter().GetResult();
            return false;
        });
        try
        {
            await process.MountAsync(fd, -1, args[1], flag, spec);
        }
        catch (SyscallException error)
        {
            await FailAsync(process, qflag, $"mount {args[1]}: {error.Message}", "mount");
        }

        process.Exits(null);
    }

    private static async Task FailAsync(Process process, bool qflag, string message, string status)
    {
        if (qflag)
        {
            process.Exits(null);
        }

        await process.WriteAsync(2, Encoding.UTF8.GetBytes($"mount: {message}\n"));
        process.Exits(status);
    }

    private static async Task UsageAsync(Process process)
    {
        await process.WriteAsync(2, Encoding.UTF8.GetBytes(Usage));
        process.Exits("usage");
    }
}
