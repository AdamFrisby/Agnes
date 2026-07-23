using Agnes.Abstractions;

namespace Agnes.Host.Sharing;

/// <summary>
/// The host's <see cref="ISharingBackend"/>: composes <see cref="SessionShareStore"/> (direct sharing) and
/// <see cref="PublicLinkStore"/> (public links) and is where the two security invariants live, enforced in the
/// domain rather than defaulted in a UI:
/// <list type="bullet">
///   <item><b>Permission-approval is structurally gated.</b> <see cref="ShareWithAsync"/> rejects (a typed
///     <see cref="SharingException"/>) any attempt to attach <c>allowPermissionApprovals</c> to a view-only
///     share or a share on an inactive session — the failure mode (a watcher able to approve destructive tool
///     calls) is a vulnerability, so it is refused, not merely off-by-default.</item>
///   <item><b>Public links are view-only by construction.</b> <see cref="CreatePublicLinkAsync"/> has no level
///     or approval parameter to abuse — there is no reachable code path here that grants a public link anything
///     beyond a read-only view.</item>
/// </list>
/// </summary>
public sealed class SessionSharingService : ISharingBackend
{
    private readonly SessionShareStore _shares;
    private readonly PublicLinkStore _links;
    private readonly ISessionActivityProbe _activity;
    private readonly Uri _publicBaseUrl;

    /// <param name="publicBaseUrl">Absolute base the public-link URL is built under (e.g. the host's external
    /// address). Defaults to <c>https://localhost/</c> for tests and headless setups.</param>
    public SessionSharingService(SessionShareStore shares, PublicLinkStore links, ISessionActivityProbe activity, Uri? publicBaseUrl = null)
    {
        _shares = shares;
        _links = links;
        _activity = activity;
        _publicBaseUrl = publicBaseUrl ?? new Uri("https://localhost/");
    }

    /// <inheritdoc />
    public Task<SessionShare> ShareWithAsync(string sessionId, string recipientId, SessionAccessLevel level, bool allowPermissionApprovals, CancellationToken ct = default)
        => ShareWithAsync(sessionId, recipientId, level, allowPermissionApprovals, sharedByDevice: string.Empty, ct);

    /// <summary>As <see cref="ISharingBackend.ShareWithAsync"/>, recording which device performed the share for
    /// audit. This is the overload the hub calls (it knows the acting device); the interface method delegates.</summary>
    public Task<SessionShare> ShareWithAsync(string sessionId, string recipientId, SessionAccessLevel level, bool allowPermissionApprovals, string sharedByDevice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new SharingException("A session id is required.");
        }

        if (string.IsNullOrWhiteSpace(recipientId))
        {
            throw new SharingException("A recipient is required (a GitHub login or a paired device id).");
        }

        // Security invariant: permission-approval rights can only ride on a share that can actually drive the
        // session, on a session that is actually running. Refuse — do not silently downgrade — so a caller
        // learns their request was rejected rather than quietly stripped.
        if (allowPermissionApprovals)
        {
            if (level == SessionAccessLevel.ViewOnly)
            {
                throw new SharingException("Permission approvals cannot be granted to a view-only share; raise the access level to CanEdit or higher.");
            }

            if (!_activity.IsActive(sessionId))
            {
                throw new SharingException("Permission approvals cannot be granted on an inactive session.");
            }
        }

        var record = _shares.Share(sessionId, recipientId, level, allowPermissionApprovals, sharedByDevice);
        return Task.FromResult(record.ToShare());
    }

    /// <inheritdoc />
    public Task RevokeAsync(string sessionId, string recipientId, CancellationToken ct = default)
    {
        _shares.Revoke(sessionId, recipientId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PublicSessionLink> CreatePublicLinkAsync(string sessionId, PublicLinkOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new SharingException("A session id is required.");
        }

        // There is deliberately no level/approval input here: a public link is read-only by construction.
        var mint = _links.Create(sessionId, options);
        var url = BuildUrl(sessionId, mint.RawToken);
        return Task.FromResult(new PublicSessionLink(mint.Record.TokenHash, url, mint.Record.Options, mint.Record.UseCount));
    }

    /// <inheritdoc />
    public Task RevokePublicLinkAsync(string sessionId, CancellationToken ct = default)
    {
        _links.Revoke(sessionId);
        return Task.CompletedTask;
    }

    /// <summary>The active direct shares on a session (audit / manage view). Secret-free.</summary>
    public IReadOnlyList<SessionShare> ListShares(string sessionId)
        => _shares.ListActiveForSession(sessionId).Select(r => r.ToShare()).ToArray();

    private Uri BuildUrl(string sessionId, string rawToken)
        => new(_publicBaseUrl, $"public/session/{Uri.EscapeDataString(sessionId)}?t={Uri.EscapeDataString(rawToken)}");
}
