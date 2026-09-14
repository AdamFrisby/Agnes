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

    /// <summary>The oldest sequence loaded, or 0 when nothing is. Greater than 1 means the view started
    /// part-way through the log — a tail-first subscription — and older history can still be fetched.</summary>
    public long FirstSequence { get; private set; }

    /// <summary>Raised after <see cref="Prepend"/> inserted older events; a consumer rebuilds from
    /// <see cref="Events"/>, since the new events precede everything it has already rendered.</summary>
    public event Action? HistoryPrepended;

    /// <summary>Snapshot of applied events in order.</summary>
    public IReadOnlyList<SessionEvent> Events
    {
        get { lock (_gate) { return _events.ToArray(); } }
    }

    /// <summary>Raised (outside the lock) for each newly applied event.</summary>
    public event Action<SessionEvent>? EventAppended;

    /// <summary>
    /// Raised for each event that arrived live — pushed by the hub rather than read from a snapshot — with
    /// the sequence the view held just before it. The pair says exactly which stretch of the log the event
    /// answers for, which is what a durable cache needs to extend its range without assuming sequences are
    /// dense. Snapshot events are not raised here: whoever fetched the snapshot knows its bounds already.
    /// </summary>
    public event Action<long, SessionEvent>? LiveApplied;

    /// <summary>Session metadata from the snapshot (modes, adapter, …), once applied.</summary>
    public SessionInfo? Info { get; private set; }

    public void ApplySnapshot(SessionSnapshot snapshot)
    {
        Info = snapshot.Session;
        List<SessionEvent> toRaise = [];
        List<(long Previous, SessionEvent Event)> live = [];
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
                var previous = LastSequence;
                if (AppendLocked(buffered))
                {
                    toRaise.Add(buffered);
                    live.Add((previous, buffered));
                }
            }

            _pending.Clear();
        }

        Raise(toRaise);
        foreach (var (previous, @event) in live)
        {
            LiveApplied?.Invoke(previous, @event);
        }
    }

    public void Apply(SessionEvent @event)
    {
        bool appended;
        long previous;
        lock (_gate)
        {
            if (!_snapshotApplied)
            {
                _pending.Add(@event);
                return;
            }

            previous = LastSequence;
            appended = AppendLocked(@event);
        }

        if (appended)
        {
            EventAppended?.Invoke(@event);
            LiveApplied?.Invoke(previous, @event);
        }
    }

    private bool AppendLocked(SessionEvent @event)
    {
        if (@event.Sequence <= LastSequence)
        {
            return false;
        }
        if (_events.Count == 0)
        {
            FirstSequence = @event.Sequence;
        }
        _events.Add(@event);
        LastSequence = @event.Sequence;
        return true;
    }

    /// <summary>
    /// Inserts events older than anything loaded, in order, and says so. Anything at or past
    /// <see cref="FirstSequence"/> is ignored — it is either already here or belongs to <see cref="Apply"/>.
    /// </summary>
    public int Prepend(IEnumerable<SessionEvent> older)
    {
        List<SessionEvent> added;
        lock (_gate)
        {
            var first = _events.Count == 0 ? long.MaxValue : _events[0].Sequence;
            added = older.Where(e => e.Sequence < first)
                .GroupBy(e => e.Sequence).Select(g => g.First())
                .OrderBy(e => e.Sequence)
                .ToList();
            if (added.Count == 0)
            {
                return 0;
            }
            _events.InsertRange(0, added);
            FirstSequence = _events[0].Sequence;
            if (LastSequence == 0)
            {
                LastSequence = _events[^1].Sequence;
            }
        }
        HistoryPrepended?.Invoke();
        return added.Count;
    }

    private void Raise(List<SessionEvent> events)
    {
        foreach (var @event in events)
        {
            EventAppended?.Invoke(@event);
        }
    }
}
