using System.Collections.Concurrent;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Approvals;

/// <summary>
/// Coordinates the gating table, the durable request store, and the live in-memory actions (notifications/02
/// tier 2). This is the single branch point the design calls for: invoking an <see cref="IApprovalGatedAction"/>
/// either runs it immediately (ungated surface) or parks it as a durable <see cref="ApprovalRequest"/> that
/// shows up in the same inbox as tier 1, to be resolved later. Resolution is uniform regardless of the caller:
/// approving runs the parked action (status → <see cref="ApprovalStatus.Executed"/>, or
/// <see cref="ApprovalStatus.Failed"/> if it throws); rejecting turns it down without running it. The durable
/// record survives a restart; the parked live action does not, so a request left over from a previous process
/// resolves to <see cref="ApprovalStatus.Failed"/> rather than silently doing nothing.
/// </summary>
public sealed class ApprovalGateService
{
    private readonly ApprovalGate _gate;
    private readonly ApprovalRequestStore _store;
    private readonly ILogger<ApprovalGateService>? _logger;

    // The live actions parked against their open request ids. Kept out of the durable store because a delegate
    // can't be serialized — a request that outlives its process loses its action and can only fail on approve.
    private readonly ConcurrentDictionary<string, IApprovalGatedAction> _pending = new(StringComparer.Ordinal);

    // Callers that must block on the human's decision (e.g. the credential broker, which needs a bool answer)
    // park a waiter here; Approve/Reject completes it. Callers that fire-and-forget (a commit) never register one.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiters = new(StringComparer.Ordinal);

    public ApprovalGateService(ApprovalGate gate, ApprovalRequestStore store, ILogger<ApprovalGateService>? logger = null)
    {
        _gate = gate;
        _store = store;
        _logger = logger;
    }

    /// <summary>Whether this action from this surface needs approval (false ⇒ callers run it immediately).</summary>
    public bool RequiresApproval(string actionId, ApprovalSurface surface)
        => _gate.RequiresApproval(actionId, surface);

    /// <summary>
    /// Invokes <paramref name="action"/> as coming from <paramref name="surface"/>. On an ungated surface it
    /// runs immediately and returns null (nothing to approve). On a gated surface it does NOT run — it creates a
    /// durable <see cref="ApprovalStatus.Open"/> request, parks the live action, and returns the request for the
    /// inbox. The returned request is the seam callers surface to the user ("waiting for approval").
    /// </summary>
    public async Task<ApprovalRequest?> InvokeAsync(IApprovalGatedAction action, ApprovalSurface surface, CancellationToken ct = default)
    {
        if (!_gate.RequiresApproval(action.ActionId, surface))
        {
            await action.ExecuteAsync(ct).ConfigureAwait(false);
            return null;
        }

        var request = _store.Create(action.ActionId, surface, action.Summary, action.Preview);
        _pending[request.Id] = action;
        return request;
    }

    /// <summary>
    /// Invokes an action whose caller must block on the human's yes/no answer (a decision gate such as sharing a
    /// credential). Ungated ⇒ runs immediately and returns true. Gated ⇒ creates the same durable Open request as
    /// <see cref="InvokeAsync"/>, then awaits its resolution: <see cref="ApprovalStatus.Executed"/> ⇒ true,
    /// <see cref="ApprovalStatus.Rejected"/>/<see cref="ApprovalStatus.Failed"/> ⇒ false. The request stays in the
    /// inbox (durable) the whole time, so it never simply vanishes if the user navigates away.
    /// </summary>
    public async Task<bool> InvokeForDecisionAsync(IApprovalGatedAction action, ApprovalSurface surface, CancellationToken ct = default)
    {
        if (!_gate.RequiresApproval(action.ActionId, surface))
        {
            await action.ExecuteAsync(ct).ConfigureAwait(false);
            return true;
        }

        var request = _store.Create(action.ActionId, surface, action.Summary, action.Preview);
        _pending[request.Id] = action;
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[request.Id] = waiter;
        return await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The request by id, whatever its status (used by tests and the inbox detail).</summary>
    public ApprovalRequest? Get(string id) => _store.Get(id);

    /// <summary>Every still-Open request, oldest first — unioned into the cross-session approvals inbox.</summary>
    public IReadOnlyList<ApprovalRequest> ListOpen() => _store.ListOpen();

    /// <summary>
    /// Approves an <see cref="ApprovalStatus.Open"/> request and runs its parked action: on success the request
    /// ends <see cref="ApprovalStatus.Executed"/>, on a throw <see cref="ApprovalStatus.Failed"/> (the exception
    /// is contained so a bad action can't fault the inbox). Returns the final record, or null if the id is
    /// unknown or no longer Open (a double-approve is a no-op). A request whose parked action is gone (e.g. it
    /// outlived a restart) resolves to Failed.
    /// </summary>
    public async Task<ApprovalRequest?> ApproveAsync(string id, CancellationToken ct = default)
    {
        var approved = _store.TryTransition(id, ApprovalStatus.Open, ApprovalStatus.Approved);
        if (approved is null)
        {
            return null; // unknown or already resolved.
        }

        if (!_pending.TryRemove(id, out var action))
        {
            _logger?.LogWarning("Approval request {RequestId} ({ActionId}) has no live action (restart?); marking failed.", id, approved.ActionId);
            return Finish(id, success: false);
        }

        try
        {
            await action.ExecuteAsync(ct).ConfigureAwait(false);
            return Finish(id, success: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Approved action {ActionId} (request {RequestId}) threw; marking failed.", approved.ActionId, id);
            return Finish(id, success: false);
        }
    }

    /// <summary>Rejects an Open request: it ends <see cref="ApprovalStatus.Rejected"/> and its action never runs.
    /// Returns the final record, or null if unknown or no longer Open.</summary>
    public ApprovalRequest? Reject(string id)
    {
        var rejected = _store.TryTransition(id, ApprovalStatus.Open, ApprovalStatus.Rejected);
        if (rejected is null)
        {
            return null;
        }

        _pending.TryRemove(id, out _);
        SignalWaiter(id, allowed: false);
        return rejected;
    }

    // Moves an Approved request to its terminal state and unblocks any decision-waiter with the matching answer.
    private ApprovalRequest? Finish(string id, bool success)
    {
        var final = _store.TryTransition(id, ApprovalStatus.Approved, success ? ApprovalStatus.Executed : ApprovalStatus.Failed);
        SignalWaiter(id, allowed: success);
        return final;
    }

    private void SignalWaiter(string id, bool allowed)
    {
        if (_waiters.TryRemove(id, out var waiter))
        {
            waiter.TrySetResult(allowed);
        }
    }
}
