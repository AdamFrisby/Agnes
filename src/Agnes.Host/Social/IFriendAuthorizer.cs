using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Social;

/// <summary>
/// The enforcement seam every sharing path calls before acting on a shared resource. This is the primitive
/// <c>collaboration/02</c> session-sharing consumes: it asks "may this actor do this to this resource?" and
/// gets a yes/no with no ambient trust. An authorization succeeds <em>only</em> when an active (non-revoked)
/// grant covers the actor, resource, and required scope, <em>and</em> the actor's GitHub identity is currently
/// valid. Handle-knowledge alone never authorizes anything.
/// </summary>
public interface IFriendAuthorizer
{
    /// <summary>Whether <paramref name="actorLogin"/> may access <paramref name="resource"/> at
    /// <paramref name="requiredScope"/> or higher, right now.</summary>
    Task<bool> AuthorizeAsync(string actorLogin, string resource, GrantScope requiredScope, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IFriendAuthorizer"/>: reads the <see cref="GrantStore"/> for a covering active grant and
/// re-validates the actor's GitHub identity live via <see cref="IGitHubUserLookup"/> on every call. Both
/// conditions are checked at decision time — a grant that was revoked, or an actor whose GitHub account no
/// longer exists, fails immediately.
/// </summary>
public sealed class FriendAuthorizer : IFriendAuthorizer
{
    private readonly GrantStore _grants;
    private readonly IGitHubUserLookup _lookup;

    public FriendAuthorizer(GrantStore grants, IGitHubUserLookup lookup)
    {
        _grants = grants;
        _lookup = lookup;
    }

    public async Task<bool> AuthorizeAsync(string actorLogin, string resource, GrantScope requiredScope, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actorLogin) || string.IsNullOrWhiteSpace(resource))
        {
            return false;
        }

        // A covering grant must exist: same grantee, same resource, active, and at least the required scope.
        // GrantScope is ordered least-to-most privileged, so `>= requiredScope` denies a ReadOnly grant when
        // Collaborate is required (no scope escalation), while a Collaborate grant satisfies a ReadOnly need.
        var covering = _grants.FindActiveFor(actorLogin, resource);
        if (!covering.Any(g => g.Scope >= requiredScope))
        {
            return false;
        }

        // Live identity re-validation: never authorize on the grant alone if the GitHub account behind the
        // handle is no longer valid. This is the "no ambient trust" backstop — recomputed every call.
        return await _lookup.UserExistsAsync(actorLogin, cancellationToken).ConfigureAwait(false);
    }
}
