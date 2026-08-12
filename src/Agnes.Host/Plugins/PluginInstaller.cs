using Agnes.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using System.Security.Cryptography;
using System.Text;

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
    private readonly PluginTrustPolicy _trustPolicy;

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
        ILogger<PluginInstaller> logger,
        PluginTrustPolicy? trustPolicy = null)
    {
        _feed = feed;
        _verifier = verifier;
        _state = state;
        _pluginsRoot = pluginsRoot;
        _hostServices = hostServices;
        _mergers = mergers.ToArray();
        _capabilityServices = capabilityServices.ToArray();
        _logger = logger;
        _trustPolicy = trustPolicy ?? PluginTrustPolicy.Development;
    }

    /// <summary>
    /// Reloads enabled plugins before the host begins accepting clients. Production first rebuilds every
    /// plugin directory from its exact approved archive, so stale state or tampered files fail startup
    /// instead of becoming executable code.
    /// </summary>
    public async Task RestoreEnabledPluginsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var record in _state.All().Where(r => r.Enabled))
        {
            try
            {
                var restored = await RehydrateRecordAsync(record, cancellationToken).ConfigureAwait(false);
                Load(restored.PluginId, restored.MainAssemblyPath, restored.GrantedCapabilities, restored.Settings);
                if (restored != record)
                {
                    _state.Set(restored);
                }
            }
            catch (Exception ex)
            {
                if (_trustPolicy.RequiresExactApproval)
                {
                    throw new InvalidOperationException(
                        $"Production plugin '{record.PluginId}' could not be restored from its approved archive.", ex);
                }

                _logger.LogError(ex, "Failed to restore plugin {PluginId} on startup; leaving it unloaded.", record.PluginId);
            }
        }
    }

    public async Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var results = await _feed.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        if (!_trustPolicy.RequiresExactApproval)
        {
            return results;
        }

        return results
            .Where(result => _trustPolicy.IsApprovedPackage(result.PackageId))
            .Select(result => result with { Versions = _trustPolicy.ApprovedVersions(result.PackageId) })
            .ToArray();
    }

    public async Task<InstalledPlugin> InstallAsync(string packageId, string? version, IReadOnlyCollection<string> grantedCapabilities, CancellationToken cancellationToken = default)
    {
        var approval = _trustPolicy.ResolveInstall(packageId, version);
        var package = await _feed.DownloadAsync(packageId, version, approval?.Source, cancellationToken).ConfigureAwait(false);
        var sha512 = _trustPolicy.VerifyDownloaded(package, approval);
        var manifest = await VerifyAndReadManifestAsync(package, cancellationToken).ConfigureAwait(false);
        RequireConsent(manifest, grantedCapabilities);

        var pluginDir = ExtractPackage(package, manifest);
        var mainAssemblyPath = FindMainAssembly(pluginDir, manifest);
        PersistPackageArchive(package, sha512);

        // The effective granted set is exactly what the manifest declared — RequireConsent already
        // proved that's a subset of what the caller passed in. A plugin never gets a capability it
        // didn't itself ask for, even if a caller happened to grant something broader (AC11).
        var record = new PluginRecord(manifest.Id, packageId, manifest.Version, Enabled: true,
            manifest.Capabilities, pluginDir, mainAssemblyPath, DateTimeOffset.UtcNow, PluginSettings.Empty.Values,
            package.Source, sha512);
        Load(manifest.Id, mainAssemblyPath, manifest.Capabilities, PluginSettings.Empty.Values);
        try
        {
            _state.Set(record);
        }
        catch
        {
            Unload(manifest.Id);
            throw;
        }
        _logger.LogInformation("Installed plugin {PluginId} {Version} from package {PackageId}.", manifest.Id, manifest.Version, packageId);
        return ToInstalledPlugin(record);
    }

    public async Task<InstalledPlugin> UpdateAsync(string pluginId, IReadOnlyCollection<string> grantedCapabilities, CancellationToken cancellationToken = default)
    {
        var existing = _state.Find(pluginId) ?? throw new PluginInstallException($"Unknown plugin '{pluginId}'.");

        var approval = _trustPolicy.ResolveUpdate(existing);
        if (_trustPolicy.RequiresExactApproval && approval is null)
        {
            return ToInstalledPlugin(existing);
        }

        var package = await _feed.DownloadAsync(
            existing.PackageId,
            approval?.Version.ToNormalizedString(),
            approval?.Source,
            cancellationToken).ConfigureAwait(false);
        var sha512 = _trustPolicy.VerifyDownloaded(package, approval);
        var manifest = await VerifyAndReadManifestAsync(package, cancellationToken).ConfigureAwait(false);
        RequireConsent(manifest, grantedCapabilities); // AC10: a capability the prior version didn't have requires fresh consent too

        if (string.Equals(manifest.Version, existing.Version, StringComparison.Ordinal))
        {
            return ToInstalledPlugin(existing);
        }

        var pluginDir = ExtractPackage(package, manifest);
        var mainAssemblyPath = FindMainAssembly(pluginDir, manifest);
        PersistPackageArchive(package, sha512);

        var wasEnabled = existing.Enabled;
        var record = existing with
        {
            Version = manifest.Version,
            Enabled = wasEnabled,
            GrantedCapabilities = manifest.Capabilities, // effective = what the manifest actually declares, see InstallAsync
            ExtractedPath = pluginDir,
            MainAssemblyPath = mainAssemblyPath,
            InstalledAt = DateTimeOffset.UtcNow,
            Source = package.Source,
            Sha512 = sha512,
        };

        if (wasEnabled)
        {
            Unload(pluginId);
            try
            {
                Load(record.PluginId, mainAssemblyPath, record.GrantedCapabilities, record.Settings);
            }
            catch
            {
                Load(existing.PluginId, existing.MainAssemblyPath, existing.GrantedCapabilities, existing.Settings);
                throw;
            }
        }

        try
        {
            _state.Set(record);
        }
        catch
        {
            if (wasEnabled)
            {
                Unload(pluginId);
                Load(existing.PluginId, existing.MainAssemblyPath, existing.GrantedCapabilities, existing.Settings);
            }

            throw;
        }
        _logger.LogInformation("Updated plugin {PluginId} to {Version}.", manifest.Id, manifest.Version);
        return ToInstalledPlugin(record);
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        var record = _state.Find(pluginId) ?? throw new PluginInstallException($"Unknown plugin '{pluginId}'.");
        if (record.Enabled == enabled)
        {
            return;
        }

        if (enabled)
        {
            var verified = await RehydrateRecordAsync(record, cancellationToken).ConfigureAwait(false);
            Load(pluginId, verified.MainAssemblyPath, verified.GrantedCapabilities, verified.Settings);
            record = verified;
        }
        else
        {
            Unload(pluginId);
        }

        _state.Set(record with { Enabled = enabled });
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

    public async Task ConfigureAsync(string pluginId, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
    {
        var record = _state.Find(pluginId) ?? throw new PluginInstallException($"Unknown plugin '{pluginId}'.");
        var updated = record with { Settings = settings };

        // Settings only reach the plugin's own ConfigureServices call, so a running plugin has to be
        // reloaded for a settings change to actually take effect.
        if (updated.Enabled)
        {
            updated = await RehydrateRecordAsync(updated, cancellationToken).ConfigureAwait(false);
            Unload(pluginId);
            Load(pluginId, updated.MainAssemblyPath, updated.GrantedCapabilities, updated.Settings);
        }

        _state.Set(updated);
    }

    public async Task<IReadOnlyList<InstalledPlugin>> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        var records = _state.All();
        var result = new List<InstalledPlugin>(records.Count);
        foreach (var record in records)
        {
            var updateAvailable = false;
            if (_trustPolicy.RequiresExactApproval)
            {
                updateAvailable = _trustPolicy.ResolveUpdate(record) is not null;
            }
            else try
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
        var manifest = PluginManifestReader.Read(archive);
        if (!string.Equals(manifest.Id, package.PackageId, StringComparison.OrdinalIgnoreCase) ||
            !NuGet.Versioning.NuGetVersion.TryParse(manifest.Version, out var manifestVersion) ||
            !NuGet.Versioning.NuGetVersion.TryParse(package.Version, out var packageVersion) ||
            manifestVersion != packageVersion)
        {
            throw new PluginInstallException(
                $"Package '{package.PackageId}' metadata does not match its Agnes plugin manifest.");
        }

        return manifest;
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
            var relativePath = Path.GetRelativePath(pluginDir, targetPath);
            if (relativePath == ".." ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                throw new PluginInstallException(
                    $"Package '{package.PackageId}' contains a path outside its extraction root.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var entryStream = archive.GetStream(file);
            using var target = File.Create(targetPath);
            entryStream.CopyTo(target);
        }

        return pluginDir;
    }

    private void PersistPackageArchive(NuGetPluginPackage package, string sha512)
    {
        var archivePath = PackageArchivePath(sha512);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        if (File.Exists(archivePath))
        {
            return;
        }

        var temporaryPath = archivePath + ".tmp-" + Guid.NewGuid().ToString("n");
        try
        {
            File.WriteAllBytes(temporaryPath, package.Content);
            File.Move(temporaryPath, archivePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<PluginRecord> RehydrateRecordAsync(PluginRecord record, CancellationToken cancellationToken)
    {
        if (!_trustPolicy.RequiresExactApproval)
        {
            return record;
        }

        _trustPolicy.ValidateRecordApproval(record);
        var archivePath = PackageArchivePath(record.Sha512!);
        if (!File.Exists(archivePath))
        {
            throw new PluginInstallException(
                $"Approved package archive for plugin '{record.PluginId}' is missing. Reinstall the plugin from its approved package.");
        }

        var content = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var package = new NuGetPluginPackage(record.Source!, record.PackageId, record.Version, content);
        _trustPolicy.VerifyDownloaded(package, _trustPolicy.ResolveInstall(record.PackageId, record.Version));
        var manifest = await VerifyAndReadManifestAsync(package, cancellationToken).ConfigureAwait(false);
        var pluginDir = ExtractPackage(package, manifest);
        return record with
        {
            ExtractedPath = pluginDir,
            MainAssemblyPath = FindMainAssembly(pluginDir, manifest),
        };
    }

    private string PackageArchivePath(string sha512)
    {
        var digest = Convert.FromBase64String(sha512);
        var archiveFileName = Convert.ToHexString(SHA256.HashData(digest)) + ".nupkg";
        return Path.Combine(_pluginsRoot, ".packages", archiveFileName);
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

            // The event bus is always available to a plugin (a coordination primitive, not a gated
            // resource), so a plugin can dispatch and handle its OWN event types on the same bus — not only
            // bind to core-defined events. The event contracts live in Agnes.Abstractions, which the plugin
            // already references, so a plugin defines `class MyEvent : IAgnesEvent` in its own assembly.
            if (_hostServices.GetService<Agnes.Abstractions.Events.IEventBus>() is { } hostBus)
            {
                services.AddSingleton(hostBus);
            }

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
