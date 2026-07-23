using Agnes.Abstractions;

namespace Agnes.Host.Approvals;

/// <summary>
/// One entry of the configured gating table (notifications/02 tier 2), bound from
/// <c>Agnes:Approvals:Gated</c>. Each entry names an action that must be approved when invoked from the given
/// surface. A mutable, defaulted shape because it is a configuration-binding target (the config binder needs a
/// parameterless construction path); it is read once at startup into the immutable <see cref="ApprovalGate"/>.
/// </summary>
public sealed class GatedSurfaceConfig
{
    /// <summary>The <see cref="IApprovalGatedAction.ActionId"/> to gate (e.g. <c>"git.commit"</c>).</summary>
    public string? ActionId { get; set; }

    /// <summary>The surface whose invocations of that action require approval; defaults to
    /// <see cref="ApprovalSurface.SessionAgent"/> (the untrusted, agent-initiated surface).</summary>
    public ApprovalSurface Surface { get; set; } = ApprovalSurface.SessionAgent;
}
