using Agnes.Abstractions;

namespace Agnes.Recording;

/// <summary>
/// Accumulates a session's events into a <see cref="SessionRecording"/>, stamping each with its
/// offset from the first event so playback preserves the original streaming cadence. Feed it the
/// events from an <see cref="IAgentSession"/>'s stream.
/// </summary>
public sealed class SessionRecorder
{
    private readonly List<RecordedEvent> _events = [];
    private readonly object _gate = new();
    private DateTimeOffset? _start;

    public int Count
    {
        get { lock (_gate) { return _events.Count; } }
    }

    public void Record(SessionEvent @event)
    {
        lock (_gate)
        {
            _start ??= @event.Timestamp;
            var offset = (long)(@event.Timestamp - _start.Value).TotalMilliseconds;
            _events.Add(new RecordedEvent(Math.Max(0, offset), @event));
        }
    }

    public SessionRecording Build(string name, string adapterId, string agentDisplayName)
    {
        lock (_gate)
        {
            return new SessionRecording(name, adapterId, agentDisplayName, _start ?? DateTimeOffset.UtcNow, [.. _events]);
        }
    }
}
