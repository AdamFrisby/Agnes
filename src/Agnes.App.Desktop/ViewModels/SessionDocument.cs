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
public sealed partial class SessionDocument : Document
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
        SetGitCredentialModeCommand = new RelayCommand<string>(v => { if (v is not null) { GitCredentialMode = v; } });
        SetPermissionModeCommand = new RelayCommand<string>(v => SkipPermissions = v == "Autonomous");
        SetSandboxModeCommand = new RelayCommand<string>(v => { if (v is not null && SandboxAvailable) { UseSandbox = v == "On"; } });
        SelectAgentChoiceCommand = new RelayCommand<AgentChoice>(SelectAgentChoice);
        StartSessionCommand = new AsyncRelayCommand(StartSessionAsync, () => SelectedAgent is { Available: true });

        Tags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTags));

        BeginRenameCommand = new RelayCommand(() => { RenameText = Title ?? string.Empty; IsRenaming = true; });
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        TogglePinCommand = new RelayCommand(() => { Pinned = !Pinned; _controller.PersistTabs(); });
        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand<string>(RemoveTag);
        ArchiveCommand = new RelayCommand(() => _controller.ArchiveTab(this));
        DuplicateCommand = new AsyncRelayCommand(() => _controller.DuplicateAsync(this));
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

    partial void OnSelectedAgentChanged(AgentChoice? value) => StartSessionCommand.NotifyCanExecuteChanged();

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

    // ---- session management: rename / pin / tag / archive / duplicate / fork ----
    public IRelayCommand BeginRenameCommand { get; }
    public IRelayCommand CommitRenameCommand { get; }
    public IRelayCommand CancelRenameCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
    public IRelayCommand AddTagCommand { get; }
    public IRelayCommand<string> RemoveTagCommand { get; }
    public IRelayCommand ArchiveCommand { get; }
    public IAsyncRelayCommand DuplicateCommand { get; }
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
    }

    private Task StartSessionAsync()
        => SelectedAgent is { Available: true } a
            ? _controller.SelectAgentAsync(this, a.AdapterId, a.DisplayName, SkipPermissions, GitCredentialMode, SandboxAvailable && UseSandbox)
            : Task.CompletedTask;

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
        _allAgents = agents.Select(a => new AgentChoice(a.DisplayName, a.AdapterId, a.Available)).ToList();
        SelectedAgent = null;
        AgentFilter = string.Empty;
        // Preselect the first available agent so Start is reachable without a click, but never auto-open.
        SelectAgentChoice(_allAgents.FirstOrDefault(a => a.Available));
        ApplyAgentFilter();
        Stage = TabStage.PickAgent;
        StatusText = string.Empty;
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
            else if (e.PropertyName is nameof(SessionViewModel.Usage)
                or nameof(SessionViewModel.UsageSummary))
            {
                Usage = session.Usage;
                UsageSummary = session.UsageSummary;
            }
        };
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(NeedsAttention));
        Usage = session.Usage;
        UsageSummary = session.UsageSummary;
    }

    // ---- cross-session attention (delegates to the live session) ----
    public SessionActivity Activity => Session?.Activity ?? SessionActivity.Idle;
    public string ActivityText => Session?.ActivityText ?? string.Empty;
    public bool NeedsAttention => Session?.NeedsAttention ?? false;
}
