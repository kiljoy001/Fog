using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using NinePSharp.Messages;
using WebAssembly.Runtime;

namespace NinePSharp.Fog.Wasm;

// The Preview 1 functions Fog provides, acting on the process that execed the module. Each runs on
// the guest's thread and blocks it, and only it, on the kernel call. A call reads what it needs from
// guest memory and checks where its results go before any kernel call with an effect; any access
// outside memory is FAULT. A directory starts with a directory's rights and any other descriptor with
// every right; fd_fdstat_set_rights only narrows them, and a call needs every right it names, or it
// is NOTCAPABLE before anything else.
internal sealed class Preview1Host(Process process, TimeProvider clock)
{
    // The most one call copies between guest memory and the kernel; a longer write is short.
    public const int CopyLimit = 1 << 20;

    private const ulong Nanoseconds = 1_000_000_000;
    private const int Append = 1;
    private const int Nonblock = 4;
    private const int Synced = 2 | 8 | 16;
    private const string AlreadyExists = "file already exists";

    // lib9p's, which ramfs serves, for removing a directory that is not empty.
    private const string NotEmpty = "has children";
    private static readonly Encoding Utf8 = Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    private readonly Dictionary<int, (Rights Base, Rights Inheriting)> rights = [];
    private readonly Dictionary<int, int> flags = [];
    private readonly HeldDirectories directories = new(process);
    private GuestMemory memory = null!;
    private IReadOnlyList<string> argv = [];
    private IReadOnlyList<string> environment = [];

    public void Attach(UnmanagedMemory guest) => memory = new GuestMemory(guest);

    // What the guest sees of its process once exec has committed.
    public async Task StartAsync(IReadOnlyList<string> arguments, IReadOnlyList<string> variables)
    {
        argv = arguments;
        environment = variables;
        await directories.FindPreopensAsync();
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
        "fd_close" => new Func<int, int>(fd => Call(() => Close(fd))),
        "fd_renumber" => new Func<int, int, int>((fd, to) => Call(() => Renumber(fd, to))),
        "fd_fdstat_get" => new Func<int, int, int>((fd, stat) => Call(() => FdStat(fd, (uint)stat))),
        "fd_fdstat_set_flags" => new Func<int, int, int>((fd, flags) => Call(() => SetFlags(Require(fd, Rights.FdFdstatSetFlags), flags))),
        "fd_fdstat_set_rights" => new Func<int, long, long, int>((fd, held, inheriting) => Call(() => Narrow(fd, (Rights)held, (Rights)inheriting))),
        "fd_filestat_get" => new Func<int, int, int>((fd, stat) => Call(() => FileStat(Stat(Require(fd, Rights.FdFilestatGet)), (uint)stat))),
        "fd_filestat_set_size" => new Func<int, long, int>((fd, size) => Call(() => SetSize(Require(fd, Rights.FdFilestatSetSize), size))),
        "fd_filestat_set_times" => new Func<int, long, long, int, int>((fd, atim, mtim, flags) => Call(() => SetTimes(Require(fd, Rights.FdFilestatSetTimes), mtim, flags))),
        "fd_advise" => new Func<int, long, long, int, int>((fd, offset, length, advice) => Call(() => Advise(Require(fd, Rights.FdAdvise), advice))),
        "fd_allocate" => new Func<int, long, long, int>((fd, offset, length) => Call(() => Allocate(Require(fd, Rights.FdAllocate)))),
        "clock_res_get" => new Func<int, int, int>((id, resolution) => Call(() => Clock(id, (uint)resolution, Resolution))),
        "clock_time_get" => new Func<int, long, int, int>((id, precision, time) => Call(() => Clock(id, (uint)time, Now))),
        "random_get" => new Func<int, int, int>((buffer, length) => Call(() => Random((uint)buffer, (uint)length))),
        "sched_yield" => new Func<int>(() => (int)Errno.Success),
        "sock_accept" => new Func<int, int, int, int>((fd, flags, opened) => Call(() => NotSocket(fd))),
        "sock_recv" => new Func<int, int, int, int, int, int, int>((fd, vector, count, flags, read, returned) => Call(() => NotSocket(fd))),
        "sock_send" => new Func<int, int, int, int, int, int>((fd, vector, count, flags, written) => Call(() => NotSocket(fd))),
        "sock_shutdown" => new Func<int, int, int>((fd, how) => Call(() => NotSocket(fd))),
        "fd_prestat_get" => new Func<int, int, int>((fd, prestat) => Call(() => Prestat(fd, (uint)prestat))),
        "fd_prestat_dir_name" => new Func<int, int, int, int>((fd, name, length) => Call(() => PrestatName(fd, (uint)name, (uint)length))),
        "fd_readdir" => new Func<int, int, int, long, int, int>((fd, buffer, length, cookie, used) => Call(() => ReadDirectory(Require(fd, Rights.FdReaddir), (uint)buffer, (uint)length, (ulong)cookie, (uint)used))),
        "path_open" => new Func<int, int, int, int, int, long, long, int, int, int>((fd, lookup, path, length, oflags, held, inheriting, fdflags, opened)
            => Call(() => PathOpen(Require(fd, Rights.PathOpen), Path(fd, (uint)path, (uint)length), oflags, (Rights)held, (Rights)inheriting, fdflags, (uint)opened))),
        "path_create_directory" => new Func<int, int, int, int>((fd, path, length) => Call(() => MakeDirectory(Path(Require(fd, Rights.PathCreateDirectory), (uint)path, (uint)length)))),
        "path_remove_directory" => new Func<int, int, int, int>((fd, path, length) => Call(() => Remove(Path(Require(fd, Rights.PathRemoveDirectory), (uint)path, (uint)length), directory: true))),
        "path_unlink_file" => new Func<int, int, int, int>((fd, path, length) => Call(() => Remove(Path(Require(fd, Rights.PathUnlinkFile), (uint)path, (uint)length), directory: false))),
        "path_rename" => new Func<int, int, int, int, int, int, int>((fd, from, fromLength, newfd, to, toLength)
            => Call(() => Rename(Path(Require(fd, Rights.PathRenameSource), (uint)from, (uint)fromLength), Path(Require(newfd, Rights.PathRenameTarget), (uint)to, (uint)toLength)))),
        "path_filestat_get" => new Func<int, int, int, int, int, int>((fd, lookup, path, length, stat)
            => Call(() => FileStat(Wait(process.StatAsync(Path(Require(fd, Rights.PathFilestatGet), (uint)path, (uint)length))), (uint)stat))),
        "path_filestat_set_times" => new Func<int, int, int, int, long, long, int, int>((fd, lookup, path, length, atim, mtim, flags)
            => Call(() => SetTimes(Path(Require(fd, Rights.PathFilestatSetTimes), (uint)path, (uint)length), mtim, flags))),
        "path_link" => new Func<int, int, int, int, int, int, int, int>((oldfd, lookup, from, fromLength, newfd, to, toLength) => (int)Errno.Notsup),
        "path_symlink" => new Func<int, int, int, int, int, int>((from, fromLength, fd, to, toLength) => (int)Errno.Notsup),
        "path_readlink" => new Func<int, int, int, int, int, int, int>((fd, path, length, buffer, bufferLength, used) => (int)Errno.Notsup),
        "poll_oneoff" => new Func<int, int, int, int, int>((subscriptions, events, count, ready) => Call(() => Poll((uint)subscriptions, (uint)events, (uint)count, (uint)ready))),
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
        catch (NameTooLongException)
        {
            return (int)Errno.Nametoolong;
        }
        catch (PipeSeekException)
        {
            return (int)Errno.Spipe;
        }
        catch (NotCapableException)
        {
            return (int)Errno.Notcapable;
        }
        catch (NotSupportedByFogException)
        {
            return (int)Errno.Notsup;
        }
        catch (DecoderFallbackException)
        {
            return (int)Errno.Ilseq;
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
        _ when error.StartsWith(Errors.NotExist, StringComparison.Ordinal) => Errno.Noent,
        _ when error.StartsWith(Errors.NotDirectory, StringComparison.Ordinal) => Errno.Notdir,
        _ when error.StartsWith(AlreadyExists, StringComparison.Ordinal) => Errno.Exist,
        _ when error.StartsWith(Errors.Permission, StringComparison.Ordinal) => Errno.Acces,
        NotEmpty => Errno.Notempty,
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

    private static byte[] Failed(byte[] happened, Errno errno)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(happened.AsSpan(8), (ushort)errno);
        return happened;
    }

    // From the qid and the device: a directory, a pipe (#|), whose kind Preview 1 has no name for, a
    // console device (#c), or a regular file.
    private static byte Filetype(Stat stat) => (stat.Qid.Type & QidType.QTDIR) != 0 ? (byte)3
        : stat.Type == '|' ? (byte)0
        : stat.Type == 'c' ? (byte)2
        : (byte)4;

    // A wstat that changes only the length, the modification time or the name, leaving the rest as
    // nulldir does; a size of 0 has the record sized for its name.
    private static Stat WStat(ulong length = ulong.MaxValue, uint mtime = uint.MaxValue, string name = "")
    {
        Stat none = Process.NullDir;
        return new Stat(0, none.Type, none.Dev, none.Qid, none.Mode, none.Atime, mtime, length, name, none.Uid, none.Gid, none.Muid);
    }

    private (Rights Base, Rights Inheriting) Held(int fd) => rights.GetValueOrDefault(fd, (directories.Holds(fd) ? Rights.Directory : Rights.All, Rights.All));

    private int Require(int fd, Rights needed) => (Held(fd).Base & needed) == needed ? fd : throw new NotCapableException();

    private Stat Stat(int fd) => Wait(process.FStatAsync(fd));

    private Errno Renumber(int fd, int to)
    {
        (Rights, Rights) moved = Held(fd);
        Wait(process.RenumberAsync(fd, to));
        rights[to] = moved;
        flags[to] = flags.GetValueOrDefault(fd);
        directories.Renumbered(fd, to);
        return Errno.Success;
    }

    private Errno Close(int fd)
    {
        Wait(process.CloseAsync(fd));
        directories.Closed(fd);
        return Errno.Success;
    }

    // A path read from guest memory, which must be UTF-8, made the true path it names beneath fd. A
    // trailing slash names a directory, so naming anything else with one is NOTDIR.
    private string Path(int fd, uint pointer, uint length)
    {
        string path = Utf8.GetString(memory.Read(pointer, length));
        string resolved = directories.Resolve(fd, path);
        return path.EndsWith('/') && Existing(resolved) is { } stat && !HeldDirectories.IsDirectory(stat) ? throw new SyscallException(Errors.NotDirectory) : resolved;
    }

    private string PreopenName(int fd) => directories.Preopen(fd) ?? throw new SyscallException(Errors.BadFd);

    private Errno Prestat(int fd, uint pointer)
    {
        var prestat = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(prestat.AsSpan(4), (uint)Utf8.GetByteCount(PreopenName(fd)));
        memory.Write(pointer, prestat);
        return Errno.Success;
    }

    private Errno PrestatName(int fd, uint pointer, uint length)
    {
        byte[] name = Utf8.GetBytes(PreopenName(fd));
        memory.Write(pointer, name.Length <= length ? name : throw new NameTooLongException());
        return Errno.Success;
    }

    // The open mode is the one the rights ask for: Plan 9 opens a file for reading, writing or
    // both, and a directory only for reading, with a directory's rights, so asking to write or
    // truncate one is ISDIR. CREAT opens what exists before it creates, since a Plan 9 create
    // truncates; with EXCL, what exists is EXIST. The sync flags would need the file server to
    // sync, so they are NOTSUP.
    private Errno PathOpen(int fd, string path, int oflags, Rights wanted, Rights inheriting, int fdflags, uint opened)
    {
        const int Create = 1, Directory = 2, Exclusive = 4, Truncate = 8;
        const Rights Writing = Rights.FdWrite | Rights.FdAllocate | Rights.FdFilestatSetSize;
        Rights allowed = Held(fd).Inheriting;
        bool truncate = (oflags & Truncate) != 0;
        Require(fd, ((oflags & Create) != 0 ? Rights.PathCreateFile : 0) | (truncate ? Rights.PathFilestatSetSize : 0));
        if ((wanted & ~allowed) != 0 || (inheriting & ~allowed) != 0)
        {
            throw new NotCapableException();
        }

        if ((fdflags & Synced) != 0)
        {
            throw new NotSupportedByFogException();
        }

        memory.Check(opened, 4);
        Stat? existing = Existing(path);
        if (existing is { } directory && HeldDirectories.IsDirectory(directory))
        {
            wanted = (wanted & Writing) != 0 || truncate ? throw new SyscallException(Errors.IsDirectory) : wanted & Rights.Directory;
        }

        bool write = (wanted & Writing) != 0 || truncate;
        bool read = (wanted & (Rights.FdRead | Rights.FdReaddir)) != 0 || !write;
        int mode = (read && write ? NinePConstants.ORDWR : write ? NinePConstants.OWRITE : NinePConstants.OREAD) | (truncate ? NinePConstants.OTRUNC : 0);
        int newfd = existing is not null && (oflags & Create) != 0 && (oflags & Exclusive) != 0 ? throw new SyscallException(AlreadyExists)
            : existing is not null || (oflags & Create) == 0 ? Wait(process.OpenAsync(path, mode))
            : Wait(process.CreateAsync(path, mode, 0b110_110_110));
        bool isDirectory = HeldDirectories.IsDirectory(Stat(newfd));
        if ((oflags & Directory) != 0 && !isDirectory)
        {
            Wait(process.CloseAsync(newfd));
            throw new SyscallException(Errors.NotDirectory);
        }

        rights[newfd] = (wanted, inheriting);
        flags[newfd] = fdflags;
        if (isDirectory)
        {
            directories.Opened(newfd, path);
        }

        memory.WriteUInt32(opened, (uint)newfd);
        return Errno.Success;
    }

    private Stat? Existing(string path)
    {
        try
        {
            return Wait(process.StatAsync(path));
        }
        catch (SyscallException missing) when (missing.Message.StartsWith(Errors.NotExist, StringComparison.Ordinal))
        {
            // Nothing is there.
        }

        return null;
    }

    private Errno MakeDirectory(string path)
    {
        if (Existing(path) is not null)
        {
            throw new SyscallException(AlreadyExists);
        }

        Wait(process.CloseAsync(Wait(process.CreateAsync(path, NinePConstants.OREAD, (uint)NinePConstants.FileMode9P.DMDIR | 0b111_111_111))));
        return Errno.Success;
    }

    // The file's type is checked first, so unlinking a directory is ISDIR and removing a file as a
    // directory NOTDIR.
    private Errno Remove(string path, bool directory)
    {
        if (HeldDirectories.IsDirectory(Wait(process.StatAsync(path))) != directory)
        {
            throw new SyscallException(directory ? Errors.NotDirectory : Errors.IsDirectory);
        }

        return Wait(process.RemoveAsync(path));
    }

    // A wstat of the name, within one directory; across directories it is NOTSUP rather than a copy.
    private Errno Rename(string from, string to)
    {
        int slash = from.LastIndexOf('/');
        if (to.LastIndexOf('/') != slash || !to.StartsWith(from[..(slash + 1)], StringComparison.Ordinal))
        {
            throw new NotSupportedByFogException();
        }

        Wait(process.WStatAsync(from, WStat(name: to[(slash + 1)..])));
        directories.Renamed(from, to);
        return Errno.Success;
    }

    // Entries from the directory's start: . and .. first, then the directory's own in union order.
    // A cookie counts entries; each dirent is its next cookie, inode, name length and type, then the
    // name, and the last may be cut short by the buffer's end. .. reports nothing about what is above.
    private Errno ReadDirectory(int fd, uint buffer, uint length, ulong cookie, uint used)
    {
        Stat self = Stat(fd);
        if (!HeldDirectories.IsDirectory(self))
        {
            throw new SyscallException(Errors.NotDirectory);
        }

        Wait(process.SeekAsync(fd, 0, 0));
        var entries = new List<(string Name, ulong Inode, byte Type)> { (".", self.Qid.Path, 3), ("..", 0, 3) };
        entries.AddRange(Wait(process.DirReadAsync(fd)).Select(entry => (entry.Name, entry.Qid.Path, Filetype(entry))));
        var dirents = new MemoryStream();
        for (ulong i = cookie; i < (ulong)entries.Count; i++)
        {
            byte[] name = Utf8.GetBytes(entries[(int)i].Name);
            var header = new byte[24];
            BinaryPrimitives.WriteUInt64LittleEndian(header, i + 1);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), entries[(int)i].Inode);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)name.Length);
            header[20] = entries[(int)i].Type;
            dirents.Write(header);
            dirents.Write(name);
        }

        byte[] written = dirents.ToArray()[..(int)Math.Min(dirents.Length, length)];
        memory.Write(buffer, written);
        memory.WriteUInt32(used, (uint)written.Length);
        return Errno.Success;
    }

    private Errno FdStat(int fd, uint pointer)
    {
        byte filetype = Filetype(Stat(fd));
        var fdstat = new byte[24];
        fdstat[0] = filetype;
        BinaryPrimitives.WriteUInt16LittleEndian(fdstat.AsSpan(2), (ushort)flags.GetValueOrDefault(fd));
        BinaryPrimitives.WriteUInt64LittleEndian(fdstat.AsSpan(8), (ulong)Held(fd).Base);
        BinaryPrimitives.WriteUInt64LittleEndian(fdstat.AsSpan(16), (ulong)Held(fd).Inheriting);
        memory.Write(pointer, fdstat);
        return Errno.Success;
    }

    // APPEND and NONBLOCK are kept, and a sync flag is INVAL, as under both controls. NONBLOCK changes
    // nothing on a file or directory, which never blocks; a pipe would, and Fog does not make one
    // return AGAIN, so on a pipe it is NOTSUP.
    private Errno SetFlags(int fd, int wanted)
    {
        Stat stat = Stat(fd);
        if ((wanted & ~(Append | Nonblock)) != 0)
        {
            throw new InvalidArgumentException();
        }

        flags[fd] = (wanted & Nonblock) != 0 && stat.Type == '|' ? throw new NotSupportedByFogException() : wanted;
        return Errno.Success;
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
    private Errno FileStat(Stat stat, uint pointer)
    {
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
        uint? mtime = Mtime(mtim, flags);
        Stat(fd);
        return mtime is { } seconds ? Wait(process.FWStatAsync(fd, WStat(mtime: seconds))) : Errno.Success;
    }

    private Errno SetTimes(string path, long mtim, int flags)
    {
        uint? mtime = Mtime(mtim, flags);
        Wait(process.StatAsync(path));
        return mtime is { } seconds ? Wait(process.WStatAsync(path, WStat(mtime: seconds))) : Errno.Success;
    }

    private uint? Mtime(long mtim, int which)
    {
        const int AccessTime = 1, AccessNow = 2, ModificationTime = 4, Now = 8;
        if ((which & (AccessTime | AccessNow)) == (AccessTime | AccessNow) || (which & (ModificationTime | Now)) == (ModificationTime | Now))
        {
            throw new InvalidArgumentException();
        }

        if ((which & (AccessTime | AccessNow)) != 0)
        {
            throw new NotSupportedByFogException();
        }

        return (which & Now) != 0 ? (uint)clock.GetUtcNow().ToUnixTimeSeconds()
            : (which & ModificationTime) != 0 ? (uint)((ulong)mtim / Nanoseconds)
            : null;
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

    // The realtime and monotonic clocks are the host's; a process or thread CPU-time clock is NOTSUP.
    private Errno Clock(int id, uint pointer, Func<int, ulong> read)
    {
        const int Realtime = 0, Monotonic = 1, ProcessTime = 2, ThreadTime = 3;
        ulong value = id switch
        {
            Realtime or Monotonic => read(id),
            ProcessTime or ThreadTime => throw new NotSupportedByFogException(),
            _ => throw new InvalidArgumentException(),
        };
        memory.WriteUInt64(pointer, value);
        return Errno.Success;
    }

    // Realtime is counted in 100-nanosecond ticks, monotonic time in the clock's own.
    private ulong Resolution(int id) => id == 0 ? 100 : (ulong)Math.Max(1, Nanoseconds / (ulong)clock.TimestampFrequency);

    private ulong Now(int id) => id == 0
        ? (ulong)(clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks * 100
        : (ulong)((Int128)clock.GetTimestamp() * (Int128)Nanoseconds / clock.TimestampFrequency);

    // The host's cryptographic random source, fresh on every call, straight into guest memory.
    private Errno Random(uint buffer, uint length)
    {
        memory.Fill(buffer, length, RandomNumberGenerator.Fill);
        return Errno.Success;
    }

    // Waits until a subscription is ready, looking every 10 ms, which is a clock's resolution here: a
    // clock once its deadline has passed, counted down by the time waited from the call's start or, if
    // absolute, from a time on that clock; fd_read on a pipe once bytes are queued or the other end
    // has gone, and on anything else at once, with what is left past its offset; fd_write at once. A
    // subscription that cannot be looked at is ready with its errno. Each ready one is an event.
    private Errno Poll(uint subscriptions, uint events, uint count, uint ready)
    {
        if (count == 0)
        {
            throw new InvalidArgumentException();
        }

        const long Step = 10_000_000;
        byte[][] wanted = [.. Enumerable.Range(0, (int)count).Select(i => memory.Read(subscriptions + (48 * (uint)i), 48))];
        long[] started = [(long)Now(0), (long)Now(1)];
        long waited = 0;
        List<byte[]> fired;
        while ((fired = [.. wanted.Select(subscription => Ready(subscription, started, waited)).OfType<byte[]>()]).Count == 0)
        {
            Task.Delay(TimeSpan.FromTicks(Step / 100), clock).GetAwaiter().GetResult();
            waited += Step;
        }

        for (int i = 0; i < fired.Count; i++)
        {
            memory.Write(events + (32 * (uint)i), fired[i]);
        }

        memory.WriteUInt32(ready, (uint)fired.Count);
        return Errno.Success;
    }

    // The event for a subscription that is ready, or null.
    private byte[]? Ready(byte[] subscription, long[] started, long waited)
    {
        const int Absolute = 1;
        var happened = new byte[32];
        subscription.AsSpan(0, 8).CopyTo(happened);
        happened[10] = subscription[8];
        try
        {
            if (subscription[8] == 0)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(subscription.AsSpan(16));
                if (id is not (0 or 1))
                {
                    return Failed(happened, id is 2 or 3 ? Errno.Notsup : Errno.Inval);
                }

                long timeout = BinaryPrimitives.ReadInt64LittleEndian(subscription.AsSpan(24));
                bool absolute = (BinaryPrimitives.ReadUInt16LittleEndian(subscription.AsSpan(40)) & Absolute) != 0;
                return timeout - (absolute ? started[id] : 0) - waited <= 0 ? happened : null;
            }

            int fd = Require(BinaryPrimitives.ReadInt32LittleEndian(subscription.AsSpan(16)), Rights.PollFdReadwrite);
            Stat stat = Stat(fd);
            if (subscription[8] == 2)
            {
                return happened;
            }

            (long Queued, bool HungUp)? pipe = Wait(process.PipeReadinessAsync(fd));
            long left = pipe?.Queued ?? (long)stat.Length - Wait(process.SeekAsync(fd, 0, 1));
            BinaryPrimitives.WriteInt64LittleEndian(happened.AsSpan(16), Math.Max(0, left));
            happened[24] = pipe is { HungUp: true } ? (byte)1 : (byte)0;
            return pipe is not { Queued: 0, HungUp: false } ? happened : null;
        }
        catch (NotCapableException)
        {
            return Failed(happened, Errno.Notcapable);
        }
        catch (SyscallException error)
        {
            return Failed(happened, Map(error.Message));
        }
    }

    // No descriptor is a socket.
    private Errno NotSocket(int fd)
    {
        Stat(fd);
        return Errno.Notsock;
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

    // With APPEND, a write at the descriptor's offset first moves it to the file's end.
    private Errno Write(int fd, uint vector, uint count, long offset, uint written)
    {
        byte[] data = Gather(vector, count);
        memory.Check(written, 4);
        if (offset < 0 && (flags.GetValueOrDefault(fd) & Append) != 0)
        {
            Wait(process.SeekAsync(fd, 0, 2));
        }

        memory.WriteUInt32(written, (uint)Wait(process.PWriteAsync(fd, data, offset)));
        return Errno.Success;
    }

    // One kernel read of as much as the buffers hold, up to CopyLimit, spread across them in order.
    // A directory has no right to be read, so its 9P directory entries never reach the guest.
    private Errno Read(int fd, uint vector, uint count, long offset, uint read)
    {
        List<(uint Pointer, uint Length)> buffers = Buffers(vector, count);
        foreach ((uint pointer, uint length) in buffers)
        {
            memory.Check(pointer, length);
        }

        memory.Check(read, 4);

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
        if (Stat(fd).Type == '|')
        {
            // A pipe has no offset to move.
            throw new PipeSeekException();
        }

        memory.WriteUInt64(position, (ulong)Wait(process.SeekAsync(fd, offset, whence)));
        return Errno.Success;
    }
}
