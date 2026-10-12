namespace NinePSharp.Fog.Kernel;

// An executable format beside #! scripts and managed programs. A loader returns null for a header
// that is not its own. For its own it reads the image, refuses one it cannot run with exec's
// "exec header invalid", and returns the program that runs it; nothing of the process has changed
// until then.
public delegate ValueTask<ProgramMain?> ExecLoader(Process process, string path, ReadOnlyMemory<byte> header);
