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
    }
}
