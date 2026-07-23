using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The host-side caching layer over the optional <see cref="IQuotaReportingProvider"/> capability. Given a
/// stored profile id it looks the profile up, finds the matching registered
/// <see cref="IConnectedServiceProvider"/> by <see cref="ConnectedServiceProfile.ProviderId"/>, and — IF that
/// provider also implements <see cref="IQuotaReportingProvider"/> — returns its snapshot cached per profile
/// behind a configurable staleness window. Quota data changes slowly relative to how often a client redraws a
/// badge, and hitting a provider's usage endpoint on every render risks tripping that endpoint's own rate
/// limits, so within the window repeated requests are served from cache and make no redundant provider call.
/// </summary>
/// <remarks>
/// This is deliberately parallel to <see cref="ConnectedServiceBroker"/> (same lookup/routing shape) but a
/// separate service: credential resolution and usage reporting are independent concerns, and a provider can
/// do the first without the second. A provider that doesn't implement the capability yields a clean null
/// ("not supported") rather than an error, so quota reporting never gates whether a profile can be used.
/// </remarks>
public sealed class QuotaService
{
    private readonly ConnectedServiceProfileStore _profiles;
    private readonly IPluginRegistry<IConnectedServiceProvider> _providers;
    private readonly TimeProvider _time;
    private readonly TimeSpan _staleness;
    private readonly ILogger<QuotaService>? _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <param name="staleness">
    /// How long a cached snapshot is served before the provider is asked again. Configurable because different
    /// providers update usage at different cadences; a few minutes is a sane default.
    /// </param>
    public QuotaService(
        ConnectedServiceProfileStore profiles,
        IPluginRegistry<IConnectedServiceProvider> providers,
        TimeProvider? time = null,
        TimeSpan? staleness = null,
        ILogger<QuotaService>? logger = null)
    {
        _profiles = profiles;
        _providers = providers;
        _time = time ?? TimeProvider.System;
        _staleness = staleness ?? TimeSpan.FromMinutes(5);
        _logger = logger;
    }

    /// <summary>
    /// The current quota snapshot for the profile with id <paramref name="profileId"/>, or null when it can't
    /// be reported — an unknown profile, a provider that isn't registered, a provider that doesn't implement
    /// <see cref="IQuotaReportingProvider"/>, or a transient fetch failure. Never throws for these ordinary
    /// "no data" cases: null is the distinguishable "unavailable" state the client renders. A snapshot fetched
    /// within the staleness window is returned from cache, making no call to the provider's usage API.
    /// </summary>
    public async Task<QuotaSnapshot?> GetQuotaAsync(string profileId, CancellationToken ct = default)
    {
        var profile = _profiles.Find(profileId);
        if (profile is null)
        {
            // Unknown profile: a clean "unavailable", not an exception — the caller may be racing a deletion.
            return null;
        }

        // Only a provider that opted into the capability can report usage; its absence is "not supported".
        if (_providers.Find(profile.ProviderId) is not IQuotaReportingProvider reporter)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_cache.TryGetValue(profileId, out var cached) && now - cached.CachedAt < _staleness)
            {
                return cached.Snapshot;
            }
        }

        QuotaSnapshot? fresh;
        try
        {
            fresh = await reporter.GetQuotaAsync(profileId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider fetch failure must surface as "unavailable", never crash the caller or fail the
            // underlying session. Serve the last known snapshot if we have one (honestly stale beats nothing),
            // otherwise null.
            _logger?.LogWarning(ex, "Quota fetch failed for profile {ProfileId}; serving cached/none.", profileId);
            lock (_gate)
            {
                return _cache.TryGetValue(profileId, out var stale) ? stale.Snapshot : null;
            }
        }

        if (fresh is not null)
        {
            lock (_gate)
            {
                _cache[profileId] = new CacheEntry(fresh, _time.GetUtcNow());
            }
        }

        return fresh;
    }

    private readonly record struct CacheEntry(QuotaSnapshot Snapshot, DateTimeOffset CachedAt);
}
