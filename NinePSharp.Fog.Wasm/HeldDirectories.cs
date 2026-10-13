using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;

namespace NinePSharp.Fog.Wasm;

// The directories the application holds, by descriptor. Plan 9 has no open relative to a
// descriptor, so each is kept by the name the application knows it by, its true path in the
// process's namespace, and its identity when the application got it. A path beneath one is cleaned
// first: an absolute path, or a .. above the directory, is an escape, NOTCAPABLE. The true path is
// then resolved through the namespace as it is now, and what it names must have the identity held,
// or the call is NOENT and nothing is walked from it. Calls run on the guest's thread.
internal sealed class HeldDirectories(Process process)
{
    private readonly Dictionary<int, Held> held = [];

    public static bool IsDirectory(Stat stat) => (stat.Qid.Type & QidType.QTDIR) != 0;

    // The directories the parent left open are the preopens, under the names they were opened by.
    public async Task FindPreopensAsync()
    {
        int listing = await process.OpenAsync("/fd", NinePConstants.OREAD);
        IEnumerable<int> open = (await process.DirReadAsync(listing)).Select(entry => entry.Name).Where(name => !name.EndsWith("ctl", StringComparison.Ordinal)).Select(int.Parse);
        foreach (int fd in open.Where(fd => fd != listing))
        {
            if (IsDirectory(await process.FStatAsync(fd)))
            {
                string name = await process.Fd2PathAsync(fd);
                held[fd] = new Held(name, name, Identity(await process.StatAsync(name)), true);
            }
        }

        await process.CloseAsync(listing);
    }

    public bool Holds(int fd) => held.ContainsKey(fd);

    public string? Preopen(int fd) => held.TryGetValue(fd, out Held? directory) && directory.Preopen ? directory.Name : null;

    // The true path a path beneath the directory descriptor fd names.
    public string Resolve(int fd, string path)
    {
        Held directory = Directory(fd);
        if (path.Length == 0)
        {
            throw new SyscallException(Errors.NotExist);
        }

        if (path.StartsWith('/'))
        {
            throw new NotCapableException();
        }

        var kept = new List<string>();
        foreach (string element in path.Split('/'))
        {
            if (element == "..")
            {
                kept.RemoveAt(kept.Count > 0 ? kept.Count - 1 : throw new NotCapableException());
            }
            else if (element is not ("" or "."))
            {
                kept.Add(element);
            }
        }

        if (Identity(Wait(process.StatAsync(directory.TruePath))) != directory.Identity)
        {
            throw new SyscallException(Errors.NotExist);
        }

        return $"{directory.TruePath}/{string.Join('/', kept)}";
    }

    // A directory opened as newfd, at its true path; only a preopen's name is ever reported.
    public void Opened(int newfd, string truePath) => held[newfd] = new Held(truePath, truePath, Identity(Wait(process.StatAsync(truePath))), false);

    public void Closed(int fd) => held.Remove(fd);

    public void Renumbered(int fd, int to)
    {
        held.Remove(to);
        if (held.Remove(fd, out Held? moved))
        {
            held[to] = moved;
        }
    }

    // The application's own rename moves every directory it holds at or beneath the old path.
    public void Renamed(string from, string to)
    {
        foreach ((int fd, Held directory) in held.ToList())
        {
            if (directory.TruePath == from || directory.TruePath.StartsWith(from + "/", StringComparison.Ordinal))
            {
                held[fd] = directory with { TruePath = to + directory.TruePath[from.Length..] };
            }
        }
    }

    private static (ulong Path, ushort Type, uint Dev) Identity(Stat stat) => (stat.Qid.Path, stat.Type, stat.Dev);

    private static T Wait<T>(ValueTask<T> call) => call.AsTask().GetAwaiter().GetResult();

    private Held Directory(int fd)
    {
        if (held.TryGetValue(fd, out Held? directory))
        {
            return directory;
        }

        Wait(process.FStatAsync(fd));
        throw new SyscallException(Errors.NotDirectory);
    }

    private sealed record Held(string Name, string TruePath, (ulong Path, ushort Type, uint Dev) Identity, bool Preopen);
}
