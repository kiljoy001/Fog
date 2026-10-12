using System.Text;
using NinePSharp.Constants;
using NinePSharp.Fog.Kernel;
using WebAssembly.Runtime;

namespace NinePSharp.Fog.Wasm;

// The Preview 1 functions Fog provides, acting on the process that execed the module. Each runs on
// the guest's thread and blocks it, and only it, on the kernel call. A call reads what it needs from
// guest memory and checks where its results go before any kernel call; any access outside memory is
// FAULT.
internal sealed class Preview1Host(Process process)
{
    // The most one call copies between guest memory and the kernel; a longer write is short.
    public const int CopyLimit = 1 << 20;

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
        "fd_write" => new Func<int, int, int, int, int>((fd, vector, count, written) => Call(() => Write(fd, (uint)vector, (uint)count, -1, (uint)written))),
        "fd_pwrite" => new Func<int, int, int, long, int, int>((fd, vector, count, offset, written) => Call(() => Write(fd, (uint)vector, (uint)count, Offset(offset), (uint)written))),
        "fd_read" => new Func<int, int, int, int, int>((fd, vector, count, read) => Call(() => Read(fd, (uint)vector, (uint)count, -1, (uint)read))),
        "fd_pread" => new Func<int, int, int, long, int, int>((fd, vector, count, offset, read) => Call(() => Read(fd, (uint)vector, (uint)count, Offset(offset), (uint)read))),
        "fd_seek" => new Func<int, long, int, int, int>((fd, offset, whence, position) => Call(() => Seek(fd, offset, whence, (uint)position))),
        "fd_tell" => new Func<int, int, int>((fd, position) => Call(() => Seek(fd, 0, 1, (uint)position))),
        "fd_close" => new Func<int, int>(fd => Call(() => Wait(process.CloseAsync(fd)))),
        "fd_renumber" => new Func<int, int, int>((fd, to) => Call(() => Wait(process.RenumberAsync(fd, to)))),
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
