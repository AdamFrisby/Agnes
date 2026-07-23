using Agnes.Abstractions;

namespace Agnes.Host.Approvals;

/// <summary>
/// The per-surface gating table (notifications/02 tier 2): for a given <c>(ActionId, ApprovalSurface)</c> pair,
/// decides whether an <see cref="IApprovalGatedAction"/> executes immediately or must be approved by a human
/// first. Immutable once built. The default is <em>ungated</em> for every pair not explicitly listed, so an
/// action stays a plain, immediate operation until a gate is deliberately configured — existing permission,
/// commit and credential behaviour is unchanged unless someone opts a surface into gating.
/// </summary>
public sealed class ApprovalGate
{
    private readonly HashSet<(string ActionId, ApprovalSurface Surface)> _gated;

    /// <summary>Builds a gate from the set of <c>(ActionId, Surface)</c> pairs that require approval. Anything
    /// not listed is ungated (executes immediately). A null/empty set gates nothing.</summary>
    public ApprovalGate(IEnumerable<(string ActionId, ApprovalSurface Surface)>? gated = null)
        => _gated = new(gated ?? []);

    /// <summary>Whether an invocation of <paramref name="actionId"/> from <paramref name="surface"/> must be
    /// approved by a human before it runs. False (ungated ⇒ immediate) for any pair not explicitly configured.</summary>
    public bool RequiresApproval(string actionId, ApprovalSurface surface)
        => _gated.Contains((actionId, surface));
}
