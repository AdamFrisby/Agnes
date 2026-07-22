using Agnes.Abstractions;

namespace Agnes.Host.Plugins;

/// <summary>A downloaded, not-yet-verified plugin package.</summary>
public sealed record NuGetPluginPackage(string PackageId, string Version, byte[] Content);

/// <summary>
/// The subset of NuGet's search/download protocol <see cref="PluginInstaller"/> needs, behind an
/// injectable seam so tests can exercise the installer's own logic (manifest validation, capability
/// gating, ALC load/unload, state persistence) against an in-memory feed instead of a real NuGet
/// source. <see cref="NuGetProtocolFeed"/> is the real implementation.
/// </summary>
public interface INuGetPluginFeed
{
    Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListVersionsAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>Downloads a package's raw .nupkg bytes. <paramref name="version"/> null means "latest".</summary>
    Task<NuGetPluginPackage> DownloadAsync(string packageId, string? version, CancellationToken cancellationToken = default);
}
