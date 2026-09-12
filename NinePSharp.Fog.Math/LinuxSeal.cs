using System.Runtime.InteropServices;

namespace NinePSharp.Fog.Symbolics;

/// <summary>Demo defense in depth, applied to all runtime threads before source input. Not the v1 syscall allowlist.</summary>
internal static class LinuxSeal
{
    private const uint Allow = 0x7fff0000;
    private const uint Denied = 0x00050001;

    internal static void Apply()
    {
        IntPtr filter = Create();
        try
        {
            Check(Attribute(filter, 4, 1)); // TSYNC: the filter covers existing .NET threads too.
            AddRules(filter);
            Check(Load(filter));
        }
        finally { Release(filter); }
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsLinux()) throw new FogException("unavailable");
        Check(Prctl(38, 1, 0, 0, 0));
        IntPtr filter = Init(Allow);
        if (filter == IntPtr.Zero) throw new FogException("unavailable");
        return filter;
    }

    private static void AddRules(IntPtr filter)
    {
        foreach (string name in new[]
        {
                "socket", "socketpair", "connect", "bind", "listen", "accept", "accept4", "execve", "execveat", "fork", "vfork",
                "ptrace", "process_vm_readv", "process_vm_writev", "bpf", "perf_event_open", "io_uring_setup", "io_uring_enter", "io_uring_register",
                "keyctl", "add_key", "request_key", "mount", "umount2", "pivot_root", "chroot", "unshare", "setns",
                "fsopen", "fsconfig", "fsmount", "fspick", "open_tree", "move_mount", "mount_setattr",
                "init_module", "finit_module", "delete_module", "kexec_load", "kexec_file_load", "reboot", "swapon", "swapoff",
                "setuid", "setgid", "setreuid", "setregid", "setresuid", "setresgid", "setfsuid", "setfsgid", "setgroups", "capset", "setrlimit",
            }) Rule(filter, name, Denied, []);
        Rule(filter, "clone3", 0x00050026, []); // ENOSYS allows libc's thread-only clone fallback.
        Rule(filter, "clone", Denied, [new(0, 7, 0x10000, 0)]); // Reject non-thread clones.
        foreach (ulong flag in new ulong[] { 0x80, 0x20000, 0x2000000, 0x4000000, 0x8000000, 0x10000000, 0x20000000, 0x40000000 })
            Rule(filter, "clone", Denied, [new(0, 7, flag, flag)]);
        Rule(filter, "prlimit64", Denied, [new(2, 1, 0, 0)]); // New limits pointer must be null; queries remain possible.
    }

    private static void Rule(IntPtr filter, string name, uint action, Comparison[] comparisons)
    {
        int syscall = Resolve(name);
        if (syscall >= 0) Check(Add(filter, action, syscall, (uint)comparisons.Length, comparisons));
    }

    private static void Check(int code) { if (code != 0) throw new FogException("unavailable"); }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Comparison(uint argument, uint operation, ulong first, ulong second)
    {
        private readonly uint argument = argument;
        private readonly uint operation = operation;
        private readonly ulong first = first;
        private readonly ulong second = second;
    }

    [DllImport("libc", EntryPoint = "prctl")]
    private static extern int Prctl(int option, ulong a, ulong b, ulong c, ulong d);
    [DllImport("libseccomp.so.2", EntryPoint = "seccomp_init")]
    private static extern IntPtr Init(uint action);
    [DllImport("libseccomp.so.2", EntryPoint = "seccomp_attr_set")]
    private static extern int Attribute(IntPtr filter, uint attribute, uint value);
    [DllImport("libseccomp.so.2", EntryPoint = "seccomp_syscall_resolve_name")]
    private static extern int Resolve([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport("libseccomp.so.2", EntryPoint = "seccomp_rule_add_array")]
    private static extern int Add(IntPtr filter, uint action, int syscall, uint count, [In] Comparison[] comparisons);
    [DllImport("libseccomp.so.2", EntryPoint = "seccomp_load")]
    private static extern int Load(IntPtr filter);
    [DllImport("libseccomp.so.2", EntryPoint = "seccomp_release")]
    private static extern void Release(IntPtr filter);
}
