using System;

namespace Agnes.App.Desktop.Views;

/// <summary>The single owner of transcript scroll intent. Geometry remains in the Avalonia view.</summary>
internal enum TranscriptScrollState
{
    FollowingTail,
    ReadingHistory,
    GestureScrolling,
    IndexDragging,
    IndexSettling,
}

/// <summary>A coalesced index target tagged with the interaction generation that owns it.</summary>
internal readonly record struct IndexSeekRequest(int Row, long Generation, bool FollowTail);

/// <summary>
/// Pure interaction policy for the virtualized transcript. Every input path advances a generation, so
/// layout callbacks from an older wheel, drag, session, or index target can prove that they are stale.
/// </summary>
internal sealed class TranscriptScrollPolicy
{
    private int? _pendingRow;
    private bool _targetIsTail;
    private bool _targetSettled;
    private long _generation;

    public TranscriptScrollState State { get; private set; } = TranscriptScrollState.FollowingTail;

    public long Generation => _generation;

    public double FrozenIndexMaximum { get; private set; }

    public bool IsAtLiveTail => State == TranscriptScrollState.FollowingTail;

    public bool ShouldFollowAppend => IsAtLiveTail
        || (_targetIsTail && State is TranscriptScrollState.IndexDragging or TranscriptScrollState.IndexSettling);

    public long SwitchSession() => FollowTail();

    public long FollowTail()
    {
        InvalidateDeferredWork();
        State = TranscriptScrollState.FollowingTail;
        return _generation;
    }

    public long ReadHistory()
    {
        InvalidateDeferredWork();
        State = TranscriptScrollState.ReadingHistory;
        return _generation;
    }

    public long BeginGesture()
    {
        InvalidateDeferredWork();
        State = TranscriptScrollState.GestureScrolling;
        return _generation;
    }

    public bool FinishGesture(long generation, bool isGenuinelyAtBottom)
    {
        if (!IsCurrent(generation) || State != TranscriptScrollState.GestureScrolling)
        {
            return false;
        }

        State = isGenuinelyAtBottom
            ? TranscriptScrollState.FollowingTail
            : TranscriptScrollState.ReadingHistory;
        return true;
    }

    public void BeginIndexDrag(double maximum)
    {
        if (State == TranscriptScrollState.IndexDragging)
        {
            return;
        }

        InvalidateDeferredWork();
        FrozenIndexMaximum = Math.Max(0, maximum);
        State = TranscriptScrollState.IndexDragging;
    }

    public long RequestIndexTarget(double value, double currentMaximum)
    {
        var dragging = State == TranscriptScrollState.IndexDragging;
        var maximum = dragging ? FrozenIndexMaximum : Math.Max(0, currentMaximum);
        InvalidateDeferredWork();
        State = dragging ? TranscriptScrollState.IndexDragging : TranscriptScrollState.IndexSettling;

        var clamped = Math.Clamp(value, 0, maximum);
        _targetIsTail = TranscriptIndexScroll.IsAtEnd(clamped, maximum);
        _pendingRow = (int)Math.Round(clamped);
        return _generation;
    }

    public void EndIndexDrag()
    {
        if (State != TranscriptScrollState.IndexDragging)
        {
            return;
        }

        State = _targetIsTail
            ? TranscriptScrollState.FollowingTail
            : _targetSettled ? TranscriptScrollState.ReadingHistory : TranscriptScrollState.IndexSettling;
    }

    public bool TryTakeLatestIndexTarget(out IndexSeekRequest request)
    {
        if (_pendingRow is not { } row)
        {
            request = default;
            return false;
        }

        _pendingRow = null;
        request = new IndexSeekRequest(row, _generation, _targetIsTail);
        return true;
    }

    public bool IsCurrent(long generation) => generation == _generation;

    public void CompleteIndexSeek(long generation)
    {
        if (!IsCurrent(generation)
            || State is not (TranscriptScrollState.IndexSettling or TranscriptScrollState.IndexDragging))
        {
            return;
        }

        _targetSettled = true;
        if (State == TranscriptScrollState.IndexDragging)
        {
            return;
        }

        State = _targetIsTail
            ? TranscriptScrollState.FollowingTail
            : TranscriptScrollState.ReadingHistory;
    }

    private void InvalidateDeferredWork()
    {
        _generation++;
        _pendingRow = null;
        _targetIsTail = false;
        _targetSettled = false;
    }
}
