using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Namespaces;

namespace NinePSharp.Fog.Kernel;

// devcons's bintime, cons, consctl, hostowner, null, pid, ppid, sysname, user and zero, and devrtc's
// nvram; each reads as the calling process sees it, and each is the host owner's as it is now.
// bintime's time is the host's, and is not set through it; sysname is the kernel's, which only the
// host owner may write. What is written to cons goes to the host's console, as putstrn puts it on the
// screen, and what is typed there is read from it, cooked or raw as consctl makes it, as drawterm's
// devcons reads it. The host owner may name another in hostowner, as auth.c's hostownerwrite does.
internal sealed class ConsDevice(Process caller, TimeProvider clock, long booted) : IResourceDataOperations
{
    public const string Provider = "cons";
    private const int NumSize = 12;

    // KNAMELEN: a name, with the NUL that ends it.
    private const int NameLength = 28;
    private static readonly (string Name, uint Permissions)[] Files =
    [
        ("bintime", 0b110_110_100),
        ("cons", 0b110_110_000),
        ("consctl", 0b010_010_000),
        ("hostowner", 0b110_110_100),
        ("null", 0b110_110_110),
        ("nvram", 0b110_110_100),
        ("pid", 0b100_100_100),
        ("ppid", 0b100_100_100),
        ("sysname", 0b110_110_100),
        ("user", 0b110_110_110),
        ("zero", 0b100_100_100),
    ];

    private static readonly uint[] Access = [0b100_000_000, 0b010_000_000, 0b110_000_000, 0b001_000_000];

    public static ResourceHandle Root { get; } = new(new ResourceIdentity(Provider, "#c", 0), QidType.QTDIR);

    public ValueTask<ResourceHandle?> WalkAsync(ResourceHandle directory, string name, CancellationToken cancellationToken)
    {
        int index = Array.FindIndex(Files, file => file.Name == name);
        return ValueTask.FromResult(index < 0 ? null : File(index));
    }

    public ValueTask<IReadOnlyList<ResourceDirectoryEntry>> ReadDirectoryAsync(ResourceHandle directory, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<ResourceDirectoryEntry>>(Files.Select((file, index) => new ResourceDirectoryEntry(file.Name, File(index))).ToArray());

    public ValueTask<ResourceStat> StatAsync(ResourceHandle resource, CancellationToken cancellationToken)
    {
        (string name, uint permissions) = resource.IsDirectory
            ? ("#c", (uint)NinePConstants.FileMode9P.DMDIR | 0b101_101_101)
            : Files[resource.Identity.Path - 1];
        string owner = caller.Kernel.User;
        return ValueTask.FromResult(new ResourceStat(resource, name, permissions, 0, 0, 0, owner, owner, owner));
    }

    // devpermcheck: the files are the host owner's, so the owner's bits serve it and the others' anyone
    // else; rtcopen lets only the host owner open nvram. Each open of consctl is counted.
    public async ValueTask<ResourceOpenHandle> OpenAsync(ResourceHandle resource, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        uint access = Access[mode & 3];
        bool eve = caller.User == caller.Kernel.User;
        if (!resource.IsDirectory && ((Files[resource.Identity.Path - 1].Permissions << (eve ? 0 : 6)) & access) != access)
        {
            throw new IOException(Errors.Permission);
        }

        string? name = resource.IsDirectory ? null : Files[resource.Identity.Path - 1].Name;
        if (name == "nvram" && !eve)
        {
            throw new IOException(Errors.Permission);
        }

        if (name == "consctl")
        {
            await caller.Kernel.Keyboard.OpenctlAsync(cancellationToken);
        }

        string handle = $"{context.OperationId.SessionId}/{context.OperationId.Sequence}";
        return new ResourceOpenHandle(resource, handle, mode, 0);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ResourceOpenHandle openHandle, ulong offset, uint count, CancellationToken cancellationToken)
    {
        string name = Files[openHandle.Resource.Identity.Path - 1].Name;
        return name != "cons" ? Read(name, offset, count)
            : count == 0 ? ReadOnlyMemory<byte>.Empty
            : await caller.Kernel.Keyboard.ReadAsync((int)count, cancellationToken);
    }

    public async ValueTask<uint> WriteAsync(ResourceOpenHandle openHandle, ulong offset, ReadOnlyMemory<byte> data, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        // userwrite: a process may only give up its user, becoming none.
        string name = Files[openHandle.Resource.Identity.Path - 1].Name;
        if (name == "user")
        {
            caller.User = data.Span.SequenceEqual("none"u8) ? "none" : throw new IOException(Errors.Permission);
        }
        else if (name == "bintime")
        {
            throw new IOException(Errors.Permission);
        }
        else if (name == "cons")
        {
            caller.Kernel.Console.Write(data.Span);
        }
        else if (name == "consctl")
        {
            await caller.Kernel.Keyboard.CtlAsync(data, cancellationToken);
        }
        else if (name == "hostowner")
        {
            Hostownerwrite(data.Span);
        }
        else if (name == "nvram")
        {
            return (uint)caller.Kernel.Nvram.Write((long)offset, data.Span);
        }
        else if (name == "sysname")
        {
            // From one write at the start, of less than conswrite's buffer, without a last newline.
            if (offset != 0 || data.Length is <= 0 or >= 256)
            {
                throw new IOException(Errors.BadArg);
            }

            string sysname = Encoding.UTF8.GetString(data.Span);
            int nul = sysname.IndexOf('\0', StringComparison.Ordinal);
            sysname = nul < 0 ? sysname : sysname[..nul];
            caller.Kernel.Sysname = sysname.EndsWith('\n') ? sysname[..^1] : sysname;
        }

        return (uint)data.Length;
    }

    // consclose: a close of consctl counted.
    public async ValueTask ClunkAsync(ResourceOpenHandle openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
    {
        if (!openHandle.Resource.IsDirectory && Files[openHandle.Resource.Identity.Path - 1].Name == "consctl")
        {
            await caller.Kernel.Keyboard.ClosectlAsync();
        }
    }

    public ValueTask<ResourceHandle> CreateAsync(ResourceHandle directory, string name, bool directoryEntry, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask<ResourceOpenHandle> CreateAndOpenAsync(ResourceHandle directory, string name, uint permissions, byte mode, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    public ValueTask RemoveAsync(ResourceHandle resource, ResourceOpenHandle? openHandle, ResourceOperationContext context, CancellationToken cancellationToken)
        => throw new IOException(Errors.Permission);

    private static ResourceHandle File(int index) => new(new ResourceIdentity(Provider, "#c", (ulong)index + 1), QidType.QTFILE);

    // readnum: right-aligned in NUMSIZE-1 places and a space.
    private static byte[] Number(long value) => Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture).PadLeft(NumSize - 1) + " ");

    private static ReadOnlyMemory<byte> Slice(byte[] data, ulong offset, uint count)
    {
        int start = (int)Math.Min(offset, (ulong)data.Length);
        return data.AsMemory(start, (int)Math.Min(count, (uint)(data.Length - start)));
    }

    // Each file but cons: the texts read from the offset, bintime's numbers, null's nothing, the
    // nvram's bytes, and zero's zeros.
    private ReadOnlyMemory<byte> Read(string name, ulong offset, uint count)
    {
        var texts = new Dictionary<string, Func<byte[]>>
        {
            ["hostowner"] = () => Encoding.UTF8.GetBytes(caller.Kernel.User),
            ["pid"] = () => Number(caller.Pid),
            ["ppid"] = () => Number(caller.ParentPid),
            ["sysname"] = () => Encoding.UTF8.GetBytes(caller.Kernel.Sysname),
            ["user"] = () => Encoding.UTF8.GetBytes(caller.User),
        };
        return texts.TryGetValue(name, out Func<byte[]>? text) ? Slice(text(), offset, count)
            : name == "bintime" ? Bintime(count)
            : name == "null" ? ReadOnlyMemory<byte>.Empty
            : name == "nvram" ? caller.Kernel.Nvram.Read((long)offset, (int)count)
            : new byte[count];
    }

    // hostownerwrite: the host owner's new name, up to a NUL, which every process and #s file of the
    // old one takes, and the writer with them; a name of nothing is refused.
    private void Hostownerwrite(ReadOnlySpan<byte> a)
    {
        FogKernel kernel = caller.Kernel;
        if (caller.User != kernel.User)
        {
            throw new IOException(Errors.Permission);
        }

        if (a.Length >= NameLength)
        {
            throw new IOException(Errors.TooLong);
        }

        // Fog's users all have names, so one a NUL ends before it begins is as bad as none.
        int nul = a.IndexOf((byte)0);
        string buf = Encoding.UTF8.GetString(nul < 0 ? a : a[..nul]);
        if (buf.Length == 0)
        {
            throw new IOException(Errors.BadArg);
        }

        kernel.Renameuser(kernel.User, buf);
        kernel.Srv.Renameuser(kernel.User, buf);
        kernel.User = buf;
        caller.User = buf;
    }

    // readbintime: as many of the epoch's nanoseconds, the ticks, their frequency and the nanoseconds
    // since boot as fit, whatever the offset.
    private byte[] Bintime(uint count)
    {
        long ticks = clock.GetTimestamp();
        long[] numbers =
        [
            (clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks * 100,
            ticks,
            clock.TimestampFrequency,
            (long)clock.GetElapsedTime(booted, ticks).TotalNanoseconds,
        ];
        byte[] data = new byte[Math.Min(count / 8, numbers.Length) * 8];
        for (int i = 0; i < data.Length / 8; i++)
        {
            BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(i * 8), numbers[i]);
        }

        return data;
    }
}
