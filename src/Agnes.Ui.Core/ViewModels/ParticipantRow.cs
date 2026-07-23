namespace Agnes.Ui.Core.ViewModels;

/// <summary>Whether a roster participant is the session's lead agent or a delegated subagent.</summary>
public enum ParticipantKind
{
    Lead,
    Subagent,
}

/// <summary>
/// One row in a session's participant roster (sessions/04, Tier 1 — visibility only): the lead agent as the
/// root participant, plus a row per subagent the adapter has reported via
/// <see cref="Agnes.Abstractions.SubagentStartedEvent"/>. Immutable value: the roster VM rebuilds rows, it
/// never mutates one in place.
/// </summary>
/// <remarks>
/// <see cref="Controllable"/> is false for every subagent today — Agnes can <em>observe</em> a delegated
/// subagent but no adapter yet exposes addressed send/stop, so the row's route/stop affordance stays
/// disabled. Deferred control path: flip this to true (and route through an <c>ISubagentCapableSession</c>
/// send/stop call) once an adapter reports a controllable-subagent capability — sessions/04 Tier 2.
/// </remarks>
public sealed record ParticipantRow(string? Id, string Name, ParticipantKind Kind, bool Controllable)
{
    /// <summary>Whether this row is the session's lead/root participant — drives the roster's icon choice.</summary>
    public bool IsLead => Kind == ParticipantKind.Lead;

    /// <summary>True when Agnes can only watch this participant (no route/stop) — drives the observe-only badge.</summary>
    public bool IsObserveOnly => !Controllable;

    /// <summary>Whether the roster's route/stop affordance is enabled for this row (the lead only, for now).</summary>
    public bool CanControl => Controllable;

    /// <summary>Tooltip for the route/stop affordance, explaining the observe-only limitation when it applies.</summary>
    public string ControlTooltip => Controllable
        ? "Send a message to this participant."
        : "Observe-only — Agnes can watch this subagent but can't message or stop it yet.";
}
