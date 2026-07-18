using System.Collections.ObjectModel;
using Agnes.Client;
using Agnes.Ui.Core.Mvvm;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Root view model: connects to hosts, lists their agents, and opens sessions. Designed to
/// aggregate many agents across many hosts (the pool lives in <see cref="AgnesClient"/>).
/// </summary>
public sealed class WorkspaceViewModel : ObservableObject
{
    private readonly AgnesClient _client;
    private readonly IUiDispatcher _dispatcher;
    private string _hostUrl = "https://localhost:5081";
    private string _token = string.Empty;
    private string _workingDirectory = ".";
    private string _status = "Not connected";
    private SessionViewModel? _activeSession;

    public WorkspaceViewModel(AgnesClient client, IUiDispatcher dispatcher)
    {
        _client = client;
        _dispatcher = dispatcher;
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !string.IsNullOrWhiteSpace(HostUrl));
    }

    public string HostUrl
    {
        get => _hostUrl;
        set { if (Set(ref _hostUrl, value)) { ConnectCommand.RaiseCanExecuteChanged(); } }
    }

    public string Token
    {
        get => _token;
        set => Set(ref _token, value);
    }

    /// <summary>Working directory (on the host) for new sessions.</summary>
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => Set(ref _workingDirectory, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public ObservableCollection<AgentEntryViewModel> Agents { get; } = [];

    public SessionViewModel? ActiveSession
    {
        get => _activeSession;
        private set { if (Set(ref _activeSession, value)) { Raise(nameof(HasActiveSession)); } }
    }

    public AsyncRelayCommand ConnectCommand { get; }

    /// <summary>Whether a session is currently open (drives mobile back-navigation).</summary>
    public bool HasActiveSession => ActiveSession is not null;

    /// <summary>Closes the active session view (returns to the agent list on mobile).</summary>
    public void CloseSession() => ActiveSession = null;

    private async Task ConnectAsync()
    {
        try
        {
            Status = "Connecting…";
            var host = await _client.AddHostAsync(HostUrl, Token);
            var info = await host.GetHostInfoAsync();
            var agents = await host.ListAgentsAsync();
            _dispatcher.Post(() =>
            {
                Agents.Clear();
                foreach (var agent in agents)
                {
                    Agents.Add(new AgentEntryViewModel(host, agent, this));
                }

                Status = $"Connected to {info.DisplayName} — {agents.Count} agent(s)";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Error: " + ex.Message);
        }
    }

    public async Task OpenSessionAsync(AgentEntryViewModel entry)
    {
        var info = await entry.Host.OpenSessionAsync(entry.AdapterId, WorkingDirectory);
        var view = await entry.Host.SubscribeAsync(info.SessionId);
        _dispatcher.Post(() => ActiveSession = new SessionViewModel(entry.Host, view, _dispatcher, entry.DisplayName));
    }
}
