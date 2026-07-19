using Agnes.App.Desktop.Persistence;
using Agnes.Client;
using Agnes.Protocol;
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
    private readonly SessionStateStore _archiveStore;
    private readonly HostRegistryStore _hostStore;
    private readonly IPromptStore _prompts;
    private readonly IPermissionPolicy _policy;
    private readonly SettingsStore _settingsStore;
    private readonly DockFactory _factory;
    private readonly List<KnownHost> _knownHosts = [];
    private AppSettings _settings;
    private bool _ready;

    /// <summary>Surfaces session notifications (toast / OS). Set by the shell once a window exists.</summary>
    public INotifier Notifier { get; set; } = NullNotifier.Instance;

    /// <summary>Whether the window is focused — completion toasts are suppressed while it is.</summary>
    public bool WindowActive { get; set; } = true;

    public MainWindowViewModel(
        IAgnesConnector connector,
        IUiDispatcher dispatcher,
        SessionStateStore tabStore,
        HostRegistryStore hostStore,
        IPromptStore? prompts = null,
        SessionStateStore? archiveStore = null,
        SettingsStore? settingsStore = null,
        IPermissionPolicy? policy = null)
    {
        _connector = connector;
        _dispatcher = dispatcher;
        _tabStore = tabStore;
        _archiveStore = archiveStore ?? new SessionStateStore(SessionStateStore.DefaultPath().Replace("desktop-tabs.json", "desktop-archive.json"));
        _hostStore = hostStore;
        _prompts = prompts ?? new FilePromptStore();
        _policy = policy ?? new FilePermissionPolicy();
        _settingsStore = settingsStore ?? new SettingsStore();
        _settings = _settingsStore.Load();

        _knownHosts.Add(SimulatedHost);
        _knownHosts.Add(RecordedHost);
        _knownHosts.AddRange(hostStore.Load());

        foreach (var archived in _archiveStore.Load())
        {
            ArchivedSessions.Add(archived);
        }

        ArchivedSessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasArchived));

        _factory = new DockFactory
        {
            NewDocumentFactory = CreateTab,
            LayoutChanged = () => _dispatcher.Post(() => { SaveState(); RefreshSessions(); }),
        };
        Layout = _factory.CreateLayout();
        _factory.InitLayout(Layout);

        NewTabCommand = new RelayCommand(AddTab);
        ReopenArchivedCommand = new RelayCommand<SessionDescriptor>(d => { if (d is not null) { ReopenArchived(d); } });
        SelectGlobalHitCommand = new RelayCommand<GlobalHit>(SelectGlobalHit);
        ActivateSessionCommand = new RelayCommand<SessionDocument>(d => { if (d is not null) { _factory.SetActiveDockable(d); } });
        CloseActiveTabCommand = new RelayCommand(CloseActiveTab);
        ToggleReducedMotionCommand = new RelayCommand(() => ReducedMotion = !ReducedMotion);
    }

    public IRelayCommand CloseActiveTabCommand { get; }
    public IRelayCommand ToggleReducedMotionCommand { get; }
    public IRelayCommand<SessionDocument> ActivateSessionCommand { get; }

    // ---- cross-session attention / switcher ----

    private readonly HashSet<SessionDocument> _watched = [];

    /// <summary>All open tabs, for the session switcher.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SessionDocument> OpenSessions { get; } = [];

    public int AttentionCount => OpenSessions.Count(d => d.NeedsAttention);
    public bool HasAttention => AttentionCount > 0;

    private void RefreshSessions()
    {
        var docs = OpenTabs().ToList();
        foreach (var doc in docs)
        {
            if (_watched.Add(doc))
            {
                doc.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SessionDocument.NeedsAttention))
                    {
                        RaiseAttention();
                    }
                };
            }
        }

        OpenSessions.Clear();
        foreach (var doc in docs)
        {
            OpenSessions.Add(doc);
        }

        RaiseAttention();
    }

    private void RaiseAttention()
    {
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HasAttention));
    }

    /// <summary>Accessibility: disables non-essential motion/animation when on.</summary>
    public bool ReducedMotion
    {
        get => _settings.ReducedMotion;
        set
        {
            if (value != _settings.ReducedMotion)
            {
                _settings = _settings with { ReducedMotion = value };
                _settingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The persisted UI settings (window geometry, theme, density) for the shell to apply.</summary>
    public AppSettings Settings => _settings;

    /// <summary>Persists the window geometry so it reopens where the user left it.</summary>
    public void SaveWindowState(double width, double height, int x, int y, bool maximized)
    {
        _settings = _settings with
        {
            WindowWidth = width,
            WindowHeight = height,
            WindowX = x,
            WindowY = y,
            WindowMaximized = maximized,
        };
        _settingsStore.Save(_settings);
    }

    /// <summary>Creates a session view model and wires its notifications to the shell.</summary>
    private SessionViewModel CreateSession(IAgnesHost host, SessionView view, string title)
    {
        var session = new SessionViewModel(host, view, _dispatcher, title, _prompts, _policy);
        session.NotificationRaised += n => _dispatcher.Post(() => Surface(n));
        return session;
    }

    private void Surface(AppNotification notification)
    {
        // The user is already looking — don't toast a completion. Blockers/errors always show.
        if (notification.Kind == NotificationKind.Completion && WindowActive)
        {
            return;
        }

        Notifier.Notify(notification);
    }

    private void CloseActiveTab()
    {
        if (_factory.DocumentDock?.ActiveDockable is SessionDocument doc)
        {
            _factory.CloseDockable(doc);
            SaveState();
        }
    }

    public IRootDock Layout { get; }
    public IFactory Factory => _factory;
    public IRelayCommand NewTabCommand { get; }
    public IRelayCommand<SessionDescriptor> ReopenArchivedCommand { get; }
    public IRelayCommand<GlobalHit> SelectGlobalHitCommand { get; }

    public bool HasArchived => ArchivedSessions.Count > 0;

    // ---- cross-session search ----

    private string _globalSearchQuery = string.Empty;

    public string GlobalSearchQuery
    {
        get => _globalSearchQuery;
        set { if (SetProperty(ref _globalSearchQuery, value)) { RunGlobalSearch(); } }
    }

    /// <summary>Matches found across every open session for <see cref="GlobalSearchQuery"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<GlobalHit> GlobalResults { get; } = [];

    public bool HasGlobalResults => GlobalResults.Count > 0;

    private void RunGlobalSearch()
    {
        GlobalResults.Clear();
        var query = _globalSearchQuery;
        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var doc in OpenTabs())
            {
                if (doc.Session is not { } session)
                {
                    continue;
                }

                foreach (var hit in session.Find(query, doc.Title))
                {
                    GlobalResults.Add(new GlobalHit(doc, hit));
                    if (GlobalResults.Count >= 100)
                    {
                        break;
                    }
                }
            }
        }

        OnPropertyChanged(nameof(HasGlobalResults));
    }

    private void SelectGlobalHit(GlobalHit? hit)
    {
        if (hit is null)
        {
            return;
        }

        _factory.SetActiveDockable(hit.Tab);
        hit.Tab.Session?.ScrollTo(hit.Hit.AnchorId);
    }

    private IEnumerable<SessionDocument> OpenTabs()
        => _factory.DocumentDock?.VisibleDockables?.OfType<SessionDocument>() ?? [];

    /// <summary>Archived (closed-but-kept) sessions, restorable from the tab menu.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SessionDescriptor> ArchivedSessions { get; } = [];

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
                Pinned = descriptor.Pinned,
            };
            ApplyTags(doc, descriptor.Tags);
            AddDocument(doc);
            _ = ReconnectAsync(doc, descriptor);
        }

        return Task.CompletedTask;
    }

    private static void ApplyTags(SessionDocument doc, IReadOnlyList<string>? tags)
    {
        if (tags is null)
        {
            return;
        }

        foreach (var tag in tags)
        {
            doc.Tags.Add(tag);
        }
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

        // The field takes a pairing code: exchange it for a durable per-device token. If pairing
        // doesn't apply (e.g. a pre-issued bootstrap token was pasted), fall back to using it directly.
        var codeOrToken = doc.NewHostToken.Trim();
        var token = codeOrToken;
        if (!string.IsNullOrEmpty(codeOrToken))
        {
            try
            {
                doc.StatusText = "Pairing…";
                var deviceName = $"{Environment.MachineName} (desktop)";
                var paired = await Agnes.Client.DevicePairing.PairAsync(url, codeOrToken, deviceName);
                token = paired.Token;
            }
            catch
            {
                // Not a valid pairing code — treat the entry as a direct token.
            }
        }

        var host = new KnownHost(string.IsNullOrWhiteSpace(doc.NewHostName) ? url : doc.NewHostName.Trim(), url, token);
        _knownHosts.Add(host);
        _hostStore.Save(_knownHosts.Where(h => h.Url != SimulatedHost.Url && h.Url != RecordedHost.Url).ToList());
        _dispatcher.Post(() => doc.ShowAddHost = false);
        await SelectHostAsync(doc, host);
    }

    public async Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName, bool skipPermissions = false)
    {
        if (doc.Host is null)
        {
            return;
        }

        var info = await doc.Host.OpenSessionAsync(adapterId, WorkingDirectory, skipPermissions: skipPermissions);
        var view = await doc.Host.SubscribeAsync(info.SessionId);
        var title = ProjectTitle(info.WorkingDirectory, displayName);
        _dispatcher.Post(() =>
        {
            doc.AgentName = displayName;
            doc.AttachSession(CreateSession(doc.Host!, view, title));
            doc.Title = title;
            doc.Descriptor = new SessionDescriptor(
                doc.HostName, doc.Host!.HostUrl, doc.HostToken, info.SessionId, adapterId, title);
            SaveState();
        });
    }

    // A tab is named for the project it works on (its working-directory folder), not the agent —
    // the agent is shown in the status bar. Falls back to the agent name if there's no directory.
    private static string ProjectTitle(string workingDirectory, string fallback)
    {
        var name = Path.GetFileName(workingDirectory.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
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

        RefreshSessions();
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
                doc.AttachSession(CreateSession(host, view, descriptor.Title));
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
            doc.Usage = host.Usage;
        });
        host.StateChanged += state => _dispatcher.Post(() => doc.ConnectionState = state);
        host.UsageChanged += usage => _dispatcher.Post(() => { doc.UsageSummary = usage; doc.Usage = host.Usage; });

        if (_inboxHosts.Add(host))
        {
            host.InboxRunReceived += run => _dispatcher.Post(() => AddInboxRun(run));
            _ = LoadInboxAsync(host);
        }
    }

    // ---- background-run inbox (across hosts) ----

    private readonly HashSet<IAgnesHost> _inboxHosts = [];
    private readonly HashSet<string> _inboxIds = [];

    public System.Collections.ObjectModel.ObservableCollection<InboxRun> Inbox { get; } = [];
    public int InboxCount => Inbox.Count;
    public bool HasInbox => Inbox.Count > 0;

    private void AddInboxRun(InboxRun run)
    {
        if (_inboxIds.Add(run.Id))
        {
            Inbox.Insert(0, run);
            OnPropertyChanged(nameof(InboxCount));
            OnPropertyChanged(nameof(HasInbox));
        }
    }

    private async Task LoadInboxAsync(IAgnesHost host)
    {
        try
        {
            var runs = await host.GetInboxAsync();
            _dispatcher.Post(() =>
            {
                foreach (var run in runs)
                {
                    AddInboxRun(run);
                }
            });
        }
        catch
        {
            // best-effort
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
            .Select(Snapshot)
            .ToList() ?? [];
        _tabStore.Save(tabs);
    }

    // Rebuilds a descriptor from the tab's current metadata so rename/pin/tag persist.
    private static SessionDescriptor Snapshot(SessionDocument doc)
        => doc.Descriptor! with
        {
            Title = string.IsNullOrWhiteSpace(doc.Title) ? doc.Descriptor!.Title : doc.Title!,
            Pinned = doc.Pinned,
            Tags = doc.Tags.Count > 0 ? doc.Tags.ToList() : null,
        };

    // ---- session management: persist / archive / duplicate / fork ----

    public void PersistTabs() => _dispatcher.Post(SaveState);

    public void ArchiveTab(SessionDocument doc)
    {
        if (doc.Descriptor is not null)
        {
            ArchivedSessions.Insert(0, Snapshot(doc));
            _archiveStore.Save(ArchivedSessions.ToList());
        }

        _factory.CloseDockable(doc);
        SaveState();
    }

    public void ReopenArchived(SessionDescriptor descriptor)
    {
        ArchivedSessions.Remove(descriptor);
        _archiveStore.Save(ArchivedSessions.ToList());

        var doc = new SessionDocument(this)
        {
            Title = descriptor.Title,
            CanClose = true,
            Descriptor = descriptor,
            HostName = descriptor.HostName,
            AgentName = descriptor.Title,
            Pinned = descriptor.Pinned,
        };
        ApplyTags(doc, descriptor.Tags);
        AddDocument(doc);
        _ = ReconnectAsync(doc, descriptor);
    }

    public async Task DuplicateAsync(SessionDocument doc)
    {
        if (doc.Host is null || doc.Descriptor is not { } descriptor)
        {
            return;
        }

        var copy = new SessionDocument(this)
        {
            Title = $"{doc.Title} (view)",
            CanClose = true,
            HostName = doc.HostName,
            AgentName = doc.AgentName,
        };
        ApplyTags(copy, doc.Tags.ToList());
        AddDocument(copy);

        try
        {
            copy.Host = doc.Host;
            copy.HostToken = doc.HostToken;
            WireStatus(copy, doc.Host);

            // Same session id → a second live client view of the same conversation.
            var view = await doc.Host.SubscribeAsync(descriptor.SessionId);
            _dispatcher.Post(() =>
            {
                copy.AttachSession(CreateSession(doc.Host!, view, copy.Title!));
                copy.Descriptor = descriptor with { Title = copy.Title! };
                SaveState();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => copy.StatusText = "Error: " + ex.Message);
        }
    }

    public async Task ForkAsync(SessionDocument doc)
    {
        if (doc.Host is null || doc.Descriptor is not { } descriptor)
        {
            return;
        }

        var fork = new SessionDocument(this)
        {
            Title = $"{doc.Title} (fork)",
            CanClose = true,
            HostName = doc.HostName,
            AgentName = doc.AgentName,
        };
        ApplyTags(fork, doc.Tags.ToList());
        AddDocument(fork);

        try
        {
            fork.Host = doc.Host;
            fork.HostToken = doc.HostToken;
            WireStatus(fork, doc.Host);

            // New session on the same host/agent, isolated in its own git worktree so the two
            // sessions can run in parallel without colliding.
            var info = await doc.Host.OpenSessionAsync(descriptor.AdapterId, WorkingDirectory, useWorktree: true);
            var view = await doc.Host.SubscribeAsync(info.SessionId);
            _dispatcher.Post(() =>
            {
                fork.AttachSession(CreateSession(doc.Host!, view, fork.Title!));
                fork.Descriptor = descriptor with { SessionId = info.SessionId, Title = fork.Title! };
                SaveState();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => fork.StatusText = "Error: " + ex.Message);
        }
    }
}
