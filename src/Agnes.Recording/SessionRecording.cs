using Agnes.Abstractions;

namespace Agnes.Recording;

/// <summary>One recorded event, with its offset (ms) from the start of the recording.</summary>
public sealed record RecordedEvent(long OffsetMs, SessionEvent Event);

/// <summary>
/// A captured session: the ordered <see cref="SessionEvent"/> stream a real (or scripted) agent
/// produced, with relative timing. Replayed as realistic test data by the playback host.
/// </summary>
public sealed record SessionRecording(
    string Name,
    string AdapterId,
    string AgentDisplayName,
    DateTimeOffset RecordedAt,
    IReadOnlyList<RecordedEvent> Events)
{
    /// <summary>Total wall-clock duration of the recording.</summary>
    public long DurationMs => Events.Count == 0 ? 0 : Events[^1].OffsetMs;
}
