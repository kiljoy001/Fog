using System.Text;
using NinePSharp.Fog.Kernel;
using WebAssembly.Runtime;

namespace NinePSharp.Fog.Wasm;

// The Preview 1 functions Fog provides, acting on the process that execed the module. Each runs on
// the guest's thread and blocks it, and only it, on the kernel call. A call reads what it needs from
// guest memory and checks where its results go before any kernel call; any access outside memory is
// FAULT.
internal sealed class Preview1Host(Process process, IReadOnlyList<string> argv, IReadOnlyList<string> environment)
{
    // The most one call copies between guest memory and the kernel; a longer write is short.
    public const int CopyLimit = 1 << 20;

    private GuestMemory memory = null!;

    public void Attach(UnmanagedMemory guest) => memory = new GuestMemory(guest);

    public Delegate Function(string name) => name switch
    {
        "args_get" => new Func<int, int, int>((list, buffer) => Call(() => Strings(argv, (uint)list, (uint)buffer))),
        "args_sizes_get" => new Func<int, int, int>((count, size) => Call(() => Sizes(argv, (uint)count, (uint)size))),
        "environ_get" => new Func<int, int, int>((list, buffer) => Call(() => Strings(environment, (uint)list, (uint)buffer))),
        "environ_sizes_get" => new Func<int, int, int>((count, size) => Call(() => Sizes(environment, (uint)count, (uint)size))),
        "fd_write" => new Func<int, int, int, int, int>((fd, vector, count, written) => Call(() => FdWrite(fd, (uint)vector, (uint)count, (uint)written))),
        "proc_exit" => new Action<int>(code => throw new ProcExitException((uint)code)),
        _ => Preview1.Unsupported(Preview1.Functions[name]),
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
        catch (SyscallException error)
        {
            return (int)(error.Message == Errors.BadFd ? Errno.Badf : Errno.Io);
        }
    }

    // A kernel call made for the guest, on the guest's thread.
    private static T Wait<T>(ValueTask<T> call) => call.AsTask().GetAwaiter().GetResult();

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

    private Errno FdWrite(int fd, uint vector, uint count, uint written)
    {
        var data = new MemoryStream();
        for (uint i = 0; i < count; i++)
        {
            uint pointer = memory.ReadUInt32(vector + (8 * i));
            uint length = memory.ReadUInt32(vector + (8 * i) + 4);
            memory.Check(pointer, length);
            data.Write(memory.Read(pointer, (uint)Math.Min(length, CopyLimit - data.Length)));
        }

        memory.Check(written, 4);
        memory.WriteUInt32(written, (uint)Wait(process.WriteAsync(fd, data.ToArray())));
        return Errno.Success;
    }
}
