using Agnes.Host.Hosting;

namespace Agnes.Host.Social;

/// <summary>
/// Decides, <em>live</em>, whether a target GitHub user is <em>eligible</em> to be offered access by an actor.
/// Eligibility is never a stored trust bit — it is recomputed on every call from two sources:
/// <list type="bullet">
///   <item>the target is an <b>explicit friend</b> in the host owner's directory, or</item>
///   <item>the actor and target <b>share a configured GitHub org/team</b>, checked live against the GitHub API
///     via the existing membership lookup (never cached).</item>
/// </list>
/// Being eligible confers nothing by itself; it is only the precondition for an explicit, revocable
/// <see cref="Agnes.Abstractions.AccessGrant"/>. Because the org/team check is live, revoking someone's org
/// membership on GitHub flips eligibility on the very next call — there is no ambient, cached trust to stale.
/// </summary>
public sealed class FriendEligibilityService
{
    private readonly FriendStore _friends;
    private readonly IGitHubUserLookup _lookup;
    private readonly GitHubAuthOptions _options;

    public FriendEligibilityService(FriendStore friends, IGitHubUserLookup lookup, GitHubAuthOptions options)
    {
        _friends = friends;
        _lookup = lookup;
        _options = options;
    }

    /// <summary>
    /// Whether <paramref name="targetLogin"/> is eligible for <paramref name="actorLogin"/> to grant access to:
    /// true if the target is an explicit friend, or the two share a configured org/team (recomputed live).
    /// </summary>
    public async Task<bool> IsEligibleAsync(string actorLogin, string targetLogin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetLogin))
        {
            return false;
        }

        // Explicit friend of the owner — the address-book path. Cheap and offline, so check it first. This
        // path needs no actor login, so a non-GitHub-paired owner can still grant to an explicit friend.
        if (_friends.Contains(targetLogin))
        {
            return true;
        }

        // The shared-org path needs the actor's own login to check "both are members"; without one, there is
        // nothing to share, so the only route left is the explicit-friend path already checked above.
        if (string.IsNullOrWhiteSpace(actorLogin))
        {
            return false;
        }

        // Shared configured org/team — the live path. Both parties must currently be members of the SAME
        // configured org (or org/team); membership is probed by login on each call, never cached.
        foreach (var spec in _options.AllowedOrgs)
        {
            var parts = spec.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            bool shared = parts.Length == 1
                ? await BothMembersAsync(
                    l => _lookup.IsOrgMemberByLoginAsync(parts[0], l, cancellationToken), actorLogin, targetLogin).ConfigureAwait(false)
                : await BothMembersAsync(
                    l => _lookup.IsTeamMemberByLoginAsync(parts[0], parts[1], l, cancellationToken), actorLogin, targetLogin).ConfigureAwait(false);
            if (shared)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> BothMembersAsync(Func<string, Task<bool>> isMember, string actorLogin, string targetLogin)
    {
        // Short-circuit on the actor: no point probing the target for an org the actor isn't in.
        if (!await isMember(actorLogin).ConfigureAwait(false))
        {
            return false;
        }

        return await isMember(targetLogin).ConfigureAwait(false);
    }
}
