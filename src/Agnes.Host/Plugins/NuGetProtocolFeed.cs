using Agnes.Abstractions;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Agnes.Host.Plugins;

/// <summary>
/// The real <see cref="INuGetPluginFeed"/>: searches/downloads against one or more configured NuGet
/// sources (<c>Agnes:Plugins:Sources</c>, defaulting to nuget.org) using <c>NuGet.Protocol</c> —
/// permissionless publishing stays the default, matching the rest of the .NET ecosystem, rather than
/// Agnes running its own bespoke plugin catalog server.
/// </summary>
public sealed class NuGetProtocolFeed : INuGetPluginFeed
{
    /// <summary>NuGet's own <c>packageType</c> filter syntax, appended to every search — restricts
    /// results to packages that declared themselves an Agnes plugin, the same mechanism
    /// <c>dotnet tool search</c> uses to find only tool packages.</summary>
    private const string PackageTypeFilter = "packageType:AgnesPlugin";

    private readonly IReadOnlyList<SourceRepository> _sources;
    private readonly SourceCacheContext _cache = new();
    private readonly NuGet.Common.ILogger _logger;

    public NuGetProtocolFeed(IReadOnlyList<string> sourceUrls, NuGet.Common.ILogger? logger = null)
    {
        if (sourceUrls.Count == 0)
        {
            sourceUrls = ["https://api.nuget.org/v3/index.json"];
        }

        _sources = sourceUrls.Select(url => Repository.Factory.GetCoreV3(new PackageSource(url))).ToArray();
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var filteredQuery = string.IsNullOrWhiteSpace(query) ? PackageTypeFilter : $"{query} {PackageTypeFilter}";
        var results = new List<PluginSearchResult>();

        foreach (var source in _sources)
        {
            var search = await source.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
            var hits = await search.SearchAsync(filteredQuery, new SearchFilter(includePrerelease: false), skip: 0, take: 50, _logger, cancellationToken).ConfigureAwait(false);
            foreach (var hit in hits)
            {
                results.Add(new PluginSearchResult(
                    PackageId: hit.Identity.Id,
                    DisplayName: string.IsNullOrWhiteSpace(hit.Title) ? hit.Identity.Id : hit.Title,
                    Description: hit.Description,
                    Publisher: string.IsNullOrWhiteSpace(hit.Owners) ? (hit.Authors ?? "") : hit.Owners,
                    Versions: [hit.Identity.Version.ToNormalizedString()],
                    IsReviewed: false));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<string>> ListVersionsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        foreach (var source in _sources)
        {
            var findById = await source.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
            var versions = await findById.GetAllVersionsAsync(packageId, _cache, _logger, cancellationToken).ConfigureAwait(false);
            var list = versions.OrderByDescending(v => v).Select(v => v.ToNormalizedString()).ToArray();
            if (list.Length > 0)
            {
                return list;
            }
        }

        return [];
    }

    public async Task<NuGetPluginPackage> DownloadAsync(string packageId, string? version, CancellationToken cancellationToken = default)
    {
        foreach (var source in _sources)
        {
            var findById = await source.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);

            NuGetVersion? resolvedVersion;
            if (version is not null)
            {
                resolvedVersion = NuGetVersion.Parse(version);
            }
            else
            {
                var versions = await findById.GetAllVersionsAsync(packageId, _cache, _logger, cancellationToken).ConfigureAwait(false);
                resolvedVersion = versions.OrderByDescending(v => v).FirstOrDefault();
            }

            if (resolvedVersion is null)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            var ok = await findById.CopyNupkgToStreamAsync(packageId, resolvedVersion, buffer, _cache, _logger, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                continue;
            }

            return new NuGetPluginPackage(packageId, resolvedVersion.ToNormalizedString(), buffer.ToArray());
        }

        throw new InvalidOperationException($"Package '{packageId}' (version {version ?? "latest"}) was not found on any configured source.");
    }
}
