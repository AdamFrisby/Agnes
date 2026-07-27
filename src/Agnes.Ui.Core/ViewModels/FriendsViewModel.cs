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
    private readonly Func<IReadOnlyList<GrantTarget>> _grantTargets;

    /// <param name="grantTargets">
    /// The things a grant can name on this client right now — the connected host and its open sessions. A grant's
    /// resource is an opaque id, which makes it undiscoverable if the UI just asks the user to type one; this
    /// supplies the real candidates instead. Defaults to none, which the surface reports rather than offering an
    /// empty picker.
    /// </param>
    public FriendsViewModel(
        Func<IAgnesHost?> host,
        IUiDispatcher dispatcher,
        Func<IReadOnlyList<GrantTarget>>? grantTargets = null)
    {
        _host = host;
        _dispatcher = dispatcher;
        _grantTargets = grantTargets ?? (static () => []);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AddFriendCommand = new AsyncRelayCommand(AddFriendAsync);
        RemoveFriendCommand = new AsyncRelayCommand<FriendRowVm>(RemoveFriendAsync);
        CheckEligibilityCommand = new AsyncRelayCommand(CheckEligibilityAsync);
        CheckFriendEligibilityCommand = new AsyncRelayCommand<FriendRowVm>(CheckFriendEligibilityAsync);
        GrantCommand = new AsyncRelayCommand(GrantAsync);
        RevokeGrantCommand = new AsyncRelayCommand<AccessGrant>(RevokeGrantAsync);
    }

    /// <summary>The host owner's friend directory.</summary>
    public ObservableCollection<FriendRowVm> Friends { get; } = [];

    /// <summary>What a grant can be made against on this client: the host, and each open session.</summary>
    public ObservableCollection<GrantTarget> GrantTargets { get; } = [];

    public bool HasGrantTargets => GrantTargets.Count > 0;

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

    private FriendRowVm? _selectedFriend;
    public FriendRowVm? SelectedFriend
    {
        get => _selectedFriend;
        set => SetProperty(ref _selectedFriend, value);
    }

    /// <summary>The resource a new grant will name, chosen from <see cref="GrantTargets"/>.</summary>
    private GrantTarget? _selectedGrantTarget;
    public GrantTarget? SelectedGrantTarget
    {
        get => _selectedGrantTarget;
        set => SetProperty(ref _selectedGrantTarget, value);
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

    /// <summary>Re-asks the host whether an already-listed friend is eligible, so the answer can be read off
    /// the row that person is on rather than by retyping their handle into the add box.</summary>
    public ICommand CheckFriendEligibilityCommand { get; }

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
        var previouslySelected = SelectedFriend?.GitHubLogin;

        Friends.Clear();
        foreach (var f in friends)
        {
            Friends.Add(new FriendRowVm(f));
        }

        Grants.Clear();
        foreach (var g in grants)
        {
            Grants.Add(g);
        }

        // Re-offer the same candidate list the caller sees now (open sessions come and go).
        GrantTargets.Clear();
        foreach (var t in _grantTargets())
        {
            GrantTargets.Add(t);
        }

        SelectedFriend = Friends.FirstOrDefault(f => f.GitHubLogin == previouslySelected);
        SelectedGrantTarget = GrantTargets.FirstOrDefault(t => t.Id == SelectedGrantTarget?.Id) ?? GrantTargets.FirstOrDefault();

        OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(HasGrants));
        OnPropertyChanged(nameof(HasGrantTargets));
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

    private async Task RemoveFriendAsync(FriendRowVm? friend)
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

    /// <summary>Re-checks one listed friend's eligibility and writes the answer onto their row.</summary>
    private async Task CheckFriendEligibilityAsync(FriendRowVm? row)
    {
        var host = _host();
        if (host is null || row is null)
        {
            return;
        }

        try
        {
            var eligible = await host.CheckEligibilityAsync(row.GitHubLogin).ConfigureAwait(false);
            _dispatcher.Post(() => row.EligibilityNote = eligible
                ? "eligible for a grant"
                : "not eligible right now");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => row.EligibilityNote = "couldn't check: " + ex.Message);
        }
    }

    private async Task GrantAsync()
    {
        var host = _host();
        var grantee = SelectedFriend?.GitHubLogin;
        var resource = SelectedGrantTarget?.Id;
        if (host is null || string.IsNullOrWhiteSpace(grantee) || string.IsNullOrWhiteSpace(resource))
        {
            _dispatcher.Post(() => Status = GrantTargets.Count == 0
                ? "Nothing to grant against yet — connect a host (and open a session) first."
                : "Pick a friend and what they should get access to.");
            return;
        }

        try
        {
            await host.GrantAccessAsync(grantee, resource, NewGrantScope).ConfigureAwait(false);
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

/// <summary>
/// Something a grant can name: the opaque <see cref="Id"/> that goes on the wire, and the <see cref="Label"/> a
/// person can recognise. A grant's resource is an opaque string by design, so the UI offers the real candidates
/// rather than asking anyone to guess the format.
/// </summary>
public sealed record GrantTarget(string Id, string Label);

/// <summary>
/// One directory entry. Beyond the handle it answers the two questions the raw <see cref="Friend"/> can't:
/// <em>why is this person in my list</em> (they were added by hand, or they turned up through a shared GitHub
/// org/team), and <em>can I actually grant to them right now</em>, checked against the host on demand for the
/// row itself instead of by retyping the handle into the add box.
/// </summary>
public sealed class FriendRowVm : ObservableObject
{
    public FriendRowVm(Friend friend) => Friend = friend;

    public Friend Friend { get; }

    public string GitHubLogin => Friend.GitHubLogin;

    public string DisplayName => Friend.DisplayName ?? string.Empty;

    public string SourceLabel => Friend.Source == FriendSource.SharedOrg
        ? "shares a configured org/team"
        : "added by you";

    public string AddedLabel => $"added {Friend.AddedAt.ToLocalTime():yyyy-MM-dd}";

    private string _eligibilityNote = string.Empty;

    /// <summary>The last eligibility answer for this friend, blank until checked.</summary>
    public string EligibilityNote
    {
        get => _eligibilityNote;
        set
        {
            if (SetProperty(ref _eligibilityNote, value))
            {
                OnPropertyChanged(nameof(HasEligibilityNote));
            }
        }
    }

    public bool HasEligibilityNote => _eligibilityNote.Length > 0;
}
