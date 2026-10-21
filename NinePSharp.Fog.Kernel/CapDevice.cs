using System.Security.Cryptography;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devcap: #¤, whose caphash takes a capability from the host owner, the HMAC-SHA1 of from@to keyed
// by a secret, and whose capuse spends one, making the writer to, as factotum and auth_chuid use
// them. The capabilities are the kernel's, as capalloc is, each kept for a minute by its clock.
internal sealed class CapDevice(FogKernel kernel) : IResourceDataOperations, IDirectoryOpenRefusal
{
    public const string Provider = "cap";
    private const int Hashlen = 20;
    private const int Maxhash = 256;
    private const int Timeout = 60;
    private const ulong Qdir = 0;
    private const ulong Quse = 1;
    private const ulong Qhash = 2;
    private readonly List<(byte[] Hash, DateTimeOffset Made)> capalloc = [];

    // capremove: once the host owner removes caphash, capdir ends before it.
    private bool hashRemoved;

    public static ResourceHandle Root { get; } = Handle(Qdir, QidType.QTDIR);

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
        => ValueTask.FromResult(Capdir().FirstOrDefault(entry => entry.Name == name)?.Handle);

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<ResourceDirectoryEntry>>([.. Capdir()]);

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        // Stryker disable once String : a file stats under the name it was reached by, as devstat names it from the channel
        (string name, uint mode) = resource.Identity.Path switch
        {
            Qdir => (".", (uint)NinePConstants.FileMode9P.DMDIR | 0b101_000_000),
            Quse => ("capuse", 0b010_010_010u),
            _ => ("caphash", 0b010_000_000u),
        };
        return ValueTask.FromResult(new ResourceStat(resource, name, mode, 0, 0, 0, kernel.User, kernel.User, kernel.User));
    }

    // capopen: the directory only for reading, and caphash only for the host owner.
    public ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (resource.Identity.Path == Qhash && context.User != kernel.User)
        {
            throw new IOException(Errors.Permission);
        }

        return ValueTask.FromResult(new ResourceOpenHandle(resource, $"{context.OperationId.SessionId}/{context.OperationId.Sequence}", mode, 0));
    }

    public string Refusal(byte mode) => Errors.BadArg;

    public ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    // capwrite: caphash adds the hash the host owner writes; capuse spends the one from@to@key
    // names, and the writer becomes to.
    public ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (openHandle.Resource.Identity.Path == Qhash)
        {
            if (context.User != kernel.User)
            {
                throw new IOException(Errors.Permission);
            }

            if (data.Length < Hashlen)
            {
                throw new IOException(Errors.Short);
            }

            Addcap(data[..Hashlen].ToArray());
            return ValueTask.FromResult((uint)data.Length);
        }

        if (data.Length >= 1024)
        {
            throw new IOException(Errors.TooBig);
        }

        // As a C string, the text ends at its first NUL.
        string cp = Encoding.UTF8.GetString(data.Span);
        int nul = cp.IndexOf('\0', StringComparison.Ordinal);
        cp = nul < 0 ? cp : cp[..nul];
        int at = cp.LastIndexOf('@');
        if (at < 0)
        {
            throw new IOException(Errors.Short);
        }

        string from = cp[..at], key = cp[(at + 1)..];
        byte[] hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(from));

        // A from user, when given, must be the writer's.
        string to = from;
        int split = from.IndexOf('@', StringComparison.Ordinal);
        if (split >= 0)
        {
            (from, to) = (from[..split], from[(split + 1)..]);
            if (from != context.User)
            {
                throw new IOException("capability must match user");
            }
        }

        if (!Remcap(hash))
        {
            throw new IOException($"invalid capability {from}@{key}");
        }

        kernel.Processes[context.ProcessId].User = to;
        return ValueTask.FromResult((uint)data.Length);
    }

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    // capremove: only caphash, and only by the host owner.
    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (context.User != kernel.User || resource.Identity.Path != Qhash)
        {
            throw new IOException(Errors.Permission);
        }

        hashRemoved = true;
        return ValueTask.CompletedTask;
    }

    private static ResourceHandle Handle(ulong path, QidType type) => new(new ResourceIdentity(Provider, "#¤", path), type);

    private IEnumerable<ResourceDirectoryEntry> Capdir()
    {
        yield return new ResourceDirectoryEntry("capuse", Handle(Quse, QidType.QTFILE));
        if (!hashRemoved)
        {
            yield return new ResourceDirectoryEntry("caphash", Handle(Qhash, QidType.QTFILE));
        }
    }

    // trimcaps: the oldest go while there are too many to add one or they have timed out.
    private void Trimcaps()
    {
        DateTimeOffset now = kernel.Clock.GetUtcNow();
        while (capalloc.Count > 0 && (capalloc.Count >= Maxhash || (long)(now - capalloc[0].Made).TotalSeconds >= Timeout))
        {
            capalloc.RemoveAt(0);
        }
    }

    private void Addcap(byte[] hash)
    {
        lock (capalloc)
        {
            // Stryker disable once Statement : remcap trims as it looks, so what this would let go is gone before any use could find it
            Trimcaps();
            capalloc.Add((hash, kernel.Clock.GetUtcNow()));
        }
    }

    // remcap: the matching capability, compared in constant time as tsmemcmp does, taken away.
    private bool Remcap(byte[] hash)
    {
        lock (capalloc)
        {
            Trimcaps();
            int found = capalloc.FindIndex(cap => CryptographicOperations.FixedTimeEquals(cap.Hash, hash));
            if (found < 0)
            {
                return false;
            }

            capalloc.RemoveAt(found);
            return true;
        }
    }
}
