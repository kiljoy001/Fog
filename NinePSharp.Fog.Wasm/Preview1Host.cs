using System.Buffers.Binary;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
using WebAssembly.Runtime;

namespace NinePSharp.Fog.Wasm;

// The Preview 1 functions Fog provides, acting on the process that execed the module. Each runs on
// the guest's thread and blocks it, and only it, on the kernel call. A call reads what it needs from
// guest memory and checks where its results go before any kernel call with an effect; any access
// outside memory is FAULT. Each descriptor starts with every right; fd_fdstat_set_rights only narrows them, and a call
// needs every right it names, or it is NOTCAPABLE before anything else.
internal sealed class Preview1Host(Process process, TimeProvider clock)
{
    // The most one call copies between guest memory and the kernel; a longer write is short.
    public const int CopyLimit = 1 << 20;

    private const ulong Nanoseconds = 1_000_000_000;
    private readonly Dictionary<int, (Rights Base, Rights Inheriting)> rights = [];
    private GuestMemory memory = null!;
    private IReadOnlyList<string> argv = [];
    private IReadOnlyList<string> environment = [];

    public void Attach(UnmanagedMemory guest) => memory = new GuestMemory(guest);

    // What the guest sees of its process once exec has committed.
    public void Start(IReadOnlyList<string> arguments, IReadOnlyList<string> variables)
    {
        argv = arguments;
        environment = variables;
    }

    public Delegate Function(string name) => name switch
    {
        "args_get" => new Func<int, int, int>((list, buffer) => Call(() => Strings(argv, (uint)list, (uint)buffer))),
        "args_sizes_get" => new Func<int, int, int>((count, size) => Call(() => Sizes(argv, (uint)count, (uint)size))),
        "environ_get" => new Func<int, int, int>((list, buffer) => Call(() => Strings(environment, (uint)list, (uint)buffer))),
        "environ_sizes_get" => new Func<int, int, int>((count, size) => Call(() => Sizes(environment, (uint)count, (uint)size))),
        "fd_write" => new Func<int, int, int, int, int>((fd, vector, count, written) => Call(() => Write(Require(fd, Rights.FdWrite), (uint)vector, (uint)count, -1, (uint)written))),
        "fd_pwrite" => new Func<int, int, int, long, int, int>((fd, vector, count, offset, written) => Call(() => Write(Require(fd, Rights.FdWrite | Rights.FdSeek), (uint)vector, (uint)count, Offset(offset), (uint)written))),
        "fd_read" => new Func<int, int, int, int, int>((fd, vector, count, read) => Call(() => Read(Require(fd, Rights.FdRead), (uint)vector, (uint)count, -1, (uint)read))),
        "fd_pread" => new Func<int, int, int, long, int, int>((fd, vector, count, offset, read) => Call(() => Read(Require(fd, Rights.FdRead | Rights.FdSeek), (uint)vector, (uint)count, Offset(offset), (uint)read))),
        "fd_seek" => new Func<int, long, int, int, int>((fd, offset, whence, position) => Call(() => Seek(Require(fd, Rights.FdSeek), offset, whence, (uint)position))),
        "fd_tell" => new Func<int, int, int>((fd, position) => Call(() => Seek(Require(fd, Rights.FdTell), 0, 1, (uint)position))),
        "fd_close" => new Func<int, int>(fd => Call(() => Wait(process.CloseAsync(fd)))),
        "fd_renumber" => new Func<int, int, int>((fd, to) => Call(() => Renumber(fd, to))),
        "fd_fdstat_get" => new Func<int, int, int>((fd, stat) => Call(() => FdStat(fd, (uint)stat))),
        "fd_fdstat_set_flags" => new Func<int, int, int>((fd, flags) => Call(() => SetFlags(Require(fd, Rights.FdFdstatSetFlags), flags))),
        "fd_fdstat_set_rights" => new Func<int, long, long, int>((fd, held, inheriting) => Call(() => Narrow(fd, (Rights)held, (Rights)inheriting))),
        "fd_filestat_get" => new Func<int, int, int>((fd, stat) => Call(() => FileStat(Require(fd, Rights.FdFilestatGet), (uint)stat))),
        "fd_filestat_set_size" => new Func<int, long, int>((fd, size) => Call(() => SetSize(Require(fd, Rights.FdFilestatSetSize), size))),
        "fd_filestat_set_times" => new Func<int, long, long, int, int>((fd, atim, mtim, flags) => Call(() => SetTimes(Require(fd, Rights.FdFilestatSetTimes), mtim, flags))),
        "fd_advise" => new Func<int, long, long, int, int>((fd, offset, length, advice) => Call(() => Advise(Require(fd, Rights.FdAdvise), advice))),
        "fd_allocate" => new Func<int, long, long, int>((fd, offset, length) => Call(() => Allocate(Require(fd, Rights.FdAllocate)))),
        "proc_exit" => new Action<int>(code => throw new ProcExitException((uint)code)),
        _ => Preview1.Unsupported(Preview1.Unprovided[name]),
    };

    private static int Call(Func<Errno> call)
    {
        try
        {
            return (int)call();
        }
        catch (GuestFaultException)
        {
            return (int)Errno.Fault;
        }
        catch (InvalidArgumentException)
        {
            return (int)Errno.Inval;
        }
        catch (NotCapableException)
        {
            return (int)Errno.Notcapable;
        }
        catch (NotSupportedByFogException)
        {
            return (int)Errno.Notsup;
        }
        catch (SyscallException error)
        {
            return (int)Map(error.Message);
        }
    }

    // The kernel's error strings as Preview 1 errnos, as Wasm.md tabulates them.
    private static Errno Map(string error) => error switch
    {
        Errors.BadFd => Errno.Badf,
        Errors.IsDirectory => Errno.Isdir,
        Errors.NegativeOffset => Errno.Inval,
        _ => Errno.Io,
    };

    // A kernel call made for the guest, on the guest's thread.
    private static T Wait<T>(ValueTask<T> call) => call.AsTask().GetAwaiter().GetResult();

    private static Errno Wait(ValueTask call)
    {
        call.AsTask().GetAwaiter().GetResult();
        return Errno.Success;
    }

    // WASI offsets are unsigned; one past the largest signed offset would reach Plan 9's -1, which
    // means the descriptor's own offset.
    private static long Offset(long offset) => offset >= 0 ? offset : throw new InvalidArgumentException();

    private static byte Filetype(Stat stat) => (stat.Qid.Type & QidType.QTDIR) != 0 ? (byte)3 : (byte)4;

    // A wstat that changes only the length or the modification time, leaving the rest as nulldir does.
    private static Stat WStat(ulong length = ulong.MaxValue, uint mtime = uint.MaxValue)
    {
        Stat none = Process.NullDir;
        return new Stat(none.Size, none.Type, none.Dev, none.Qid, none.Mode, none.Atime, mtime, length, none.Name, none.Uid, none.Gid, none.Muid);
    }

    private (Rights Base, Rights Inheriting) Held(int fd) => rights.GetValueOrDefault(fd, (Rights.All, Rights.All));

    private int Require(int fd, Rights needed) => (Held(fd).Base & needed) == needed ? fd : throw new NotCapableException();

    private Stat Stat(int fd) => Wait(process.FStatAsync(fd));

    private Errno Renumber(int fd, int to)
    {
        (Rights, Rights) moved = Held(fd);
        Wait(process.RenumberAsync(fd, to));
        rights[to] = moved;
        return Errno.Success;
    }

    private Errno FdStat(int fd, uint pointer)
    {
        byte filetype = Filetype(Stat(fd));
        var fdstat = new byte[24];
        fdstat[0] = filetype;
        BinaryPrimitives.WriteUInt64LittleEndian(fdstat.AsSpan(8), (ulong)Held(fd).Base);
        BinaryPrimitives.WriteUInt64LittleEndian(fdstat.AsSpan(16), (ulong)Held(fd).Inheriting);
        memory.Write(pointer, fdstat);
        return Errno.Success;
    }

    // Fog keeps no append, sync or nonblocking flags on a descriptor; asking for one is NOTSUP.
    private Errno SetFlags(int fd, int flags)
    {
        Stat(fd);
        return flags == 0 ? Errno.Success : throw new NotSupportedByFogException();
    }

    private Errno Narrow(int fd, Rights held, Rights inheriting)
    {
        Stat(fd);
        (Rights Base, Rights Inheriting) now = Held(fd);
        if ((held & ~now.Base) != 0 || (inheriting & ~now.Inheriting) != 0)
        {
            throw new NotCapableException();
        }

        rights[fd] = (held, inheriting);
        return Errno.Success;
    }

    // dev is 0, ino the qid path and nlink 1: no POSIX identity is invented. Plan 9 keeps no
    // status-change time, so ctim is the modification time.
    private Errno FileStat(int fd, uint pointer)
    {
        Stat stat = Stat(fd);
        var filestat = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(filestat.AsSpan(8), stat.Qid.Path);
        filestat[16] = Filetype(stat);
        BinaryPrimitives.WriteUInt64LittleEndian(filestat.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(filestat.AsSpan(32), stat.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(filestat.AsSpan(40), stat.Atime * Nanoseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(filestat.AsSpan(48), stat.Mtime * Nanoseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(filestat.AsSpan(56), stat.Mtime * Nanoseconds);
        memory.Write(pointer, filestat);
        return Errno.Success;
    }

    private Errno SetSize(int fd, long size) => Wait(process.FWStatAsync(fd, WStat(length: (ulong)size)));

    // Only the modification time can be set, to a time given or to now, in Plan 9's whole seconds;
    // asking to set the access time is NOTSUP, and both a time and now is INVAL.
    private Errno SetTimes(int fd, long mtim, int flags)
    {
        const int AccessTime = 1 | 2, ModificationTime = 4, Now = 8;
        if ((flags & AccessTime) != 0)
        {
            throw new NotSupportedByFogException();
        }

        if ((flags & ModificationTime) != 0 && (flags & Now) != 0)
        {
            throw new InvalidArgumentException();
        }

        Stat(fd);
        if ((flags & (ModificationTime | Now)) != 0)
        {
            ulong seconds = (flags & Now) != 0 ? (ulong)clock.GetUtcNow().ToUnixTimeSeconds() : (ulong)mtim / Nanoseconds;
            Wait(process.FWStatAsync(fd, WStat(mtime: (uint)seconds)));
        }

        return Errno.Success;
    }

    // Advice is accepted and ignored.
    private Errno Advise(int fd, int advice)
    {
        if ((uint)advice > 5)
        {
            throw new InvalidArgumentException();
        }

        Stat(fd);
        return Errno.Success;
    }

    // Space cannot be reserved in a 9P file.
    private Errno Allocate(int fd)
    {
        Stat(fd);
        throw new NotSupportedByFogException();
    }

    private Errno Sizes(IReadOnlyList<string> strings, uint count, uint size)
    {
        memory.WriteUInt32(count, (uint)strings.Count);
        memory.WriteUInt32(size, (uint)strings.Sum(s => Encoding.UTF8.GetByteCount(s) + 1));
        return Errno.Success;
    }

    // Each string's pointer goes in the list, and the string, NUL-terminated, in the buffer after the
    // one before it.
    private Errno Strings(IReadOnlyList<string> strings, uint list, uint buffer)
    {
        foreach (string s in strings)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(s + "\0");
            memory.WriteUInt32(list, buffer);
            memory.Write(buffer, encoded);
            list += 4;
            buffer += (uint)encoded.Length;
        }

        return Errno.Success;
    }

    // The bytes of a vector of buffers, copied out of guest memory, up to CopyLimit; every buffer
    // must lie inside memory.
    private byte[] Gather(uint vector, uint count)
    {
        var data = new MemoryStream();
        foreach ((uint pointer, uint length) in Buffers(vector, count))
        {
            memory.Check(pointer, length);
            data.Write(memory.Read(pointer, (uint)Math.Min(length, CopyLimit - data.Length)));
        }

        return data.ToArray();
    }

    private List<(uint Pointer, uint Length)> Buffers(uint vector, uint count)
    {
        var buffers = new List<(uint, uint)>();
        for (uint i = 0; i < count; i++)
        {
            buffers.Add((memory.ReadUInt32(vector + (8 * i)), memory.ReadUInt32(vector + (8 * i) + 4)));
        }

        return buffers;
    }

    private Errno Write(int fd, uint vector, uint count, long offset, uint written)
    {
        byte[] data = Gather(vector, count);
        memory.Check(written, 4);
        memory.WriteUInt32(written, (uint)Wait(process.PWriteAsync(fd, data, offset)));
        return Errno.Success;
    }

    // One kernel read of as much as the buffers hold, up to CopyLimit, spread across them in order.
    // A directory reads as 9P directory entries, which the guest is never given.
    private Errno Read(int fd, uint vector, uint count, long offset, uint read)
    {
        List<(uint Pointer, uint Length)> buffers = Buffers(vector, count);
        foreach ((uint pointer, uint length) in buffers)
        {
            memory.Check(pointer, length);
        }

        memory.Check(read, 4);
        if ((Wait(process.FStatAsync(fd)).Qid.Type & QidType.QTDIR) != 0)
        {
            throw new SyscallException(Errors.IsDirectory);
        }

        long room = Math.Min(buffers.Sum(buffer => (long)buffer.Length), CopyLimit);
        ReadOnlyMemory<byte> data = Wait(process.PReadAsync(fd, (int)room, offset));
        int placed = 0;
        foreach ((uint pointer, uint length) in buffers)
        {
            int take = Math.Min((int)length, data.Length - placed);
            memory.Write(pointer, data.Slice(placed, take).ToArray());
            placed += take;
        }

        memory.WriteUInt32(read, (uint)data.Length);
        return Errno.Success;
    }

    private Errno Seek(int fd, long offset, int whence, uint position)
    {
        if ((uint)whence > 2)
        {
            throw new InvalidArgumentException();
        }

        memory.Check(position, 8);
        memory.WriteUInt64(position, (ulong)Wait(process.SeekAsync(fd, offset, whence)));
        return Errno.Success;
    }
}
