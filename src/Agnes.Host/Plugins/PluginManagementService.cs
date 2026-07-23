using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Protocol;

namespace Agnes.Host.Plugins;

/// <summary>
/// Adapts <see cref="IPluginInstaller"/> to the wire contract: maps abstraction records to their
/// <c>*Dto</c> shapes and — critically — turns the two "the caller should retry with consent" cases
/// (<see cref="PluginConsentRequiredException"/>) and hard failures (<see cref="PluginInstallException"/>)
/// into a typed <see cref="PluginInstallOutcome"/> rather than letting them cross the SignalR boundary as
/// opaque exceptions. Kept separate from <c>AgnesHub</c> so this mapping is unit-testable without a hub.
///
/// Every mutating operation is routed through the event spine: a governance plugin can veto an install,
/// enable/disable, or uninstall (Before* events), and observers see the committed change (*edEvent).
/// </summary>
public sealed class PluginManagementService(IPluginInstaller installer, IEventBus? bus = null)
{
    private readonly IEventBus _bus = bus ?? new EventBus();

    public async Task<IReadOnlyList<PluginSearchResultDto>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var results = await installer.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return results.Select(r => new PluginSearchResultDto(r.PackageId, r.DisplayName, r.Description, r.Publisher, r.Versions, r.IsReviewed)).ToArray();
    }

    public async Task<PluginInstallOutcome> InstallAsync(InstallPluginRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _bus.AllowsAsync(new BeforePluginInstallEvent(request.PackageId, request.Version)).ConfigureAwait(false))
        {
            return new PluginInstallOutcome(Success: false, Plugin: null, ConsentRequired: false, MissingCapabilities: [], Error: "Installation was blocked by a plugin.");
        }

        return await RunInstallAsync(() => installer.InstallAsync(request.PackageId, request.Version, request.GrantedCapabilities, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<PluginInstallOutcome> UpdateAsync(string pluginId, IReadOnlyList<string> grantedCapabilities, CancellationToken cancellationToken = default)
    {
        if (!await _bus.AllowsAsync(new BeforePluginInstallEvent(pluginId, version: null)).ConfigureAwait(false))
        {
            return new PluginInstallOutcome(Success: false, Plugin: null, ConsentRequired: false, MissingCapabilities: [], Error: "Update was blocked by a plugin.");
        }

        return await RunInstallAsync(() => installer.UpdateAsync(pluginId, grantedCapabilities, cancellationToken)).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!await _bus.AllowsAsync(new BeforePluginEnableChangeEvent(pluginId, enabled)).ConfigureAwait(false))
        {
            return; // a plugin kept the current enabled state
        }

        await installer.SetEnabledAsync(pluginId, enabled, cancellationToken).ConfigureAwait(false);
        await _bus.DispatchAsync(new PluginEnableChangedEvent(pluginId, enabled)).ConfigureAwait(false);
    }

    public Task ConfigureAsync(string pluginId, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
        => installer.ConfigureAsync(pluginId, settings, cancellationToken);

    public async Task UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (!await _bus.AllowsAsync(new BeforePluginUninstallEvent(pluginId)).ConfigureAwait(false))
        {
            return; // a plugin kept it installed
        }

        await installer.UninstallAsync(pluginId, cancellationToken).ConfigureAwait(false);
        await _bus.DispatchAsync(new PluginUninstalledEvent(pluginId)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InstalledPluginDto>> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        var installed = await installer.ListInstalledAsync(cancellationToken).ConfigureAwait(false);
        return installed.Select(ToDto).ToArray();
    }

    private async Task<PluginInstallOutcome> RunInstallAsync(Func<Task<InstalledPlugin>> action)
    {
        try
        {
            var plugin = await action().ConfigureAwait(false);
            await _bus.DispatchAsync(new PluginInstalledEvent(plugin.PluginId, plugin.Version)).ConfigureAwait(false);
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
