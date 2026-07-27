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
/// Drives the desktop "Collaborators" area (collaboration/01) against whatever <see cref="IAgnesHost"/> the accessor
/// returns, so it works identically over a real SignalR host and the offline simulation. Owner-only host-side —
/// a non-owner client's calls are refused, surfaced here as a status message. Three jobs: manage the collaborator
/// directory (add by GitHub handle, remove), show a <em>live</em> eligibility hint before granting (an explicit
/// collaborator, or a shared configured org/team — recomputed on the host, never cached), and manage the explicit,
/// revocable access grants that sit on top. Adding a collaborator or granting never shares anything by itself; a grant
/// is the only thing that confers access, and it is always revocable.
/// </summary>
public sealed class CollaboratorsViewModel : ObservableObject
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
    public CollaboratorsViewModel(
        Func<IAgnesHost?> host,
        IUiDispatcher dispatcher,
        Func<IReadOnlyList<GrantTarget>>? grantTargets = null)
    {
        _host = host;
        _dispatcher = dispatcher;
        _grantTargets = grantTargets ?? (static () => []);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AddCollaboratorCommand = new AsyncRelayCommand(AddCollaboratorAsync);
        RemoveCollaboratorCommand = new AsyncRelayCommand<CollaboratorRowVm>(RemoveCollaboratorAsync);
        CheckEligibilityCommand = new AsyncRelayCommand(CheckEligibilityAsync);
        CheckCollaboratorEligibilityCommand = new AsyncRelayCommand<CollaboratorRowVm>(CheckCollaboratorEligibilityAsync);
        GrantCommand = new AsyncRelayCommand(GrantAsync);
        RevokeGrantCommand = new AsyncRelayCommand<AccessGrant>(RevokeGrantAsync);
    }

    /// <summary>The host owner's collaborator directory.</summary>
    public ObservableCollection<CollaboratorRowVm> Collaborators { get; } = [];

    /// <summary>What a grant can be made against on this client: the host, and each open session.</summary>
    public ObservableCollection<GrantTarget> GrantTargets { get; } = [];

    public bool HasGrantTargets => GrantTargets.Count > 0;

    /// <summary>The active (non-revoked) access grants.</summary>
    public ObservableCollection<AccessGrant> Grants { get; } = [];

    /// <summary>The scopes a grant can carry, for a picker.</summary>
    public IReadOnlyList<GrantScope> Scopes { get; } = Enum.GetValues<GrantScope>();

    private string _newCollaboratorHandle = string.Empty;
    public string NewCollaboratorHandle
    {
        get => _newCollaboratorHandle;
        set => SetProperty(ref _newCollaboratorHandle, value);
    }

    private string _newCollaboratorDisplayName = string.Empty;
    public string NewCollaboratorDisplayName
    {
        get => _newCollaboratorDisplayName;
        set => SetProperty(ref _newCollaboratorDisplayName, value);
    }

    private string _eligibilityHint = string.Empty;
    public string EligibilityHint
    {
        get => _eligibilityHint;
        set => SetProperty(ref _eligibilityHint, value);
    }

    private CollaboratorRowVm? _selectedCollaborator;
    public CollaboratorRowVm? SelectedCollaborator
    {
        get => _selectedCollaborator;
        set => SetProperty(ref _selectedCollaborator, value);
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

    public bool HasCollaborators => Collaborators.Count > 0;
    public bool HasGrants => Grants.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand AddCollaboratorCommand { get; }
    public ICommand RemoveCollaboratorCommand { get; }
    public ICommand CheckEligibilityCommand { get; }

    /// <summary>Re-asks the host whether an already-listed collaborator is eligible, so the answer can be read off
    /// the row that person is on rather than by retyping their handle into the add box.</summary>
    public ICommand CheckCollaboratorEligibilityCommand { get; }

    public ICommand GrantCommand { get; }
    public ICommand RevokeGrantCommand { get; }

    /// <summary>Loads collaborators and grants from the host and rebuilds the lists.</summary>
    public async Task RefreshAsync()
    {
        var host = _host();
        if (host is null)
        {
            _dispatcher.Post(() =>
            {
                Collaborators.Clear();
                Grants.Clear();
                OnPropertyChanged(nameof(HasCollaborators));
                OnPropertyChanged(nameof(HasGrants));
                Status = "Connect to a host to manage collaborators.";
            });
            return;
        }

        try
        {
            var collaborators = await host.ListCollaboratorsAsync().ConfigureAwait(false);
            var grants = await host.ListGrantsAsync().ConfigureAwait(false);
            _dispatcher.Post(() => Rebuild(collaborators, grants));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't load collaborators: " + ex.Message);
        }
    }

    private void Rebuild(IReadOnlyList<Collaborator> collaborators, IReadOnlyList<AccessGrant> grants)
    {
        var previouslySelected = SelectedCollaborator?.GitHubLogin;

        Collaborators.Clear();
        foreach (var f in collaborators)
        {
            Collaborators.Add(new CollaboratorRowVm(f));
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

        SelectedCollaborator = Collaborators.FirstOrDefault(f => f.GitHubLogin == previouslySelected);
        SelectedGrantTarget = GrantTargets.FirstOrDefault(t => t.Id == SelectedGrantTarget?.Id) ?? GrantTargets.FirstOrDefault();

        OnPropertyChanged(nameof(HasCollaborators));
        OnPropertyChanged(nameof(HasGrants));
        OnPropertyChanged(nameof(HasGrantTargets));
        Status = $"{Collaborators.Count} collaborator(s), {Grants.Count} active grant(s).";
    }

    private async Task AddCollaboratorAsync()
    {
        var host = _host();
        var handle = NewCollaboratorHandle?.Trim();
        if (host is null || string.IsNullOrWhiteSpace(handle))
        {
            return;
        }

        try
        {
            await host.AddCollaboratorAsync(handle, string.IsNullOrWhiteSpace(NewCollaboratorDisplayName) ? null : NewCollaboratorDisplayName.Trim()).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                NewCollaboratorHandle = string.Empty;
                NewCollaboratorDisplayName = string.Empty;
                EligibilityHint = string.Empty;
            });
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't add collaborator: " + ex.Message);
        }
    }

    private async Task RemoveCollaboratorAsync(CollaboratorRowVm? collaborator)
    {
        var host = _host();
        if (host is null || collaborator is null)
        {
            return;
        }

        try
        {
            await host.RemoveCollaboratorAsync(collaborator.GitHubLogin).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't remove collaborator: " + ex.Message);
        }
    }

    /// <summary>Asks the host, live, whether the typed handle is currently eligible for a grant, and shows a hint.</summary>
    public async Task CheckEligibilityAsync()
    {
        var host = _host();
        var handle = NewCollaboratorHandle?.Trim();
        if (host is null || string.IsNullOrWhiteSpace(handle))
        {
            _dispatcher.Post(() => EligibilityHint = string.Empty);
            return;
        }

        try
        {
            var eligible = await host.CheckEligibilityAsync(handle).ConfigureAwait(false);
            _dispatcher.Post(() => EligibilityHint = eligible
                ? $"{handle} is eligible (a collaborator, or shares a configured org/team)."
                : $"{handle} is not eligible yet — add them as a collaborator, or share an org/team.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => EligibilityHint = "Couldn't check eligibility: " + ex.Message);
        }
    }

    /// <summary>Re-checks one listed collaborator's eligibility and writes the answer onto their row.</summary>
    private async Task CheckCollaboratorEligibilityAsync(CollaboratorRowVm? row)
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
        var grantee = SelectedCollaborator?.GitHubLogin;
        var resource = SelectedGrantTarget?.Id;
        if (host is null || string.IsNullOrWhiteSpace(grantee) || string.IsNullOrWhiteSpace(resource))
        {
            _dispatcher.Post(() => Status = GrantTargets.Count == 0
                ? "Nothing to grant against yet — connect a host (and open a session) first."
                : "Pick a collaborator and what they should get access to.");
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
/// One directory entry. Beyond the handle it answers the two questions the raw <see cref="Collaborator"/> can't:
/// <em>why is this person in my list</em> (they were added by hand, or they turned up through a shared GitHub
/// org/team), and <em>can I actually grant to them right now</em>, checked against the host on demand for the
/// row itself instead of by retyping the handle into the add box.
/// </summary>
public sealed class CollaboratorRowVm : ObservableObject
{
    public CollaboratorRowVm(Collaborator collaborator) => Collaborator = collaborator;

    public Collaborator Collaborator { get; }

    public string GitHubLogin => Collaborator.GitHubLogin;

    public string DisplayName => Collaborator.DisplayName ?? string.Empty;

    public string SourceLabel => Collaborator.Source == CollaboratorSource.SharedOrg
        ? "shares a configured org/team"
        : "added by you";

    public string AddedLabel => $"added {Collaborator.AddedAt.ToLocalTime():yyyy-MM-dd}";

    private string _eligibilityNote = string.Empty;

    /// <summary>The last eligibility answer for this collaborator, blank until checked.</summary>
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
