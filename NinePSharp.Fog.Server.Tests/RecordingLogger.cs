using Microsoft.Extensions.Logging;

namespace NinePSharp.Fog.Server.Tests;

internal sealed class RecordingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> entries = new();

    internal TaskCompletionSource FirstError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (entries)
            {
                return entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (entries)
        {
            entries.Add((logLevel, formatter(state, exception), exception));
        }

        if (logLevel == LogLevel.Error)
        {
            FirstError.TrySetResult();
        }
    }

    internal IEnumerable<string> At(LogLevel level) => Entries.Where(entry => entry.Level == level).Select(entry => entry.Message);
}
