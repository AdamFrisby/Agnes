using System.Collections.ObjectModel;
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
        AddHostCommand = new AsyncRelayCommand(() => _controller.AddHostAsync(this));
        ToggleAddHostCommand = new RelayCommand(() => ShowAddHost = !ShowAddHost);
        BackCommand = new RelayCommand(() => _controller.BackToHosts(this));

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
    }

    [ObservableProperty]
    private TabStage _stage = TabStage.PickHost;

    [ObservableProperty]
    private ObservableCollection<HostChoice>? _hosts;

    [ObservableProperty]
    private ObservableCollection<AgentChoice>? _agents;

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
    private bool _showAddHost;

    [ObservableProperty]
    private bool _pinned;

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

    [ObservableProperty]
    private string _newHostToken = string.Empty;

    /// <summary>The connected host backing this tab (set once a host is chosen).</summary>
    public IAgnesHost? Host { get; set; }

    /// <summary>Token used to connect the host (for persistence).</summary>
    public string HostToken { get; set; } = string.Empty;

    public SessionDescriptor? Descriptor { get; set; }

    public IAsyncRelayCommand AddHostCommand { get; }
    public IRelayCommand ToggleAddHostCommand { get; }
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
    public bool IsLive => Stage == TabStage.Live;

    /// <summary>Status bar shows once a host is connected (agent-pick and live stages).</summary>
    public bool ShowStatusBar => Stage != TabStage.PickHost;

    partial void OnStageChanged(TabStage value)
    {
        OnPropertyChanged(nameof(IsPickingHost));
        OnPropertyChanged(nameof(IsPickingAgent));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(ShowStatusBar));
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
            new HostChoice(h.Name, h.Url, new AsyncRelayCommand(() => _controller.SelectHostAsync(this, h)))));
        Stage = TabStage.PickHost;
        StatusText = string.Empty;
    }

    public void ShowAgents(IEnumerable<AgentInfo> agents)
    {
        Agents = new ObservableCollection<AgentChoice>(agents.Select(a =>
            new AgentChoice(a.DisplayName, a.AdapterId,
                new AsyncRelayCommand(() => _controller.SelectAgentAsync(this, a.AdapterId, a.DisplayName)))));
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
        };
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(NeedsAttention));
    }

    // ---- cross-session attention (delegates to the live session) ----
    public SessionActivity Activity => Session?.Activity ?? SessionActivity.Idle;
    public string ActivityText => Session?.ActivityText ?? string.Empty;
    public bool NeedsAttention => Session?.NeedsAttention ?? false;
}
