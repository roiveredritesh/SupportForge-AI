using Microsoft.Extensions.Logging;

namespace SupportForge.Api.Tests.Agents;

/// <summary>Minimal in-memory <see cref="ILogger{T}"/> capture used by agent/verifier tests to assert
/// on logged entry/completion/retry/failure output without pulling in a logging test framework.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));
}
