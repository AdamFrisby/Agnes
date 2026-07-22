using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>
/// Drives <see cref="PluginManagementViewModel"/> against the in-memory <see cref="SimulatedHost"/> (which
/// enforces the same capability-consent semantics as the real installer). Everything runs inline via
/// <see cref="ImmediateDispatcher"/> and the sim's synchronous plugin methods, so command execution
/// completes before the call returns.
/// </summary>
public class PluginManagementViewModelTests
{
    private static PluginManagementViewModel NewVm(out SimulatedHost host)
    {
        host = new SimulatedHost();
        var h = host;
        return new PluginManagementViewModel(() => h, ImmediateDispatcher.Instance);
    }

    [Fact]
    public async Task Search_finds_catalog_plugins_by_query()
    {
        var vm = NewVm(out _);

        vm.SearchQuery = "slack";
        vm.SearchCommand.Execute(null);

        var hit = Assert.Single(vm.SearchResults);
        Assert.Equal("Agnes.Plugins.SlackNotifications", hit.PackageId);
        Assert.True(hit.IsReviewed);

        await Task.CompletedTask;
    }

    [Fact]
    public void Installing_a_plugin_that_needs_a_capability_requires_consent_then_succeeds()
    {
        var vm = NewVm(out _);
        vm.SearchQuery = string.Empty;
        vm.SearchCommand.Execute(null);
        var slack = vm.SearchResults.Single(r => r.PackageId == "Agnes.Plugins.SlackNotifications");

        // First install attempt is refused pending consent — nothing is installed yet.
        vm.InstallCommand.Execute(slack);
        Assert.True(vm.HasPendingConsent);
        Assert.NotNull(vm.PendingConsent);
        Assert.Contains("network", vm.PendingConsent!.Capabilities);
        Assert.Empty(vm.Installed);

        // Approving grants the requested capability and completes the install.
        vm.ConfirmConsentCommand.Execute(null);
        Assert.Null(vm.PendingConsent);
        Assert.False(vm.HasPendingConsent);
        var installed = Assert.Single(vm.Installed);
        Assert.Equal("Agnes.Plugins.SlackNotifications", installed.PluginId);
        Assert.True(installed.Enabled);
        Assert.Contains("network", installed.GrantedCapabilities);
    }

    [Fact]
    public void Installing_a_plugin_with_no_capabilities_needs_no_consent()
    {
        var vm = NewVm(out _);
        vm.SearchCommand.Execute(null);
        var local = vm.SearchResults.Single(r => r.PackageId == "Agnes.Plugins.LocalPrompts");

        vm.InstallCommand.Execute(local);

        Assert.Null(vm.PendingConsent);
        Assert.Contains(vm.Installed, p => p.PluginId == "Agnes.Plugins.LocalPrompts");
    }

    [Fact]
    public void Cancelling_consent_does_not_install()
    {
        var vm = NewVm(out _);
        vm.SearchCommand.Execute(null);
        var slack = vm.SearchResults.Single(r => r.PackageId == "Agnes.Plugins.SlackNotifications");

        vm.InstallCommand.Execute(slack);
        Assert.True(vm.HasPendingConsent);

        vm.CancelConsentCommand.Execute(null);
        Assert.False(vm.HasPendingConsent);
        Assert.Empty(vm.Installed);
    }

    [Fact]
    public void Disable_then_uninstall_updates_the_installed_list()
    {
        var vm = NewVm(out _);
        vm.SearchCommand.Execute(null);
        var local = vm.SearchResults.Single(r => r.PackageId == "Agnes.Plugins.LocalPrompts");
        vm.InstallCommand.Execute(local);
        var row = vm.Installed.Single();
        Assert.True(row.Enabled);

        vm.ToggleEnabledCommand.Execute(row);
        Assert.False(row.Enabled);

        vm.UninstallCommand.Execute(row);
        Assert.Empty(vm.Installed);
    }

    [Fact]
    public async Task Refresh_with_no_connected_host_reports_it_and_clears()
    {
        var vm = new PluginManagementViewModel(() => null, ImmediateDispatcher.Instance);

        await vm.RefreshInstalledAsync();

        Assert.Empty(vm.Installed);
        Assert.Contains("Connect to a host", vm.Status);
    }
}
