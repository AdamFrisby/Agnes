using Agnes.Abstractions;
using Agnes.Host.Groups;
using Agnes.Host.Sessions;

namespace Agnes.Host.Sharing;

/// <summary>
/// A resolved caller as the sharing layer sees it: the host owner, and/or a paired device with an id, and/or a
/// GitHub-identified user. A direct share can name either identity, so an access decision considers both — the
/// device id AND the GitHub login are candidate recipient ids.
/// </summary>
public sealed record SharingCaller(string? DeviceId, string? GitHubLogin, bool IsOwner)
{
    /// <summary>The non-empty identities this caller could be matched to a share by.</summary>
    public IEnumerable<string> Identities()
    {
        if (!string.IsNullOrWhiteSpace(DeviceId))
        {
            yield return DeviceId;
        }

        if (!string.IsNullOrWhiteSpace(GitHubLogin))
        {
            yield return GitHubLogin;
        }
    }
}

/// <summary>
/// The single host-side gate every write/read path on a shared session consults — the enforcement point the
/// spec requires to live below the UI, so no client can bypass it by calling a hub method directly. Every
/// decision is recomputed from the live <see cref="SessionShareStore"/>, so a revoked share stops authorizing
/// immediately (even on an already-open connection, which re-asks on its next request).
///
/// <para>Crucially, this class has <b>no method that takes a public-link token</b> and no method that grants a
/// public viewer anything: public links are handled solely by <see cref="PublicLinkStore.Validate"/>, which only
/// ever yields a read-only subscribe. That absence is the structural guarantee behind AC3 — there is simply no
/// reachable code path by which a public link could send, approve, or manage.</para>
/// </summary>
public sealed class SessionAccessAuthorizer
{
    private readonly SessionShareStore _shares;
    private readonly GroupMembershipService? _groups;
    private readonly SessionIsolation _isolation;

    public SessionAccessAuthorizer(SessionShareStore shares, GroupMembershipService? groups = null, SessionSecurityOptions? security = null)
    {
        _shares = shares;
        _groups = groups;
        _isolation = security?.SessionIsolation ?? SessionIsolation.Shared;
    }

    /// <summary>Whether session isolation is off (the common case) — lets callers skip the async owner/group path.</summary>
    public bool IsolationDisabled => _isolation == SessionIsolation.Shared;

    /// <summary>Whether the caller may subscribe to (watch) the session: the owner, or any active share of any
    /// level. A public-link viewer is authorized separately via <see cref="PublicLinkStore"/>, never here.</summary>
    public bool CanSubscribe(string sessionId, SharingCaller caller)
        => caller.IsOwner || ShareFor(sessionId, caller) is not null;

    /// <summary>Whether the caller may send prompts (drive the agent): the owner, or a share at
    /// <see cref="SessionAccessLevel.CanEdit"/> or higher. A view-only share is rejected server-side.</summary>
    public bool CanPrompt(string sessionId, SharingCaller caller)
        => caller.IsOwner || ShareFor(sessionId, caller) is { Level: >= SessionAccessLevel.CanEdit };

    /// <summary>Whether the caller may answer this session's tool-permission prompts: the owner, or a share whose
    /// orthogonal <see cref="SessionShareRecord.AllowPermissionApprovals"/> flag is set — independent of level.</summary>
    public bool CanApprovePermissions(string sessionId, SharingCaller caller)
        => caller.IsOwner || ShareFor(sessionId, caller) is { AllowPermissionApprovals: true };

    /// <summary>Whether the caller may change other collaborators' access on the session: the owner, or a share
    /// at <see cref="SessionAccessLevel.CanManage"/>. A CanEdit collaborator cannot re-share.</summary>
    public bool CanManage(string sessionId, SharingCaller caller)
        => caller.IsOwner || ShareFor(sessionId, caller) is { Level: SessionAccessLevel.CanManage };

    private SessionShareRecord? ShareFor(string sessionId, SharingCaller caller)
        => _shares.FindActiveForAny(sessionId, caller.Identities());

    // ---- session-isolation grants (Agnes:Security:SessionIsolation) ----
    // These layer ADDITIVE access on top of the share checks above: under PerUser a caller reaches sessions they
    // own; under PerGroup, also sessions whose group they belong to. The host owner and explicit shares are
    // unaffected. Callers use the *Async variants when isolation is on (IsolationDisabled == false).

    /// <summary>Subscribe (watch) including isolation grants: base decision, or the caller owns / is in the group.</summary>
    public async Task<bool> CanSubscribeAsync(string sessionId, string? owner, string? group, SharingCaller caller, CancellationToken cancellationToken = default)
        => CanSubscribe(sessionId, caller) || await OwnsOrInGroupAsync(owner, group, caller, allowGroup: true, cancellationToken).ConfigureAwait(false);

    /// <summary>Prompt (drive the agent) including isolation grants — the owner and group members may edit.</summary>
    public async Task<bool> CanPromptAsync(string sessionId, string? owner, string? group, SharingCaller caller, CancellationToken cancellationToken = default)
        => CanPrompt(sessionId, caller) || await OwnsOrInGroupAsync(owner, group, caller, allowGroup: true, cancellationToken).ConfigureAwait(false);

    /// <summary>Answer permission prompts including isolation grants (owner + group members).</summary>
    public async Task<bool> CanApprovePermissionsAsync(string sessionId, string? owner, string? group, SharingCaller caller, CancellationToken cancellationToken = default)
        => CanApprovePermissions(sessionId, caller) || await OwnsOrInGroupAsync(owner, group, caller, allowGroup: true, cancellationToken).ConfigureAwait(false);

    /// <summary>Manage sharing including isolation grants — only the session <b>owner</b> (not mere group members).</summary>
    public async Task<bool> CanManageAsync(string sessionId, string? owner, string? group, SharingCaller caller, CancellationToken cancellationToken = default)
        => CanManage(sessionId, caller) || await OwnsOrInGroupAsync(owner, group, caller, allowGroup: false, cancellationToken).ConfigureAwait(false);

    private async Task<bool> OwnsOrInGroupAsync(string? owner, string? group, SharingCaller caller, bool allowGroup, CancellationToken cancellationToken)
    {
        if (_isolation == SessionIsolation.Shared)
        {
            return false; // isolation off — no additive grant beyond shares/host-owner.
        }

        if (owner is { Length: > 0 }
            && caller.Identities().Any(id => string.Equals(id, owner, StringComparison.OrdinalIgnoreCase)))
        {
            return true; // the caller owns this session (matched across their devices).
        }

        if (allowGroup && _isolation == SessionIsolation.PerGroup && group is { Length: > 0 } && _groups is not null)
        {
            return await _groups.IsMemberAsync(new GroupPrincipal(caller.DeviceId, caller.GitHubLogin), group, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
