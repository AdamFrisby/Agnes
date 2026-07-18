using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// A client-side, ordered view of one session's event log. Applies a snapshot then live
/// events, deduping by sequence so an event that appears in both is harmless. Live events
/// that arrive before the snapshot are buffered and flushed in order — the mechanism that
/// makes reconnect and multi-client sync consistent.
/// </summary>
public sealed class SessionView
{
    private readonly object _gate = new();
    private readonly List<SessionEvent> _events = [];
    private readonly List<SessionEvent> _pending = [];
    private bool _snapshotApplied;

    public SessionView(string sessionId) => SessionId = sessionId;

    public string SessionId { get; }

    /// <summary>Highest applied sequence; the cursor to resume from on reconnect.</summary>
    public long LastSequence { get; private set; }

    /// <summary>Snapshot of applied events in order.</summary>
    public IReadOnlyList<SessionEvent> Events
    {
        get { lock (_gate) { return _events.ToArray(); } }
    }

    /// <summary>Raised (outside the lock) for each newly applied event.</summary>
    public event Action<SessionEvent>? EventAppended;

    /// <summary>Session metadata from the snapshot (modes, adapter, …), once applied.</summary>
    public SessionInfo? Info { get; private set; }

    public void ApplySnapshot(SessionSnapshot snapshot)
    {
        Info = snapshot.Session;
        List<SessionEvent> toRaise = [];
        lock (_gate)
        {
            foreach (var @event in snapshot.Events)
            {
                if (AppendLocked(@event))
                {
                    toRaise.Add(@event);
                }
            }

            _snapshotApplied = true;
            foreach (var buffered in _pending.OrderBy(e => e.Sequence))
            {
                if (AppendLocked(buffered))
                {
                    toRaise.Add(buffered);
                }
            }

            _pending.Clear();
        }

        Raise(toRaise);
    }

    public void Apply(SessionEvent @event)
    {
        bool appended;
        lock (_gate)
        {
            if (!_snapshotApplied)
            {
                _pending.Add(@event);
                return;
            }

            appended = AppendLocked(@event);
        }

        if (appended)
        {
            EventAppended?.Invoke(@event);
        }
    }

    private bool AppendLocked(SessionEvent @event)
    {
        if (@event.Sequence <= LastSequence)
        {
            return false;
        }

        _events.Add(@event);
        LastSequence = @event.Sequence;
        return true;
    }

    private void Raise(List<SessionEvent> events)
    {
        foreach (var @event in events)
        {
            EventAppended?.Invoke(@event);
        }
    }
}
