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
/// Owns the tabbed dock layout and the connection lifecycle: new tabs pick an agent, open a
/// session, and are persisted so they auto-reconnect on relaunch. Uses <see cref="IAgnesConnector"/>
/// so it runs against the simulated server or a real host unchanged.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private const string HostUrl = "sim://demo";
    private const string Token = "";
    private const string WorkingDirectory = "/tmp/agnes";

    private readonly IAgnesConnector _connector;
    private readonly IUiDispatcher _dispatcher;
    private readonly SessionStateStore _store;
    private readonly DockFactory _factory;
    private bool _ready;

    public MainWindowViewModel(IAgnesConnector connector, IUiDispatcher dispatcher, SessionStateStore store)
    {
        _connector = connector;
        _dispatcher = dispatcher;
        _store = store;

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

    /// <summary>Restores previously open tabs (reconnecting each), or opens one fresh tab.</summary>
    public Task RestoreAsync()
    {
        // Load BEFORE enabling persistence, so tab-add events during construction/restore
        // can't clobber the saved state before we've read it.
        var saved = _store.Load();
        _ready = true;

        if (saved.Count == 0)
        {
            AddTab();
            return Task.CompletedTask;
        }

        foreach (var descriptor in saved)
        {
            // Stamp the descriptor immediately so a save during restore keeps the tab.
            var doc = new SessionDocument { Title = descriptor.Title, CanClose = true, Descriptor = descriptor };
            AddDocument(doc);
            _ = ReconnectAsync(doc, descriptor);
        }

        return Task.CompletedTask;
    }

    private void AddTab()
    {
        var doc = CreateTab();
        AddDocument(doc);
    }

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
        var doc = new SessionDocument { Title = "New session", CanClose = true };
        _ = LoadAgentsAsync(doc);
        return doc;
    }

    private async Task LoadAgentsAsync(SessionDocument doc)
    {
        try
        {
            var host = await _connector.ConnectAsync(HostUrl, Token);
            var agents = await host.ListAgentsAsync();
            _dispatcher.Post(() => doc.ShowAgents(agents.Select(a =>
                new AgentChoice(a.DisplayName, a.AdapterId,
                    new AsyncRelayCommand(() => OpenAgentAsync(doc, host, a.AdapterId, a.DisplayName))))));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = "Error: " + ex.Message);
        }
    }

    private async Task OpenAgentAsync(SessionDocument doc, IAgnesHost host, string adapterId, string displayName)
    {
        var info = await host.OpenSessionAsync(adapterId, WorkingDirectory);
        var view = await host.SubscribeAsync(info.SessionId);
        _dispatcher.Post(() =>
        {
            doc.AttachSession(new SessionViewModel(host, view, _dispatcher, displayName));
            doc.Title = displayName;
            doc.Descriptor = new SessionDescriptor(host.HostUrl, Token, info.SessionId, adapterId, displayName);
            SaveState();
        });
    }

    private async Task ReconnectAsync(SessionDocument doc, SessionDescriptor descriptor)
    {
        try
        {
            _dispatcher.Post(() => doc.StatusText = "Reconnecting…");
            var host = await _connector.ConnectAsync(descriptor.HostUrl, descriptor.Token);
            var view = await host.SubscribeAsync(descriptor.SessionId);
            _dispatcher.Post(() =>
            {
                doc.AttachSession(new SessionViewModel(host, view, _dispatcher, descriptor.Title));
                doc.Descriptor = descriptor;
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = "Reconnect failed: " + ex.Message);
        }
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
        _store.Save(tabs);
    }
}
