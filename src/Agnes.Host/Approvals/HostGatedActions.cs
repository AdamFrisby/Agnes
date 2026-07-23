using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Host.Approvals;

/// <summary>
/// A git commit expressed as an approval-gated action (notifications/02 tier 2). The invocation arguments
/// (session + message) are captured immutably; the actual commit runs through the injected
/// <see cref="Commit"/> delegate — the same host commit path used for an ungated, immediate commit — so a
/// commit approved from the inbox is byte-for-byte the commit that would have happened directly. A commit that
/// reports failure (without throwing) is surfaced as a throw here, so the request ends
/// <see cref="ApprovalStatus.Failed"/> rather than a misleading <see cref="ApprovalStatus.Executed"/>.
/// </summary>
internal sealed record GitCommitAction(
    string SessionId,
    string Message,
    Func<string, string, CancellationToken, Task<GitCommitResult>> Commit) : IApprovalGatedAction
{
    /// <summary>The gating-table key for git commits.</summary>
    public const string Id = "git.commit";

    public string ActionId => Id;

    public string Summary => $"Commit in session {SessionId}: {Message}";

    public string? Preview => null;

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var result = await Commit(SessionId, Message, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }
    }
}

/// <summary>
/// Sharing a brokered git credential with a sandboxed session, expressed as an approval-gated action
/// (notifications/02 tier 2). This is a decision gate: the effect that matters is the yes/no answer the broker
/// blocks on (via <see cref="ApprovalGateService.InvokeForDecisionAsync"/>), while <see cref="ExecuteAsync"/>
/// records the grant in the session's audit log. When the surface is ungated the existing live-consent prompt
/// is used unchanged; when gated it becomes a durable inbox entry instead of a prompt that vanishes if unseen.
/// </summary>
internal sealed record CredentialShareAction(
    string SessionId,
    string Host,
    string? Repo,
    Func<Task> Grant) : IApprovalGatedAction
{
    /// <summary>The gating-table key for brokered credential sharing.</summary>
    public const string Id = "credential.share";

    public string ActionId => Id;

    public string Summary => Repo is { Length: > 0 } r
        ? $"Share git credential for {Host}/{r} with session {SessionId}"
        : $"Share git credential for {Host} with session {SessionId}";

    public string? Preview => null;

    public async Task ExecuteAsync(CancellationToken ct = default) => await Grant().ConfigureAwait(false);
}
