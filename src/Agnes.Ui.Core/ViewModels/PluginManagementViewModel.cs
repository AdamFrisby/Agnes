using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives the plugin-management surface (see <c>.ideas/00-plugin-architecture.md</c>): browse a host's
/// configured NuGet source(s), install with explicit capability consent, and manage installed plugins
/// (enable/disable/configure/update/uninstall). Host-agnostic — it talks to whatever <see cref="IAgnesHost"/>
/// the accessor returns, so it drives a real SignalR host and the in-memory simulation identically, and
/// every action goes over the wire (a paired client manages a remote host exactly as a local one would).
/// </summary>
public sealed class PluginManagementViewModel : ObservableObject
{
    private readonly Func<IAgnesHost?> _host;
    private readonly IUiDispatcher _dispatcher;

    public PluginManagementViewModel(Func<IAgnesHost?> host, IUiDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;

        RefreshCommand = new AsyncRelayCommand(RefreshInstalledAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        InstallCommand = new AsyncRelayCommand<PluginSearchRow>(InstallAsync);
        UpdateCommand = new AsyncRelayCommand<InstalledPluginRow>(UpdateAsync);
        ToggleEnabledCommand = new AsyncRelayCommand<InstalledPluginRow>(ToggleEnabledAsync);
        UninstallCommand = new AsyncRelayCommand<InstalledPluginRow>(UninstallAsync);
        SaveConfigurationCommand = new AsyncRelayCommand<InstalledPluginRow>(SaveConfigurationAsync);
        ConfirmConsentCommand = new AsyncRelayCommand(ConfirmConsentAsync);
        CancelConsentCommand = new RelayCommand(() => PendingConsent = null);
        ShowInstalledCommand = new RelayCommand(() => ShowBrowse = false);
        ShowBrowseCommand = new RelayCommand(() => ShowBrowse = true);
        SelectCommand = new RelayCommand<object>(o => Selected = o);
    }

    /// <summary>Installed plugins on the active host.</summary>
    public ObservableCollection<InstalledPluginRow> Installed { get; } = [];

    /// <summary>Results of the last Browse/search against the host's NuGet source(s).</summary>
    public ObservableCollection<PluginSearchRow> SearchResults { get; } = [];

    private string _searchQuery = string.Empty;
    public string SearchQuery { get => _searchQuery; set => SetProperty(ref _searchQuery, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    /// <summary>False = Installed tab, true = Browse tab.</summary>
    private bool _showBrowse;
    public bool ShowBrowse { get => _showBrowse; set => SetProperty(ref _showBrowse, value); }

    /// <summary>The row shown in the detail pane (an <see cref="InstalledPluginRow"/> or <see cref="PluginSearchRow"/>).</summary>
    private object? _selected;
    public object? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    /// <summary>Set when the host refuses an install/update pending consent to new capabilities; the UI shows
    /// a consent prompt and <see cref="ConfirmConsentCommand"/> retries with them granted.</summary>
    private PendingConsent? _pendingConsent;
    public PendingConsent? PendingConsent
    {
        get => _pendingConsent;
        set { if (SetProperty(ref _pendingConsent, value)) { OnPropertyChanged(nameof(HasPendingConsent)); } }
    }

    /// <summary>Whether a consent prompt is currently pending (bindable for the UI, avoids a null converter).</summary>
    public bool HasPendingConsent => _pendingConsent is not null;

    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand SaveConfigurationCommand { get; }
    public ICommand ConfirmConsentCommand { get; }
    public ICommand CancelConsentCommand { get; }
    public ICommand ShowInstalledCommand { get; }
    public ICommand ShowBrowseCommand { get; }
    public ICommand SelectCommand { get; }

    /// <summary>Loads the installed-plugin list from the active host. Safe to call when no host is connected
    /// (it just reports that and clears the list).</summary>
    public async Task RefreshInstalledAsync()
    {
        var host = _host();
        if (host is null)
        {
            _dispatcher.Post(() => { Installed.Clear(); Status = "Connect to a host to manage its plugins."; });
            return;
        }

        try
        {
            _dispatcher.Post(() => Status = "Loading installed plugins…");
            var list = await host.ListInstalledPluginsAsync().ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                Installed.Clear();
                foreach (var p in list) { Installed.Add(new InstalledPluginRow(p)); }
                Status = Installed.Count == 0 ? "No plugins installed." : $"{Installed.Count} plugin(s) installed.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't load plugins: " + ex.Message);
        }
    }

    private async Task SearchAsync()
    {
        var host = _host();
        if (host is null) { _dispatcher.Post(() => Status = "Connect to a host to browse plugins."); return; }

        try
        {
            _dispatcher.Post(() => { IsBusy = true; Status = "Searching…"; });
            var results = await host.SearchPluginsAsync(SearchQuery ?? string.Empty).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                SearchResults.Clear();
                foreach (var r in results) { SearchResults.Add(new PluginSearchRow(r)); }
                Status = SearchResults.Count == 0 ? "No matching plugins." : $"{SearchResults.Count} result(s).";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Search failed: " + ex.Message);
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    private Task InstallAsync(PluginSearchRow? row)
        // First attempt grants nothing: the host replies ConsentRequired listing every capability the
        // package declares, which the UI surfaces before anything from the package runs.
        => row is null
            ? Task.CompletedTask
            : RunInstallAsync(h => h.InstallPluginAsync(new InstallPluginRequest(row.PackageId, row.SelectedVersion, [])),
                row.PackageId, row.SelectedVersion, isUpdate: false, pluginId: null);

    private Task UpdateAsync(InstalledPluginRow? row)
        // Baseline is what the plugin was already granted, so consent is re-requested only for capabilities
        // the new version adds that the installed one didn't have (AC10).
        => row is null
            ? Task.CompletedTask
            : RunInstallAsync(h => h.UpdatePluginAsync(row.PluginId, row.GrantedCapabilities),
                row.PluginId, version: null, isUpdate: true, pluginId: row.PluginId);

    private async Task RunInstallAsync(
        Func<IAgnesHost, Task<PluginInstallOutcome>> call, string packageId, string? version, bool isUpdate, string? pluginId)
    {
        var host = _host();
        if (host is null) { _dispatcher.Post(() => Status = "Connect to a host first."); return; }

        try
        {
            _dispatcher.Post(() => { IsBusy = true; Status = isUpdate ? "Updating…" : "Installing…"; });
            var outcome = await call(host).ConfigureAwait(false);

            if (outcome.ConsentRequired)
            {
                _dispatcher.Post(() =>
                {
                    PendingConsent = new PendingConsent(packageId, version, pluginId, isUpdate, outcome.MissingCapabilities);
                    Status = "Consent required before this plugin can run.";
                });
                return;
            }

            if (!outcome.Success)
            {
                _dispatcher.Post(() => Status = outcome.Error ?? (isUpdate ? "Update failed." : "Install failed."));
                return;
            }

            _dispatcher.Post(() => Status = isUpdate ? "Updated." : "Installed.");
            await RefreshInstalledAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Error: " + ex.Message);
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    private async Task ConfirmConsentAsync()
    {
        var pc = _pendingConsent;
        if (pc is null) { return; }
        _dispatcher.Post(() => PendingConsent = null);

        if (pc.IsUpdate && pc.PluginId is { } id)
        {
            // Grant the union of what was already granted plus the newly-consented capabilities.
            var existing = Installed.FirstOrDefault(r => r.PluginId == id)?.GrantedCapabilities ?? [];
            var granted = existing.Concat(pc.Capabilities).Distinct().ToArray();
            await RunInstallAsync(h => h.UpdatePluginAsync(id, granted), pc.PackageId, pc.Version, isUpdate: true, pluginId: id).ConfigureAwait(false);
        }
        else
        {
            await RunInstallAsync(h => h.InstallPluginAsync(new InstallPluginRequest(pc.PackageId, pc.Version, pc.Capabilities)),
                pc.PackageId, pc.Version, isUpdate: false, pluginId: null).ConfigureAwait(false);
        }
    }

    private async Task ToggleEnabledAsync(InstalledPluginRow? row)
    {
        var host = _host();
        if (row is null || host is null) { return; }

        var target = !row.Enabled;
        try
        {
            _dispatcher.Post(() => Status = target ? "Enabling…" : "Disabling…");
            await host.SetPluginEnabledAsync(row.PluginId, target).ConfigureAwait(false);
            _dispatcher.Post(() => { row.Enabled = target; Status = target ? "Enabled." : "Disabled."; });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't change plugin state: " + ex.Message);
        }
    }

    private async Task UninstallAsync(InstalledPluginRow? row)
    {
        var host = _host();
        if (row is null || host is null) { return; }

        try
        {
            _dispatcher.Post(() => Status = "Uninstalling…");
            await host.UninstallPluginAsync(row.PluginId).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                Installed.Remove(row);
                if (ReferenceEquals(Selected, row)) { Selected = null; }
                Status = "Uninstalled.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Uninstall failed: " + ex.Message);
        }
    }

    private async Task SaveConfigurationAsync(InstalledPluginRow? row)
    {
        var host = _host();
        if (row is null || host is null) { return; }

        var settings = row.Settings.ToDictionary(s => s.Key, s => s.Value);
        try
        {
            _dispatcher.Post(() => Status = "Saving configuration…");
            await host.ConfigurePluginAsync(row.PluginId, settings).ConfigureAwait(false);
            _dispatcher.Post(() => Status = "Configuration saved.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't save configuration: " + ex.Message);
        }
    }
}

/// <summary>An installed plugin as a bindable row (enabled/version/update state are observable so per-row
/// toggles and update badges update in place).</summary>
public sealed class InstalledPluginRow : ObservableObject
{
    public InstalledPluginRow(InstalledPluginDto dto)
    {
        PluginId = dto.PluginId;
        GrantedCapabilities = dto.GrantedCapabilities;
        _version = dto.Version;
        _enabled = dto.Enabled;
        _updateAvailable = dto.UpdateAvailable;
    }

    public string PluginId { get; }
    public IReadOnlyList<string> GrantedCapabilities { get; }

    private string _version;
    public string Version { get => _version; set => SetProperty(ref _version, value); }

    private bool _enabled;
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; set => SetProperty(ref _updateAvailable, value); }

    /// <summary>Editable flat settings for the Configure panel. Empty unless the UI populates it.</summary>
    public ObservableCollection<PluginSettingRow> Settings { get; } = [];

    /// <summary>Whether this plugin exposes any configurable settings (drives the Configure panel).</summary>
    public bool HasSettings => Settings.Count > 0;

    public string CapabilitiesLabel => GrantedCapabilities.Count == 0 ? "no special access" : string.Join(", ", GrantedCapabilities);
}

/// <summary>One editable key/value setting on the Configure panel.</summary>
public sealed class PluginSettingRow : ObservableObject
{
    public PluginSettingRow(string key, string value)
    {
        Key = key;
        _value = value;
    }

    public string Key { get; }

    private string _value;
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

/// <summary>A search hit from a host's NuGet source(s), as a bindable row.</summary>
public sealed class PluginSearchRow(PluginSearchResultDto dto)
{
    public string PackageId { get; } = dto.PackageId;
    public string DisplayName { get; } = dto.DisplayName;
    public string? Description { get; } = dto.Description;
    public string Publisher { get; } = dto.Publisher;
    public IReadOnlyList<string> Versions { get; } = dto.Versions;
    public bool IsReviewed { get; } = dto.IsReviewed;

    /// <summary>The newest version, installed by default.</summary>
    public string? SelectedVersion => Versions.Count > 0 ? Versions[0] : null;

    public string ReviewedLabel => IsReviewed ? "✓ reviewed" : string.Empty;
}

/// <summary>A pending capability-consent request the user must approve before an install/update proceeds.</summary>
public sealed record PendingConsent(
    string PackageId, string? Version, string? PluginId, bool IsUpdate, IReadOnlyList<string> Capabilities);
