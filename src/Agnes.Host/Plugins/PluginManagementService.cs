using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Host.Plugins;

/// <summary>
/// Adapts <see cref="IPluginInstaller"/> to the wire contract: maps abstraction records to their
/// <c>*Dto</c> shapes and — critically — turns the two "the caller should retry with consent" cases
/// (<see cref="PluginConsentRequiredException"/>) and hard failures (<see cref="PluginInstallException"/>)
/// into a typed <see cref="PluginInstallOutcome"/> rather than letting them cross the SignalR boundary as
/// opaque exceptions. Kept separate from <c>AgnesHub</c> so this mapping is unit-testable without a hub.
/// </summary>
public sealed class PluginManagementService(IPluginInstaller installer)
{
    public async Task<IReadOnlyList<PluginSearchResultDto>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var results = await installer.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return results.Select(r => new PluginSearchResultDto(r.PackageId, r.DisplayName, r.Description, r.Publisher, r.Versions, r.IsReviewed)).ToArray();
    }

    public Task<PluginInstallOutcome> InstallAsync(InstallPluginRequest request, CancellationToken cancellationToken = default)
        => RunInstallAsync(() => installer.InstallAsync(request.PackageId, request.Version, request.GrantedCapabilities, cancellationToken));

    public Task<PluginInstallOutcome> UpdateAsync(string pluginId, IReadOnlyList<string> grantedCapabilities, CancellationToken cancellationToken = default)
        => RunInstallAsync(() => installer.UpdateAsync(pluginId, grantedCapabilities, cancellationToken));

    public Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
        => installer.SetEnabledAsync(pluginId, enabled, cancellationToken);

    public Task ConfigureAsync(string pluginId, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
        => installer.ConfigureAsync(pluginId, settings, cancellationToken);

    public Task UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
        => installer.UninstallAsync(pluginId, cancellationToken);

    public async Task<IReadOnlyList<InstalledPluginDto>> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        var installed = await installer.ListInstalledAsync(cancellationToken).ConfigureAwait(false);
        return installed.Select(ToDto).ToArray();
    }

    private static async Task<PluginInstallOutcome> RunInstallAsync(Func<Task<InstalledPlugin>> action)
    {
        try
        {
            var plugin = await action().ConfigureAwait(false);
            return new PluginInstallOutcome(Success: true, ToDto(plugin), ConsentRequired: false, MissingCapabilities: [], Error: null);
        }
        catch (PluginConsentRequiredException ex)
        {
            return new PluginInstallOutcome(Success: false, Plugin: null, ConsentRequired: true, ex.MissingCapabilities, Error: null);
        }
        catch (PluginInstallException ex)
        {
            return new PluginInstallOutcome(Success: false, Plugin: null, ConsentRequired: false, MissingCapabilities: [], ex.Message);
        }
    }

    private static InstalledPluginDto ToDto(InstalledPlugin p)
        => new(p.PluginId, p.Version, p.Enabled, p.GrantedCapabilities, p.UpdateAvailable);
}
