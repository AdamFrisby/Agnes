using System.Collections.Concurrent;
using Agnes.Abstractions;

namespace Agnes.Host.Groups;

/// <summary>
/// Aggregates the registered <see cref="IGroupProvider"/> plugins behind one membership check, with a short-TTL
/// cache so an authorization hot path (every subscribe/prompt) doesn't hit a provider — or GitHub's API — on
/// each call. A provider that throws is treated as "not a member via that provider" (fail-closed on extra
/// access, never a crashed request). <see cref="HasProviders"/> lets callers skip group work entirely when
/// none are installed.
/// </summary>
public sealed class GroupMembershipService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<IGroupProvider> _providers;
    private readonly ConcurrentDictionary<string, (bool Member, DateTimeOffset At)> _cache = new();

    public GroupMembershipService(IPluginRegistry<IGroupProvider>? providers = null)
        => _providers = providers?.All.ToArray() ?? [];

    /// <summary>Whether any group provider is installed at all.</summary>
    public bool HasProviders => _providers.Count > 0;

    /// <summary>Whether the principal belongs to the group, via any provider that handles the group id. Cached.</summary>
    public async Task<bool> IsMemberAsync(GroupPrincipal principal, string groupId, CancellationToken cancellationToken = default)
    {
        if (_providers.Count == 0 || string.IsNullOrEmpty(groupId))
        {
            return false;
        }

        var key = $"{principal.GitHubLogin}{principal.DeviceId}{groupId}";
        if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheTtl)
        {
            return cached.Member;
        }

        var member = false;
        foreach (var provider in _providers)
        {
            if (!provider.Handles(groupId))
            {
                continue;
            }

            try
            {
                if (await provider.IsMemberAsync(principal, groupId, cancellationToken).ConfigureAwait(false))
                {
                    member = true;
                    break;
                }
            }
            catch
            {
                // A membership backend being unavailable denies the extra grant; it never faults the request.
            }
        }

        _cache[key] = (member, DateTimeOffset.UtcNow);
        return member;
    }
}
