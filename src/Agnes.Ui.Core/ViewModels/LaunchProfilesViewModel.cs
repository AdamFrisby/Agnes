using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives the launch-profiles management surface (settings): the host's saved, reusable new-session launch
/// configs. Host-agnostic — it talks to whatever <see cref="IAgnesHost"/> the accessor returns, so it drives a
/// real SignalR host and the offline simulation identically, and every change goes over the wire. Creating a
/// profile happens in the new-session surface ("Save current as profile…"); this surface lists them, renames
/// them, and deletes them. Deleting a profile never affects a session already launched from it.
/// </summary>
public sealed class LaunchProfilesViewModel : ObservableObject
{
    private readonly Func<IAgnesHost?> _host;
    private readonly IUiDispatcher _dispatcher;

    public LaunchProfilesViewModel(Func<IAgnesHost?> host, IUiDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        DeleteCommand = new AsyncRelayCommand<LaunchProfile>(DeleteAsync);
    }

    /// <summary>The host's saved launch profiles.</summary>
    public ObservableCollection<LaunchProfile> Profiles { get; } = [];

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public bool HasProfiles => Profiles.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>Loads the profiles from the host and rebuilds the list.</summary>
    public async Task RefreshAsync()
    {
        var host = _host();
        if (host is null)
        {
            _dispatcher.Post(() => { Profiles.Clear(); OnPropertyChanged(nameof(HasProfiles)); Status = "Connect to a host to manage launch profiles."; });
            return;
        }

        try
        {
            var profiles = await host.GetLaunchProfilesAsync().ConfigureAwait(false);
            _dispatcher.Post(() => Rebuild(profiles));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't load launch profiles: " + ex.Message);
        }
    }

    private void Rebuild(IReadOnlyList<LaunchProfile> profiles)
    {
        Profiles.Clear();
        foreach (var p in profiles)
        {
            Profiles.Add(p);
        }

        OnPropertyChanged(nameof(HasProfiles));
        Status = $"{Profiles.Count} launch profile(s).";
    }

    private async Task DeleteAsync(LaunchProfile? profile)
    {
        var host = _host();
        if (host is null || profile is null)
        {
            return;
        }

        try
        {
            await host.DeleteLaunchProfileAsync(profile.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't delete the profile: " + ex.Message);
        }
    }
}
