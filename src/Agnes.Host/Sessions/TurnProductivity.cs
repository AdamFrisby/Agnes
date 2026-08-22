using Agnes.Abstractions;

namespace Agnes.Host.Sessions;

/// <summary>
/// What a turn actually produced, accumulated as its events stream past. Pure over its inputs so the
/// stall rule is one testable expression rather than flags threaded through the event pump.
/// </summary>
/// <remarks>
/// A <b>stall</b> is a turn the agent reported as a normal completion that produced nothing actionable.
/// Reasoning alone counts as nothing: an agent that thinks for two minutes and then ends its turn has done
/// no work the user can see or build on. This is deliberately the strictest reading, so a legitimately
/// terse answer ("Done.") is never mistaken for a stall.
///
/// "Actionable" is wider than message-or-tool-call, because two of these mean the agent is *waiting on the
/// user*: a question and an unresolved permission request both end a turn having emitted no message, and
/// auto-continuing one would talk straight over the thing the person is being asked. An error is excluded
/// for the opposite reason — it is a specific, probably-repeatable failure, not a silent stop.
///
/// Only <see cref="StopReason.EndTurn"/> qualifies. The other reasons are the agent telling us something
/// specific — cancelled by a person, out of tokens, refused — and re-prompting would either fight the user
/// or repeat a failure that will repeat again.
/// </remarks>
internal readonly record struct TurnProductivity(bool SawOutput, bool SawWaitOnUser)
{
    /// <summary>A turn that has produced nothing yet.</summary>
    public static TurnProductivity Empty => new(false, false);

    /// <summary>Folds one streamed event into the running tally.</summary>
    public TurnProductivity WithEvent(SessionEvent @event) => @event switch
    {
        // Output the user can see or build on.
        MessageChunkEvent { Role: MessageRole.Assistant } => this with { SawOutput = true },
        ToolCallEvent => this with { SawOutput = true },
        PlanEvent => this with { SawOutput = true },
        // A specific failure, not a silent stop — retrying blindly would just repeat it.
        AgentErrorEvent => this with { SawOutput = true },
        // The agent is blocked on a person; continuing would speak over them.
        QuestionAskedEvent => this with { SawWaitOnUser = true },
        PermissionRequestedEvent => this with { SawWaitOnUser = true },
        _ => this,
    };

    /// <summary>Whether a turn ending with <paramref name="reason"/> stalled rather than completed.</summary>
    public bool IsStall(StopReason reason)
        => reason == StopReason.EndTurn && !SawOutput && !SawWaitOnUser;
}

/// <summary>How the host reacts to a stalled turn. Disabled (or a non-positive cap) means "report it and
/// stop" — the stall is always surfaced, only the automatic retry is optional.</summary>
public sealed record AutoContinueOptions
{
    /// <summary>Whether a stalled turn is automatically continued.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How many consecutive stalls will be auto-continued before the host gives up and asks the
    /// user. Bounded because a model that reliably produces nothing would otherwise be re-prompted forever,
    /// burning tokens on every attempt. The counter resets as soon as a turn produces real output.</summary>
    public int MaxAttempts { get; init; } = 2;

    /// <summary>What is sent to the agent to resume. Not logged as a user message — the transcript gets a
    /// notice instead, so it never looks like the person typed this.</summary>
    public string Prompt { get; init; } =
        "Your previous turn ended without producing a result. Continue from where you left off.";
}

/// <summary>
/// Where a sandboxed agent reaches Agnes's own MCP endpoint. Bridge-local plain HTTP by design: the route
/// never leaves the sandbox bridge, the same containment the credential broker and MCP forward already rely
/// on, and terminating TLS there would mean trusting a self-signed host cert inside every guest.
/// Null/absent disables the whole feature — no endpoint, and no agnes server offered to any agent.
/// </summary>
public sealed record GuestMcpOptions
{
    /// <summary>The URL as the guest sees it, e.g. <c>http://10.99.5.1:5099/mcp-agnes</c>.</summary>
    public string? Url { get; init; }

    /// <summary>The address:port the host binds for it, e.g. <c>http://10.99.5.1:5099</c>.</summary>
    public string? BindUrl { get; init; }
}
