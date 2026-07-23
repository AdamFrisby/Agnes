namespace Agnes.Abstractions;

/// <summary>
/// How a <see cref="Friend"/> entry came to exist. This is provenance only — it never confers trust on its
/// own. A <see cref="SharedOrg"/> friend was surfaced because the two accounts share a configured GitHub
/// org/team; an <see cref="Explicit"/> friend was added by handle by the host owner. Either way, access is
/// only ever conferred by an explicit, revocable <see cref="AccessGrant"/>, never by being a friend.
/// </summary>
public enum FriendSource
{
    /// <summary>The host owner added this GitHub handle to their friend directory by hand.</summary>
    Explicit,

    /// <summary>Surfaced from shared GitHub org/team membership (eligibility), not an explicit add.</summary>
    SharedOrg,
}

/// <summary>
/// A friend in the host owner's directory: a GitHub-verified user identified by their canonical GitHub
/// <paramref name="GitHubLogin"/>. Membership in this directory is an address-book convenience — it makes a
/// user <em>eligible</em> to be granted access, but it is not itself an access grant. Carries no secret, so
/// it is safe to persist and to list to a client.
/// </summary>
public sealed record Friend(string GitHubLogin, string? DisplayName, DateTimeOffset AddedAt, FriendSource Source);

/// <summary>
/// What an <see cref="AccessGrant"/> permits. Ordered least-to-most privileged so a required scope can be
/// compared against a granted one (<c>granted &gt;= required</c>): a <see cref="Collaborate"/> grant also
/// satisfies a <see cref="ReadOnly"/> requirement, but not the reverse.
/// </summary>
public enum GrantScope
{
    /// <summary>May observe the resource (e.g. watch a session) but not act on it.</summary>
    ReadOnly,

    /// <summary>May act on the resource (e.g. prompt/participate in a session).</summary>
    Collaborate,
}

/// <summary>
/// An explicit, revocable authorization: GitHub user <paramref name="GranteeLogin"/> may access
/// <paramref name="Resource"/> (an opaque id — a host, or later a session) at <paramref name="Scope"/>.
/// This is the seam <c>collaboration/02</c> session-sharing consumes: it creates grants with a session
/// resource id and enforces them via <c>IFriendAuthorizer</c>. A grant is created by an authenticated owner
/// device only for an <em>eligible</em> grantee; setting <see cref="RevokedAt"/> invalidates it permanently
/// (a revoked grant can never again authorize anything). Carries no secret.
/// </summary>
public sealed record AccessGrant(
    string Id,
    string GranteeLogin,
    string Resource,
    GrantScope Scope,
    DateTimeOffset GrantedAt,
    string GrantedByDevice,
    DateTimeOffset? RevokedAt = null)
{
    /// <summary>True while the grant has not been revoked. Only active grants can authorize access.</summary>
    public bool IsActive => RevokedAt is null;
}
