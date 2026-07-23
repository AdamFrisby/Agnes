using System.Collections.ObjectModel;
using System.Threading;
using Agnes.App.Desktop.Persistence;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// One browser-style tab. It walks through picking a host, then an agent, then holds a live
/// session — so the host is a per-tab primitive, not a window-wide setting. Also carries the
/// per-tab status (connection state + usage) for the tab's status bar.
/// </summary>
public sealed partial class SessionDocument : Document, ITraySession
{
    private readonly ITabController _controller;

    public SessionDocument(ITabController controller)
    {
        _controller = controller;
        _workingDirectory = controller.DefaultWorkingDirectory;
        // Disabled until the URL field is more than the "https://" prefill, so Connect can't fire on junk.
        AddHostCommand = new AsyncRelayCommand(() => _controller.AddHostAsync(this),
            () => NewHostUrl.Trim().Length > "https://".Length);
        SignInWithGitHubCommand = new AsyncRelayCommand(() => _controller.SignInWithGitHubAsync(this));
        SignInWithKeyCommand = new AsyncRelayCommand(() => _controller.SignInWithKeyAsync(this));
        ToggleAddHostCommand = new RelayCommand(() => ShowAddHost = !ShowAddHost);
        BackCommand = new RelayCommand(() => _controller.BackToHosts(this));
        CloseLoginTerminalCommand = new RelayCommand(() => LoginTerminal = null);
        SetGitCredentialModeCommand = new RelayCommand<string>(v => { if (v is not null) { GitCredentialMode = v; } });
        SetPermissionModeCommand = new RelayCommand<string>(v => SkipPermissions = v == "Autonomous");
        SetSandboxModeCommand = new RelayCommand<string>(v => { if (v is not null && SandboxAvailable) { UseSandbox = v == "On"; } });
        SelectAgentChoiceCommand = new RelayCommand<AgentChoice>(SelectAgentChoice);
        SelectModelChoiceCommand = new RelayCommand<ModelChoice>(SelectModelChoice);
        StartSessionCommand = new AsyncRelayCommand(StartSessionAsync, () => SelectedAgent is { Available: true });
        ApplyProfileCommand = new RelayCommand(() => { if (SelectedProfile is { } p) { ApplyLaunchProfile(p); } });
        ToggleSaveProfileCommand = new RelayCommand(() => { ShowSaveProfile = !ShowSaveProfile; if (ShowSaveProfile && string.IsNullOrWhiteSpace(NewProfileName)) { NewProfileName = SelectedAgent?.DisplayName ?? string.Empty; } });
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => SelectedAgent is { Available: true } && !string.IsNullOrWhiteSpace(NewProfileName));
        // Direct/watch sessions (sessions/02): find sessions a CLI created outside Agnes, then watch one read-only.
        DiscoverExternalSessionsCommand = new AsyncRelayCommand(() => _controller.DiscoverExternalSessionsAsync(this));
        WatchExternalSessionCommand = new AsyncRelayCommand<Agnes.Abstractions.ExternalSessionInfo>(
            e => e is null ? Task.CompletedTask : _controller.WatchExternalSessionAsync(this, e));

        Tags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTags));

        BeginRenameCommand = new RelayCommand(() => { RenameText = Title ?? string.Empty; IsRenaming = true; });
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        TogglePinCommand = new RelayCommand(() => { Pinned = !Pinned; _controller.PersistTabs(); });
        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand<string>(RemoveTag);
        ArchiveCommand = new RelayCommand(() => _controller.ArchiveTab(this));
        DuplicateCommand = new AsyncRelayCommand(() => _controller.DuplicateAsync(this));
        SameSetupCommand = new AsyncRelayCommand(() => _controller.NewSessionSameSetupAsync(this));
        ForkCommand = new AsyncRelayCommand(() => _controller.ForkAsync(this));
        MoveToWindowCommand = new RelayCommand(() => _controller.FloatTab(this));
        // Stop waiting on a slow/opaque session open and drop back to the agent picker (defect #8/#10).
        CancelStartCommand = new RelayCommand(() =>
        {
            StartCts?.Cancel();
            Stage = TabStage.PickAgent;
            StatusText = "Cancelled — choose an agent to try again.";
        });
    }

    /// <summary>The host directory a new session will run in (the project folder).</summary>
    [ObservableProperty]
    private string _workingDirectory;

    [ObservableProperty]
    private TabStage _stage = TabStage.PickHost;

    [ObservableProperty]
    private ObservableCollection<HostChoice>? _hosts;

    [ObservableProperty]
    private ObservableCollection<AgentChoice>? _agents;

    /// <summary>The full agent list; <see cref="Agents"/> is this filtered by <see cref="AgentFilter"/>.</summary>
    private IReadOnlyList<AgentChoice> _allAgents = [];

    /// <summary>Search text that filters the agent list (so it scales past a handful of agents).</summary>
    [ObservableProperty]
    private string _agentFilter = string.Empty;

    /// <summary>The highlighted agent; the session opens on Start, not on selection.</summary>
    [ObservableProperty]
    private AgentChoice? _selectedAgent;

    partial void OnAgentFilterChanged(string value) => ApplyAgentFilter();

    partial void OnSelectedAgentChanged(AgentChoice? value)
    {
        StartSessionCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    // ---- model picker (per selected agent; populated from the host's live/static catalog) ----

    /// <summary>Models offered for the selected agent, reconciled against the user's favorites. Null/empty
    /// hides the picker (the agent has no model axis Agnes knows about).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModels))]
    private ObservableCollection<ModelChoice>? _models;

    /// <summary>The chosen catalog model; the free-text <see cref="CustomModelId"/> overrides it when set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomEntryAllowed))]
    private ModelChoice? _selectedModel;

    /// <summary>A free-text model id, for a model newer than Agnes's catalog. Gated by
    /// <see cref="CustomEntryAllowed"/> (the selected model's <c>IsCustomEntryAllowed</c>).</summary>
    [ObservableProperty]
    private string _customModelId = string.Empty;

    public bool HasModels => Models is { Count: > 0 };

    /// <summary>Whether a free-text custom id is accepted right now (defaults allowed when nothing is selected).</summary>
    public bool CustomEntryAllowed => SelectedModel?.IsCustomEntryAllowed ?? true;

    /// <summary>The model id a new session should launch with: the (allowed) custom entry if typed, else the
    /// selected available catalog model, else null (the CLI's own default).</summary>
    public string? EffectiveModelId
    {
        get
        {
            var custom = CustomModelId?.Trim();
            if (!string.IsNullOrEmpty(custom))
            {
                return CustomEntryAllowed ? custom : null;
            }

            return SelectedModel is { IsAvailable: true } m ? m.Id : null;
        }
    }

    public IRelayCommand<ModelChoice> SelectModelChoiceCommand { get; }

    private void SelectModelChoice(ModelChoice? choice)
    {
        if (choice is null || !choice.IsAvailable)
        {
            return; // an unavailable (stale-favorite) row is shown but not selectable.
        }

        if (Models is { } models)
        {
            foreach (var m in models)
            {
                m.IsSelected = ReferenceEquals(m, choice);
            }
        }

        SelectedModel = choice;
    }

    /// <summary>Replaces the model picker's contents (called by the controller once the catalog is resolved),
    /// preselecting the first available model.</summary>
    public void SetModels(IEnumerable<ModelChoice> models)
    {
        Models = new ObservableCollection<ModelChoice>(models);
        CustomModelId = string.Empty;
        SelectedModel = null;
        SelectModelChoice(Models.FirstOrDefault(m => m.IsAvailable));
        OnPropertyChanged(nameof(HasModels));
    }

    // ---- provider login terminal (platform/03): a live, interactive terminal for a CLI's login flow ----

    /// <summary>The terminal panel for an in-progress provider login, or null when none is running. Bound to
    /// the same <see cref="TerminalPanelViewModel"/> the in-session terminal uses, so the user can watch the
    /// login CLI's prompts and type responses. Cleared to close the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoginTerminalVisible))]
    private TerminalPanelViewModel? _loginTerminal;

    /// <summary>Whether the provider-login terminal panel is showing.</summary>
    public bool IsLoginTerminalVisible => LoginTerminal is not null;

    /// <summary>Shows the login terminal panel bound to the given view model (called by the controller once the
    /// login session is subscribed).</summary>
    public void ShowLoginTerminal(TerminalPanelViewModel terminal) => LoginTerminal = terminal;

    /// <summary>Closes the login terminal panel (the login PTY keeps running host-side until it exits).</summary>
    public IRelayCommand CloseLoginTerminalCommand { get; }

    // ---- launch profiles (providers/04): pick a saved config to prefill the new-session controls ----

    /// <summary>The host's saved launch profiles, offered in the new-session picker. Empty hides the picker.</summary>
    public ObservableCollection<LaunchProfile> LaunchProfiles { get; } = [];

    public bool HasLaunchProfiles => LaunchProfiles.Count > 0;

    /// <summary>The profile chosen in the picker; applying it prefills the launch controls (the user can still
    /// tweak before starting).</summary>
    [ObservableProperty]
    private LaunchProfile? _selectedProfile;

    /// <summary>Whether the "Save current as profile…" name field is showing.</summary>
    [ObservableProperty]
    private bool _showSaveProfile;

    /// <summary>The name typed for a new profile when saving the current selections.</summary>
    [ObservableProperty]
    private string _newProfileName = string.Empty;

    partial void OnNewProfileNameChanged(string value) => SaveProfileCommand.NotifyCanExecuteChanged();

    public IRelayCommand ApplyProfileCommand { get; private set; } = null!;
    public IRelayCommand ToggleSaveProfileCommand { get; private set; } = null!;
    public IAsyncRelayCommand SaveProfileCommand { get; private set; } = null!;

    /// <summary>Replaces the tab's launch-profile list (called by the controller after loading from the host).</summary>
    public void SetLaunchProfiles(IEnumerable<LaunchProfile> profiles)
    {
        LaunchProfiles.Clear();
        foreach (var p in profiles)
        {
            LaunchProfiles.Add(p);
        }

        OnPropertyChanged(nameof(HasLaunchProfiles));
    }

    /// <summary>Applies <paramref name="profile"/>'s captured options to this tab's new-session controls:
    /// selects the matching agent, and prefills the working directory, permission posture, git-credential mode,
    /// sandbox toggle, and model. The MCP-approval posture is a client-global setting, applied via the
    /// controller. Everything remains editable before Start (profiles are a starting point, not a lock).</summary>
    public void ApplyLaunchProfile(LaunchProfile profile)
    {
        SelectedProfile = profile;

        if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory))
        {
            WorkingDirectory = profile.WorkingDirectory;
        }

        SkipPermissions = profile.SkipPermissions;
        GitCredentialMode = profile.GitCredentialMode;
        UseSandbox = profile.UseSandbox && SandboxAvailable;
        _controller.ApplyLaunchProfileMcpApproval(profile.McpApproval);

        // Select the agent the profile targets, if it's present and available; this also (re)loads its models.
        var agent = _allAgents.FirstOrDefault(a => a.AdapterId == profile.AdapterId && a.Available);
        if (agent is not null)
        {
            SelectAgentChoice(agent);
        }

        // The model catalog loads asynchronously; stash the profile's model as free-text so it's applied
        // regardless of whether the catalog lists it.
        CustomModelId = profile.ModelId ?? string.Empty;

        StatusText = $"Applied profile \"{profile.Name}\" — adjust anything, then Start.";
    }

    [ObservableProperty]
    private SessionViewModel? _session;

    [ObservableProperty]
    private string _statusText = "Choose a host";

    [ObservableProperty]
    private string _hostName = string.Empty;

    [ObservableProperty]
    private string _agentName = string.Empty;

    [ObservableProperty]
    private AgnesConnectionState _connectionState = AgnesConnectionState.Disconnected;

    [ObservableProperty]
    private string? _usageSummary;

    [ObservableProperty]
    private UsageInfo? _usage;

    [ObservableProperty]
    private bool _showAddHost;

    // ---- auth methods the chosen host offers (discovered from GET /auth/methods) ----
    [ObservableProperty]
    private bool _hostSupportsGitHub;

    [ObservableProperty]
    private bool _hostSupportsPairing = true;

    [ObservableProperty]
    private bool _isGitHubAuthorizing;

    [ObservableProperty]
    private string _gitHubUserCode = string.Empty;

    [ObservableProperty]
    private string _gitHubVerificationUri = string.Empty;

    [ObservableProperty]
    private bool _hostSupportsKeypair;

    /// <summary>The public-key line to add to the host's authorized_keys (shown for keypair sign-in).</summary>
    [ObservableProperty]
    private string _publicKeyLine = string.Empty;

    [ObservableProperty]
    private bool _showKeyInfo;

    /// <summary>The host's public GitHub OAuth client id (from discovery), used to run the device flow.</summary>
    public string? GitHubClientId { get; set; }

    partial void OnShowAddHostChanged(bool value)
    {
        if (value)
        {
            _ = _controller.DiscoverAuthMethodsAsync(this);
        }
    }

    /// <summary>True while connecting to a chosen host, so the picker can disable + show progress (defect #8).</summary>
    [ObservableProperty]
    private bool _isConnectingHost;

    /// <summary>Cancels an in-flight session open; the "Starting" screen's Cancel uses it.</summary>
    public CancellationTokenSource? StartCts { get; set; }

    [ObservableProperty]
    private bool _pinned;

    /// <summary>New-session choice: run the agent autonomously (skip per-tool approval). Default off.
    /// The label/description stay fixed — only the segmented control on the right reflects the state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermAsk))]
    [NotifyPropertyChangedFor(nameof(PermAutonomous))]
    private bool _skipPermissions;

    public bool PermAsk => !SkipPermissions;
    public bool PermAutonomous => SkipPermissions;

    /// <summary>
    /// New-session choice for a sandboxed session: whether the agent may `git push`, and how — "Off"
    /// (no credentials), "Ask" (a permission card per push), or "Trust" (auto-allow). Default "Off".
    /// The label/description stay fixed — only the Off/Ask/Trust segmented control reflects the state.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitCredOff))]
    [NotifyPropertyChangedFor(nameof(GitCredAsk))]
    [NotifyPropertyChangedFor(nameof(GitCredTrust))]
    private string _gitCredentialMode = "Ask";

    public bool GitCredOff => GitCredentialMode == "Off";
    public bool GitCredAsk => GitCredentialMode == "Ask";
    public bool GitCredTrust => GitCredentialMode == "Trust";

    /// <summary>New-session choice: isolate the agent in a per-session sandbox VM. Defaults on when the
    /// host supports it (<see cref="SandboxAvailable"/>); the control is disabled and off otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SandboxOn))]
    [NotifyPropertyChangedFor(nameof(SandboxOff))]
    private bool _useSandbox = true;

    /// <summary>Whether the connected host can sandbox at all (from HostInfo).</summary>
    [ObservableProperty]
    private bool _sandboxAvailable;

    public bool SandboxOn => UseSandbox;
    public bool SandboxOff => !UseSandbox;

    public IRelayCommand<string> SetSandboxModeCommand { get; }

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private string _tagInput = string.Empty;

    /// <summary>User labels for organising tabs; persisted with the tab.</summary>
    public ObservableCollection<string> Tags { get; } = [];

    public bool HasTags => Tags.Count > 0;

    [ObservableProperty]
    private string _newHostName = string.Empty;

    [ObservableProperty]
    private string _newHostUrl = "https://";

    partial void OnNewHostUrlChanged(string value)
    {
        AddHostCommand.NotifyCanExecuteChanged();
        _ = _controller.DiscoverAuthMethodsAsync(this); // refresh which sign-in methods this host offers
    }

    [ObservableProperty]
    private string _newHostToken = string.Empty;

    /// <summary>The connected host backing this tab (set once a host is chosen).</summary>
    public IAgnesHost? Host { get; set; }

    /// <summary>Token used to connect the host (for persistence).</summary>
    public string HostToken { get; set; } = string.Empty;

    public SessionDescriptor? Descriptor { get; set; }

    public IAsyncRelayCommand AddHostCommand { get; }
    public IAsyncRelayCommand SignInWithGitHubCommand { get; }
    public IAsyncRelayCommand SignInWithKeyCommand { get; }
    public IRelayCommand ToggleAddHostCommand { get; }
    public IRelayCommand<string> SetGitCredentialModeCommand { get; }
    public IRelayCommand<string> SetPermissionModeCommand { get; }
    public IRelayCommand<AgentChoice> SelectAgentChoiceCommand { get; }
    public IAsyncRelayCommand StartSessionCommand { get; }
    public IRelayCommand BackCommand { get; }

    // ---- Direct/watch sessions (sessions/02): sessions a CLI created outside Agnes ----
    public IAsyncRelayCommand DiscoverExternalSessionsCommand { get; }
    public IAsyncRelayCommand<Agnes.Abstractions.ExternalSessionInfo> WatchExternalSessionCommand { get; }

    /// <summary>Sessions the CLI created outside Agnes for this working directory (from
    /// <see cref="DiscoverExternalSessionsCommand"/>) — each offers a read-only "Watch".</summary>
    public ObservableCollection<Agnes.Abstractions.ExternalSessionInfo> DiscoveredSessions { get; } = [];

    public bool HasDiscoveredSessions => DiscoveredSessions.Count > 0;

    private string _discoverStatus = string.Empty;

    /// <summary>A short status line for the discovery action (count found / none / error).</summary>
    public string DiscoverStatus
    {
        get => _discoverStatus;
        set => SetProperty(ref _discoverStatus, value);
    }

    /// <summary>Replaces the discovered-session list (marshalled onto the UI thread by the caller).</summary>
    public void ShowDiscoveredSessions(IEnumerable<Agnes.Abstractions.ExternalSessionInfo> sessions)
    {
        DiscoveredSessions.Clear();
        foreach (var session in sessions)
        {
            DiscoveredSessions.Add(session);
        }

        OnPropertyChanged(nameof(HasDiscoveredSessions));
    }

    // ---- session management: rename / pin / tag / archive / duplicate / fork ----
    public IRelayCommand BeginRenameCommand { get; }
    public IRelayCommand CommitRenameCommand { get; }
    public IRelayCommand CancelRenameCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
    public IRelayCommand AddTagCommand { get; }
    public IRelayCommand<string> RemoveTagCommand { get; }
    public IRelayCommand ArchiveCommand { get; }
    public IAsyncRelayCommand DuplicateCommand { get; }

    /// <summary>Opens a fresh, empty session on the same host/agent with this session's launch config.</summary>
    public IAsyncRelayCommand SameSetupCommand { get; }
    public IAsyncRelayCommand ForkCommand { get; }
    public IRelayCommand MoveToWindowCommand { get; }
    public IRelayCommand CancelStartCommand { get; }

    private void CommitRename()
    {
        var name = RenameText.Trim();
        if (name.Length > 0)
        {
            Title = name;
            _controller.PersistTabs();
        }

        IsRenaming = false;
    }

    private void AddTag()
    {
        var tag = TagInput.Trim();
        if (tag.Length > 0 && !Tags.Contains(tag))
        {
            Tags.Add(tag);
            _controller.PersistTabs();
        }

        TagInput = string.Empty;
    }

    private void RemoveTag(string? tag)
    {
        if (tag is not null && Tags.Remove(tag))
        {
            _controller.PersistTabs();
        }
    }

    // ---- stage helpers ----

    public bool IsPickingHost => Stage == TabStage.PickHost;
    public bool IsPickingAgent => Stage == TabStage.PickAgent;
    public bool IsStarting => Stage == TabStage.Starting;
    public bool IsLive => Stage == TabStage.Live;

    /// <summary>Status bar shows once a host is connected (agent-pick and live stages).</summary>
    public bool ShowStatusBar => Stage != TabStage.PickHost;

    partial void OnStageChanged(TabStage value)
    {
        OnPropertyChanged(nameof(IsPickingHost));
        OnPropertyChanged(nameof(IsPickingAgent));
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(ShowStatusBar));
    }

    // ---- agent picking: select (highlight) then Start (open) ----

    private void SelectAgentChoice(AgentChoice? choice)
    {
        if (choice is null || !choice.Available)
        {
            return;
        }

        foreach (var a in _allAgents)
        {
            a.IsSelected = ReferenceEquals(a, choice);
        }

        SelectedAgent = choice;
        // The model catalog is per-agent; reset it and (re)load for the newly chosen agent.
        Models = null;
        _ = _controller.LoadModelsAsync(this, choice.AdapterId);
    }

    private Task StartSessionAsync()
    {
        if (SelectedAgent is not { Available: true } a)
        {
            return Task.CompletedTask;
        }

        // A free-text id typed against a model that forbids custom entry is rejected up front (clear message)
        // rather than launching with a bad id.
        if (!string.IsNullOrWhiteSpace(CustomModelId) && !CustomEntryAllowed)
        {
            StatusText = "This model doesn't allow a custom model id — clear it or pick another model.";
            return Task.CompletedTask;
        }

        return _controller.SelectAgentAsync(this, a.AdapterId, a.DisplayName, SkipPermissions, GitCredentialMode, SandboxAvailable && UseSandbox, EffectiveModelId);
    }

    private async Task SaveProfileAsync()
    {
        if (SelectedAgent is not { Available: true } || string.IsNullOrWhiteSpace(NewProfileName))
        {
            return;
        }

        await _controller.SaveCurrentAsLaunchProfileAsync(this, NewProfileName.Trim()).ConfigureAwait(true);
        ShowSaveProfile = false;
        NewProfileName = string.Empty;
    }

    private void ApplyAgentFilter()
    {
        var q = AgentFilter?.Trim() ?? string.Empty;
        var matches = q.Length == 0
            ? _allAgents
            : _allAgents.Where(a =>
                a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || a.AdapterId.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Agents = new ObservableCollection<AgentChoice>(matches);
    }

    // ---- status-bar helpers ----

    public bool IsConnected => ConnectionState == AgnesConnectionState.Connected;

    public string ConnectionText => ConnectionState switch
    {
        AgnesConnectionState.Connected => "Connected",
        AgnesConnectionState.Connecting => "Connecting…",
        AgnesConnectionState.Reconnecting => "Reconnecting…",
        _ => "Disconnected",
    };

    partial void OnConnectionStateChanged(AgnesConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionText));
    }

    // ---- stage transitions ----

    public void ShowHosts(IEnumerable<KnownHost> hosts)
    {
        Hosts = new ObservableCollection<HostChoice>(hosts.Select(h =>
            new HostChoice(h.Name, h.Url,
                new AsyncRelayCommand(() => _controller.SelectHostAsync(this, h)),
                _controller.IsForgettableHost(h.Url)
                    ? new AsyncRelayCommand(() => _controller.ForgetHostAsync(this, h))
                    : null)));
        Stage = TabStage.PickHost;
        StatusText = string.Empty;
    }

    public void ShowAgents(IEnumerable<AgentInfo> agents)
    {
        _allAgents = agents.Select(a => new AgentChoice(
            a.DisplayName, a.AdapterId, a.Available, a.Auth,
            () => _controller.CheckAgentAuthAsync(this, a.AdapterId),
            () => _controller.BeginProviderLoginAsync(this, a.AdapterId))).ToList();
        SelectedAgent = null;
        AgentFilter = string.Empty;
        // Preselect the first available agent so Start is reachable without a click, but never auto-open.
        SelectAgentChoice(_allAgents.FirstOrDefault(a => a.Available));
        ApplyAgentFilter();
        Stage = TabStage.PickAgent;
        StatusText = string.Empty;
        // Offer any saved launch profiles alongside the from-scratch agent picker.
        _ = _controller.LoadLaunchProfilesAsync(this);
    }

    /// <summary>Refreshes the picker's login badges from a host-pushed agent list (OnAgentsChanged), so a
    /// "Check now" on one client — or a background probe — updates every client's picker.</summary>
    public void UpdateAgentsAuth(IReadOnlyList<AgentInfo> agents)
    {
        foreach (var choice in _allAgents)
        {
            var match = agents.FirstOrDefault(a => a.AdapterId == choice.AdapterId);
            if (match is not null)
            {
                choice.Auth = match.Auth;
            }
        }
    }

    public void AttachSession(SessionViewModel session)
    {
        Session = session;
        Stage = TabStage.Live;
        StatusText = "Connected";

        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionViewModel.Activity)
                or nameof(SessionViewModel.ActivityText)
                or nameof(SessionViewModel.NeedsAttention))
            {
                OnPropertyChanged(nameof(Activity));
                OnPropertyChanged(nameof(ActivityText));
                OnPropertyChanged(nameof(NeedsAttention));
            }
            else if (e.PropertyName is nameof(SessionViewModel.IsUnread))
            {
                OnPropertyChanged(nameof(IsUnread));
            }
            else if (e.PropertyName is nameof(SessionViewModel.Usage)
                or nameof(SessionViewModel.UsageSummary))
            {
                Usage = session.Usage;
                UsageSummary = session.UsageSummary;
            }
            else if (e.PropertyName is nameof(SessionViewModel.AgentTitle) && session.HasAgentTitle)
            {
                // The agent produced a name for the conversation — use it for the tab (the strip trims it
                // to fit; the working folder stays available as the tab's tooltip).
                Title = session.AgentTitle;
            }
        };
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(NeedsAttention));
        Usage = session.Usage;
        UsageSummary = session.UsageSummary;
        if (session.HasAgentTitle)
        {
            Title = session.AgentTitle; // a replayed title on (re)attach
        }
    }

    // ---- cross-session attention (delegates to the live session) ----
    /// <summary>Whether this tab has unread activity (sessions/05) — shown as a dot on the tab strip.</summary>
    public bool IsUnread => Session?.IsUnread ?? false;

    public SessionActivity Activity => Session?.Activity ?? SessionActivity.Idle;
    public string ActivityText => Session?.ActivityText ?? string.Empty;
    public bool NeedsAttention => Session?.NeedsAttention ?? false;

    /// <summary>The live session's id, or empty until one is attached (ITraySession — feeds the tray's
    /// jump-to-session menu).</summary>
    public string SessionId => Session?.SessionId ?? string.Empty;
}
