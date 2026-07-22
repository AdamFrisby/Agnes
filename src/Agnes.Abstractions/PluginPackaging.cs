using Microsoft.Extensions.DependencyInjection;

namespace Agnes.Abstractions;

/// <summary>
/// Well-known capability ids a plugin package's manifest can declare it needs — enforced, not just
/// displayed (see <see cref="IPluginInstaller"/>): a plugin that didn't declare (and get granted) a
/// capability has no constructor-injectable path to the scoped service backing it.
/// </summary>
public static class PluginCapabilityIds
{
    /// <summary>Outbound network access (e.g. a transport or voice provider calling an external API).</summary>
    public const string Network = "network";

    /// <summary>Reading/writing the local filesystem beyond the plugin's own extracted package directory.</summary>
    public const string Filesystem = "filesystem";

    /// <summary>Resolving stored credentials via <see cref="ICredentialBroker"/>.</summary>
    public const string Credentials = "credentials";

    /// <summary>Reading session transcript content (messages, tool calls, file diffs).</summary>
    public const string SessionContent = "sessionContent";
}

/// <summary>
/// The scoped service a plugin gets when it declares (and is granted) the <see cref="PluginCapabilityIds.Credentials"/>
/// capability. Deliberately minimal — a plugin resolves a credential for a host by name; it never sees
/// how or where that credential is actually stored.
/// </summary>
public interface ICredentialBroker
{
    /// <summary>Resolves a credential (e.g. a token) usable against <paramref name="host"/>, or null if
    /// no linked source can serve one.</summary>
    Task<string?> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

/// <summary>
/// The parsed, validated contents of a plugin package's <c>agnes-plugin.json</c> manifest.
/// </summary>
/// <param name="Id">Stable package/plugin id (the NuGet package id).</param>
/// <param name="DisplayName">Human-friendly name shown in the plugin management UI.</param>
/// <param name="Version">The package version this manifest describes.</param>
/// <param name="PluginPoints">Which plugin-point interface(s) this package implements, e.g. <c>"ITransportProvider"</c>.</param>
/// <param name="AgnesApiVersion">A semver range of compatible <c>Agnes.Abstractions</c> versions.</param>
/// <param name="Capabilities">Declared <see cref="PluginCapabilityIds"/> this plugin needs access to.</param>
public sealed record PluginManifest(
    string Id,
    string DisplayName,
    string Version,
    IReadOnlyList<string> PluginPoints,
    string AgnesApiVersion,
    IReadOnlyList<string> Capabilities,
    string? Publisher = null,
    string? RepositoryUrl = null,
    string? Homepage = null);

/// <summary>
/// The single entry point a plugin package's main assembly exposes. The host calls
/// <see cref="ConfigureServices"/> exactly once, after the user has consented to the manifest's
/// declared capabilities — everything else about how the plugin registers its
/// <c>IAgentAdapter</c>/<c>ITransportProvider</c>/etc. instances happens through ordinary DI
/// registration inside this method. <paramref name="services"/> also always carries a
/// <see cref="PluginSettings"/> singleton (empty if the user hasn't configured anything yet) — one
/// configuration model serves both a built-in provider and a NuGet-installed one.
/// </summary>
public interface IAgnesPluginModule
{
    void ConfigureServices(IServiceCollection services);
}

/// <summary>The flat key/value settings a user entered for this plugin via the Configure panel — the
/// same values <c>IPluginInstaller.ConfigureAsync</c> persists. Injectable from a plugin's own
/// <see cref="IAgnesPluginModule.ConfigureServices"/>.</summary>
public sealed record PluginSettings(IReadOnlyDictionary<string, string> Values)
{
    public static readonly PluginSettings Empty = new(new Dictionary<string, string>());
}

/// <summary>A plugin package found via <see cref="IPluginInstaller.SearchAsync"/>, not yet installed.</summary>
public sealed record PluginSearchResult(
    string PackageId,
    string DisplayName,
    string? Description,
    string Publisher,
    IReadOnlyList<string> Versions,
    bool IsReviewed);

/// <summary>An installed plugin's current state.</summary>
public sealed record InstalledPlugin(
    string PluginId,
    string Version,
    bool Enabled,
    IReadOnlyList<string> GrantedCapabilities,
    bool UpdateAvailable);

/// <summary>
/// Owns the full lifecycle of a NuGet-packaged plugin: search, install (with mandatory signature
/// verification and capability consent), enable/disable (unload/reload its isolation context without
/// a host restart), configure, and uninstall. See .ideas/00-plugin-architecture.md.
/// </summary>
public interface IPluginInstaller
{
    Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Downloads, verifies, and loads a plugin package. <paramref name="grantedCapabilities"/> is
    /// the set of the manifest's declared capabilities the user actually consented to — installation fails
    /// if the manifest declares a capability not present in this set.</summary>
    Task<InstalledPlugin> InstallAsync(string packageId, string? version, IReadOnlyCollection<string> grantedCapabilities, CancellationToken cancellationToken = default);

    /// <summary>Updates to the latest version. If the new manifest declares a capability the currently
    /// installed version didn't have, <paramref name="grantedCapabilities"/> must include it (fresh
    /// consent) or the update fails without applying (AC10) — the previously installed version keeps running.</summary>
    Task<InstalledPlugin> UpdateAsync(string pluginId, IReadOnlyCollection<string> grantedCapabilities, CancellationToken cancellationToken = default);

    Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default);

    Task UninstallAsync(string pluginId, CancellationToken cancellationToken = default);

    Task ConfigureAsync(string pluginId, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstalledPlugin>> ListInstalledAsync(CancellationToken cancellationToken = default);
}
