namespace Agnes.Host.Sessions;

/// <summary>What a live session looks like right now, as far as "is it getting anywhere" goes.</summary>
/// <param name="TurnActive">Whether a turn is in flight.</param>
/// <param name="ToolCallsInFlight">Tool calls started and not yet reported finished.</param>
/// <param name="Quiet">How long since the session last emitted anything.</param>
public readonly record struct SessionActivity(bool TurnActive, int ToolCallsInFlight, TimeSpan Quiet);

/// <summary>What the watchdog makes of a session.</summary>
public enum LivenessVerdict
{
    /// <summary>Nothing to say: idle between turns, or visibly getting on with it.</summary>
    Fine,

    /// <summary>A turn is in flight with nothing outstanding and nothing emitted for a long time.</summary>
    Wedged,
}

/// <summary>
/// Decides whether a session that claims to be working actually is.
/// </summary>
/// <remarks>
/// Agnes could previously only tell a live agent from a dead one: <c>IsTurnActive</c> is set on prompt and
/// cleared on turn-end, and crash recovery fires when the event stream ends. A process that stays alive with
/// its stream open but stops producing anything looks exactly like one hard at work, forever.
///
/// The naive test — "no events for N minutes" — is wrong, and observably so: a real session here ran
/// <b>1h38m</b> without emitting a single parent event while a subagent worked perfectly, because a
/// subagent's progress never crosses the ACP boundary. What distinguished it was a tool call sitting
/// in-flight the whole time. So an outstanding tool call means "busy, however long it takes", and only
/// silence with <i>nothing</i> outstanding is suspicious.
/// </remarks>
public static class SessionLiveness
{
    /// <summary>How long a turn may emit nothing, with no tool call outstanding, before it is called wedged.
    /// Generous on purpose: a model can think for minutes between tokens on a throttled provider, and a
    /// false alarm here trains the operator to ignore the real ones.</summary>
    public static readonly TimeSpan DefaultQuietLimit = TimeSpan.FromMinutes(10);

    /// <summary>Pure: the caller supplies the observation, so every branch is testable without a clock.</summary>
    public static LivenessVerdict Assess(SessionActivity activity, TimeSpan quietLimit)
    {
        // No turn: the session is simply idle. That is the goal watcher's business, not this one's.
        if (!activity.TurnActive)
        {
            return LivenessVerdict.Fine;
        }

        // Something is outstanding — a tool, a subagent — so silence is expected and means nothing.
        if (activity.ToolCallsInFlight > 0)
        {
            return LivenessVerdict.Fine;
        }

        return activity.Quiet >= quietLimit ? LivenessVerdict.Wedged : LivenessVerdict.Fine;
    }
}

/// <summary>How the host reacts to a session that stops getting anywhere mid-turn.</summary>
public sealed record LivenessOptions
{
    /// <summary>Whether the watchdog runs at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Silence (with nothing outstanding) that counts as wedged.</summary>
    public TimeSpan QuietLimit { get; init; } = SessionLiveness.DefaultQuietLimit;
}
