using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Commands;

// rm(1), as 9front's rm.c: the status is the last error reported, so -f leaves it empty.
public static class Rm
{
    public static async Task MainAsync(Process process, IReadOnlyList<string> argv)
    {
        var rm = new Remover(process);
        bool recurse = false;
        var arguments = new Arguments(argv);
        foreach (char c in arguments.Flags())
        {
            switch (c)
            {
                case 'r':
                    recurse = true;
                    break;
                case 'f':
                    rm.IgnoreErrors = true;
                    break;
                default:
                    await process.WriteAsync(2, "usage: rm [-fr] file ...\n"u8.ToArray());
                    process.Exits("usage");
                    break;
            }
        }

        foreach (string f in arguments.Rest())
        {
            if (await rm.RemoveAsync(f) is not { } error)
            {
                continue;
            }

            if (recurse && await rm.DirstatAsync(f) is { } db && (db.Qid.Type & QidType.QTDIR) != 0)
            {
                await rm.RmdirAsync(f);
            }
            else
            {
                await rm.ErrAsync(f, error);
            }
        }

        process.Exits(rm.Errbuf);
    }

    private sealed class Remover(Process process)
    {
        public bool IgnoreErrors { get; set; }

        public string Errbuf { get; private set; } = string.Empty;

        // remove: null, or why it failed.
        public async Task<string?> RemoveAsync(string f)
        {
            try
            {
                await process.RemoveAsync(f);
                return null;
            }
            catch (SyscallException error)
            {
                return error.Message;
            }
        }

        // dirstat: nil for a file that cannot be stated.
        public async Task<Stat?> DirstatAsync(string f)
        {
            Stat? dir = null;
            try
            {
                dir = await process.StatAsync(f);
            }
            catch (SyscallException)
            {
            }

            return dir;
        }

        public async Task ErrAsync(string f, string error)
        {
            if (!IgnoreErrors)
            {
                Errbuf = error;
                await process.WriteAsync(2, Encoding.UTF8.GetBytes($"rm: {f}: {error}\n"));
            }
        }

        // rmdir: f is a directory that is not empty; its contents go, then it.
        public async Task RmdirAsync(string f)
        {
            int fd;
            try
            {
                fd = await process.OpenAsync(f, NinePConstants.OREAD);
            }
            catch (SyscallException error)
            {
                await ErrAsync(f, error.Message);
                return;
            }

            IReadOnlyList<Stat> dirbuf;
            try
            {
                dirbuf = await process.DirReadAsync(fd);
            }
            catch (SyscallException error)
            {
                await ErrAsync("dirreadall", error.Message);
                return;
            }
            finally
            {
                await process.CloseAsync(fd);
            }

            var directories = new List<string>();
            foreach (Stat entry in dirbuf)
            {
                string name = $"{f}/{entry.Name}";
                if (await RemoveAsync(name) is not { } error)
                {
                    continue;
                }

                if ((entry.Qid.Type & QidType.QTDIR) != 0)
                {
                    directories.Add(name);
                }
                else
                {
                    await ErrAsync(name, error);
                }
            }

            foreach (string name in directories)
            {
                await RmdirAsync(name);
            }

            if (await RemoveAsync(f) is { } failed)
            {
                await ErrAsync(f, failed);
            }
        }
    }
}
