namespace Agnes.Abstractions;

/// <summary>
/// What a direct session share lets an identified recipient do, ordered least-to-most privileged so a required
/// level can be compared against a granted one (<c>granted &gt;= required</c>). Deliberately three states — no
/// more, no fewer:
/// <list type="bullet">
///   <item><see cref="ViewOnly"/> — watch the session's event stream; may never send a prompt.</item>
///   <item><see cref="CanEdit"/> — additionally drive the agent (send prompts).</item>
///   <item><see cref="CanManage"/> — additionally change <em>other</em> collaborators' access on this session.</item>
/// </list>
/// The ability to approve tool-permission requests is a <em>separate, orthogonal</em> capability
/// (<see cref="SessionShare.AllowPermissionApprovals"/>), never implied by any level — approving a tool call that
/// can delete files or run commands is a distinct trust decision from sending a chat message.
/// </summary>
public enum SessionAccessLevel
{
    /// <summary>May observe the session (read the event stream) but never send prompts or manage access.</summary>
    ViewOnly,

    /// <summary>May observe and drive the agent (send prompts), but not change other collaborators' access.</summary>
    CanEdit,

    /// <summary>May do everything <see cref="CanEdit"/> can and additionally manage other collaborators' access.</summary>
    CanManage,
}

/// <summary>
/// A direct share of one session with one identified recipient. The recipient is an opaque identity string —
/// either a GitHub login (a friend, per collaboration/01) or the id of a device already paired to this host
/// (the v1 fallback when a cross-host account model isn't available). Carries no secret, so it is safe to
/// persist and to list to a client. <see cref="AllowPermissionApprovals"/> is an explicit, orthogonal grant of
/// the right to answer this session's tool-permission prompts — never implied by <see cref="Level"/>, and
/// structurally impossible to set for a <see cref="SessionAccessLevel.ViewOnly"/> share (see
/// <c>ISharingBackend.ShareWithAsync</c>).
/// </summary>
public sealed record SessionShare(
    string SessionId,
    string RecipientId,
    SessionAccessLevel Level,
    bool AllowPermissionApprovals);

/// <summary>
/// Configuration for a public, unauthenticated view link. There is deliberately no access-level or
/// permission-approval field here: a public link is <em>always view-only by construction</em> — the type
/// literally cannot express a writable or approval-capable link.
/// </summary>
/// <param name="Expiry">How long the link stays valid from creation; null means "never expires".</param>
/// <param name="MaxUses">The maximum number of times the link may be opened; null means unbounded.</param>
/// <param name="RequireConsent">When true a viewer must click through an "accept and view" gate before access
/// is logged and granted.</param>
public sealed record PublicLinkOptions(TimeSpan? Expiry, int? MaxUses, bool RequireConsent);

/// <summary>
/// A created public link. The raw token is returned exactly once, embedded in <see cref="Url"/>; the server only
/// ever retains <see cref="TokenHash"/> (a SHA-256 hash), so a lost link is reissued, never recovered. A public
/// link only ever authorizes read-only viewing — there is no field, and no code path, that grants it more.
/// </summary>
public sealed record PublicSessionLink(string TokenHash, Uri Url, PublicLinkOptions Options, int UseCount);

/// <summary>
/// The two deliberately-separate sharing mechanisms for a session, with very different trust models: direct
/// sharing with a specific, identified recipient (three access levels plus an orthogonal permission-approval
/// toggle), and an always-view-only public link that anyone with the URL can open. Security invariants — a
/// public link can never write or approve, a permission-approval grant can never attach to a view-only or
/// public or inactive share — are enforced structurally here (typed errors), never merely defaulted off.
/// </summary>
public interface ISharingBackend
{
    /// <summary>
    /// Shares <paramref name="sessionId"/> with <paramref name="recipientId"/> at <paramref name="level"/>.
    /// <paramref name="allowPermissionApprovals"/> additionally grants the right to answer this session's
    /// tool-permission prompts; it is rejected (a typed <c>SharingException</c>) — not silently defaulted off —
    /// when <paramref name="level"/> is <see cref="SessionAccessLevel.ViewOnly"/> or the session is not active,
    /// because a view-only or dormant collaborator must never be able to cause tool calls to execute.
    /// Re-sharing with an existing recipient supersedes the prior share.
    /// </summary>
    Task<SessionShare> ShareWithAsync(
        string sessionId, string recipientId, SessionAccessLevel level,
        bool allowPermissionApprovals, CancellationToken ct = default);

    /// <summary>Revokes a recipient's share on a session — immediate and permanent; the recipient loses all
    /// access (including on an already-open connection) on their next request.</summary>
    Task RevokeAsync(string sessionId, string recipientId, CancellationToken ct = default);

    /// <summary>
    /// Creates (or reissues, superseding any prior link) an always-view-only public link for a session. There is
    /// no parameter by which this could grant send, approve, or manage access — read-only is a property of the
    /// code path, not a default. The returned <see cref="PublicSessionLink.Url"/> carries the raw token once;
    /// only its hash is retained.
    /// </summary>
    Task<PublicSessionLink> CreatePublicLinkAsync(
        string sessionId, PublicLinkOptions options, CancellationToken ct = default);

    /// <summary>Invalidates a session's public link immediately; any URL already handed out stops working.</summary>
    Task RevokePublicLinkAsync(string sessionId, CancellationToken ct = default);
}
