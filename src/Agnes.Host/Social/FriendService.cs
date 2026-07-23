using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Social;

/// <summary>Raised when an owner action is refused for a domain reason (unknown GitHub user, ineligible
/// grantee) — a normal outcome the caller surfaces, not an infrastructure fault.</summary>
public sealed class FriendActionException(string message) : Exception(message);

/// <summary>
/// Orchestrates the owner-facing friend/grant actions so the hub stays thin and the safety rules live in one
/// testable place. Every rule the maintainer's model fixes is enforced here:
/// <list type="bullet">
///   <item>adding a friend requires the target to be a <b>real GitHub user</b> (verified live);</item>
///   <item>granting requires the grantee to be <b>eligible</b> (explicit friend or shared org, recomputed live);</item>
///   <item>revocation is immediate and permanent (delegated to <see cref="GrantStore"/>).</item>
/// </list>
/// This composes the <see cref="FriendStore"/>, <see cref="GrantStore"/>, <see cref="FriendEligibilityService"/>
/// and <see cref="IGitHubUserLookup"/> — none of which trust a handle on its own.
/// </summary>
public sealed class FriendService
{
    private readonly FriendStore _friends;
    private readonly GrantStore _grants;
    private readonly FriendEligibilityService _eligibility;
    private readonly IGitHubUserLookup _lookup;
    private readonly TimeProvider _time;

    public FriendService(
        FriendStore friends,
        GrantStore grants,
        FriendEligibilityService eligibility,
        IGitHubUserLookup lookup,
        TimeProvider? time = null)
    {
        _friends = friends;
        _grants = grants;
        _eligibility = eligibility;
        _lookup = lookup;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The owner's friend directory.</summary>
    public IReadOnlyList<Friend> ListFriends() => _friends.List();

    /// <summary>
    /// Adds an explicit friend by GitHub handle. The handle must resolve to a real GitHub account (checked live
    /// via <see cref="IGitHubUserLookup.UserExistsAsync"/>); a non-existent user is rejected with a
    /// <see cref="FriendActionException"/> rather than silently stored.
    /// </summary>
    public async Task<Friend> AddFriendAsync(string gitHubLogin, string? displayName, CancellationToken cancellationToken = default)
    {
        var login = gitHubLogin?.Trim();
        if (string.IsNullOrWhiteSpace(login))
        {
            throw new FriendActionException("A GitHub handle is required.");
        }

        if (!await _lookup.UserExistsAsync(login, cancellationToken).ConfigureAwait(false))
        {
            throw new FriendActionException($"'{login}' is not a GitHub user.");
        }

        var trimmedDisplay = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        return _friends.Add(new Friend(login, trimmedDisplay, _time.GetUtcNow(), FriendSource.Explicit));
    }

    /// <summary>Removes a friend by handle. Never revokes any grant already issued to that handle.</summary>
    public bool RemoveFriend(string gitHubLogin) => _friends.Remove(gitHubLogin);

    /// <summary>Whether <paramref name="targetLogin"/> is currently eligible (live) for <paramref name="actorLogin"/>
    /// to grant access to — drives the client's live-eligibility hint.</summary>
    public Task<bool> IsEligibleAsync(string actorLogin, string targetLogin, CancellationToken cancellationToken = default)
        => _eligibility.IsEligibleAsync(actorLogin, targetLogin, cancellationToken);

    /// <summary>The active (non-revoked) grants.</summary>
    public IReadOnlyList<AccessGrant> ListGrants() => _grants.ListActive();

    /// <summary>
    /// Grants <paramref name="granteeLogin"/> access to <paramref name="resource"/> at <paramref name="scope"/>,
    /// on behalf of the authenticated owner <paramref name="actorLogin"/> (device <paramref name="grantedByDevice"/>).
    /// Rejects with a <see cref="FriendActionException"/> if the grantee is not currently eligible — eligibility
    /// is recomputed live, so a grant can never be minted for someone who isn't a friend and shares no org.
    /// </summary>
    public async Task<AccessGrant> GrantAsync(
        string actorLogin,
        string granteeLogin,
        string resource,
        GrantScope scope,
        string grantedByDevice,
        CancellationToken cancellationToken = default)
    {
        var grantee = granteeLogin?.Trim();
        if (string.IsNullOrWhiteSpace(grantee))
        {
            throw new FriendActionException("A grantee GitHub handle is required.");
        }

        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new FriendActionException("A resource is required.");
        }

        if (!await _eligibility.IsEligibleAsync(actorLogin, grantee, cancellationToken).ConfigureAwait(false))
        {
            throw new FriendActionException($"'{grantee}' is not eligible: add them as a friend, or share a configured org/team, first.");
        }

        return _grants.Grant(grantee, resource, scope, grantedByDevice);
    }

    /// <summary>Revokes a grant by id — immediate and permanent. Returns true if an active grant was revoked.</summary>
    public bool RevokeGrant(string grantId) => _grants.Revoke(grantId) is not null;
}
