using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Commands;

// mount(1), as 9front's mount.c: unless -n, the server is asked to authenticate the caller, through
// factotum, before it attaches; a server that needs no authentication refuses, and the mount goes
// on without it. No getkey prompts for a key factotum lacks.
public static class Mount
{
    private const string Usage = "usage: mount [-a|-b] [-cCnNq] [-k keypattern] /srv/service dir [spec]\n";

    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var arguments = new Arguments(argv);
        (MountFlags flag, bool qflag, bool asnone, bool noauth, string keyspec) = await FlagsAsync(process, arguments);
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
            await BecomeNoneAsync(process, qflag);
        }

        process.Notify(note =>
        {
            process.WriteAsync(2, Encoding.UTF8.GetBytes($"mount: {note}\n")).AsTask().GetAwaiter().GetResult();
            return false;
        });
        try
        {
            if (noauth)
            {
                await process.MountAsync(fd, -1, args[1], flag, spec);
            }
            else
            {
                await Amount0Async(process, fd, args[1], flag, spec, keyspec);
            }
        }
        catch (SyscallException error)
        {
            await FailAsync(process, qflag, $"mount {args[1]}: {error.Message}", "mount");
        }

        process.Exits(null);
    }

    // amount0: fauth, and if the server gave an auth file, factotum proxied through it as the
    // client; then the mount, authenticated by it, and the auth file closed.
    private static async Task Amount0Async(Process process, int fd, string mntpt, MountFlags flags, string aname, string keyspec)
    {
        int afd;
        try
        {
            afd = await process.FauthAsync(fd, aname);
        }
        catch (SyscallException)
        {
            await process.MountAsync(fd, -1, mntpt, flags, aname);
            return;
        }

        try
        {
            await Libauth.AuthProxyAsync(process, afd, null, $"proto=p9any role=client {keyspec}");
        }
        catch (SyscallException error)
        {
            await process.WriteAsync(2, Encoding.UTF8.GetBytes($"mount: auth_proxy: {error.Message}\n"));
        }

        try
        {
            await process.MountAsync(fd, afd, mntpt, flags, aname);
        }
        finally
        {
            await process.CloseAsync(afd);
        }
    }

    // The flags, as mount.c's ARGBEGIN takes them: the mount's, -k's keyspec, -q, -N and -n.
    private static async Task<(MountFlags Flag, bool Qflag, bool Asnone, bool Noauth, string Keyspec)> FlagsAsync(Process process, Arguments arguments)
    {
        MountFlags flag = MountFlags.Replace;
        bool qflag = false, asnone = false, noauth = false;
        string keyspec = string.Empty;
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
                    // Stryker disable once Assignment : Fog's mount keeps no cache, so -C twice, which a toggle would undo, mounts as once does
                    flag |= MountFlags.Cache;
                    break;
                case 'k':
                    if (arguments.Value() is not { } value)
                    {
                        // Stryker disable once Statement : no value for -k leaves no arguments either, which is usage all the same
                        await UsageAsync(process);
                    }
                    else
                    {
                        keyspec = value;
                    }

                    break;
                case 'N':
                    asnone = true;
                    noauth = true;
                    break;
                case 'n':
                    noauth = true;
                    break;
                case 'q':
                    qflag = true;
                    break;
                default:
                    await UsageAsync(process);
                    break;
            }
        }

        return (flag, qflag, asnone, noauth, keyspec);
    }

    // procsetuser of none, which writes #c/user, which Fog, naming no devices by #, binds on /dev.
    private static async Task BecomeNoneAsync(Process process, bool qflag)
    {
        try
        {
            int user = await process.OpenAsync("/dev/user", NinePConstants.OWRITE);
            await process.WriteAsync(user, "none"u8.ToArray());

            // Stryker disable once Statement : mount exits once it has mounted, which closes what it left open
            await process.CloseAsync(user);
        }
        catch (SyscallException error)
        {
            await FailAsync(process, qflag, $"can't become none: {error.Message}", "becomenone");
        }
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
