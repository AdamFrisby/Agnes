using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Agnes.Acp.Tests;

/// <summary>Minimal logger that collects formatted messages for assertions/diagnostics.</summary>
internal sealed class CapturingLogger : ILogger
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Enqueue($"{logLevel}: {formatter(state, exception)}");

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
