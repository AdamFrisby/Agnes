using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives the desktop "Friends" area (collaboration/01) against whatever <see cref="IAgnesHost"/> the accessor
/// returns, so it works identically over a real SignalR host and the offline simulation. Owner-only host-side —
/// a non-owner client's calls are refused, surfaced here as a status message. Three jobs: manage the friend
/// directory (add by GitHub handle, remove), show a <em>live</em> eligibility hint before granting (an explicit
/// friend, or a shared configured org/team — recomputed on the host, never cached), and manage the explicit,
/// revocable access grants that sit on top. Adding a friend or granting never shares anything by itself; a grant
/// is the only thing that confers access, and it is always revocable.
/// </summary>
public sealed class FriendsViewModel : ObservableObject
{
    private readonly Func<IAgnesHost?> _host;
    private readonly IUiDispatcher _dispatcher;

    public FriendsViewModel(Func<IAgnesHost?> host, IUiDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AddFriendCommand = new AsyncRelayCommand(AddFriendAsync);
        RemoveFriendCommand = new AsyncRelayCommand<Friend>(RemoveFriendAsync);
        CheckEligibilityCommand = new AsyncRelayCommand(CheckEligibilityAsync);
        GrantCommand = new AsyncRelayCommand(GrantAsync);
        RevokeGrantCommand = new AsyncRelayCommand<AccessGrant>(RevokeGrantAsync);
    }

    /// <summary>The host owner's friend directory.</summary>
    public ObservableCollection<Friend> Friends { get; } = [];

    /// <summary>The active (non-revoked) access grants.</summary>
    public ObservableCollection<AccessGrant> Grants { get; } = [];

    /// <summary>The scopes a grant can carry, for a picker.</summary>
    public IReadOnlyList<GrantScope> Scopes { get; } = Enum.GetValues<GrantScope>();

    private string _newFriendHandle = string.Empty;
    public string NewFriendHandle
    {
        get => _newFriendHandle;
        set => SetProperty(ref _newFriendHandle, value);
    }

    private string _newFriendDisplayName = string.Empty;
    public string NewFriendDisplayName
    {
        get => _newFriendDisplayName;
        set => SetProperty(ref _newFriendDisplayName, value);
    }

    private string _eligibilityHint = string.Empty;
    public string EligibilityHint
    {
        get => _eligibilityHint;
        set => SetProperty(ref _eligibilityHint, value);
    }

    private Friend? _selectedFriend;
    public Friend? SelectedFriend
    {
        get => _selectedFriend;
        set => SetProperty(ref _selectedFriend, value);
    }

    private string _grantResource = string.Empty;
    public string GrantResource
    {
        get => _grantResource;
        set => SetProperty(ref _grantResource, value);
    }

    private GrantScope _newGrantScope = GrantScope.ReadOnly;
    public GrantScope NewGrantScope
    {
        get => _newGrantScope;
        set => SetProperty(ref _newGrantScope, value);
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool HasFriends => Friends.Count > 0;
    public bool HasGrants => Grants.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand AddFriendCommand { get; }
    public ICommand RemoveFriendCommand { get; }
    public ICommand CheckEligibilityCommand { get; }
    public ICommand GrantCommand { get; }
    public ICommand RevokeGrantCommand { get; }

    /// <summary>Loads friends and grants from the host and rebuilds the lists.</summary>
    public async Task RefreshAsync()
    {
        var host = _host();
        if (host is null)
        {
            _dispatcher.Post(() =>
            {
                Friends.Clear();
                Grants.Clear();
                OnPropertyChanged(nameof(HasFriends));
                OnPropertyChanged(nameof(HasGrants));
                Status = "Connect to a host to manage friends.";
            });
            return;
        }

        try
        {
            var friends = await host.ListFriendsAsync().ConfigureAwait(false);
            var grants = await host.ListGrantsAsync().ConfigureAwait(false);
            _dispatcher.Post(() => Rebuild(friends, grants));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't load friends: " + ex.Message);
        }
    }

    private void Rebuild(IReadOnlyList<Friend> friends, IReadOnlyList<AccessGrant> grants)
    {
        Friends.Clear();
        foreach (var f in friends)
        {
            Friends.Add(f);
        }

        Grants.Clear();
        foreach (var g in grants)
        {
            Grants.Add(g);
        }

        OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(HasGrants));
        Status = $"{Friends.Count} friend(s), {Grants.Count} active grant(s).";
    }

    private async Task AddFriendAsync()
    {
        var host = _host();
        var handle = NewFriendHandle?.Trim();
        if (host is null || string.IsNullOrWhiteSpace(handle))
        {
            return;
        }

        try
        {
            await host.AddFriendAsync(handle, string.IsNullOrWhiteSpace(NewFriendDisplayName) ? null : NewFriendDisplayName.Trim()).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                NewFriendHandle = string.Empty;
                NewFriendDisplayName = string.Empty;
                EligibilityHint = string.Empty;
            });
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't add friend: " + ex.Message);
        }
    }

    private async Task RemoveFriendAsync(Friend? friend)
    {
        var host = _host();
        if (host is null || friend is null)
        {
            return;
        }

        try
        {
            await host.RemoveFriendAsync(friend.GitHubLogin).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't remove friend: " + ex.Message);
        }
    }

    /// <summary>Asks the host, live, whether the typed handle is currently eligible for a grant, and shows a hint.</summary>
    public async Task CheckEligibilityAsync()
    {
        var host = _host();
        var handle = NewFriendHandle?.Trim();
        if (host is null || string.IsNullOrWhiteSpace(handle))
        {
            _dispatcher.Post(() => EligibilityHint = string.Empty);
            return;
        }

        try
        {
            var eligible = await host.CheckEligibilityAsync(handle).ConfigureAwait(false);
            _dispatcher.Post(() => EligibilityHint = eligible
                ? $"{handle} is eligible (a friend, or shares a configured org/team)."
                : $"{handle} is not eligible yet — add them as a friend, or share an org/team.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => EligibilityHint = "Couldn't check eligibility: " + ex.Message);
        }
    }

    private async Task GrantAsync()
    {
        var host = _host();
        var grantee = SelectedFriend?.GitHubLogin;
        var resource = GrantResource?.Trim();
        if (host is null || string.IsNullOrWhiteSpace(grantee) || string.IsNullOrWhiteSpace(resource))
        {
            _dispatcher.Post(() => Status = "Pick a friend and enter a resource to grant.");
            return;
        }

        try
        {
            await host.GrantAccessAsync(grantee, resource, NewGrantScope).ConfigureAwait(false);
            _dispatcher.Post(() => GrantResource = string.Empty);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't grant access: " + ex.Message);
        }
    }

    private async Task RevokeGrantAsync(AccessGrant? grant)
    {
        var host = _host();
        if (host is null || grant is null)
        {
            return;
        }

        try
        {
            await host.RevokeGrantAsync(grant.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't revoke grant: " + ex.Message);
        }
    }
}
