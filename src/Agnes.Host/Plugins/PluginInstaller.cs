using Agnes.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;

namespace Agnes.Host.Plugins;

/// <summary>Thrown when install/update fails for a reason the caller should show verbatim (a bad
/// signature, an incompatible <c>agnesApiVersion</c>, a malformed manifest, …).</summary>
public sealed class PluginInstallException(string message) : Exception(message);

/// <summary>
/// Thrown when a manifest declares a capability the caller didn't include in <c>grantedCapabilities</c>
/// — install/update never proceeds on partial consent (AC6/AC10). The caller (the hub method handler,
/// ultimately the UI) is expected to show <see cref="MissingCapabilities"/> to the user and retry with
/// their explicit approval, rather than this exception ever being a normal "ask forgiveness" path.
/// </summary>
public sealed class PluginConsentRequiredException(string pluginId, IReadOnlyList<string> missingCapabilities)
    : Exception($"Plugin '{pluginId}' requires consent for: {string.Join(", ", missingCapabilities)}")
{
    public string PluginId { get; } = pluginId;
    public IReadOnlyList<string> MissingCapabilities { get; } = missingCapabilities;
}

/// <summary>A capability id a plugin can declare, and how to seed the scoped service that backs it into
/// a plugin's own <see cref="IServiceCollection"/> — the enforcement half of the security model: a
/// plugin only gets this registration if its manifest declared the capability AND the caller granted
/// it, so a plugin that skipped declaring it has no constructor-injectable path to the service at all,
/// regardless of what its own code tries to resolve (AC11).</summary>
public sealed record PluginCapabilityService(string CapabilityId, Action<IServiceCollection, IServiceProvider> Register);

/// <summary>
/// The real <see cref="IPluginInstaller"/>: owns the full NuGet-package plugin lifecycle described in
/// .ideas/00-plugin-architecture.md — search, install (download, verify, extract, validate, consent,
/// load), enable/disable/update/uninstall, and configure. See <see cref="PluginLoadContext"/> for the
/// isolation tier and <see cref="PluginPointMerger{TProvider}"/> for how a loaded plugin's instances
/// reach the same registries the host's built-ins are resolved from.
/// </summary>
public sealed class PluginInstaller : IPluginInstaller
{
    private readonly INuGetPluginFeed _feed;
    private readonly IPluginPackageVerifier _verifier;
    private readonly PluginStateStore _state;
    private readonly string _pluginsRoot;
    private readonly IServiceProvider _hostServices;
    private readonly IReadOnlyList<IPluginPointMerger> _mergers;
    private readonly IReadOnlyList<PluginCapabilityService> _capabilityServices;
    private readonly ILogger<PluginInstaller> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, PluginLoadContext> _contexts = new();
    private readonly Dictionary<string, ServiceProvider> _pluginProviders = new();

    public PluginInstaller(
        INuGetPluginFeed feed,
        IPluginPackageVerifier verifier,
        PluginStateStore state,
        string pluginsRoot,
        IServiceProvider hostServices,
        IEnumerable<IPluginPointMerger> mergers,
        IEnumerable<PluginCapabilityService> capabilityServices,
        ILogger<PluginInstaller> logger)
    {
        _feed = feed;
        _verifier = verifier;
        _state = state;
        _pluginsRoot = pluginsRoot;
        _hostServices = hostServices;
        _mergers = mergers.ToArray();
        _capabilityServices = capabilityServices.ToArray();
        _logger = logger;

        RestoreEnabledPlugins();
    }

    // Reload every previously installed, enabled plugin from its already-extracted directory — a
    // plugin survives a host restart exactly like a paired device does. One plugin failing to load
    // (a deleted dependency, a corrupted extraction) is logged and skipped rather than blocking the
    // rest of the host's plugins, or the host itself, from starting (consistent with AC2).
    private void RestoreEnabledPlugins()
    {
        foreach (var record in _state.All().Where(r => r.Enabled))
        {
            try
            {
                Load(record.PluginId, record.MainAssemblyPath, record.GrantedCapabilities, record.Settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore plugin {PluginId} on startup; leaving it unloaded.", record.PluginId);
            }
        }
    }

    public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => _feed.SearchAsync(query, cancellationToken);

    public async Task<InstalledPlugin> InstallAsync(string packageId, string? version, IReadOnlyCollection<string> grantedCapabilities, CancellationToken cancellationToken = default)
    {
        var package = await _feed.DownloadAsync(packageId, version, cancellationToken).ConfigureAwait(false);
        var manifest = await VerifyAndReadManifestAsync(package, cancellationToken).ConfigureAwait(false);
        RequireConsent(manifest, grantedCapabilities);

        var pluginDir = ExtractPackage(package, manifest);
        var mainAssemblyPath = FindMainAssembly(pluginDir, manifest);

        Load(manifest.Id, mainAssemblyPath, grantedCapabilities.ToArray(), PluginSettings.Empty.Values);

        var record = new PluginRecord(manifest.Id, packageId, manifest.Version, Enabled: true,
            grantedCapabilities.ToArray(), pluginDir, mainAssemblyPath, DateTimeOffset.UtcNow, PluginSettings.Empty.Values);
        _state.Set(record);
        _logger.LogInformation("Installed plugin {PluginId} {Version} from package {PackageId}.", manifest.Id, manifest.Version, packageId);
        return ToInstalledPlugin(record);
    }

    public async Task<InstalledPlugin> UpdateAsync(string pluginId, IReadOnlyCollection<string> grantedCapabilities, CancellationToken cancellationToken = default)
    {
        var existing = _state.Find(pluginId) ?? throw new PluginInstallException($"Unknown plugin '{pluginId}'.");

        var package = await _feed.DownloadAsync(existing.PackageId, version: null, cancellationToken).ConfigureAwait(false);
        var manifest = await VerifyAndReadManifestAsync(package, cancellationToken).ConfigureAwait(false);
        RequireConsent(manifest, grantedCapabilities); // AC10: a capability the prior version didn't have requires fresh consent too

        var pluginDir = ExtractPackage(package, manifest);
        var mainAssemblyPath = FindMainAssembly(pluginDir, manifest);

        var wasEnabled = existing.Enabled;
        if (wasEnabled)
        {
            Unload(pluginId);
        }

        var record = existing with
        {
            Version = manifest.Version,
            Enabled = wasEnabled,
            GrantedCapabilities = grantedCapabilities.ToArray(),
            ExtractedPath = pluginDir,
            MainAssemblyPath = mainAssemblyPath,
            InstalledAt = DateTimeOffset.UtcNow,
        };

        if (wasEnabled)
        {
            Load(record.PluginId, mainAssemblyPath, record.GrantedCapabilities, record.Settings);
        }

        _state.Set(record);
        _logger.LogInformation("Updated plugin {PluginId} to {Version}.", manifest.Id, manifest.Version);
        return ToInstalledPlugin(record);
    }

    public Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        var record = _state.Find(pluginId) ?? throw new PluginInstallException($"Unknown plugin '{pluginId}'.");
        if (record.Enabled == enabled)
        {
            return Task.CompletedTask;
        }

        if (enabled)
        {
            Load(pluginId, record.MainAssemblyPath, record.GrantedCapabilities, record.Settings);
        }
        else
        {
            Unload(pluginId);
        }

        _state.Set(record with { Enabled = enabled });
        return Task.CompletedTask;
    }

    public Task UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var record = _state.Find(pluginId);
        if (record is null)
        {
            return Task.CompletedTask;
        }

        if (record.Enabled)
        {
            Unload(pluginId);
        }

        try
        {
            if (Directory.Exists(record.ExtractedPath))
            {
                Directory.Delete(record.ExtractedPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete plugin files at {Path} for {PluginId}; state was still removed.", record.ExtractedPath, pluginId);
        }

        _state.Remove(pluginId);
        _logger.LogInformation("Uninstalled plugin {PluginId}.", pluginId);
        return Task.CompletedTask;
    }

    public Task ConfigureAsync(string pluginId, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
    {
        var record = _state.Find(pluginId) ?? throw new PluginInstallException($"Unknown plugin '{pluginId}'.");
        var updated = record with { Settings = settings };

        // Settings only reach the plugin's own ConfigureServices call, so a running plugin has to be
        // reloaded for a settings change to actually take effect.
        if (updated.Enabled)
        {
            Unload(pluginId);
            Load(pluginId, updated.MainAssemblyPath, updated.GrantedCapabilities, updated.Settings);
        }

        _state.Set(updated);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<InstalledPlugin>> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        var records = _state.All();
        var result = new List<InstalledPlugin>(records.Count);
        foreach (var record in records)
        {
            var updateAvailable = false;
            try
            {
                var versions = await _feed.ListVersionsAsync(record.PackageId, cancellationToken).ConfigureAwait(false);
                updateAvailable = versions.Count > 0 && versions[0] != record.Version;
            }
            catch (Exception ex)
            {
                // "update available" is informational, not load-bearing — a feed hiccup shouldn't make
                // the installed-plugins list itself fail.
                _logger.LogDebug(ex, "Could not check for updates to {PluginId}.", record.PluginId);
            }

            result.Add(ToInstalledPlugin(record) with { UpdateAvailable = updateAvailable });
        }

        return result;
    }

    private async Task<PluginManifest> VerifyAndReadManifestAsync(NuGetPluginPackage package, CancellationToken cancellationToken)
    {
        var verification = await _verifier.VerifyAsync(package.Content, cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
        {
            throw new PluginInstallException($"Package '{package.PackageId}' failed signature verification: {verification.Reason}");
        }

        using var stream = new MemoryStream(package.Content);
        using var archive = new PackageArchiveReader(stream, leaveStreamOpen: true);
        return PluginManifestReader.Read(archive);
    }

    private static void RequireConsent(PluginManifest manifest, IReadOnlyCollection<string> grantedCapabilities)
    {
        if (!PluginManifestReader.IsCompatibleWithHost(manifest))
        {
            throw new PluginInstallException(
                $"Plugin '{manifest.Id}' declares agnesApiVersion '{manifest.AgnesApiVersion}', which this host's Agnes.Abstractions version doesn't satisfy.");
        }

        var missing = manifest.Capabilities.Where(c => !grantedCapabilities.Contains(c)).ToArray();
        if (missing.Length > 0)
        {
            throw new PluginConsentRequiredException(manifest.Id, missing);
        }
    }

    private string ExtractPackage(NuGetPluginPackage package, PluginManifest manifest)
    {
        var pluginDir = Path.Combine(_pluginsRoot, manifest.Id, manifest.Version);
        if (Directory.Exists(pluginDir))
        {
            Directory.Delete(pluginDir, recursive: true);
        }

        Directory.CreateDirectory(pluginDir);

        using var stream = new MemoryStream(package.Content);
        using var archive = new PackageArchiveReader(stream, leaveStreamOpen: true);
        foreach (var file in archive.GetFiles())
        {
            // Skip NuGet's own package metadata/signature bookkeeping — only the plugin's own payload
            // (its assemblies under lib/, agnes-plugin.json, any content it ships) is extracted.
            if (file.StartsWith("_rels/", StringComparison.Ordinal) || file.StartsWith("package/", StringComparison.Ordinal) ||
                file is "[Content_Types].xml" || file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".p7s", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(pluginDir, file));
            if (!targetPath.StartsWith(pluginDir, StringComparison.Ordinal))
            {
                continue; // reject a zip-slip path escaping the extraction root
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var entryStream = archive.GetStream(file);
            using var target = File.Create(targetPath);
            entryStream.CopyTo(target);
        }

        return pluginDir;
    }

    // NuGet convention: the assembly name usually matches the package id. Falls back to the only DLL
    // under a lib/<tfm>/ folder when it doesn't — full multi-TFM best-match resolution (what a real
    // `dotnet restore` does) is out of scope; a plugin package targets net10.0 directly.
    private static string FindMainAssembly(string pluginDir, PluginManifest manifest)
    {
        var libDir = Path.Combine(pluginDir, "lib");
        if (!Directory.Exists(libDir))
        {
            throw new PluginInstallException($"Plugin '{manifest.Id}' package has no lib/ folder — nothing to load.");
        }

        var candidates = Directory.GetFiles(libDir, "*.dll", SearchOption.AllDirectories);
        var byConvention = candidates.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (byConvention is not null)
        {
            return byConvention;
        }

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        throw new PluginInstallException(
            $"Plugin '{manifest.Id}' package's lib/ folder has {candidates.Length} assemblies and none is named '{manifest.Id}.dll' — can't determine which one is the plugin's entry assembly.");
    }

    private void Load(string pluginId, string mainAssemblyPath, IReadOnlyCollection<string> grantedCapabilities, IReadOnlyDictionary<string, string> settings)
    {
        lock (_gate)
        {
            var context = new PluginLoadContext(pluginId, mainAssemblyPath);
            System.Reflection.Assembly assembly;
            try
            {
                assembly = context.LoadMainAssembly();
            }
            catch
            {
                context.Unload();
                throw;
            }

            var moduleType = assembly.GetTypes().FirstOrDefault(t =>
                typeof(IAgnesPluginModule).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass);
            if (moduleType is null)
            {
                context.Unload();
                throw new PluginInstallException($"Plugin '{pluginId}' assembly does not contain a public class implementing IAgnesPluginModule.");
            }

            var module = (IAgnesPluginModule)Activator.CreateInstance(moduleType)!;
            var services = new ServiceCollection();
            services.AddSingleton(new PluginSettings(settings));
            foreach (var capabilityService in _capabilityServices.Where(c => grantedCapabilities.Contains(c.CapabilityId)))
            {
                capabilityService.Register(services, _hostServices);
            }

            module.ConfigureServices(services);
            var pluginServices = services.BuildServiceProvider();

            foreach (var merger in _mergers)
            {
                merger.MergeFrom(pluginServices, pluginId);
            }

            _contexts[pluginId] = context;
            _pluginProviders[pluginId] = pluginServices;
        }
    }

    private void Unload(string pluginId)
    {
        lock (_gate)
        {
            foreach (var merger in _mergers)
            {
                merger.RemoveFrom(pluginId);
            }

            if (_pluginProviders.Remove(pluginId, out var provider))
            {
                provider.Dispose();
            }

            if (_contexts.Remove(pluginId, out var context))
            {
                context.Unload();
            }
        }
    }

    private InstalledPlugin ToInstalledPlugin(PluginRecord record)
        => new(record.PluginId, record.Version, record.Enabled, record.GrantedCapabilities, UpdateAvailable: false);
}
