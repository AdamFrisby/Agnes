namespace Agnes.Host.Groups;

/// <summary>A resolved principal whose group membership is being tested: the identities a caller could match by.</summary>
public sealed record GroupPrincipal(string? DeviceId, string? GitHubLogin);

/// <summary>
/// Plugin point: a source of group membership. A "group" is an opaque, provider-specific id — the shipped
/// <see cref="GitHubRepoGroupProvider"/> treats it as a repo key ("host/owner/repo"), so "membership" = write
/// access to that repo. Used to scope session visibility on a shared host when
/// <c>Agnes:Security:SessionIsolation = PerGroup</c>: a caller may reach a session in group G if they belong to
/// G. New membership backends (LDAP, SSO groups, a static roster, …) ship as additional
/// <see cref="IGroupProvider"/> implementations without touching core — the same
/// <c>IPluginRegistry&lt;T&gt;</c> pattern as agents/sandboxes/auth methods.
/// </summary>
public interface IGroupProvider
{
    /// <summary>Stable id for the plugin registry.</summary>
    string Id { get; }

    /// <summary>Whether this provider recognises the given group id, so unrelated providers are skipped cheaply.</summary>
    bool Handles(string groupId);

    /// <summary>Whether <paramref name="principal"/> is a member of <paramref name="groupId"/>. Called during
    /// authorization, so implementations should be cheap or cached. Must never throw — return false on any error
    /// (a membership backend being down denies extra access, it doesn't crash the request).</summary>
    Task<bool> IsMemberAsync(GroupPrincipal principal, string groupId, CancellationToken cancellationToken = default);
}
