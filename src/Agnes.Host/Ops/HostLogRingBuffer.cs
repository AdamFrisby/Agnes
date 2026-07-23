using Microsoft.Extensions.Logging;

namespace Agnes.Host.Ops;

/// <summary>One captured host log line (already formatted). Immutable — the ring stores snapshots, not
/// live references, so a reader never sees a half-written entry.</summary>
public sealed record HostLogLine(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    /// <summary>A compact one-line rendering for the diagnostic bundle.</summary>
    public override string ToString()
        => $"{Timestamp:O} {Level,-11} {Category}: {Message}";
}

/// <summary>
/// A bounded, thread-safe in-memory ring of the most recent host log lines. This is the ONLY place recent
/// host logs are kept readable for the owner-only diagnostic bundle — Agnes has no on-disk log Serilog sink
/// to scrape, so a small buffer is added rather than reaching into the console. Oldest lines are dropped once
/// the capacity is reached, so memory stays bounded regardless of log volume.
/// </summary>
public sealed class HostLogRingBuffer
{
    private readonly int _capacity;
    private readonly Queue<HostLogLine> _lines;
    private readonly object _gate = new();

    public HostLogRingBuffer(int capacity = 500)
    {
        _capacity = Math.Max(1, capacity);
        _lines = new Queue<HostLogLine>(_capacity);
    }

    /// <summary>Appends a line, evicting the oldest once at capacity.</summary>
    public void Add(HostLogLine line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    /// <summary>A point-in-time copy of the buffered lines, oldest first.</summary>
    public IReadOnlyList<HostLogLine> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToArray();
        }
    }
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that tees host log lines (at or above <see cref="MinLevel"/>) into a
/// shared <see cref="HostLogRingBuffer"/>. Registered alongside the console provider so the same lines the
/// operator sees are also available, most-recent-first, to the diagnostic collector — never shipped anywhere
/// on their own, only assembled into an owner-only, opt-in bundle.
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly HostLogRingBuffer _buffer;

    public RingBufferLoggerProvider(HostLogRingBuffer buffer, LogLevel minLevel = LogLevel.Information)
    {
        _buffer = buffer;
        MinLevel = minLevel;
    }

    /// <summary>The lowest level captured into the ring; lower-level chatter is ignored.</summary>
    public LogLevel MinLevel { get; }

    public ILogger CreateLogger(string categoryName) => new RingLogger(categoryName, _buffer, MinLevel);

    public void Dispose()
    {
        // Nothing to release: the ring buffer is a shared singleton owned by DI, not by this provider.
    }

    private sealed class RingLogger(string category, HostLogRingBuffer buffer, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message} [{exception.GetType().Name}: {exception.Message}]";
            }

            buffer.Add(new HostLogLine(DateTimeOffset.UtcNow, logLevel, category, message));
        }
    }
}
