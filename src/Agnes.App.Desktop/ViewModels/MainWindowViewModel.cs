using Agnes.App.Desktop.Persistence;
using Agnes.Client;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// Owns the tabbed dock and acts as each tab's controller. Host is a per-tab choice: a new tab
/// picks a host (from the known-host registry, including the built-in simulated host, or a newly
/// added one), then an agent on that host, then opens a session. Uses <see cref="IAgnesConnector"/>
/// so simulated and real hosts work the same way. Open tabs auto-reconnect on relaunch.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, ITabController
{
    private const string WorkingDirectory = "/tmp/agnes";
    private static readonly KnownHost SimulatedHost = new("Simulated host", "sim://demo", string.Empty);
    private static readonly KnownHost RecordedHost = new("Recorded sessions", "rec://local", string.Empty);

    private readonly IAgnesConnector _connector;
    private readonly IUiDispatcher _dispatcher;
    private readonly SessionStateStore _tabStore;
    private readonly HostRegistryStore _hostStore;
    private readonly IPromptStore _prompts;
    private readonly DockFactory _factory;
    private readonly List<KnownHost> _knownHosts = [];
    private bool _ready;

    public MainWindowViewModel(
        IAgnesConnector connector,
        IUiDispatcher dispatcher,
        SessionStateStore tabStore,
        HostRegistryStore hostStore,
        IPromptStore? prompts = null)
    {
        _connector = connector;
        _dispatcher = dispatcher;
        _tabStore = tabStore;
        _hostStore = hostStore;
        _prompts = prompts ?? new JsonPromptStore();

        _knownHosts.Add(SimulatedHost);
        _knownHosts.Add(RecordedHost);
        _knownHosts.AddRange(hostStore.Load());

        _factory = new DockFactory
        {
            NewDocumentFactory = CreateTab,
            LayoutChanged = () => _dispatcher.Post(SaveState),
        };
        Layout = _factory.CreateLayout();
        _factory.InitLayout(Layout);

        NewTabCommand = new RelayCommand(AddTab);
    }

    public IRootDock Layout { get; }
    public IFactory Factory => _factory;
    public IRelayCommand NewTabCommand { get; }

    public Task RestoreAsync()
    {
        var saved = _tabStore.Load();
        _ready = true;

        if (saved.Count == 0)
        {
            AddTab();
            return Task.CompletedTask;
        }

        foreach (var descriptor in saved)
        {
            var doc = new SessionDocument(this)
            {
                Title = descriptor.Title,
                CanClose = true,
                Descriptor = descriptor,
                HostName = descriptor.HostName,
                AgentName = descriptor.Title,
            };
            AddDocument(doc);
            _ = ReconnectAsync(doc, descriptor);
        }

        return Task.CompletedTask;
    }

    // ---- ITabController ----

    public async Task SelectHostAsync(SessionDocument doc, KnownHost host)
    {
        try
        {
            _dispatcher.Post(() =>
            {
                doc.HostName = host.Name;
                doc.HostToken = host.Token;
                doc.StatusText = $"Connecting to {host.Name}…";
            });

            var agnesHost = await _connector.ConnectAsync(host.Url, host.Token);
            doc.Host = agnesHost;
            WireStatus(doc, agnesHost);

            var agents = await agnesHost.ListAgentsAsync();
            _dispatcher.Post(() => doc.ShowAgents(agents));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = "Error: " + ex.Message);
        }
    }

    public async Task AddHostAsync(SessionDocument doc)
    {
        var url = doc.NewHostUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var host = new KnownHost(string.IsNullOrWhiteSpace(doc.NewHostName) ? url : doc.NewHostName.Trim(), url, doc.NewHostToken);
        _knownHosts.Add(host);
        _hostStore.Save(_knownHosts.Where(h => h.Url != SimulatedHost.Url && h.Url != RecordedHost.Url).ToList());
        _dispatcher.Post(() => doc.ShowAddHost = false);
        await SelectHostAsync(doc, host);
    }

    public async Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName)
    {
        if (doc.Host is null)
        {
            return;
        }

        var info = await doc.Host.OpenSessionAsync(adapterId, WorkingDirectory);
        var view = await doc.Host.SubscribeAsync(info.SessionId);
        _dispatcher.Post(() =>
        {
            doc.AgentName = displayName;
            doc.AttachSession(new SessionViewModel(doc.Host!, view, _dispatcher, displayName, _prompts));
            doc.Title = displayName;
            doc.Descriptor = new SessionDescriptor(
                doc.HostName, doc.Host!.HostUrl, doc.HostToken, info.SessionId, adapterId, displayName);
            SaveState();
        });
    }

    public void BackToHosts(SessionDocument doc) => doc.ShowHosts(_knownHosts);

    // ---- tab lifecycle ----

    private void AddTab() => AddDocument(CreateTab());

    private void AddDocument(SessionDocument doc)
    {
        if (_factory.DocumentDock is { } dock)
        {
            _factory.AddDockable(dock, doc);
            _factory.SetActiveDockable(doc);
            _factory.SetFocusedDockable(dock, doc);
        }
    }

    private SessionDocument CreateTab()
    {
        var doc = new SessionDocument(this) { Title = "New session", CanClose = true };
        doc.ShowHosts(_knownHosts);
        return doc;
    }

    private async Task ReconnectAsync(SessionDocument doc, SessionDescriptor descriptor)
    {
        try
        {
            _dispatcher.Post(() => doc.StatusText = "Reconnecting…");
            var host = await _connector.ConnectAsync(descriptor.HostUrl, descriptor.Token);
            doc.Host = host;
            doc.HostToken = descriptor.Token;
            WireStatus(doc, host);

            var view = await host.SubscribeAsync(descriptor.SessionId);
            _dispatcher.Post(() =>
            {
                doc.AttachSession(new SessionViewModel(host, view, _dispatcher, descriptor.Title, _prompts));
                doc.Descriptor = descriptor;
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = "Reconnect failed: " + ex.Message);
        }
    }

    private void WireStatus(SessionDocument doc, IAgnesHost host)
    {
        _dispatcher.Post(() =>
        {
            doc.ConnectionState = host.State;
            doc.UsageSummary = host.UsageSummary;
        });
        host.StateChanged += state => _dispatcher.Post(() => doc.ConnectionState = state);
        host.UsageChanged += usage => _dispatcher.Post(() => doc.UsageSummary = usage);
    }

    private void SaveState()
    {
        if (!_ready)
        {
            return;
        }

        var tabs = _factory.DocumentDock?.VisibleDockables?
            .OfType<SessionDocument>()
            .Where(d => d.Descriptor is not null)
            .Select(d => d.Descriptor!)
            .ToList() ?? [];
        _tabStore.Save(tabs);
    }
}
