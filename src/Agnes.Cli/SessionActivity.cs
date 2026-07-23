using Agnes.Abstractions;

namespace Agnes.Cli;

/// <summary>The idle/running/errored classification of a session, derived purely from its event log.</summary>
public enum SessionState
{
    /// <summary>A turn is in flight (a prompt was accepted and no terminal event has closed it yet).</summary>
    Running,

    /// <summary>The current turn has ended and nothing is queued — safe to send the next prompt.</summary>
    Idle,

    /// <summary>The most recent turn terminated in an agent/adapter error rather than a normal end.</summary>
    Errored,
}

/// <summary>
/// The single, pure rule for "is this session idle?", shared by the up-front snapshot check and the live
/// wait loop so both agree. A user message opens a turn; a <see cref="TurnEndedEvent"/> closes it; an
/// <see cref="AgentErrorEvent"/> closes it as a failure. The most recent terminal signal wins, so a
/// session that erred and was then re-prompted reads as running again.
/// </summary>
public static class SessionActivity
{
    public static SessionState Evaluate(IEnumerable<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var running = false;
        var errored = false;

        foreach (var e in events.OrderBy(e => e.Sequence))
        {
            switch (e)
            {
                case MessageChunkEvent { Role: MessageRole.User }:
                    running = true;
                    errored = false;
                    break;
                case AgentErrorEvent:
                    running = false;
                    errored = true;
                    break;
                case TurnEndedEvent:
                    running = false;
                    errored = false;
                    break;
                default:
                    // Mid-turn activity (assistant/thought chunks, tool calls, usage, …) doesn't change
                    // the idle/running boundary — only turn delimiters do.
                    break;
            }
        }

        if (running)
        {
            return SessionState.Running;
        }

        return errored ? SessionState.Errored : SessionState.Idle;
    }
}
