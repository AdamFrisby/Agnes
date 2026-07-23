using Agnes.Abstractions;

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

    public SessionAccessAuthorizer(SessionShareStore shares)
    {
        _shares = shares;
    }

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
}
