using Agnes.Abstractions;
using Agnes.Abstractions.Events;

namespace Agnes.Host.Ops;

/// <summary>One recorded error: where it came from and a short message. Immutable; the store keeps snapshots.</summary>
public sealed record ErrorTelemetryEntry(DateTimeOffset Timestamp, string Source, string Message);

/// <summary>
/// A bounded, thread-safe ring of the most recent host-side errors, fed by two sources: agent/adapter faults
/// arriving on the event spine (<see cref="AgentErrorEvent"/>, observed) and process-level failures the host
/// wires in (unhandled exceptions, unobserved task exceptions) via <see cref="Record"/>. It only accumulates
/// in memory — nothing is sent anywhere. The diagnostic collector reads it, and that bundle is attached to a
/// bug report only on the owner-only, explicitly-opted-in path.
/// </summary>
public sealed class ErrorTelemetryStore : IEventObserver<AgentErrorEvent>
{
    private readonly int _capacity;
    private readonly Queue<ErrorTelemetryEntry> _entries;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    public ErrorTelemetryStore(int capacity = 100, Func<DateTimeOffset>? clock = null)
    {
        _capacity = Math.Max(1, capacity);
        _entries = new Queue<ErrorTelemetryEntry>(_capacity);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Records an error from any source, evicting the oldest once at capacity.</summary>
    public void Record(string source, string message)
    {
        var entry = new ErrorTelemetryEntry(_clock(), source, message);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    /// <summary>A point-in-time copy of the recorded errors, oldest first.</summary>
    public IReadOnlyList<ErrorTelemetryEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    /// <summary>Spine hook: every agent/adapter-reported error is recorded automatically.</summary>
    public ValueTask ObserveAsync(AgentErrorEvent evt, CancellationToken cancellationToken = default)
    {
        Record("agent", evt.Message);
        return ValueTask.CompletedTask;
    }
}
