using Agnes.Abstractions;

namespace Agnes.Host.Approvals;

/// <summary>Lifecycle of an approval-gated action (notifications/02 tier 2): waiting on a human
/// (<see cref="Open"/>), signed off (<see cref="Approved"/>), then the terminal outcome of running it
/// (<see cref="Executed"/> / <see cref="Failed"/>), or turned down without ever running (<see cref="Rejected"/>).</summary>
public enum ApprovalStatus
{
    /// <summary>Created and waiting for a human to approve or reject.</summary>
    Open,

    /// <summary>A human approved it; execution is in flight.</summary>
    Approved,

    /// <summary>A human rejected it; the action never ran.</summary>
    Rejected,

    /// <summary>Approved and the action ran to completion.</summary>
    Executed,

    /// <summary>Approved but the action threw while running.</summary>
    Failed,
}

/// <summary>
/// A durable record of an <see cref="IApprovalGatedAction"/> that was invoked from a gated surface and so is
/// waiting on a human's sign-off (notifications/02 tier 2). It persists across navigation and host restarts —
/// unlike a transient modal — because an agent might ask to commit while nobody is looking, and the request
/// must survive until someone answers it. Immutable: every state transition produces a new record. Only the
/// argument summary and optional preview are stored; the live <see cref="IApprovalGatedAction"/> that actually
/// runs on approval is held in memory by the coordinating service.
/// </summary>
/// <param name="Id">Host-assigned opaque id, used as the resolution key from the inbox.</param>
/// <param name="ActionId">The kind of action (e.g. <c>"git.commit"</c>) — surfaced in the inbox as the label.</param>
/// <param name="Surface">Which surface's invocation was gated.</param>
/// <param name="ArgsSummary">Human-readable one-line description of the specific invocation.</param>
/// <param name="Preview">Optional preview of the effect; null ⇒ no preview available.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="CreatedAt">Host-stamped creation time (from the injected clock).</param>
public sealed record ApprovalRequest(
    string Id,
    string ActionId,
    ApprovalSurface Surface,
    string ArgsSummary,
    string? Preview,
    ApprovalStatus Status,
    DateTimeOffset CreatedAt);
