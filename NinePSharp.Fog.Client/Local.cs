using System.Threading.Channels;

namespace NinePSharp.Fog.Client;

// What fog has of the machine it runs on: its standard streams, its working directory, its
// environment and login name, its terminal, its interrupts, and the drives it serves.
public sealed class Local
{
    public required Stream Input { get; init; }

    public required Stream Output { get; init; }

    public required Stream Error { get; init; }

    // The working directory as the machine names it: /home/glenda/work, or C:\work on Windows.
    public required string Directory { get; init; }

    public required Func<string, string?> Environment { get; init; }

    public required string LoginName { get; init; }

    // Reads a password after the prompt without echoing it, or gives null at the end of input.
    public required Func<string, CancellationToken, ValueTask<string?>> ReadPassword { get; init; }

    // Reads a line the terminal has cooked, its newline included, or gives null at its end.
    public required Func<CancellationToken, ValueTask<string?>> ReadConsole { get; init; }

    // Writes to the terminal.
    public required Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> WriteConsole { get; init; }

    // Each interrupt the user types, as "interrupt".
    public ChannelReader<string> Interrupts { get; init; } = Channel.CreateUnbounded<string>().Reader;

    // Windows' drives, by drawterm's names for them (C:), and where each is; none on a machine
    // whose files are one tree from /.
    public IReadOnlyDictionary<string, string> Drives { get; init; } = new Dictionary<string, string>();

    // The tree /mnt/term/root serves when there are no drives.
    public string Root { get; init; } = "/";
}
