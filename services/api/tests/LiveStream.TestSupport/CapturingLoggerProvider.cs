using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LiveStream.TestSupport;

public sealed record CapturedLog(LogLevel Level, string Category, string Message, Exception? Exception);

/// <summary>
/// Captures host logs so tests can diagnose failures inside the API and assert that operationally
/// important events are actually logged (docs/12-observability-and-reliability.md).
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _logs = new();

    public IReadOnlyCollection<CapturedLog> Logs => _logs;

    public IEnumerable<CapturedLog> Failures =>
        _logs.Where(l => l.Level >= LogLevel.Error);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _logs);

    public void Clear() => _logs.Clear();

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            sink.Enqueue(new CapturedLog(logLevel, category, formatter(state, exception), exception));
    }
}
