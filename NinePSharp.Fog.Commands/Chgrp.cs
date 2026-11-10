using System.Text;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// chgrp(1), as 9front's chgrp.c: each file's group, or with -u or -o its owner, set by a wstat that
// changes nothing else.
public static class Chgrp
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var arguments = new Arguments(argv);
        bool uflag = false;
        foreach (char c in arguments.Flags())
        {
            if (c is not ('u' or 'o'))
            {
                await UsageAsync(process);
            }

            uflag = true;
        }

        IReadOnlyList<string> args = arguments.Rest();
        if (args.Count < 1)
        {
            await UsageAsync(process);
        }

        string group = args[0];
        string? errs = null;
        foreach (string file in args.Skip(1))
        {
            Stat d = Process.NullDir;
            var dir = new Stat(0, d.Type, d.Dev, d.Qid, d.Mode, d.Atime, d.Mtime, d.Length, d.Name, uflag ? group : d.Uid, uflag ? d.Gid : group, d.Muid);
            try
            {
                await process.WStatAsync(file, dir);
            }
            catch (SyscallException error)
            {
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"chgrp: can't wstat {file}: {error.Message}\n"));
                errs = "can't wstat";
            }
        }

        process.Exits(errs);
    }

    private static async Task UsageAsync(Process process)
    {
        await process.WriteAsync(2, "usage: chgrp [ -uo ] group file ....\n"u8.ToArray());
        process.Exits("usage");
    }
}
