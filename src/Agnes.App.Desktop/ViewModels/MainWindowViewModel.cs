using System.Collections.ObjectModel;
using Agnes.Abstractions.Events;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.Keymaps;
using Agnes.App.Desktop.Plugins;
using Agnes.App.Desktop.Themes;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Onboarding;
using Agnes.Ui.Core.Plugins;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using FluentIcons.Common;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// Owns the tabbed dock and acts as each tab's controller. Host is a per-tab choice: a new tab
/// picks a host (from the known-host registry, including the built-in simulated host, or a newly
/// added one), then an agent on that host, then opens a session. Uses <see cref="IAgnesConnector"/>
/// so simulated and real hosts work the same way. Open tabs auto-reconnect on relaunch.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, ITabController
{
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
    private readonly KeymapService _keymap;
    private readonly ModelFavoritesStore _modelFavorites;
    private readonly IOnboardingStore _onboarding;
    private readonly DockFactory _factory;
    private readonly List<KnownHost> _knownHosts = [];
    private AppSettings _settings;
    private bool _ready;

    /// <summary>Surfaces session notifications (toast / OS). Set by the shell once a window exists.</summary>
    public INotifier Notifier { get; set; } = NullNotifier.Instance;

    private ClientPluginSet? _clientPlugins;

    /// <summary>The reconciliation from the last successful capability negotiation (empty until one runs) —
    /// consumers gate two-sided features on entries reported <see cref="CapabilitySupport.Both"/>.</summary>
    public IReadOnlyList<NegotiatedCapability> NegotiatedCapabilities { get; private set; } = [];

    /// <summary>On connect, compose this client's plugins (built-in + any dynamic ones) and advertise them
    /// to the host, keeping the reconciled result. Best-effort: a host that predates negotiation returns an
    /// empty reconciliation, and any failure here must never break the connection.</summary>
    private async Task NegotiateCapabilitiesAsync(IAgnesHost host)
    {
        try
        {
            var caps = DesktopClientPlugins.Capabilities(Environment.MachineName, EnsureClientPlugins());
            var result = await host.NegotiateAsync(caps);
            _dispatcher.Post(() => NegotiatedCapabilities = result.Capabilities);
        }
        catch
        {
            // Negotiation is additive and best-effort; ignore failures.
        }
    }

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
        IPermissionPolicy? policy = null,
        IOnboardingStore? onboarding = null,
        KeymapService? keymap = null)
    {
        _connector = connector;
        _dispatcher = dispatcher;
        _tabStore = tabStore;
        _archiveStore = archiveStore ?? new SessionStateStore(SessionStateStore.DefaultPath().Replace("desktop-tabs.json", "desktop-archive.json"));
        _hostStore = hostStore;
        _prompts = prompts ?? new FilePromptStore();
        _policy = policy ?? new FilePermissionPolicy();
        _settingsStore = settingsStore ?? new SettingsStore();
        _keymap = keymap ?? KeymapService.CreateDefault(_settingsStore.FilePath, watch: false);
        _settings = _settingsStore.Load();
        _modelFavorites = new ModelFavoritesStore();
        _onboarding = onboarding ?? new FileOnboardingStore();

        // First-run setup wizard: sequences the client's existing pairing/auth flows over whichever methods a
        // host actually advertises at GET /auth/methods. It never appears once any real host is paired.
        SetupWizard = new SetupWizardViewModel(
            _onboarding,
            (url, ct) => AuthDiscovery.GetMethodsAsync(url, cancellationToken: ct),
            () => _knownHosts.Any(h => IsForgettableHost(h.Url)));
        SetupWizard.MethodChosen += OnWizardMethodChosen;
        SetupWizard.Dismissed += () => IsSetupWizardOpen = false;

        // Onboarding showcase: a data-driven, shown-once feature tour (also reachable manually), keyed by app
        // version so it can double as a future "what's new" surface.
        Showcase = new ShowcaseViewModel(OnboardingCards.Default, _onboarding, AppVersion);
        ShowOnboardingCommand = new RelayCommand(() => Showcase.Show());
        OpenDeploymentDocsCommand = new RelayCommand(() => OpenExternalUrl(SetupWizardViewModel.DeploymentDocsUrl));

        // Cross-session approvals (notifications/02 tier 1): unions open permission requests across every
        // connected host, newest first, with jump-to-session. Constructed before the layout is built so an
        // early attention refresh can safely poke it.
        Approvals = new ApprovalsViewModel(SnapshotHosts, _dispatcher);
        Approvals.JumpRequested += JumpToApproval;

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
        CloseActiveTabCommand = new AsyncRelayCommand(CloseActiveTabAsync);
        ToggleReducedMotionCommand = new RelayCommand(() => ReducedMotion = !ReducedMotion);
        SetThemeCommand = new RelayCommand<string>(t => { if (t is not null) { Theme = t; } });
        // Mark the persisted theme as selected, so the picker opens showing what's actually applied.
        foreach (var option in Themes)
        {
            option.Refresh(_settings.Theme);
        }
        LoadDevicesCommand = new AsyncRelayCommand(LoadDevicesAsync);
        RevokeDeviceCommand = new AsyncRelayCommand<DeviceRowVm>(RevokeDeviceAsync);
        ApproveDeviceCommand = new AsyncRelayCommand<string>(id => DecideApprovalAsync(id, approve: true));
        DenyDeviceCommand = new AsyncRelayCommand<string>(id => DecideApprovalAsync(id, approve: false));
        LoadMcpServersCommand = new AsyncRelayCommand(LoadMcpServersAsync);
        AddMcpServerCommand = new AsyncRelayCommand(AddMcpServerAsync);
        RemoveMcpServerCommand = new AsyncRelayCommand<string>(RemoveMcpServerAsync);
        ToggleMcpServerCommand = new AsyncRelayCommand<McpServerInfo>(ToggleMcpServerAsync);
        LoadMcpPresetsCommand = new AsyncRelayCommand(LoadMcpPresetsAsync);
        InstallMcpPresetCommand = new AsyncRelayCommand<McpPresetRowVm>(InstallMcpPresetAsync);
        PreviewMcpCommand = new AsyncRelayCommand(PreviewMcpAsync);
        LoadMcpCommand = new AsyncRelayCommand(LoadMcpAsync);
        SearchMcpCatalogCommand = new AsyncRelayCommand(SearchMcpCatalogAsync);
        InstallMcpCatalogEntryCommand = new AsyncRelayCommand<McpCatalogRowVm>(InstallMcpCatalogEntryAsync);
        StartFromLaunchProfileCommand = new AsyncRelayCommand<LaunchProfileRowVm>(StartFromLaunchProfileAsync);
        SetMcpApprovalCommand = new RelayCommand<string>(v => { if (v is not null) { McpApproval = v; } });
        LoadCredentialStatusCommand = new AsyncRelayCommand(LoadCredentialStatusAsync);
        ConnectGitHubCommand = new AsyncRelayCommand(ConnectGitHubAsync);
        UnlinkGitHubCommand = new AsyncRelayCommand<GitHubAccountRowVm>(UnlinkGitHubAsync);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenDashboardCommand = new RelayCommand(OpenDashboard);
        SetSettingsCategoryCommand = new RelayCommand<string>(v => { if (v is not null) { SettingsCategory = v; } });
        LinkGitHubNowCommand = new RelayCommand(LinkGitHubNow);
        DismissGitHubLinkPromptCommand = new RelayCommand(() => ShowGitHubLinkPrompt = false);
        LoadSandboxesCommand = new AsyncRelayCommand(LoadSandboxesAsync);
        DeleteSandboxRecordCommand = new AsyncRelayCommand<SandboxRowVm>(DeleteSandboxRecordAsync);
        ResumeSandboxRecordCommand = new AsyncRelayCommand<SandboxRowVm>(ResumeSandboxRecordAsync);
        FindOrphansCommand = new AsyncRelayCommand(FindOrphansAsync);
        ReapOrphansCommand = new AsyncRelayCommand(ReapOrphansAsync);
        PauseAutomationCommand = new AsyncRelayCommand<AutomationRow>(PauseAutomationAsync);
        ResumeAutomationCommand = new AsyncRelayCommand<AutomationRow>(ResumeAutomationAsync);
        RunAutomationNowCommand = new AsyncRelayCommand<AutomationRow>(RunAutomationNowAsync);
        RemoveAutomationCommand = new AsyncRelayCommand<AutomationRow>(RemoveAutomationAsync);
        LoadProjectsCommand = new AsyncRelayCommand(LoadProjectsAsync);
        SelectProjectCommand = new RelayCommand<ProjectDto>(SelectProject);
        SaveProjectCommand = new AsyncRelayCommand(SaveProjectAsync);
        AddProjectMcpCommand = new RelayCommand(AddProjectMcp);
        RemoveProjectMcpCommand = new RelayCommand<McpServerInfo>(m => { if (m is not null) { ProjectMcp.Remove(m); } });
        SettingsCategories =
        [
            // This device (client-global)
            new SettingsCategoryVm("appearance", "Appearance", Symbol.PaintBrush, "theme dark light system ui scale zoom accessibility reduce motion font density"),
            new SettingsCategoryVm("keymap", "Keymap", Symbol.Keyboard, KeymapSearchKeywords),
            // The connected host
            new SettingsCategoryVm("github", "GitHub accounts", Symbol.BranchFork, "github git push credential token connect app scope repo installation secret account"),
            new SettingsCategoryVm("devices", "Devices", Symbol.Key, "paired devices pairing token revoke auth access per-device"),
            new SettingsCategoryVm("sandboxes", "Sandboxes", Symbol.Box, "sandbox vm incus running stopped resume restart delete reap orphan cleanup lifecycle"),
            new SettingsCategoryVm("mcp", "MCP servers", Symbol.PlugConnected, "mcp model context protocol server tool preset install curated playwright github context7 scope workspace host preview effective strict"),
            // Per-project
            new SettingsCategoryVm("projects", "Projects", Symbol.Folder, "project repo sandbox image mcp servers packages node apt npm pip agents credentials defaults per-repo"),
            new SettingsCategoryVm("plugins", "Plugins", Symbol.PuzzlePiece, "plugin plugins extension nuget install uninstall browse marketplace capability consent provider adapter transport voice notification enable disable configure"),
            // Help
            new SettingsCategoryVm("bugreport", "Report a bug", Symbol.Bug, "bug report issue github feedback problem crash diagnostics support help"),
            new SettingsCategoryVm("prompts", "Prompts", Symbol.Note, "prompt prompts template templates slash token library saved snippet reuse review insert send"),
            new SettingsCategoryVm("profiles", "Launch profiles", Symbol.Rocket, "launch profile profiles preset saved reusable session config agent permissions sandbox model new session"),
            // "friend" stays in the keywords though it's gone from the UI: it's what this was called, and
            // someone who remembers the old name should still find the page rather than conclude it's gone.
            new SettingsCategoryVm("collaborators", "Collaborators", Symbol.Handshake, "collaborator collaborators friend friends social contact colleague github handle org organization team eligible grant access share revoke"),
        ];
        SettingsCategories[0].IsSelected = true;
        SetNewMcpRunAtCommand = new RelayCommand<string>(v => { if (v is not null) { NewMcpRunAt = v; } });
        SetNewMcpTransportCommand = new RelayCommand<string>(v => { if (v is not null) { NewMcpTransport = v; } });
        LoadSandboxImageCommand = new AsyncRelayCommand(LoadSandboxImageAsync);
        SaveSandboxImageCommand = new AsyncRelayCommand(SaveSandboxImageAsync);
        RebuildSandboxImageCommand = new AsyncRelayCommand(RebuildSandboxImageAsync);
        NextTabCommand = new RelayCommand(() => CycleTab(1));
        PrevTabCommand = new RelayCommand(() => CycleTab(-1));
        ActivateTabByIndexCommand = new RelayCommand<string>(ActivateTabByIndex);
        TogglePaletteCommand = new RelayCommand(() => IsPaletteOpen = !IsPaletteOpen);
        RunPaletteItemCommand = new RelayCommand<PaletteItem>(RunPaletteItem);
        RunTopPaletteItemCommand = new RelayCommand(RunSelectedPaletteItem);
        MovePaletteSelectionCommand = new RelayCommand<string>(MovePaletteSelection);
        ClosePaletteCommand = new RelayCommand(() => IsPaletteOpen = false);
        OpenUpdateCommand = new RelayCommand(OpenUpdate);
        SetScaleCommand = new RelayCommand<string>(s =>
        {
            FontScale = s switch { "small" => 0.9, "large" => 1.2, _ => 1.0 };
        });
        EditKeymapCommand = new AsyncRelayCommand(EditKeymapAsync);
        _keymap.Changed += OnKeymapChanged;
        _keymap.StatusChanged += OnKeymapStatusChanged;
        RebuildKeymapGroups(string.Empty);
        _factory.ActiveDockableChanged += (_, e) =>
        {
            UpdateWindowTitle();

            // Read state: the newly-focused session becomes active (marks read); the previous one goes
            // inactive so background activity can make it unread again.
            var active = e.Dockable as SessionDocument;
            foreach (var doc in AllDocuments())
            {
                doc.Session?.SetActive(ReferenceEquals(doc, active));
            }

            // Client navigation: a plugin can track which session the user is viewing (observe-only).
            if (active is { Session.SessionId: { } sid })
            {
                _ = EnsureClientPlugins().EventBus.DispatchAsync(new SessionActivatedEvent(sid));
            }
        };

        Plugins = new PluginManagementViewModel(ActiveHost, _dispatcher);

        MemorySearch = new MemorySearchViewModel(ActiveHost, _dispatcher);
        MemorySearch.OpenRequested += OpenMemoryResult;
        OpenSearchCommand = new RelayCommand(OpenSearch);
        BugReport = new BugReportViewModel(ActiveHost, _dispatcher, OpenInBrowser);
        PromptLibrary = new PromptLibraryViewModel(ActiveHost, _dispatcher);
        LaunchProfiles = new LaunchProfilesViewModel(ActiveHost, _dispatcher);
        Collaborators = new CollaboratorsViewModel(ActiveHost, _dispatcher, GrantTargets);
        MultiHost = new MultiHostViewModel(_connector, _dispatcher);
    }

    /// <summary>The multi-server surface (connectivity/02): the merged host list and cross-host session
    /// aggregate over every connected host, across whatever transports reach them.</summary>
    public MultiHostViewModel MultiHost { get; }

    /// <summary>The plugin-management surface for the active host (Browse / install / configure / enable).</summary>
    public PluginManagementViewModel Plugins { get; }

    /// <summary>Host-backed transcript search across every recorded session (the Search tab).</summary>
    public MemorySearchViewModel MemorySearch { get; }

    public IRelayCommand OpenSearchCommand { get; }

    /// <summary>The "Report a bug" surface for the active host (falls back to the public GitHub flow).</summary>
    public BugReportViewModel BugReport { get; }

    /// <summary>Opens a URL in the user's default browser (best-effort; used by the update + bug-report flows).</summary>
    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Opening the browser is best-effort.
        }
    }
    /// <summary>The prompt-library surface for the active host (saved prompts + slash-token templates).</summary>
    public PromptLibraryViewModel PromptLibrary { get; }

    /// <summary>The launch-profiles management surface for the active host (list / rename / delete).</summary>
    public LaunchProfilesViewModel LaunchProfiles { get; }

    /// <summary>The collaborators &amp; access-grants surface for the active host (owner-only; collaboration/01).</summary>
    public CollaboratorsViewModel Collaborators { get; }

    public IRelayCommand RunTopPaletteItemCommand { get; }
    public IRelayCommand ClosePaletteCommand { get; }

    // ---- update check (GitHub Releases) ----

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateVersion = string.Empty;

    private string? _updateUrl;

    public IRelayCommand OpenUpdateCommand { get; private set; } = null!;

    /// <summary>Best-effort background check; surfaces a top-bar "Update" button when a newer release exists.</summary>
    public async Task CheckForUpdatesAsync()
    {
        var current = typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var info = await UpdateCheck.CheckAsync(current);
        if (info is { IsNewer: true })
        {
            _dispatcher.Post(() =>
            {
                _updateUrl = info.Url;
                UpdateVersion = info.Version;
                UpdateAvailable = true;
                Notifier.Notify(new AppNotification("Update available", $"Agnes {info.Version} is available — click Update to download.", NotificationKind.Completion, string.Empty));
            });
        }
    }

    private void OpenUpdate()
    {
        if (_updateUrl is { Length: > 0 } url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Opening the browser is best-effort.
            }
        }
    }

    public IAsyncRelayCommand CloseActiveTabCommand { get; }
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
        // The dashboard mirrors the open tabs, so it re-reads them whenever the strip changes.
        Dashboard?.Rebuild();
    }

    private void RaiseAttention()
    {
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HasAttention));
        // A permission request appearing/clearing is what flips a session's attention flag, so this is the
        // natural moment to re-query the cross-session approvals list.
        _ = Approvals.LoadAsync();
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

    /// <summary>The selected theme's id (see <see cref="ThemeCatalog"/>). Applies immediately and
    /// persists; an unrecognised id resolves to System rather than leaving the app themeless.</summary>
    public string Theme
    {
        get => _settings.Theme;
        set
        {
            if (!string.Equals(value, _settings.Theme, StringComparison.Ordinal))
            {
                _settings = _settings with { Theme = value };
                _settingsStore.Save(_settings);
                ApplyTheme(value);
                OnPropertyChanged();
                foreach (var option in Themes)
                {
                    option.Refresh(value);
                }
            }
        }
    }

    /// <summary>Every theme on offer, as picker rows. Built once; each row tracks its own selection.</summary>
    public IReadOnlyList<ThemeOption> Themes { get; }
        = [.. ThemeCatalog.All.Select(t => new ThemeOption(t.Id, t.Name))];

    public IRelayCommand<string> SetThemeCommand { get; }

    /// <summary>Whole-UI zoom (accessibility/density), 0.9–1.3. Applied via a layout transform.</summary>
    public double FontScale
    {
        get => _settings.FontScale;
        set
        {
            var clamped = Math.Clamp(value, 0.8, 1.5);
            if (Math.Abs(clamped - _settings.FontScale) > 0.001)
            {
                _settings = _settings with { FontScale = clamped };
                _settingsStore.Save(_settings);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsScaleSmall));
                OnPropertyChanged(nameof(IsScaleDefault));
                OnPropertyChanged(nameof(IsScaleLarge));
            }
        }
    }

    public bool IsScaleSmall => FontScale < 0.95;
    public bool IsScaleDefault => FontScale is >= 0.95 and <= 1.05;
    public bool IsScaleLarge => FontScale > 1.05;
    public IRelayCommand<string> SetScaleCommand { get; private set; } = null!;

    /// <summary>The window title — reflects the active session/project so alt-tab and taskbar read well.</summary>
    [ObservableProperty]
    private string _windowTitle = "Agnes";

    private void UpdateWindowTitle()
    {
        var title = (_factory.DocumentDock?.ActiveDockable as SessionDocument)?.Title;
        WindowTitle = string.IsNullOrWhiteSpace(title) || title == "New session" ? "Agnes" : $"{title} — Agnes";
    }


    /// <summary>Applies a theme by id. The catalogue and the swap live in <see cref="Themes.ThemeManager"/>,
    /// since a flavour has to move Fluent's palette as well as the variant.</summary>
    public static void ApplyTheme(string theme) => Desktop.Themes.ThemeManager.Apply(theme);

    // ---- device management (for the active session's host) ----

    public ObservableCollection<DeviceRowVm> Devices { get; } = [];

    [ObservableProperty]
    private string _devicesStatus = "Open a session on a host to manage its paired devices.";

    public IAsyncRelayCommand LoadDevicesCommand { get; }
    public IAsyncRelayCommand<DeviceRowVm> RevokeDeviceCommand { get; }

    /// <summary>Devices asking to be let onto this host, waiting on a human here to vouch for them.</summary>
    public ObservableCollection<PendingPairApproval> PendingApprovals { get; } = [];

    public bool HasPendingApprovals => PendingApprovals.Count > 0;

    public IAsyncRelayCommand<string> ApproveDeviceCommand { get; }
    public IAsyncRelayCommand<string> DenyDeviceCommand { get; }

    /// <summary>
    /// What to put on a settings status line when a call to the host failed. The raw exception chain says
    /// "An error occurred while sending the request", which named the symptom and never the cause — a whole
    /// settings page of those is how a certificate problem reads as a mystery. This names the cause, and for a
    /// rejected certificate says what to do about it.
    /// </summary>
    private static string Explain(Exception ex) => Agnes.Client.AuthDiscovery.DescribeFailure(ex);

    private HostEndpoint? ActiveHttpHost()
    {
        static bool IsHttp(SessionDocument d) =>
            d.Host is { } h && h.HostUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        // Prefer the active session's host; but the Settings tab isn't a session, so fall back to ANY
        // connected http host among the open tabs — otherwise opening Settings dead-ends the GitHub/device/
        // sandbox management that needs a host.
        if (_factory.DocumentDock?.ActiveDockable is SessionDocument active && IsHttp(active))
        {
            return HostEndpoint.Of(active);
        }

        var any = AllDocuments().FirstOrDefault(IsHttp);
        return any is not null ? HostEndpoint.Of(any) : null;
    }

    /// <summary>The active host connection for hub-based management (plugins), preferring the active
    /// session's host and falling back to any connected host among the open tabs.</summary>
    private IAgnesHost? ActiveHost()
    {
        if (_factory.DocumentDock?.ActiveDockable is SessionDocument active && active.Host is { } h)
        {
            return h;
        }

        return AllDocuments().Select(d => d.Host).FirstOrDefault(x => x is not null);
    }

    private async Task LoadDevicesAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            _dispatcher.Post(() => { Devices.Clear(); DevicesStatus = "Open a session on a host to manage its paired devices."; });
            return;
        }

        try
        {
            DevicesStatus = "Loading…";
            var list = await DeviceManagement.ListAsync(target.Url, target.Token, target.Http);

            // Requests to join load on the same trip. A host that predates approval pairing simply
            // returns none, so this never turns an older host into an error.
            var waiting = await PairingManagement.PendingAsync(target.Url, target.Token, target.Http);

            var now = DateTimeOffset.UtcNow;
            _dispatcher.Post(() =>
            {
                Devices.Clear();
                foreach (var d in list) { Devices.Add(new DeviceRowVm(d, now)); }

                PendingApprovals.Clear();
                foreach (var p in waiting) { PendingApprovals.Add(p); }
                OnPropertyChanged(nameof(HasPendingApprovals));

                DevicesStatus = list.Count == 0 ? "No paired devices." : $"{list.Count} paired device(s).";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => DevicesStatus = "Couldn't load devices: " + Explain(ex));
        }
    }

    /// <summary>
    /// Answers a device's request to join. The digits shown beside it are derived from that device's own
    /// public key, so approving is only meaningful once a human has compared them against the asking
    /// device's screen — the UI says so, and there is no way to approve without seeing them.
    /// </summary>
    private async Task DecideApprovalAsync(string? requestId, bool approve)
    {
        var target = ActiveHttpHost();
        if (target is null || string.IsNullOrEmpty(requestId))
        {
            return;
        }

        try
        {
            if (approve)
            {
                await PairingManagement.ApproveAsync(target.Url, target.Token, requestId, target.Http);
            }
            else
            {
                await PairingManagement.DenyAsync(target.Url, target.Token, requestId, target.Http);
            }

            await LoadDevicesAsync();
            _dispatcher.Post(() => DevicesStatus = approve ? "Device approved." : "Request declined.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => DevicesStatus = "Couldn't answer that request: " + Explain(ex));
        }
    }

    /// <summary>
    /// Revoking cuts a device off for good, and the row you click might be the device you're sitting at, so
    /// the first click only arms the row — the second one commits. Arming a row disarms every other, so an
    /// armed button can't sit forgotten next to the one you meant to press.
    /// </summary>
    private async Task RevokeDeviceAsync(DeviceRowVm? row)
    {
        var target = ActiveHttpHost();
        if (target is null || row is null)
        {
            return;
        }

        if (!row.IsConfirmingRevoke)
        {
            foreach (var other in Devices)
            {
                other.IsConfirmingRevoke = ReferenceEquals(other, row);
            }

            DevicesStatus = row.IsCurrentDevice
                ? $"'{row.Name}' is the device you're using — revoking it signs this app out of {ActiveHostName}. Click again to confirm."
                : $"Click again to revoke '{row.Name}'. It will need to pair afresh to get back in.";
            return;
        }

        try
        {
            await DeviceManagement.RevokeAsync(target.Url, target.Token, row.Id, target.Http);
            _dispatcher.Post(() => DevicesStatus = $"Revoked '{row.Name}'.");
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                row.IsConfirmingRevoke = false;
                DevicesStatus = "Couldn't revoke: " + Explain(ex);
            });
        }
    }

    // ---- MCP server management (for the active session's host) ----

    public ObservableCollection<McpServerInfo> McpServers { get; } = [];

    [ObservableProperty]
    private string _mcpStatus = "Open a session on a host to manage its MCP servers.";

    // GitHub / credentials linking (per host — uses the active session's host, like MCP/devices).
    [ObservableProperty]
    private string _credentialStatus = "Open a session on a host to link GitHub.";

    public IAsyncRelayCommand LoadCredentialStatusCommand { get; }
    public IAsyncRelayCommand ConnectGitHubCommand { get; }
    public IAsyncRelayCommand<GitHubAccountRowVm> UnlinkGitHubCommand { get; }

    // ---- Settings tab (a first-class document, opened by the gear) ----
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand LinkGitHubNowCommand { get; }
    public IRelayCommand DismissGitHubLinkPromptCommand { get; }

    /// <summary>One-time onboarding: shown the first time a sandboxed session opens with no GitHub linked.</summary>
    [ObservableProperty]
    private bool _showGitHubLinkPrompt;

    /// <summary>Non-null while the "Fork session" dialog is open (target folder + copy-sandbox choice).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsForkPromptOpen))]
    private ForkPrompt? _forkPrompt;

    public bool IsForkPromptOpen => ForkPrompt is not null;

    // ---- onboarding: first-run setup wizard + feature showcase ----

    /// <summary>The first-run setup wizard (host address → discovered auth methods → existing flow).</summary>
    public SetupWizardViewModel SetupWizard { get; }

    /// <summary>The data-driven feature showcase / "what's new" surface.</summary>
    public ShowcaseViewModel Showcase { get; }

    /// <summary>Re-opens the onboarding showcase on demand (a help/about entry).</summary>
    public IRelayCommand ShowOnboardingCommand { get; }

    /// <summary>Opens the deployment docs in a browser for host-side setup the wizard links out to.</summary>
    public IRelayCommand OpenDeploymentDocsCommand { get; }

    /// <summary>Whether the setup-wizard overlay is visible. Progress persists even while hidden, so a cancel
    /// resumes cleanly next launch.</summary>
    [ObservableProperty]
    private bool _isSetupWizardOpen;

    /// <summary>This build's version — keys the shown-once showcase (a new version re-shows it as "what's new").</summary>
    private static string AppVersion =>
        typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString() ?? "dev";

    /// <summary>Evaluate first-run onboarding on startup: show the setup wizard when no host is paired, else the
    /// feature showcase the first time this version runs.</summary>
    private void EvaluateOnboarding()
    {
        if (SetupWizard.ShouldShow)
        {
            IsSetupWizardOpen = true;
        }
        else if (Showcase.ShouldAutoShow)
        {
            Showcase.Show();
        }
    }

    /// <summary>The wizard picked a sign-in method: hand off to the existing, fully-built add-host panel on a
    /// fresh tab, pre-filled and pointed at the chosen host, so the user completes pairing through the flow that
    /// already exists rather than a reimplementation. The wizard's persisted progress guards re-appearance.</summary>
    private void OnWizardMethodChosen(AuthMethodKind kind)
    {
        var doc = CreateTab();
        AddDocument(doc);
        doc.NewHostName = SetupWizard.HostName;
        doc.NewHostUrl = SetupWizard.HostUrl;
        if (kind == AuthMethodKind.Pairing && !string.IsNullOrWhiteSpace(SetupWizard.PairingCode))
        {
            doc.NewHostToken = SetupWizard.PairingCode;
        }

        doc.ShowAddHost = true;
        _ = DiscoverAuthMethodsAsync(doc);
        IsSetupWizardOpen = false;
    }

    private void LinkGitHubNow()
    {
        ShowGitHubLinkPrompt = false;
        OpenSettings();
        SettingsCategory = "github";
        _ = ConnectGitHubAsync();
    }

    // First-run nudge: when a session opens on a real (HTTP) host with no linked GitHub account, offer to
    // link once. The flag persists so it never nags again — GitHub can also be linked anytime in Settings.
    private async Task MaybePromptGitHubLinkAsync(SessionDocument doc)
    {
        if (_settings.GitHubPromptShown)
        {
            return;
        }

        var host = doc.Host?.HostUrl;
        if (string.IsNullOrEmpty(host) || !host.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var status = await CredentialManagement.GetStatusAsync(host, doc.HostToken, Agnes.Client.AgnesHttp.For(doc.Host?.PinnedFingerprint));
            var linked = status is not null && (status.State == "connected" || !string.IsNullOrWhiteSpace(status.Account));
            if (linked)
            {
                return;
            }

            _settings = _settings with { GitHubPromptShown = true };
            _settingsStore.Save(_settings);
            _dispatcher.Post(() => ShowGitHubLinkPrompt = true);
        }
        catch
        {
            // best-effort onboarding — never block a session open on it.
        }
    }
    public IRelayCommand<string> SetSettingsCategoryCommand { get; }
    public System.Collections.ObjectModel.ObservableCollection<SettingsCategoryVm> SettingsCategories { get; }

    [ObservableProperty] private string _settingsSearch = string.Empty;
    [ObservableProperty] private string _settingsCategory = "appearance";

    public bool CatAppearance => SettingsCategory == "appearance";
    public bool CatKeymap => SettingsCategory == "keymap";
    public bool CatGitHub => SettingsCategory == "github";
    public bool CatDevices => SettingsCategory == "devices";
    public bool CatSandboxes => SettingsCategory == "sandboxes";
    public bool CatMcp => SettingsCategory == "mcp";
    public bool CatProjects => SettingsCategory == "projects";
    public bool CatPlugins => SettingsCategory == "plugins";
    public bool CatBugReport => SettingsCategory == "bugreport";
    public bool CatPrompts => SettingsCategory == "prompts";
    public bool CatProfiles => SettingsCategory == "profiles";
    public bool CatCollaborators => SettingsCategory == "collaborators";

    /// <summary>The connected host these host-scoped settings apply to (e.g. GitHub, Devices, Projects).</summary>
    public string ActiveHostName => ActiveHttpHost() is { } t
        ? (_factory.DocumentDock?.ActiveDockable as SessionDocument)?.HostName ?? new Uri(t.Url).Host
        : "no connected host";

    partial void OnSettingsCategoryChanged(string value)
    {
        foreach (var c in SettingsCategories)
        {
            c.IsSelected = c.Id == value;
        }

        OnPropertyChanged(nameof(CatAppearance));
        OnPropertyChanged(nameof(CatKeymap));
        OnPropertyChanged(nameof(CatGitHub));
        OnPropertyChanged(nameof(CatDevices));
        OnPropertyChanged(nameof(CatSandboxes));
        OnPropertyChanged(nameof(CatMcp));
        OnPropertyChanged(nameof(CatProjects));
        OnPropertyChanged(nameof(CatPlugins));
        OnPropertyChanged(nameof(CatBugReport));
        OnPropertyChanged(nameof(CatPrompts));
        OnPropertyChanged(nameof(CatProfiles));
        OnPropertyChanged(nameof(CatCollaborators));
        OnPropertyChanged(nameof(ActiveHostName));
        // Opening a page IS the request to see what's on it. Nothing here should need a Refresh click to
        // show its contents for the first time — a page that opens blank tells you nothing about your host.
        if (value == "projects" && SelectedProject is null)
        {
            _ = LoadProjectsAsync();
        }
        else if (value == "sandboxes")
        {
            _ = LoadSandboxesAsync();
        }
        else if (value == "devices")
        {
            _ = LoadDevicesAsync();
        }
        else if (value == "github")
        {
            _ = LoadCredentialStatusAsync();
        }
        else if (value == "mcp")
        {
            _ = LoadMcpAsync();
        }
        else if (value == "plugins")
        {
            _ = Plugins.RefreshInstalledAsync();
        }
        else if (value == "prompts")
        {
            _ = PromptLibrary.RefreshAsync();
        }
        else if (value == "profiles")
        {
            _ = LaunchProfiles.RefreshAsync();
        }
        else if (value == "collaborators")
        {
            _ = Collaborators.RefreshAsync();
        }
        else if (value == "bugreport")
        {
            // Ask the host whether this caller may attach diagnostics, to show/hide the owner-only opt-in.
            _ = BugReport.RefreshCapabilitiesAsync();
        }
    }

    // ---- Sandboxes: the host's managed VMs (stop-on-close · resume · delete) ----
    public IAsyncRelayCommand LoadSandboxesCommand { get; }
    public IAsyncRelayCommand<SandboxRowVm> DeleteSandboxRecordCommand { get; }
    public IAsyncRelayCommand<SandboxRowVm> ResumeSandboxRecordCommand { get; }
    public IAsyncRelayCommand FindOrphansCommand { get; }
    public IAsyncRelayCommand ReapOrphansCommand { get; }

    public System.Collections.ObjectModel.ObservableCollection<SandboxRowVm> Sandboxes { get; } = [];
    public bool HasSandboxes => Sandboxes.Count > 0;

    public System.Collections.ObjectModel.ObservableCollection<string> OrphanVmNames { get; } = [];
    public bool HasOrphans => OrphanVmNames.Count > 0;
    public string ReapOrphansLabel => $"Delete {OrphanVmNames.Count} orphaned VM(s)";

    [ObservableProperty] private string _sandboxesStatus = "Open a session on a host to manage its sandboxes.";

    private async Task FindOrphansAsync()
    {
        var target = ActiveHttpHost();
        if (target is null) { return; }

        try
        {
            var orphans = await SandboxManagement.OrphansAsync(target.Url, target.Token, target.Http);
            _dispatcher.Post(() =>
            {
                OrphanVmNames.Clear();
                foreach (var o in orphans) { OrphanVmNames.Add(o); }
                OnPropertyChanged(nameof(HasOrphans));
                OnPropertyChanged(nameof(ReapOrphansLabel));
                SandboxesStatus = orphans.Count == 0
                    ? "No orphaned VMs — nothing to reap."
                    : $"Found {orphans.Count} orphaned VM(s) no session tracks. Review, then delete if you're sure.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxesStatus = "Couldn't scan for orphans: " + Explain(ex));
        }
    }

    private async Task ReapOrphansAsync()
    {
        var target = ActiveHttpHost();
        if (target is null) { return; }

        try
        {
            var reaped = await SandboxManagement.ReapAsync(target.Url, target.Token, target.Http);
            _dispatcher.Post(() =>
            {
                OrphanVmNames.Clear();
                OnPropertyChanged(nameof(HasOrphans));
                SandboxesStatus = $"Reaped {reaped} orphaned VM(s).";
            });
            await LoadSandboxesAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxesStatus = "Couldn't reap: " + Explain(ex));
        }
    }

    private async Task LoadSandboxesAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            _dispatcher.Post(() => { Sandboxes.Clear(); OnPropertyChanged(nameof(HasSandboxes)); SandboxesStatus = "Open a session on a host to manage its sandboxes."; });
            return;
        }

        try
        {
            var list = await SandboxManagement.ListAsync(target.Url, target.Token, target.Http);
            _dispatcher.Post(() =>
            {
                Sandboxes.Clear();
                foreach (var s in list) { Sandboxes.Add(new SandboxRowVm(s)); }
                OnPropertyChanged(nameof(HasSandboxes));
                SandboxesStatus = list.Count == 0
                    ? "No sandboxes yet — sandboxed sessions appear here (stopped ones stay until you delete them)."
                    : $"{list.Count} sandbox(es) on {ActiveHostName}.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxesStatus = "Couldn't load sandboxes: " + Explain(ex));
        }
    }

    private async Task ResumeSandboxRecordAsync(SandboxRowVm? sandbox)
    {
        var target = ActiveHttpHost();
        if (target is null || sandbox is null) { return; }

        // Already live → just jump to its open tab if we have one.
        if (sandbox.Live && OpenTabs().FirstOrDefault(d => d.Session?.SessionId == sandbox.SessionId) is { } open)
        {
            _factory.SetActiveDockable(open);
            return;
        }

        try
        {
            _dispatcher.Post(() => SandboxesStatus = $"Resuming '{sandbox.Title}'… (the VM cold-starts, a few seconds)");
            var info = await SandboxManagement.ResumeAsync(target.Url, target.Token, sandbox.SessionId, target.Http);
            if (info is null)
            {
                _dispatcher.Post(() => SandboxesStatus = "Resume failed — the host returned no session.");
                return;
            }

            _dispatcher.Post(() =>
            {
                // Open a tab attached to the resumed session (reuses the reconnect flow).
                var descriptor = new SessionDescriptor(ActiveHostName, target.Url, target.Token, info.SessionId, info.AdapterId, sandbox.Title);
                var doc = new SessionDocument(this, _dispatcher)
                {
                    Title = sandbox.Title,
                    CanClose = true,
                    Descriptor = descriptor,
                    HostName = ActiveHostName,
                    AgentName = sandbox.Title,
                };
                AddDocument(doc);
                _ = ReconnectAsync(doc, descriptor);
                SaveState();
                SandboxesStatus = $"Resumed '{sandbox.Title}'.";
            });
            await LoadSandboxesAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxesStatus = "Couldn't resume: " + Explain(ex));
        }
    }

    /// <summary>
    /// Deleting destroys a VM and everything in it, permanently — so, as with revoking a device, the first click
    /// arms the row and the second commits. Arming one row disarms the rest.
    /// </summary>
    private async Task DeleteSandboxRecordAsync(SandboxRowVm? row)
    {
        var target = ActiveHttpHost();
        if (target is null || row is null) { return; }

        if (!row.IsConfirmingDelete)
        {
            foreach (var other in Sandboxes)
            {
                other.IsConfirmingDelete = ReferenceEquals(other, row);
            }

            SandboxesStatus = $"Deleting '{row.Title}' destroys its VM and everything in it, permanently. Click again to confirm.";
            return;
        }

        try
        {
            var list = await SandboxManagement.DeleteAsync(target.Url, target.Token, row.SessionId, target.Http);
            _dispatcher.Post(() =>
            {
                Sandboxes.Clear();
                foreach (var s in list) { Sandboxes.Add(new SandboxRowVm(s)); }
                OnPropertyChanged(nameof(HasSandboxes));
                SandboxesStatus = $"Deleted the sandbox for '{row.Title}'.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                row.IsConfirmingDelete = false;
                SandboxesStatus = "Couldn't delete: " + Explain(ex);
            });
        }
    }

    private static string KeymapSearchKeywords { get; } = string.Join(' ',
        CommandCatalogue.All.SelectMany(d => new[] { d.Id, d.Description, d.ContextDisplay, d.Group })
            .Prepend("keymap keyboard shortcuts keys bindings gestures rebinding"));
    private static readonly HashSet<string> KeymapPageAliases = new(StringComparer.OrdinalIgnoreCase)
        { "keymap", "keyboard", "shortcut", "shortcuts", "key", "keys", "binding", "bindings", "gesture", "gestures", "rebinding" };

    public ObservableCollection<KeymapCommandGroup> KeymapGroups { get; } = [];
    public bool HasKeymapMatches => KeymapGroups.Count > 0;
    public string KeymapPath => _keymap.UserPath;
    public string KeymapStatus => _keymap.Status;
    public string KeymapDiagnostic => _keymap.Diagnostic?.ToString() ?? KeymapEditError;
    public bool HasKeymapDiagnostic => KeymapDiagnostic.Length > 0;
    public IAsyncRelayCommand EditKeymapCommand { get; }

    [ObservableProperty] private string _keymapEditError = string.Empty;

    partial void OnKeymapEditErrorChanged(string value)
    {
        OnPropertyChanged(nameof(KeymapDiagnostic));
        OnPropertyChanged(nameof(HasKeymapDiagnostic));
    }

    private async Task EditKeymapAsync()
    {
        try
        {
            KeymapEditError = string.Empty;
            await _keymap.EditAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => KeymapEditError = $"Couldn't open the keymap: {ex.Message}");
        }
    }

    private void OnKeymapChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        RebuildKeymapGroups(SettingsSearch.Trim());
        RebuildPalette();
        OnPropertyChanged(nameof(KeymapStatus));
        OnPropertyChanged(nameof(KeymapDiagnostic));
        OnPropertyChanged(nameof(HasKeymapDiagnostic));
        OnPropertyChanged(nameof(DashboardToolTip));
    });

    private void OnKeymapStatusChanged(object? sender, EventArgs e)
        => _dispatcher.Post(() => OnPropertyChanged(nameof(KeymapStatus)));

    public string DashboardToolTip
    {
        get
        {
            var gesture = GestureFor(AgnesCommand.DashboardOpen, KeymapContext.Window);
            return gesture == "Unassigned"
                ? "Status of every session, and anything waiting on you"
                : $"Status of every session, and anything waiting on you ({gesture})";
        }
    }

    private string GestureFor(AgnesCommand command, KeymapContext context)
        => _keymap.Effective.PrimaryGesture(command, context) is { } gesture
            ? KeyGestureParser.Display(gesture)
            : "Unassigned";

    private void RebuildKeymapGroups(string query)
    {
        if (KeymapPageAliases.Contains(query)) query = string.Empty;
        KeymapGroups.Clear();
        foreach (var group in CommandCatalogue.All.GroupBy(d => d.Group))
        {
            var rows = group.Select(definition =>
            {
                var rules = _keymap.Effective.Rules
                    .Where(r => r.Command == definition.Command)
                    .ToArray();
                var gestures = rules
                    .Select(r => KeyGestureParser.Display(r.Gesture))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                return new KeymapCommandRow(
                    definition.Command,
                    definition.Id,
                    definition.Description,
                    definition.ContextDisplay,
                    string.Join(" / ", gestures.DefaultIfEmpty("Unassigned")),
                    rules.LastOrDefault() is { } primary ? KeymapCommandRow.FormatJson(primary) : null);
            }).Where(row => query.Length == 0
                || row.CommandId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Context.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Gesture.Contains(query, StringComparison.OrdinalIgnoreCase))
              .ToArray();
            if (rows.Length > 0) KeymapGroups.Add(new KeymapCommandGroup(group.Key, rows));
        }

        OnPropertyChanged(nameof(HasKeymapMatches));
    }

    partial void OnSettingsSearchChanged(string value)
    {
        var query = (value ?? string.Empty).Trim();
        RebuildKeymapGroups(query);

        SettingsCategoryVm? firstMatch = null;
        foreach (var c in SettingsCategories)
        {
            c.IsVisible = c.Id == "keymap" ? HasKeymapMatches : c.Matches(query);
            firstMatch ??= c.IsVisible ? c : null;
        }

        // If the current category was filtered out by the search, jump to the first match.
        if (query.Length > 0 && firstMatch is not null
            && SettingsCategories.FirstOrDefault(c => c.Id == SettingsCategory) is { IsVisible: false })
        {
            SettingsCategory = firstMatch.Id;
        }
    }

    private void OpenSettings()
    {
        if (_factory.DocumentDock is not { } dock)
        {
            return;
        }

        var existing = dock.VisibleDockables?.OfType<SettingsDocument>().FirstOrDefault();
        if (existing is null)
        {
            existing = new SettingsDocument(this);
            _factory.AddDockable(dock, existing);
        }

        dock.ActiveDockable = existing;
        _factory.SetActiveDockable(existing);
        _factory.SetFocusedDockable(dock, existing);
    }

    /// <summary>
    /// Opens (or focuses) the status dashboard tab. It's optional by design — a tab you summon, not a panel
    /// that permanently competes with the session you're actually in — so nothing creates it until asked.
    /// </summary>
    private void OpenDashboard()
    {
        if (_factory.DocumentDock is not { } dock)
        {
            return;
        }

        var existing = dock.VisibleDockables?.OfType<DashboardDocument>().FirstOrDefault();
        if (existing is null)
        {
            existing = new DashboardDocument(new DashboardViewModel(this, _dispatcher, SnapshotHosts));
            _factory.AddDockable(dock, existing);
        }

        _ = existing.Dashboard.RefreshAsync();
        dock.ActiveDockable = existing;
        _factory.SetActiveDockable(existing);
        _factory.SetFocusedDockable(dock, existing);
    }

    /// <summary>The open dashboard's state, or null when the tab isn't open. Looked up rather than cached:
    /// closing the tab disposes its view model, and a cached reference to a disposed one is exactly the kind
    /// of stale state that keeps polling in the background.</summary>
    public DashboardViewModel? Dashboard
        => _factory.DocumentDock?.VisibleDockables?.OfType<DashboardDocument>().FirstOrDefault()?.Dashboard;

    /// <summary>Opens the status dashboard tab (from its configured key, the top bar, or the palette).</summary>
    public IRelayCommand OpenDashboardCommand { get; private set; } = null!;

    /// <summary>
    /// Joins a session picked on the dashboard: it belongs in a tab of its own (the dashboard is an overview,
    /// not a place to hold a conversation), so this opens a fresh tab on that session's host and attaches it
    /// there. An already-open session is focused instead.
    /// </summary>
    public async Task JoinFromDashboardAsync(CatalogSessionRow row)
    {
        if (ActivateSessionById(row.SessionId))
        {
            return;
        }

        var doc = CreateTab();
        doc.Host = row.Host;
        doc.HostName = _knownHosts.FirstOrDefault(h => h.Url == row.Host.HostUrl)?.Name ?? row.Host.HostUrl;
        doc.HostToken = _knownHosts.FirstOrDefault(h => h.Url == row.Host.HostUrl)?.Token ?? string.Empty;
        doc.HostFingerprint = FingerprintFor(row.Host.HostUrl);
        WireStatus(doc, row.Host);
        AddDocument(doc);

        await AttachCatalogSessionAsync(doc, row).ConfigureAwait(false);
        _dispatcher.Post(() => Dashboard?.Rebuild());
    }

    private void OpenSearch()
    {
        if (_factory.DocumentDock is not { } dock)
        {
            return;
        }

        var existing = dock.VisibleDockables?.OfType<SearchDocument>().FirstOrDefault();
        if (existing is null)
        {
            existing = new SearchDocument(MemorySearch);
            _factory.AddDockable(dock, existing);
        }

        dock.ActiveDockable = existing;
        _factory.SetActiveDockable(existing);
        _factory.SetFocusedDockable(dock, existing);
    }

    // Jump from a memory-search hit to its session. If that session is open as a tab, activate it (and
    // scroll to the newest match of the query so the user lands near the hit); otherwise say it isn't open —
    // opening a closed session straight from search is a follow-up (it needs a rebuilt session descriptor).
    private void OpenMemoryResult(MemorySearchResultRow row)
    {
        var doc = OpenTabs().FirstOrDefault(d => d.Session?.SessionId == row.SessionId);
        if (doc is null)
        {
            MemorySearch.Status = "That session isn't open — open it from the sessions list to view the match.";
            return;
        }

        _factory.SetActiveDockable(doc);
        foreach (var hit in doc.Session?.Find(MemorySearch.Query, doc.Title) ?? [])
        {
            doc.Session?.ScrollTo(hit.AnchorId);
            break;
        }
    }

    // ---- Projects: per-repo bundles on the connected host (sandbox + MCP + GitHub account + defaults) ----
    public IAsyncRelayCommand LoadProjectsCommand { get; }
    public IRelayCommand<ProjectDto> SelectProjectCommand { get; }
    public IAsyncRelayCommand SaveProjectCommand { get; }
    public IRelayCommand AddProjectMcpCommand { get; }
    public IRelayCommand<McpServerInfo> RemoveProjectMcpCommand { get; }

    public ObservableCollection<ProjectDto> Projects { get; } = [];
    public ObservableCollection<McpServerInfo> ProjectMcp { get; } = [];
    public ObservableCollection<string> GitHubAccounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedProject))]
    private ProjectDto? _selectedProject;

    [ObservableProperty] private string _projectsStatus = "Open a session on a host to manage its projects.";
    [ObservableProperty] private string _projName = string.Empty;
    [ObservableProperty] private bool _projNode;
    [ObservableProperty] private string _projApt = string.Empty;
    [ObservableProperty] private string _projNpm = string.Empty;
    [ObservableProperty] private string _projPip = string.Empty;
    // Sandbox resource overrides — blank means "inherit the host's default".
    [ObservableProperty] private string _projCpu = string.Empty;
    [ObservableProperty] private string _projMemoryGiB = string.Empty;
    [ObservableProperty] private string _projDiskGiB = string.Empty;
    [ObservableProperty] private string _projGitMode = "Ask";
    [ObservableProperty] private bool _projSkipPermissions;
    [ObservableProperty] private string _projMcpApproval = "Ask";
    [ObservableProperty] private string _projAccount = string.Empty;
    [ObservableProperty] private string _projRepo = string.Empty;

    public bool HasSelectedProject => SelectedProject is not null;

    private async Task LoadProjectsAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            _dispatcher.Post(() => { Projects.Clear(); ProjectsStatus = "Open a session on a host to manage its projects."; });
            return;
        }

        try
        {
            var list = await ProjectManagement.ListAsync(target.Url, target.Token, target.Http);
            var credentials = await CredentialManagement.GetStatusAsync(target.Url, target.Token, target.Http);
            _dispatcher.Post(() =>
            {
                Projects.Clear();
                foreach (var p in list) { Projects.Add(p); }
                GitHubAccounts.Clear();
                if (credentials?.Account is { Length: > 0 } accounts)
                {
                    foreach (var a in accounts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) { GitHubAccounts.Add(a); }
                }

                ProjectsStatus = list.Count == 0 ? "No projects yet — open a session in a repo and it becomes one." : $"{list.Count} project(s) on {ActiveHostName}.";
                if (list.Count > 0) { SelectProject(list.FirstOrDefault(p => p.Id == SelectedProject?.Id) ?? list[0]); }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => ProjectsStatus = "Couldn't load projects: " + Explain(ex));
        }
    }

    // The project the user asked to switch to while another one had unsaved edits. Clicking it again commits
    // to discarding those edits.
    private ProjectDto? _armedProjectSwitch;

    /// <summary>
    /// Whether the project editor holds changes that aren't on the host yet. Saving a project rebuilds a VM
    /// image, so it isn't done implicitly — which means switching projects, or refreshing, would otherwise throw
    /// the edits away without a word.
    /// </summary>
    public bool IsProjectDirty => SelectedProject is { } p && !EditorMatches(p);

    private bool EditorMatches(ProjectDto p)
        => ProjName == p.Name
           && ProjNode == p.Sandbox.Node
           && ProjApt == string.Join(' ', p.Sandbox.AptPackages)
           && ProjNpm == string.Join(' ', p.Sandbox.NpmGlobals)
           && ProjPip == string.Join(' ', p.Sandbox.PipPackages)
           && ProjCpu == (p.SandboxCpu?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
           && ProjMemoryGiB == (p.SandboxMemoryGiB?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
           && ProjDiskGiB == (p.SandboxDiskGiB?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
           && ProjGitMode == p.Defaults.GitCredentialMode
           && ProjSkipPermissions == p.Defaults.SkipPermissions
           && ProjMcpApproval == p.Defaults.McpApproval
           && ProjAccount == (p.CredentialAccount ?? string.Empty)
           && ProjRepo == (p.Repo ?? string.Empty)
           && ProjectMcp.Select(m => m.Id).SequenceEqual(p.McpServers.Select(m => m.Id), StringComparer.Ordinal);

    private void SelectProject(ProjectDto? project)
    {
        if (project is null) { return; }

        // Don't silently discard unsaved edits — say what would be lost and make the second click mean it.
        if (SelectedProject is { } current && IsProjectDirty)
        {
            var switching = project.Id != current.Id;
            if (switching && _armedProjectSwitch?.Id != project.Id)
            {
                _armedProjectSwitch = project;
                ProjectsStatus = $"'{current.Name}' has unsaved changes. Save project first, or click '{project.Name}' again to discard them.";
                return;
            }

            if (!switching)
            {
                // A reload landing on the project being edited: keep the edits rather than overwriting them.
                SelectedProject = project;
                return;
            }
        }

        _armedProjectSwitch = null;
        SelectedProject = project;
        ProjName = project.Name;
        ProjNode = project.Sandbox.Node;
        ProjApt = string.Join(' ', project.Sandbox.AptPackages);
        ProjNpm = string.Join(' ', project.Sandbox.NpmGlobals);
        ProjPip = string.Join(' ', project.Sandbox.PipPackages);
        ProjCpu = project.SandboxCpu?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        ProjMemoryGiB = project.SandboxMemoryGiB?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        ProjDiskGiB = project.SandboxDiskGiB?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        ProjGitMode = project.Defaults.GitCredentialMode;
        ProjSkipPermissions = project.Defaults.SkipPermissions;
        ProjMcpApproval = project.Defaults.McpApproval;
        ProjAccount = project.CredentialAccount ?? string.Empty;
        ProjRepo = project.Repo ?? string.Empty;
        ProjectMcp.Clear();
        foreach (var m in project.McpServers) { ProjectMcp.Add(m); }
    }

    /// <summary>True while a project save + sandbox-image rebuild is in flight, so the UI can disable Save
    /// and show progress instead of looking idle during a multi-minute operation (defect #8).</summary>
    [ObservableProperty]
    private bool _isSavingProject;

    private async Task SaveProjectAsync()
    {
        var target = ActiveHttpHost();
        if (target is null || SelectedProject is null || IsSavingProject) { return; }

        static IReadOnlyList<string> Split(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Blank or non-positive → inherit the host default (null on the wire).
        static int? PositiveOrNull(string s) => int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        var sandbox = SelectedProject.Sandbox with { Node = ProjNode, AptPackages = Split(ProjApt), NpmGlobals = Split(ProjNpm), PipPackages = Split(ProjPip) };
        var dto = SelectedProject with
        {
            Name = ProjName,
            Sandbox = sandbox,
            McpServers = ProjectMcp.ToArray(),
            CredentialAccount = string.IsNullOrWhiteSpace(ProjAccount) ? null : ProjAccount,
            Repo = string.IsNullOrWhiteSpace(ProjRepo) ? null : ProjRepo.Trim(),
            Defaults = new ProjectDefaultsDto(ProjSkipPermissions, ProjGitMode, ProjMcpApproval),
            SandboxCpu = PositiveOrNull(ProjCpu),
            SandboxMemoryGiB = PositiveOrNull(ProjMemoryGiB),
            SandboxDiskGiB = PositiveOrNull(ProjDiskGiB),
        };

        try
        {
            IsSavingProject = true;
            ProjectsStatus = $"Saving '{dto.Name}' — rebuilding its sandbox image, this can take a minute…";
            await ProjectManagement.SaveAsync(target.Url, target.Token, dto, target.Http);
            _dispatcher.Post(() =>
            {
                // The editor now matches the host again, so a switch no longer has anything to warn about.
                SelectedProject = dto;
                _armedProjectSwitch = null;
                ProjectsStatus = $"Saved '{dto.Name}' — its sandbox image is rebuilding.";
            });
            await LoadProjectsAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => ProjectsStatus = "Couldn't save: " + Explain(ex));
        }
        finally
        {
            _dispatcher.Post(() => IsSavingProject = false);
        }
    }

    private void AddProjectMcp()
    {
        if (string.IsNullOrWhiteSpace(NewMcpName)) { return; }
        var args = NewMcpArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ProjectMcp.Add(new McpServerInfo(
            Guid.NewGuid().ToString("n"), NewMcpName.Trim(), NewMcpRunAt, true, NewMcpTransport,
            NewMcpIsStdio ? NewMcpCommand : null, NewMcpIsStdio ? args : [], new Dictionary<string, string>(),
            NewMcpIsHttp ? NewMcpUrl : null, null));
        NewMcpName = string.Empty;
        NewMcpCommand = string.Empty;
        NewMcpArgs = string.Empty;
        NewMcpUrl = string.Empty;
    }

    private async Task LoadCredentialStatusAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            _dispatcher.Post(() =>
            {
                LinkedGitHubAccounts.Clear();
                OnPropertyChanged(nameof(HasLinkedGitHubAccounts));
                CredentialStatus = "Open a session on a host to link GitHub.";
            });
            return;
        }

        try
        {
            var status = await CredentialManagement.GetStatusAsync(target.Url, target.Token, target.Http);
            _dispatcher.Post(() =>
            {
                ApplyGitHubAccounts(status);
                CredentialStatus = status switch
                {
                    { Installed: true } => $"GitHub connected ({status.Slug}). Sandboxed pushes mint scoped tokens.",
                    { State: "app-created" } => "GitHub app created — finish installing it to enable pushes.",
                    _ => "GitHub not linked. Sandboxed git push needs a linked account.",
                };
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => CredentialStatus = "Couldn't load credential status: " + Explain(ex));
        }
    }

    /// <summary>
    /// Fills both views of the same fact: the account names the Projects page picks from, and the rows the
    /// GitHub page lists with an Unlink beside each. Reads the typed <see cref="CredentialStatus.Accounts"/>
    /// where the host offers it and falls back to splitting the older comma-joined field otherwise.
    /// </summary>
    private void ApplyGitHubAccounts(CredentialStatus? status)
    {
        var accounts = status?.Accounts
            ?? status?.Account?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            ?? [];

        GitHubAccounts.Clear();
        LinkedGitHubAccounts.Clear();
        foreach (var a in accounts)
        {
            GitHubAccounts.Add(a);
            LinkedGitHubAccounts.Add(new GitHubAccountRowVm(a));
        }

        OnPropertyChanged(nameof(HasLinkedGitHubAccounts));
    }

    /// <summary>The GitHub accounts linked on the connected host, each unlinkable.</summary>
    public ObservableCollection<GitHubAccountRowVm> LinkedGitHubAccounts { get; } = [];

    public bool HasLinkedGitHubAccounts => LinkedGitHubAccounts.Count > 0;

    /// <summary>
    /// Unlinks a GitHub account from the host. Two-step for the same reason revoking a device is: it drops the
    /// App private key, so every sandboxed push through that account stops working and the only way back is to
    /// run the connect flow again.
    /// </summary>
    private async Task UnlinkGitHubAsync(GitHubAccountRowVm? row)
    {
        var target = ActiveHttpHost();
        if (target is null || row is null)
        {
            return;
        }

        if (!row.IsConfirmingUnlink)
        {
            foreach (var other in LinkedGitHubAccounts)
            {
                other.IsConfirmingUnlink = ReferenceEquals(other, row);
            }

            CredentialStatus = $"Unlinking '{row.Account}' stops sandboxed pushes that use it, and re-linking means running Connect again. Click again to confirm.";
            return;
        }

        try
        {
            var removed = await CredentialManagement.DisconnectGitHubAsync(target.Url, target.Token, row.Account, target.Http);
            if (!removed)
            {
                _dispatcher.Post(() =>
                {
                    row.IsConfirmingUnlink = false;
                    CredentialStatus = $"The host didn't have '{row.Account}' linked (or is too old to unlink accounts).";
                });
                return;
            }

            await LoadCredentialStatusAsync();
            _dispatcher.Post(() => CredentialStatus = $"Unlinked '{row.Account}'.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                row.IsConfirmingUnlink = false;
                CredentialStatus = "Couldn't unlink: " + Explain(ex);
            });
        }
    }

    private async Task ConnectGitHubAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            CredentialStatus = "Open a session on a host first, then Connect GitHub.";
            return;
        }

        try
        {
            var url = await CredentialManagement.ConnectGitHubAsync(target.Url, target.Token, target.Http);
            if (url is { Length: > 0 })
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                _dispatcher.Post(() => CredentialStatus = "Continue in your browser: create the app, then choose repositories.");
            }
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => CredentialStatus = "Couldn't start GitHub connect: " + Explain(ex));
        }
    }

    // New-server form fields.
    [ObservableProperty] private string _newMcpName = string.Empty;
    [ObservableProperty] private string _newMcpRunAt = "host";       // "host" | "sandbox"
    [ObservableProperty] private string _newMcpTransport = "stdio";  // "stdio" | "http"
    [ObservableProperty] private string _newMcpCommand = string.Empty;
    [ObservableProperty] private string _newMcpArgs = string.Empty;  // space-separated
    [ObservableProperty] private string _newMcpUrl = string.Empty;

    public bool NewMcpIsStdio => NewMcpTransport == "stdio";
    public bool NewMcpIsHttp => NewMcpTransport == "http";
    public bool NewMcpRunAtHost => NewMcpRunAt == "host";
    public bool NewMcpRunAtSandbox => NewMcpRunAt == "sandbox";

    partial void OnNewMcpTransportChanged(string value)
    {
        OnPropertyChanged(nameof(NewMcpIsStdio));
        OnPropertyChanged(nameof(NewMcpIsHttp));
    }

    partial void OnNewMcpRunAtChanged(string value)
    {
        OnPropertyChanged(nameof(NewMcpRunAtHost));
        OnPropertyChanged(nameof(NewMcpRunAtSandbox));
    }

    /// <summary>Gating posture for MCP tools Agnes proxies: "Ask" (prompt on first use) or "Trust".</summary>
    public string McpApproval
    {
        get => _settings.McpApproval;
        set
        {
            if (!string.Equals(value, _settings.McpApproval, StringComparison.Ordinal))
            {
                _settings = _settings with { McpApproval = value };
                _settingsStore.Save(_settings);
                OnPropertyChanged();
                OnPropertyChanged(nameof(McpApprovalAsk));
                OnPropertyChanged(nameof(McpApprovalTrust));
            }
        }
    }

    public bool McpApprovalAsk => McpApproval != "Trust";
    public bool McpApprovalTrust => McpApproval == "Trust";
    public IRelayCommand<string> SetMcpApprovalCommand { get; }
    public IRelayCommand<string> SetNewMcpRunAtCommand { get; }
    public IRelayCommand<string> SetNewMcpTransportCommand { get; }

    public IAsyncRelayCommand LoadMcpServersCommand { get; }
    public IAsyncRelayCommand AddMcpServerCommand { get; }
    public IAsyncRelayCommand<string> RemoveMcpServerCommand { get; }
    public IAsyncRelayCommand<McpServerInfo> ToggleMcpServerCommand { get; }

    // ---- MCP curated presets + effective-config preview (for the active session's host) ----
    public ObservableCollection<McpPresetRowVm> McpPresets { get; } = [];
    public ObservableCollection<McpServerInfo> EffectiveMcp { get; } = [];

    [ObservableProperty] private string _mcpPreviewStatus = "Open a session on a host to see what would be active.";

    public IAsyncRelayCommand LoadMcpPresetsCommand { get; }
    public IAsyncRelayCommand<McpPresetRowVm> InstallMcpPresetCommand { get; }
    public IAsyncRelayCommand PreviewMcpCommand { get; }

    /// <summary>Reloads the whole MCP page — servers, presets and the effective preview — in one go.</summary>
    public IAsyncRelayCommand LoadMcpCommand { get; }

    // ---- MCP catalogue: searching the registries the host has, and installing from them ----

    /// <summary>Results from every MCP catalogue the host offers, each row naming where it came from.</summary>
    public ObservableCollection<McpCatalogRowVm> McpCatalogResults { get; } = [];

    public bool HasMcpCatalogResults => McpCatalogResults.Count > 0;

    [ObservableProperty] private string _mcpCatalogQuery = string.Empty;

    [ObservableProperty] private bool _isSearchingMcpCatalog;

    [ObservableProperty]
    private string _mcpCatalogStatus = "Search the registries for a server by name or by what it does.";

    public IAsyncRelayCommand SearchMcpCatalogCommand { get; }
    public IAsyncRelayCommand<McpCatalogRowVm> InstallMcpCatalogEntryCommand { get; }

    /// <summary>
    /// Searches every MCP catalogue the host has — the built-in curated set plus whatever registry plugins are
    /// installed — and shows the results together. A registry that couldn't answer is named in the status
    /// rather than quietly contributing nothing.
    /// </summary>
    private async Task SearchMcpCatalogAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            _dispatcher.Post(() => McpCatalogStatus = "Open a session on a host to search its MCP registries.");
            return;
        }

        try
        {
            IsSearchingMcpCatalog = true;
            var results = await McpManagement.SearchCatalogAsync(target.Url, target.Token, McpCatalogQuery ?? string.Empty, target.Http);
            _dispatcher.Post(() =>
            {
                McpCatalogResults.Clear();
                foreach (var hit in results.Hits)
                {
                    var installed = McpServers.Any(s => string.Equals(s.Name, hit.Entry.Name, StringComparison.OrdinalIgnoreCase));
                    McpCatalogResults.Add(new McpCatalogRowVm(hit, installed));
                }

                OnPropertyChanged(nameof(HasMcpCatalogResults));
                McpCatalogStatus = DescribeCatalog(results, McpCatalogQuery ?? string.Empty);
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpCatalogStatus = "Couldn't search: " + Explain(ex));
        }
        finally
        {
            _dispatcher.Post(() => IsSearchingMcpCatalog = false);
        }
    }

    private static string DescribeCatalog(Agnes.Abstractions.CatalogResults<Agnes.Abstractions.McpCatalogEntry> results, string query)
    {
        var found = results.Hits.Count switch
        {
            0 when query.Length > 0 => $"Nothing matched '{query}'.",
            0 => "The registries are offering nothing right now.",
            var n when query.Length > 0 => $"{n} match(es) for '{query}'.",
            var n => $"{n} server(s) offered.",
        };

        return results.Failures.Count == 0 ? found : $"{found} Couldn't reach: {string.Join("; ", results.Failures)}";
    }

    /// <summary>
    /// Installs a catalogued server. The host resolves the entry against its registry again and maps it into a
    /// server configuration, so the client never has to understand how a given registry describes a launch.
    /// </summary>
    private async Task InstallMcpCatalogEntryAsync(McpCatalogRowVm? row)
    {
        var target = ActiveHttpHost();
        if (target is null || row is null || row.IsInstalled) { return; }

        try
        {
            _dispatcher.Post(() => McpCatalogStatus = $"Installing '{row.Name}'…");
            await McpManagement.InstallFromCatalogAsync(target.Url, target.Token, row.CatalogId, row.EntryId, runAt: null, httpClient: target.Http);
            await LoadMcpAsync();
            await SearchMcpCatalogAsync();
            _dispatcher.Post(() => McpCatalogStatus = row.NeedsConfiguration
                ? $"Installed '{row.Name}'. Fill in {row.RequiredEnvironment} under Configured servers before using it."
                : $"Installed '{row.Name}'. It applies to sessions started from now on.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpCatalogStatus = $"Couldn't install '{row.Name}': " + Explain(ex));
        }
    }

    private async Task LoadMcpPresetsAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            _dispatcher.Post(() => McpPresets.Clear());
            return;
        }

        try
        {
            var presets = await McpManagement.PresetsAsync(target.Url, target.Token, target.Http);
            _dispatcher.Post(() =>
            {
                McpPresets.Clear();
                foreach (var p in presets)
                {
                    // A preset already in the configured list has nothing left to offer, so it says so
                    // rather than inviting a second install of the same server.
                    var installed = McpServers.Any(s => string.Equals(s.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    McpPresets.Add(new McpPresetRowVm(p, installed));
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't load presets: " + Explain(ex));
        }
    }

    private async Task InstallMcpPresetAsync(McpPresetRowVm? row)
    {
        var target = ActiveHttpHost();
        if (target is null || row is null || row.IsInstalled)
        {
            return;
        }

        try
        {
            // Quick-install: reuse the host's normal add-server path (no command/args retyped).
            await McpManagement.InstallPresetAsync(target.Url, target.Token, row.Preset, httpClient: target.Http);
            await LoadMcpAsync();
            _dispatcher.Post(() => McpStatus = $"Installed '{row.Name}'. It applies to sessions started from now on.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't install preset: " + Explain(ex));
        }
    }

    /// <summary>
    /// Loads the whole MCP page in the order its parts depend on each other: the configured servers first (the
    /// presets need them to know what's already installed), then the presets, then what would actually be
    /// active. One call so the page is never half-populated.
    /// </summary>
    private async Task LoadMcpAsync()
    {
        await LoadMcpServersAsync();
        await LoadMcpPresetsAsync();
        await PreviewMcpAsync();
    }

    private async Task PreviewMcpAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            _dispatcher.Post(() =>
            {
                EffectiveMcp.Clear();
                McpPreviewStatus = "Open a session on a host to see what would be active.";
            });
            return;
        }

        try
        {
            // Host-wide effective view (no workspace filter) — what would be active for a plain session now.
            var effective = await McpManagement.PreviewEffectiveAsync(target.Url, target.Token, workspaceId: null, agentId: null, httpClient: target.Http);
            _dispatcher.Post(() =>
            {
                EffectiveMcp.Clear();
                foreach (var s in effective) { EffectiveMcp.Add(s); }
                var native = effective.Count(s => s.NativeConfig);
                McpPreviewStatus = effective.Count == 0
                    ? "No MCP servers would be active."
                    : native == 0
                        ? $"{effective.Count} server(s) would be active."
                        : $"{effective.Count} server(s) would be active ({native} read-only, from a CLI's native config).";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpPreviewStatus = "Couldn't preview: " + Explain(ex));
        }
    }

    private async Task LoadMcpServersAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            _dispatcher.Post(() => { McpServers.Clear(); McpStatus = "Open a session on a host to manage its MCP servers."; });
            return;
        }

        try
        {
            McpStatus = "Loading…";
            var list = await McpManagement.ListAsync(target.Url, target.Token, target.Http);
            _dispatcher.Post(() =>
            {
                McpServers.Clear();
                foreach (var s in list) { McpServers.Add(s); }
                McpStatus = list.Count == 0 ? "No MCP servers configured." : $"{list.Count} MCP server(s).";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't load MCP servers: " + Explain(ex));
        }
    }

    private async Task AddMcpServerAsync()
    {
        var target = ActiveHttpHost();
        if (target is null || string.IsNullOrWhiteSpace(NewMcpName))
        {
            return;
        }

        var args = NewMcpArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var request = new McpServerRequest(
            Name: NewMcpName.Trim(),
            RunAt: NewMcpRunAt,
            Enabled: true,
            Transport: NewMcpTransport,
            Command: NewMcpIsStdio ? NewMcpCommand.Trim() : null,
            Args: NewMcpIsStdio ? args : null,
            Url: NewMcpIsHttp ? NewMcpUrl.Trim() : null);

        try
        {
            await McpManagement.AddAsync(target.Url, target.Token, request, target.Http);
            _dispatcher.Post(() =>
            {
                NewMcpName = NewMcpCommand = NewMcpArgs = NewMcpUrl = string.Empty;
            });
            await LoadMcpAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't add server: " + Explain(ex));
        }
    }

    private async Task RemoveMcpServerAsync(string? id)
    {
        var target = ActiveHttpHost();
        if (target is null || string.IsNullOrEmpty(id))
        {
            return;
        }

        try
        {
            await McpManagement.RemoveAsync(target.Url, target.Token, id, target.Http);
            await LoadMcpAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't remove server: " + Explain(ex));
        }
    }

    private async Task ToggleMcpServerAsync(McpServerInfo? server)
    {
        var target = ActiveHttpHost();
        if (target is null || server is null)
        {
            return;
        }

        var request = new McpServerRequest(
            server.Name, server.RunAt, !server.Enabled, server.Transport,
            server.Command, server.Args, server.Env, server.Url, server.BearerTokenEnv,
            server.ApplyScope, server.WorkspaceId);

        try
        {
            await McpManagement.UpdateAsync(target.Url, target.Token, server.Id, request, target.Http);
            // Reload the whole page: enabling or disabling a server changes what "would be active" says, and a
            // stale answer there is worse than no answer.
            await LoadMcpAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't update server: " + Explain(ex));
        }
    }

    // ---- sandbox baseline image (for the active session's host) ----

    [ObservableProperty] private string _sandboxImageStatus = "Open a session on a host to manage its sandbox image.";
    [ObservableProperty] private string _sandboxImageBase = "images:ubuntu/24.04/cloud";
    [ObservableProperty] private bool _sandboxImageNode = true;
    [ObservableProperty] private string _sandboxImageApt = string.Empty;   // space-separated
    [ObservableProperty] private string _sandboxImageNpm = string.Empty;
    [ObservableProperty] private string _sandboxImagePip = string.Empty;

    // The last-loaded manifest, so alias + agents (not edited here) survive a save.
    private SandboxImageDto? _loadedImage;

    public IAsyncRelayCommand LoadSandboxImageCommand { get; }
    public IAsyncRelayCommand SaveSandboxImageCommand { get; }
    public IAsyncRelayCommand RebuildSandboxImageCommand { get; }

    private static string[] SplitPackages(string value)
        => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void SetImageStatus(SandboxImageStatusDto? status)
        => SandboxImageStatus = status is null ? "unknown" : $"{status.State}: {status.Message}";

    private async Task LoadSandboxImageAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            _dispatcher.Post(() => SandboxImageStatus = "Open a session on a host to manage its sandbox image.");
            return;
        }

        try
        {
            var view = await SandboxImageManagement.GetAsync(target.Url, target.Token, target.Http);
            _dispatcher.Post(() =>
            {
                if (view is null)
                {
                    SandboxImageStatus = "This host has no sandbox configured.";
                    return;
                }

                _loadedImage = view.Manifest;
                SandboxImageBase = view.Manifest.BaseImage;
                SandboxImageNode = view.Manifest.Node;
                SandboxImageApt = string.Join(' ', view.Manifest.AptPackages);
                SandboxImageNpm = string.Join(' ', view.Manifest.NpmGlobals);
                SandboxImagePip = string.Join(' ', view.Manifest.PipPackages);
                SetImageStatus(view.Status);
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxImageStatus = "Couldn't load: " + Explain(ex));
        }
    }

    private SandboxImageDto BuildImageDto() => new(
        SandboxImageBase.Trim(),
        _loadedImage?.Alias ?? "agnes-baseline",
        SandboxImageNode,
        SplitPackages(SandboxImageApt),
        SplitPackages(SandboxImageNpm),
        SplitPackages(SandboxImagePip),
        _loadedImage?.Agents ?? []);

    private async Task SaveSandboxImageAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            return;
        }

        try
        {
            var status = await SandboxImageManagement.SaveAsync(target.Url, target.Token, BuildImageDto(), target.Http);
            _dispatcher.Post(() => SetImageStatus(status));
            await PollImageStatusAsync(target);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxImageStatus = "Couldn't save: " + Explain(ex));
        }
    }

    private async Task RebuildSandboxImageAsync()
    {
        var target = ActiveHttpHost();
        if (target is null)
        {
            return;
        }

        try
        {
            var status = await SandboxImageManagement.RebuildAsync(target.Url, target.Token, target.Http);
            _dispatcher.Post(() => SetImageStatus(status));
            await PollImageStatusAsync(target);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxImageStatus = "Couldn't rebuild: " + Explain(ex));
        }
    }

    // Refresh status while a bake is in progress (baking can take minutes).
    private async Task PollImageStatusAsync(HostEndpoint target)
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(3000);
            SandboxImageStatusDto? status;
            try
            {
                status = await SandboxImageManagement.GetStatusAsync(target.Url, target.Token, target.Http);
            }
            catch
            {
                return;
            }

            _dispatcher.Post(() => SetImageStatus(status));
            if (status is null || !string.Equals(status.State, "building", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

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
        var session = new SessionViewModel(host, view, _dispatcher, title, _prompts, _policy, EnsureClientPlugins().EventBus);
        session.NotificationRaised += n => _dispatcher.Post(() => Surface(n));
        _ = EnsureClientPlugins().EventBus.DispatchAsync(new SessionTabOpenedEvent(view.SessionId)); // observe-only
        return session;
    }

    private void Surface(AppNotification notification)
    {
        // The user is already looking — don't toast a completion. Blockers/errors always show.
        if (notification.Kind == NotificationKind.Completion && WindowActive)
        {
            return;
        }

        // Route through the client event spine so a client plugin can intercept/rewrite/suppress it. The
        // common case (synchronous interceptors, or none) stays on this UI thread; a rare async interceptor
        // marshals the final show back onto the UI dispatcher.
        var evt = new BeforeNotificationEvent(notification);
        var dispatch = EnsureClientPlugins().EventBus.DispatchAsync(evt);
        if (dispatch.IsCompletedSuccessfully)
        {
            if (!evt.IsCanceled) { Notifier.Notify(evt.Notification); }
        }
        else
        {
            _ = dispatch.ContinueWith(
                _ => _dispatcher.Post(() => { if (!evt.IsCanceled) { Notifier.Notify(evt.Notification); } }),
                TaskScheduler.Default);
        }
    }

    private ClientPluginSet EnsureClientPlugins()
        => _clientPlugins ??= DesktopClientPlugins.Build(Notifier,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Agnes", "client-plugins"));

    /// <summary>Custom screens contributed by client plugins, for a menu / command-palette to list and open.</summary>
    public IReadOnlyList<ICustomScreenProvider> CustomScreens => EnsureClientPlugins().CustomScreens;

    /// <summary>Opens a plugin's custom screen as a dock document — the same way <see cref="OpenSettings"/>
    /// opens Settings, so a plugin screen can replace the conversation view in a tab.</summary>
    public void OpenCustomScreen(ICustomScreenProvider provider)
    {
        if (_factory.DocumentDock is not { } dock)
        {
            return;
        }

        var existing = dock.VisibleDockables?.OfType<PluginScreenDocument>()
            .FirstOrDefault(d => (string?)d.Id == provider.ScreenId);
        if (existing is null)
        {
            existing = new PluginScreenDocument(provider);
            _factory.AddDockable(dock, existing);
        }

        dock.ActiveDockable = existing;
        _factory.SetActiveDockable(existing);
        _factory.SetFocusedDockable(dock, existing);
        _ = EnsureClientPlugins().EventBus.DispatchAsync(new CustomScreenOpenedEvent(provider.ScreenId)); // observe-only
    }

    /// <summary>The directory to prefill for a new session — last used, else the user's home.</summary>
    public string DefaultWorkingDirectory =>
        string.IsNullOrWhiteSpace(_settings.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _settings.WorkingDirectory;

    public void RememberWorkingDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && path != _settings.WorkingDirectory)
        {
            _settings = _settings with { WorkingDirectory = path };
            _settingsStore.Save(_settings);
        }
    }

    /// <summary>Detaches a tab into its own floating window (Dock manages re-docking on drag-back).</summary>
    public void FloatTab(SessionDocument doc)
    {
        _factory.FloatDockable(doc);
        SaveState();
    }

    /// <summary>
    /// Jumps to the session (and the specific transcript item) a notification came from — in the main
    /// window or in a detached floating window. Returns true if it focused a floating window (the
    /// caller then need not activate the main window).
    /// </summary>
    public bool ActivateNotification(AppNotification notification)
    {
        // Main window tabs.
        var doc = OpenTabs().FirstOrDefault(d => d.Session?.SessionId == notification.SessionId);
        if (doc is not null)
        {
            _factory.SetActiveDockable(doc);
            RevealAnchor(doc, notification);
            return false;
        }

        // Detached (floating) windows.
        foreach (var window in ((IRootDock)Layout).Windows ?? [])
        {
            var floated = DocumentsIn(window.Layout).FirstOrDefault(d => d.Session?.SessionId == notification.SessionId);
            if (floated is not null)
            {
                _factory.SetActiveDockable(floated);
                RevealAnchor(floated, notification);
                window.Host?.SetActive();
                return true;
            }
        }

        return false;
    }

    /// <summary>Brings the given session's tab to the front (main window or a detached window). Used by the
    /// system-tray menu to jump straight to a session needing attention. Returns true if a session was found
    /// and activated.</summary>
    public bool ActivateSessionById(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        var doc = AllDocuments().FirstOrDefault(d => d.Session?.SessionId == sessionId);
        if (doc is null)
        {
            return false;
        }

        FocusDocument(doc);
        return true;
    }

    private static void RevealAnchor(SessionDocument doc, AppNotification notification)
    {
        if (!string.IsNullOrEmpty(notification.AnchorId))
        {
            doc.Session?.ScrollTo(notification.AnchorId);
        }
    }

    // All open session documents, across the main window and any detached windows.
    private IEnumerable<SessionDocument> AllDocuments()
        => OpenTabs().Concat(((IRootDock)Layout).Windows?.SelectMany(w => DocumentsIn(w.Layout)) ?? []);

    private bool IsFloating(SessionDocument doc)
        => (((IRootDock)Layout).Windows ?? []).Any(w => DocumentsIn(w.Layout).Contains(doc));

    // Activates a document and brings its window (main or detached) forward.
    private void FocusDocument(SessionDocument doc)
    {
        _factory.SetActiveDockable(doc);
        foreach (var window in ((IRootDock)Layout).Windows ?? [])
        {
            if (DocumentsIn(window.Layout).Contains(doc))
            {
                window.Host?.SetActive();
                return;
            }
        }
    }

    private static IEnumerable<SessionDocument> DocumentsIn(IDock? dock)
    {
        if (dock?.VisibleDockables is null)
        {
            yield break;
        }

        foreach (var dockable in dock.VisibleDockables)
        {
            if (dockable is SessionDocument sd)
            {
                yield return sd;
            }
            else if (dockable is IDock nested)
            {
                foreach (var inner in DocumentsIn(nested))
                {
                    yield return inner;
                }
            }
        }
    }

    private async Task CloseActiveTabAsync()
    {
        if (_factory.DocumentDock?.ActiveDockable is not SessionDocument doc)
        {
            return;
        }

        // A live session's tab can be guarded by a client plugin (BeforeSessionClose veto); an unstarted
        // tab has no session id, so it just closes.
        if (doc.Session?.SessionId is { } sid)
        {
            if (!await EnsureClientPlugins().EventBus.AllowsAsync(new BeforeSessionCloseEvent(sid)))
            {
                return; // a plugin kept the tab open
            }

            _factory.CloseDockable(doc);
            SaveState();
            _ = EnsureClientPlugins().EventBus.DispatchAsync(new SessionClosedEvent(sid)); // observe-only
            return;
        }

        _factory.CloseDockable(doc);
        SaveState();
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

    /// <summary>
    /// What an access grant can be made against from here: the connected host as a whole, and each live session
    /// on it. A grant's resource is an opaque id, so offering the real candidates is the difference between a
    /// usable page and a text box nobody can fill in correctly.
    /// </summary>
    private IReadOnlyList<GrantTarget> GrantTargets()
    {
        var targets = new List<GrantTarget>();
        if (ActiveHost() is { } host)
        {
            // Name the host this grant would be recorded on. Not ActiveHostName: that describes the *http*
            // host used for the REST management pages, and reads "no connected host" for a hub-only host.
            var label = AllDocuments().FirstOrDefault(d => ReferenceEquals(d.Host, host))?.HostName ?? host.HostUrl;
            targets.Add(new GrantTarget($"host:{host.HostUrl}", $"All of {label}"));
        }

        foreach (var doc in AllDocuments())
        {
            if (doc.Descriptor is { } descriptor)
            {
                targets.Add(new GrantTarget($"session:{descriptor.SessionId}", $"Session — {doc.Title}"));
            }
        }

        return targets;
    }

    public IRelayCommand NextTabCommand { get; private set; } = null!;
    public IRelayCommand PrevTabCommand { get; private set; } = null!;
    public IRelayCommand<string> ActivateTabByIndexCommand { get; private set; } = null!;

    // ---- command palette: jump to a session or run a global action ----

    [ObservableProperty]
    private bool _isPaletteOpen;

    [ObservableProperty]
    private string _paletteQuery = string.Empty;

    public ObservableCollection<PaletteItem> PaletteItems { get; } = [];
    public IRelayCommand TogglePaletteCommand { get; private set; } = null!;
    public IRelayCommand<PaletteItem> RunPaletteItemCommand { get; private set; } = null!;
    public IRelayCommand<string> MovePaletteSelectionCommand { get; private set; } = null!;

    /// <summary>Keyboard-highlighted palette row; Up/Down move it and Enter runs it (defect #9).</summary>
    [ObservableProperty]
    private int _selectedPaletteIndex;

    partial void OnPaletteQueryChanged(string value) => RebuildPalette();

    partial void OnIsPaletteOpenChanged(bool value)
    {
        if (value)
        {
            PaletteQuery = string.Empty;
            RebuildPalette();
        }
    }

    private void RebuildPalette()
    {
        var q = PaletteQuery.Trim();
        var all = new List<PaletteItem>
        {
            new("New tab", GestureFor(AgnesCommand.TabNew, KeymapContext.Window), () => NewTabCommand.Execute(null)),
            new("Open dashboard", GestureFor(AgnesCommand.DashboardOpen, KeymapContext.Window), () => OpenDashboardCommand.Execute(null)),
            new("Show onboarding tour", "help", () => Showcase.Show()),
        };
        all.AddRange(AllDocuments().Select(t => new PaletteItem(
            string.IsNullOrWhiteSpace(t.Title) ? "New session" : t.Title,
            IsFloating(t) ? "window" : "session",
            () => FocusDocument(t))));

        PaletteItems.Clear();
        foreach (var item in all.Where(i => q.Length == 0 || i.Label.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            PaletteItems.Add(item);
        }

        // Keep a valid highlight after every filter so Enter always has a target and the list shows selection.
        SelectedPaletteIndex = PaletteItems.Count > 0 ? 0 : -1;
    }

    private void MovePaletteSelection(string? direction)
    {
        if (PaletteItems.Count == 0)
        {
            return;
        }

        var delta = direction == "up" ? -1 : 1;
        var next = SelectedPaletteIndex + delta;
        // Clamp (no wrap) so Up at the top and Down at the bottom simply stay put.
        SelectedPaletteIndex = Math.Clamp(next, 0, PaletteItems.Count - 1);
    }

    private void RunSelectedPaletteItem()
    {
        var item = SelectedPaletteIndex >= 0 && SelectedPaletteIndex < PaletteItems.Count
            ? PaletteItems[SelectedPaletteIndex]
            : PaletteItems.FirstOrDefault();
        RunPaletteItem(item);
    }

    private void RunPaletteItem(PaletteItem? item)
    {
        IsPaletteOpen = false;
        item?.Invoke();
    }

    private void CycleTab(int direction)
    {
        var tabs = OpenTabs().ToList();
        if (tabs.Count < 2)
        {
            return;
        }

        var active = _factory.DocumentDock?.ActiveDockable as SessionDocument;
        var index = active is null ? 0 : tabs.IndexOf(active);
        var next = ((index + direction) % tabs.Count + tabs.Count) % tabs.Count;
        _factory.SetActiveDockable(tabs[next]);
    }

    private void ActivateTabByIndex(string? oneBased)
    {
        if (int.TryParse(oneBased, System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            var tabs = OpenTabs().ToList();
            if (n >= 1 && n <= tabs.Count)
            {
                _factory.SetActiveDockable(tabs[n - 1]);
            }
        }
    }

    /// <summary>Archived (closed-but-kept) sessions, restorable from the tab menu.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SessionDescriptor> ArchivedSessions { get; } = [];

    public Task RestoreAsync()
    {
        var saved = _tabStore.Load();
        _ready = true;
        EvaluateOnboarding();

        if (saved.Count == 0)
        {
            AddTab();
            return Task.CompletedTask;
        }

        foreach (var descriptor in saved)
        {
            var doc = new SessionDocument(this, _dispatcher)
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

    public async Task<bool> SelectHostAsync(SessionDocument doc, KnownHost host)
    {
        try
        {
            _dispatcher.Post(() =>
            {
                doc.HostName = host.Name;
                doc.HostToken = host.Token;
                doc.HostFingerprint = host.Fingerprint;
                doc.IsConnectingHost = true;
                doc.StatusText = $"Connecting to {host.Name}…";
            });

            var agnesHost = await _connector.ConnectAsync(host.Url, host.Token, host.Fingerprint);
            doc.Host = agnesHost;
            _ = NegotiateCapabilitiesAsync(agnesHost);
            WireStatus(doc, agnesHost);
            // Keep this tab's picker badges live: a background probe or another client's "Check now" pushes
            // OnAgentsChanged, which we fold back into the agent list.
            agnesHost.AgentsChanged += agents => _dispatcher.Post(() => doc.UpdateAgentsAuth(agents));

            var agents = await agnesHost.ListAgentsAsync();
            // Learn whether this host can sandbox, so the new-session screen can default the toggle on.
            var hostInfo = await agnesHost.GetHostInfoAsync();
            _dispatcher.Post(() =>
            {
                doc.SandboxAvailable = hostInfo.SandboxAvailable;
                doc.SandboxRequired = hostInfo.RequireSandbox; // host rejects unsandboxed sessions → lock the toggle on
                doc.PermissionPromptsRequired = hostInfo.RequirePermissionPrompts; // host forbids autonomous → lock to attended
                doc.UseSandbox = hostInfo.SandboxAvailable; // default on when available (forced on when required)
                doc.ShowAgents(agents);
            });
            // What's already running here, alongside the agent picker. Best-effort and off the critical path:
            // connecting must not wait on it, and a host too old to answer just shows no list.
            _ = doc.HostSessions.LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = $"Couldn't reach {host.Name} — {ex.Message}");
            return false;
        }
        finally
        {
            _dispatcher.Post(() => doc.IsConnectingHost = false);
        }
    }

    public async Task<Agnes.Abstractions.ProviderAuthStatus?> CheckAgentAuthAsync(SessionDocument doc, string adapterId)
    {
        if (doc.Host is not { } host)
        {
            return null;
        }

        try
        {
            var info = await host.CheckAuthStatusAsync(adapterId);
            return info.Auth;
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = $"Couldn't check login status — {ex.Message}");
            return null;
        }
    }

    public async Task BeginProviderLoginAsync(SessionDocument doc, string adapterId)
    {
        if (doc.Host is not { } host)
        {
            return;
        }

        try
        {
            // The host opens the login CLI in a client-visible scratch session (id == terminal id) and streams
            // its output as TerminalOutputEvents; subscribe and bind the same terminal panel the in-session
            // terminal uses, so the user can watch the prompts and type responses. The provider's login badge
            // refreshes on its own (host-pushed OnAgentsChanged) when the login CLI exits.
            var loginId = await host.BeginProviderLoginAsync(adapterId);
            var view = await host.SubscribeAsync(loginId);
            _dispatcher.Post(() => doc.ShowLoginTerminal(new TerminalPanelViewModel(host, view, _dispatcher)));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = $"Couldn't start login for {adapterId} — {ex.Message}");
        }
    }

    public bool IsForgettableHost(string url)
        => url != SimulatedHost.Url && url != RecordedHost.Url;

    public Task ForgetHostAsync(SessionDocument doc, KnownHost host)
    {
        if (!IsForgettableHost(host.Url))
        {
            return Task.CompletedTask;
        }

        _knownHosts.RemoveAll(h => h.Url == host.Url);
        _hostStore.Save(_knownHosts.Where(h => IsForgettableHost(h.Url)).ToList());
        doc.ShowHosts(_knownHosts); // refresh the picker so the removed host is gone immediately
        return Task.CompletedTask;
    }

    private static bool IsValidHostUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
           && !string.IsNullOrEmpty(u.Host);

    /// <summary>
    /// Unpacks what the address field was given. An <c>agnes://pair?…</c> link pasted there carries three
    /// things at once — where the host is, a one-time grant, and the fingerprint of the certificate it serves.
    /// The fingerprint is the part that lets a self-signed host be trusted without a CA, and it only means
    /// anything because the link came off the host's own screen rather than off the network.
    ///
    /// Shared by every way in (pairing code, keypair, GitHub sign-in) so they all reach the same host on the
    /// same terms: reading it in only one of them is how a self-signed host ends up pairable but not
    /// signable-into.
    /// </summary>
    private static (string Url, string? Grant, string? Fingerprint) ReadHostEntry(string entry)
    {
        if (!entry.StartsWith("agnes://pair", StringComparison.OrdinalIgnoreCase))
        {
            return (entry, null, null);
        }

        return (
            Agnes.Protocol.PairingLink.HostOf(entry) ?? entry,
            Agnes.Protocol.PairingLink.SecretOf(entry),
            Agnes.Protocol.PairingLink.FingerprintOf(entry));
    }

    public async Task AddHostAsync(SessionDocument doc)
    {
        var (url, codeFromLink, fingerprint) = ReadHostEntry(doc.NewHostUrl.Trim());
        if (codeFromLink is not null || fingerprint is not null)
        {
            var resolved = url;
            _dispatcher.Post(() => doc.NewHostUrl = resolved);
        }

        if (!IsValidHostUrl(url))
        {
            _dispatcher.Post(() => doc.StatusText = "Enter a host address like https://your-host:5099");
            return;
        }

        // The field takes a pairing code: exchange it for a durable per-device token. If pairing
        // doesn't apply (e.g. a pre-issued bootstrap token was pasted), fall back to using it directly —
        // but remember the pairing failure so a mistyped/expired code produces a clear message rather than
        // silently saving a broken host.
        var codeOrToken = string.IsNullOrEmpty(codeFromLink) ? doc.NewHostToken.Trim() : codeFromLink;
        var token = codeOrToken;
        using var pinnedHttp = fingerprint is { Length: > 0 }
            ? Agnes.Client.PinnedTls.CreateClient(fingerprint)
            : null;
        var pairingFailed = false;
        if (!string.IsNullOrEmpty(codeOrToken))
        {
            try
            {
                _dispatcher.Post(() => doc.StatusText = "Pairing…");
                var deviceName = $"{Environment.MachineName} (desktop)";
                var paired = await Agnes.Client.DevicePairing.PairAsync(url, codeOrToken, deviceName, pinnedHttp);
                token = paired.Token;
            }
            catch
            {
                pairingFailed = true; // fall back to trying the entry as a direct token below.
            }
        }

        // Persist ONLY after a successful connection, so a wrong URL / expired code never gets saved.
        var host = new KnownHost(string.IsNullOrWhiteSpace(doc.NewHostName) ? url : doc.NewHostName.Trim(), url, token, fingerprint);
        var connected = await SelectHostAsync(doc, host);
        if (connected)
        {
            if (!_knownHosts.Any(h => h.Url == host.Url))
            {
                _knownHosts.Add(host);
            }

            _hostStore.Save(_knownHosts.Where(h => IsForgettableHost(h.Url)).ToList());
            _dispatcher.Post(() => doc.ShowAddHost = false);
        }
        else if (pairingFailed)
        {
            _dispatcher.Post(() => doc.StatusText =
                "Pairing failed — the code may be wrong or expired. Get a fresh code from the host, or paste a host token.");
        }
        // else: SelectHostAsync already left a clear "couldn't reach …" message.
    }

    public async Task DiscoverAuthMethodsAsync(SessionDocument doc)
    {
        // Asking a host which sign-in methods it offers is itself a call to that host, so it needs the same
        // trust decision: a pasted pairing link's fingerprint has to be honoured here or a self-signed host
        // never gets far enough to offer anything.
        var (url, _, fingerprint) = ReadHostEntry(doc.NewHostUrl.Trim());
        if (!IsValidHostUrl(url))
        {
            _dispatcher.Post(() =>
            {
                doc.HostSupportsGitHub = false;
                doc.HostSupportsKeypair = false;
                doc.HostSupportsPairing = true; // default assumption until we can ask a real host
                doc.GitHubClientId = null;
            });
            return;
        }

        var methods = await Agnes.Client.AuthDiscovery.GetMethodsAsync(url, Agnes.Client.AgnesHttp.For(fingerprint)).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            doc.HostSupportsGitHub = methods.GitHub;
            doc.HostSupportsKeypair = methods.Keypair;
            doc.HostSupportsPairing = methods.Pairing;
            doc.GitHubClientId = methods.GitHubClientId;
        });
    }

    public async Task SignInWithKeyAsync(SessionDocument doc)
    {
        // Same unpacking as pairing: a pasted link's fingerprint has to reach the enrolment call and the saved
        // host, or signing in with a key works everywhere except the self-signed hosts it exists for.
        var (url, _, fingerprint) = ReadHostEntry(doc.NewHostUrl.Trim());
        if (!IsValidHostUrl(url))
        {
            _dispatcher.Post(() => doc.StatusText = "Enter a host address like https://your-host:5099 first.");
            return;
        }

        var http = Agnes.Client.AgnesHttp.For(fingerprint);
        try
        {
            // Surface the public-key line so the operator can authorize this device on the host.
            using (var key = Agnes.Client.KeypairEnrollment.LoadOrCreateKey())
            {
                var line = Agnes.Client.KeypairEnrollment.PublicKeyLine(key);
                _dispatcher.Post(() =>
                {
                    doc.PublicKeyLine = line;
                    doc.ShowKeyInfo = true;
                    doc.StatusText = "Signing in with your key…";
                });
            }

            var deviceName = $"{Environment.MachineName} (desktop)";
            var paired = await Agnes.Client.KeypairEnrollment.AuthenticateAsync(url, deviceName, httpClient: http).ConfigureAwait(false);

            var host = new KnownHost(string.IsNullOrWhiteSpace(doc.NewHostName) ? url : doc.NewHostName.Trim(), url, paired.Token, fingerprint);
            var connected = await SelectHostAsync(doc, host);
            if (connected)
            {
                if (!_knownHosts.Any(h => h.Url == host.Url))
                {
                    _knownHosts.Add(host);
                }

                _hostStore.Save(_knownHosts.Where(h => IsForgettableHost(h.Url)).ToList());
                _dispatcher.Post(() => doc.ShowAddHost = false);
            }
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText =
                "Key sign-in failed: " + ex.Message + " Add the key line above to the host's authorized_keys, then retry.");
        }
    }

    public async Task SignInWithGitHubAsync(SessionDocument doc)
    {
        var (url, _, fingerprint) = ReadHostEntry(doc.NewHostUrl.Trim());
        if (!IsValidHostUrl(url))
        {
            _dispatcher.Post(() => doc.StatusText = "Enter a host address like https://your-host:5099 first.");
            return;
        }

        // GitHub's own device-flow endpoints are CA-signed and need no pin; the two calls that go to the
        // Agnes host — discovering its methods, and exchanging the GitHub token for a device token — do.
        var http = Agnes.Client.AgnesHttp.For(fingerprint);
        try
        {
            var methods = await Agnes.Client.AuthDiscovery.GetMethodsAsync(url, http).ConfigureAwait(false);
            if (!methods.GitHub || string.IsNullOrEmpty(methods.GitHubClientId))
            {
                _dispatcher.Post(() => doc.StatusText = "This host doesn't offer GitHub sign-in.");
                return;
            }

            _dispatcher.Post(() => doc.StatusText = "Starting GitHub sign-in…");
            var code = await Agnes.Client.GitHubDeviceLogin.StartAsync(methods.GitHubClientId).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                doc.GitHubUserCode = code.UserCode;
                doc.GitHubVerificationUri = code.VerificationUri;
                doc.IsGitHubAuthorizing = true;
                doc.StatusText = string.Empty;
            });
            OpenExternalUrl(code.VerificationUri);

            var deviceName = $"{Environment.MachineName} (desktop)";
            var paired = await Agnes.Client.GitHubDeviceLogin
                .CompleteAsync(url, methods.GitHubClientId, code, deviceName, hostClient: http).ConfigureAwait(false);

            // Same persist-on-successful-connect flow as AddHostAsync — never save a host we couldn't reach.
            var host = new KnownHost(string.IsNullOrWhiteSpace(doc.NewHostName) ? url : doc.NewHostName.Trim(), url, paired.Token, fingerprint);
            var connected = await SelectHostAsync(doc, host);
            if (connected)
            {
                if (!_knownHosts.Any(h => h.Url == host.Url))
                {
                    _knownHosts.Add(host);
                }

                _hostStore.Save(_knownHosts.Where(h => IsForgettableHost(h.Url)).ToList());
                _dispatcher.Post(() => doc.ShowAddHost = false);
            }
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = "GitHub sign-in failed: " + ex.Message);
        }
        finally
        {
            _dispatcher.Post(() => doc.IsGitHubAuthorizing = false);
        }
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // best-effort — the code + URL are also shown in the UI for manual entry.
        }
    }

    public async Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName, bool skipPermissions = false, string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null)
    {
        if (doc.Host is null)
        {
            return;
        }

        var workingDirectory = string.IsNullOrWhiteSpace(doc.WorkingDirectory) ? DefaultWorkingDirectory : doc.WorkingDirectory.Trim();
        RememberWorkingDirectory(workingDirectory);

        // Move to the "Starting" screen (progress bar + status) — opening can take a while when the host
        // has to bake the sandbox image. A background poll of the host's bake status feeds live progress
        // text; it's cancelled as soon as the open finishes (or fails). A second token lets the user Cancel
        // the wait from the Starting screen so a slow/opaque open never traps them (defect #8/#10).
        using var startingDone = new CancellationTokenSource();
        using var startCts = new CancellationTokenSource();
        doc.StartCts = startCts;
        _dispatcher.Post(() =>
        {
            doc.StatusText = $"Starting {displayName}…";
            doc.Stage = TabStage.Starting;
        });
        var progress = PollBakeStatusAsync(doc, startingDone.Token);

        try
        {
            var info = await doc.Host.OpenSessionAsync(adapterId, workingDirectory, skipPermissions: skipPermissions, mcpApproval: McpApproval, gitCredentialMode: gitCredentialMode, useSandbox: useSandbox, modelId: modelId);
            var view = await doc.Host.SubscribeAsync(info.SessionId);
            var title = ProjectTitle(info.WorkingDirectory, displayName);
            _dispatcher.Post(() =>
            {
                if (startCts.IsCancellationRequested)
                {
                    return; // the user cancelled the wait; don't yank them back into a session.
                }

                doc.AgentName = displayName;
                // Set the folder-derived base title BEFORE attaching, so if the session already carries an
                // agent title (replayed from the snapshot) AttachSession's title wins instead of being clobbered.
                doc.Title = title;
                doc.AttachSession(CreateSession(doc.Host!, view, title));
                doc.Descriptor = new SessionDescriptor(
                    doc.HostName, doc.Host!.HostUrl, doc.HostToken, info.SessionId, adapterId, title);
                SaveState();
            });
            _ = MaybePromptGitHubLinkAsync(doc); // one-time "Link GitHub?" nudge if none is linked.
        }
        catch (Exception ex)
        {
            // A server-side failure opening the session (e.g. the sandbox image bake failing) must not
            // crash the whole app — surface it on the tab and drop back to the picker so the user can
            // fix the cause and retry. If the user already cancelled, leave their "Cancelled" state be.
            _dispatcher.Post(() =>
            {
                if (startCts.IsCancellationRequested)
                {
                    return;
                }

                doc.StatusText = "Couldn't start session: " + ex.Message;
                doc.Stage = TabStage.PickAgent;
            });
        }
        finally
        {
            startingDone.Cancel();
            await progress.ConfigureAwait(false);
            doc.StartCts = null;
        }
    }

    /// <summary>Finds sessions a CLI created outside Agnes for the tab's working directory (from the CLI's own
    /// on-disk logs) and lists them on the tab (sessions/02). Best-effort — a host without the capability just
    /// reports none.</summary>
    public async Task DiscoverExternalSessionsAsync(SessionDocument doc)
    {
        if (doc.Host is null)
        {
            return;
        }

        var workingDirectory = string.IsNullOrWhiteSpace(doc.WorkingDirectory) ? DefaultWorkingDirectory : doc.WorkingDirectory.Trim();
        IReadOnlyList<Agnes.Abstractions.ExternalSessionInfo> found;
        try
        {
            found = await doc.Host.DiscoverExternalSessionsAsync(workingDirectory);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.DiscoverStatus = "Couldn't look for external sessions: " + ex.Message);
            return;
        }

        _dispatcher.Post(() =>
        {
            doc.ShowDiscoveredSessions(found);
            doc.DiscoverStatus = found.Count == 0
                ? "No sessions running outside Agnes were found in this folder."
                : $"{found.Count} session(s) running outside Agnes.";
        });
    }

    /// <summary>Opens a live, read-only watch of a discovered external session in the tab: it tails the CLI's
    /// own log into an Agnes session (composer disabled) — the "Direct" ownership model (sessions/02).</summary>
    public async Task WatchExternalSessionAsync(SessionDocument doc, Agnes.Abstractions.ExternalSessionInfo external)
    {
        if (doc.Host is null)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            doc.StatusText = "Attaching to the external session…";
            doc.Stage = TabStage.Starting;
        });

        try
        {
            var info = await doc.Host.AttachExternalSessionAsync(external.AdapterId, external.ExternalId);
            var view = await doc.Host.SubscribeAsync(info.SessionId);
            var title = ProjectTitle(info.WorkingDirectory, "Watching");
            _dispatcher.Post(() =>
            {
                doc.AgentName = external.AdapterId;
                doc.Title = title;
                doc.AttachSession(CreateSession(doc.Host!, view, title));
                doc.Descriptor = new SessionDescriptor(
                    doc.HostName, doc.Host!.HostUrl, doc.HostToken, info.SessionId, external.AdapterId, title);
                SaveState();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                doc.StatusText = "Couldn't watch that session: " + ex.Message;
                doc.Stage = TabStage.PickAgent;
            });
        }
    }

    /// <summary>
    /// Joins a session that is already running on this tab's host: subscribe to it here, nothing is opened
    /// host-side. If some other tab already holds that session, focus that tab instead — two views of one
    /// conversation in the same window is never what the click meant.
    /// </summary>
    public async Task AttachCatalogSessionAsync(SessionDocument doc, CatalogSessionRow row)
    {
        if (doc.Host is null)
        {
            return;
        }

        if (ActivateSessionById(row.SessionId))
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            doc.StatusText = $"Joining {row.Title}…";
            doc.Stage = TabStage.Starting;
        });

        try
        {
            var view = await doc.Host.SubscribeAsync(row.SessionId);
            var title = row.Summary.Title is { Length: > 0 } named ? named : ProjectTitle(row.WorkingDirectory, row.AdapterId);
            _dispatcher.Post(() =>
            {
                doc.AgentName = row.AdapterId;
                doc.WorkingDirectory = row.WorkingDirectory;
                doc.Title = title;
                doc.AttachSession(CreateSession(doc.Host!, view, title));
                doc.Descriptor = new SessionDescriptor(
                    doc.HostName, doc.Host!.HostUrl, doc.HostToken, row.SessionId, row.AdapterId, title);
                doc.HostSessions.MarkOpen(row.SessionId);
                SaveState();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                doc.StatusText = "Couldn't join that session: " + ex.Message;
                doc.Stage = TabStage.PickAgent;
            });
        }
    }

    /// <summary>Whether some tab in this window already holds the given session.</summary>
    public bool IsSessionOpen(string sessionId)
        => AllDocuments().Any(d => d.Session?.SessionId == sessionId);

    public async Task LoadModelsAsync(SessionDocument doc, string adapterId)
    {
        if (doc.Host is null)
        {
            return;
        }

        IReadOnlyList<Agnes.Abstractions.ModelInfo> catalog;
        try
        {
            catalog = await doc.Host.ListModelsAsync(adapterId);
        }
        catch
        {
            catalog = []; // best-effort: no picker rather than an error on the config screen.
        }

        // Reconcile against the user's favorites so a removed favorite shows as unavailable, not silently offered.
        var options = ModelCatalogReconciler.Reconcile(adapterId, catalog, _modelFavorites.All);
        var choices = options
            .Select(o => new ModelChoice(o, m => ToggleModelFavorite(doc, m)))
            .ToList();
        _dispatcher.Post(() =>
        {
            // Ignore a stale response if the user moved on to a different agent while this was in flight.
            if (doc.SelectedAgent?.AdapterId == adapterId)
            {
                doc.SetModels(choices);
            }
        });
    }

    public void ToggleModelFavorite(SessionDocument doc, ModelChoice model)
    {
        var adapterId = doc.SelectedAgent?.AdapterId;
        if (adapterId is null)
        {
            return;
        }

        model.IsFavorite = _modelFavorites.Toggle(adapterId, model.Id);
    }

    /// <summary>While a session is opening, poll the host's sandbox-image bake status and surface its
    /// latest message on the tab (so a long "building the image" step reads as progress, not a hang).</summary>
    private async Task PollBakeStatusAsync(SessionDocument doc, CancellationToken cancellationToken)
    {
        var url = doc.Host?.HostUrl;
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return; // no HTTP host (e.g. the simulated host) — nothing to poll.
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                var status = await SandboxImageManagement.GetStatusAsync(
                    url, doc.HostToken, Agnes.Client.AgnesHttp.For(doc.Host?.PinnedFingerprint), cancellationToken).ConfigureAwait(false);
                if (status is { State: "building" } && !string.IsNullOrWhiteSpace(status.Message) && doc.Stage == TabStage.Starting)
                {
                    _dispatcher.Post(() => doc.StatusText = "Building sandbox image · " + status.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected when the open finishes
        }
        catch
        {
            // status polling is best-effort; never let it disrupt the open.
        }
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

    /// <summary>
    /// Acts on an <c>agnes://pair</c> link that arrived from outside the app — clicked in a browser, scanned
    /// from the host's QR, or passed to a second launch that handed it to this one.
    ///
    /// Mirrors what the phone does with the same link, because it's the same job: land on the connect surface
    /// with everything already filled in. A <c>grant</c> is a one-time secret that came off the host's own
    /// screen, so there is nothing left to ask and pairing starts immediately; a typed <c>code</c> is
    /// prefilled but left for the user to confirm. An unpaired tab is reused rather than piling up a new one
    /// per click.
    /// </summary>
    public void HandleLink(string link)
    {
        // What the link means is Agnes.Ui.Core's decision, shared with the phone so the same link does the
        // same thing on both.
        if (AgnesLinkRoute.Parse(link) is not { } route || !IsValidHostUrl(route.HostUrl))
        {
            return;
        }

        if (route.Kind == AgnesLinkKind.ViewSession)
        {
            _dispatcher.Post(() => ViewSharedSession(route));
            return;
        }

        _dispatcher.Post(() =>
        {
            // Reuse a tab that hasn't picked a host yet; only open one if every tab is already in use.
            var doc = OpenTabs().FirstOrDefault(d => d.Session is null && d.Host is null);
            if (doc is null)
            {
                doc = CreateTab();
                AddDocument(doc);
            }
            else
            {
                _factory.SetActiveDockable(doc);
            }

            doc.ShowAddHost = true;
            doc.NewHostUrl = route.HostUrl;
            doc.HostFingerprint = route.Fingerprint;
            if (route.Secret is { Length: > 0 } secret)
            {
                doc.NewHostToken = secret;
            }

            doc.StatusText = route.AutoSubmit
                ? "Pairing from the link…"
                : "Check the address, then pair with the code shown on the host.";

            if (route.AutoSubmit)
            {
                _ = AddHostAsync(doc);
            }
        });
    }

    /// <summary>
    /// Opens a session someone shared a link to.
    ///
    /// The link grants nothing — that is what makes it safe to paste into a group chat — so this only works
    /// against a host this device is already paired with. If it isn't, we say so and stop. We deliberately do
    /// <em>not</em> offer to pair: a message that can prompt a stranger's client to enrol with a host they've
    /// never heard of is a phishing primitive, and pairing stays something you start yourself.
    /// </summary>
    private void ViewSharedSession(AgnesLinkRoute route)
    {
        var known = _knownHosts.FirstOrDefault(h => SameHost(h.Url, route.HostUrl));
        if (known is null)
        {
            Notifier?.Notify(new AppNotification(
                "You don't have access to that host",
                $"This link points at {route.HostUrl}. Pair with it first, then open the link again.",
                NotificationKind.Error,
                route.SessionId ?? string.Empty));
            return;
        }

        // Already open? Just go to it — and to the moment the link names.
        if (AllDocuments().FirstOrDefault(d => d.Session?.SessionId == route.SessionId) is { } open)
        {
            FocusDocument(open);
            RevealSequence(open, route.Sequence);
            return;
        }

        var doc = CreateTab();
        doc.Title = "Shared session";
        doc.HostName = known.Name;
        doc.HostFingerprint = known.Fingerprint ?? route.Fingerprint;
        doc.Descriptor = new SessionDescriptor(
            known.Name, known.Url, known.Token, route.SessionId!, string.Empty, "Shared session");
        AddDocument(doc);

        _ = AttachSharedSessionAsync(doc, route);
    }

    private async Task AttachSharedSessionAsync(SessionDocument doc, AgnesLinkRoute route)
    {
        await ReconnectAsync(doc, doc.Descriptor!);
        _dispatcher.Post(() => RevealSequence(doc, route.Sequence));
    }

    /// <summary>
    /// Scrolls a tab to the moment a link named. The transcript streams in, so the item may not exist the
    /// instant we attach; this retries briefly rather than landing at the top and looking like the link's
    /// position was ignored.
    /// </summary>
    private void RevealSequence(SessionDocument doc, long? sequence)
    {
        if (sequence is not > 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var landed = false;
                _dispatcher.Post(() => landed = doc.Session?.ScrollToSequence(sequence.Value) ?? false);
                if (landed)
                {
                    return;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }
        });
    }

    /// <summary>Whether two host addresses name the same host, ignoring trailing slashes and case.</summary>
    private static bool SameHost(string a, string b)
        => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

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
        // The real UI dispatcher, not the inline fallback: the QR view model completes HTTP work on a
        // background thread and then touches bound state.
        var doc = new SessionDocument(this, _dispatcher) { Title = "New session", CanClose = true };
        doc.ShowHosts(_knownHosts);
        return doc;
    }

    /// <summary>The pinned fingerprint recorded for a host, if it was added with one.</summary>
    private string? FingerprintFor(string hostUrl)
        => _knownHosts.FirstOrDefault(h => string.Equals(h.Url, hostUrl, StringComparison.OrdinalIgnoreCase))?.Fingerprint;

    /// <summary>Reads one query value from an <c>agnes://</c> link.</summary>
    private static string? ReadLinkValue(string link, string key)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            if (split.Length == 2 && split[0] == key)
            {
                return Uri.UnescapeDataString(split[1]);
            }
        }

        return null;
    }

    private async Task ReconnectAsync(SessionDocument doc, SessionDescriptor descriptor)
    {
        try
        {
            _dispatcher.Post(() => doc.StatusText = "Reconnecting…");
            // Reconnect pins the same certificate the host was added with; a host that starts serving a
            // different one fails rather than being trusted, the way a changed SSH host key does.
            var host = await _connector.ConnectAsync(
                descriptor.HostUrl, descriptor.Token, FingerprintFor(descriptor.HostUrl));
            doc.Host = host;
            _ = NegotiateCapabilitiesAsync(host);
            doc.HostToken = descriptor.Token;
            doc.HostFingerprint = FingerprintFor(descriptor.HostUrl);
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
        _dispatcher.Post(() => doc.ConnectionState = host.State);
        host.StateChanged += state => _dispatcher.Post(() => doc.ConnectionState = state);
        // Usage is per-session and flows through the session event stream (SessionDocument mirrors
        // its SessionViewModel.Usage) — not a host-level property.

        bool added;
        lock (_inboxHosts)
        {
            added = _inboxHosts.Add(host);
        }

        if (added)
        {
            host.InboxRunReceived += run => _dispatcher.Post(() => AddInboxRun(run));
            _ = LoadInboxAsync(host);
            // A newly-connected host may already have open approvals — pull them into the unified list.
            _ = Approvals.LoadAsync();
            _ = RefreshAutomationsAsync();
            // Fold the newly-connected host into the merged multi-server host/session aggregate.
            _dispatcher.Post(MultiHost.Refresh);
        }
    }

    // ---- cross-session approvals (notifications/02 tier 1) ----

    /// <summary>The unified open-approvals list across every connected host.</summary>
    public ApprovalsViewModel Approvals { get; }

    /// <summary>A thread-safe snapshot of the currently-connected hosts, for the approvals aggregation.</summary>
    private IEnumerable<IAgnesHost> SnapshotHosts()
    {
        lock (_inboxHosts)
        {
            return _inboxHosts.ToArray();
        }
    }

    /// <summary>Jump-to-session: focus the tab hosting the approval's originating session, if it's open.</summary>
    private void JumpToApproval(ApprovalRow row) => _dispatcher.Post(() =>
    {
        var doc = AllDocuments().FirstOrDefault(d =>
            ReferenceEquals(d.Host, row.Host) && d.Session?.SessionId == row.SessionId);
        if (doc is not null)
        {
            _factory.SetActiveDockable(doc);
        }
    });

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

    // ---- automations: scheduled tasks (pause · resume · run-now · delete, across hosts) ----

    /// <summary>One scheduled task bound to the host it lives on, so the row's buttons act on the right host.</summary>
    public sealed class AutomationRow(ScheduledTask task, IAgnesHost host)
    {
        public ScheduledTask Task { get; } = task;

        public IAgnesHost Host { get; } = host;

        public string Id => Task.Id;

        public string Prompt => Task.Prompt;

        public bool Enabled => Task.Enabled;

        public bool Paused => !Task.Enabled;

        /// <summary>Human-readable cadence, e.g. "every 300s" or "cron 0 9 * * 1-5 (America/New_York)".</summary>
        public string Schedule => string.Equals(Task.Kind, "cron", StringComparison.OrdinalIgnoreCase)
            ? $"cron {Task.CronExpression}" + (string.IsNullOrWhiteSpace(Task.Timezone) ? "" : $" ({Task.Timezone})")
            : $"every {Task.IntervalSeconds}s";
    }

    public System.Collections.ObjectModel.ObservableCollection<AutomationRow> ScheduledTasks { get; } = [];
    public int ScheduledTaskCount => ScheduledTasks.Count;
    public bool HasScheduledTasks => ScheduledTasks.Count > 0;

    public IAsyncRelayCommand<AutomationRow> PauseAutomationCommand { get; }
    public IAsyncRelayCommand<AutomationRow> ResumeAutomationCommand { get; }
    public IAsyncRelayCommand<AutomationRow> RunAutomationNowCommand { get; }
    public IAsyncRelayCommand<AutomationRow> RemoveAutomationCommand { get; }

    private async Task RefreshAutomationsAsync()
    {
        var rows = new List<AutomationRow>();
        foreach (var host in _inboxHosts.ToArray())
        {
            try
            {
                foreach (var task in await host.ListScheduledTasksAsync())
                {
                    rows.Add(new AutomationRow(task, host));
                }
            }
            catch
            {
                // best-effort; a host that can't list its tasks just contributes none
            }
        }

        _dispatcher.Post(() =>
        {
            ScheduledTasks.Clear();
            foreach (var row in rows)
            {
                ScheduledTasks.Add(row);
            }

            OnPropertyChanged(nameof(ScheduledTaskCount));
            OnPropertyChanged(nameof(HasScheduledTasks));
        });
    }

    private async Task PauseAutomationAsync(AutomationRow? row)
    {
        if (row is null)
        {
            return;
        }

        await row.Host.PauseScheduledTaskAsync(row.Id);
        await RefreshAutomationsAsync();
    }

    private async Task ResumeAutomationAsync(AutomationRow? row)
    {
        if (row is null)
        {
            return;
        }

        await row.Host.ResumeScheduledTaskAsync(row.Id);
        await RefreshAutomationsAsync();
    }

    private async Task RunAutomationNowAsync(AutomationRow? row)
    {
        if (row is not null)
        {
            await row.Host.RunScheduledTaskNowAsync(row.Id);
        }
    }

    private async Task RemoveAutomationAsync(AutomationRow? row)
    {
        if (row is null)
        {
            return;
        }

        await row.Host.RemoveScheduledTaskAsync(row.Id);
        await RefreshAutomationsAsync();
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

        var doc = new SessionDocument(this, _dispatcher)
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

        var copy = new SessionDocument(this, _dispatcher)
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
            copy.HostFingerprint = doc.HostFingerprint;
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

    /// <summary>
    /// "New session, same setup" — opens a fresh session on the SAME host/agent as <paramref name="source"/>,
    /// carrying over its launch configuration (working directory, permission mode, git-credential mode,
    /// sandbox). Unlike <see cref="DuplicateAsync"/> (a second view of the same conversation) this starts a
    /// brand-new, empty conversation; unlike <see cref="ForkAsync"/> it copies no transcript history.
    /// </summary>
    public Task NewSessionSameSetupAsync(SessionDocument source)
    {
        if (source.Host is null || source.Descriptor is not { } descriptor)
        {
            return Task.CompletedTask;
        }

        var doc = new SessionDocument(this, _dispatcher)
        {
            Title = "New session",
            CanClose = true,
            HostName = source.HostName,
            Host = source.Host,
            HostToken = source.HostToken,
            HostFingerprint = source.HostFingerprint,
            WorkingDirectory = source.WorkingDirectory,
            SkipPermissions = source.SkipPermissions,
            GitCredentialMode = source.GitCredentialMode,
            UseSandbox = source.UseSandbox,
        };
        ApplyTags(doc, source.Tags.ToList());
        AddDocument(doc);
        WireStatus(doc, source.Host);

        // Reuse the already-connected host and the source's adapter — no host/agent picker round-trip.
        // Carry the source's model forward too: prefer the model it was configured with, else the model the
        // live session is currently on (a restored session has no configure-time picker state). Without this
        // "same setup" silently reverted to the CLI's default model.
        var modelId = source.EffectiveModelId ?? source.Session?.CurrentModelId;
        return SelectAgentAsync(
            doc, descriptor.AdapterId, source.AgentName ?? descriptor.AdapterId,
            source.SkipPermissions, source.GitCredentialMode, source.UseSandbox && source.SandboxAvailable, modelId);
    }

    // ---- launch profiles (providers/04): named, reusable new-session launch configs ----

    public async Task LoadLaunchProfilesAsync(SessionDocument doc)
    {
        if (doc.Host is null)
        {
            return;
        }

        try
        {
            var profiles = await doc.Host.GetLaunchProfilesAsync();
            _dispatcher.Post(() => doc.SetLaunchProfiles(profiles));
        }
        catch
        {
            // A host without the feature (or a transient error) simply shows no profile picker.
        }
    }

    public async Task SaveCurrentAsLaunchProfileAsync(SessionDocument doc, string name)
    {
        if (doc.Host is null || doc.SelectedAgent is not { Available: true } agent || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        // Capture the tab's current new-session selections (plus the client-global MCP-approval posture) into a
        // profile. WorkingDirectory is stored only when the user pinned one; a blank keeps the profile
        // directory-agnostic so it can be reused across folders.
        var dir = string.IsNullOrWhiteSpace(doc.WorkingDirectory) ? null : doc.WorkingDirectory.Trim();
        var profile = new LaunchProfile(
            string.Empty, name.Trim(), agent.AdapterId, dir, UseWorktree: false,
            doc.SkipPermissions, McpApproval, doc.GitCredentialMode, doc.SandboxAvailable && doc.UseSandbox, doc.EffectiveModelId);

        try
        {
            await doc.Host.SaveLaunchProfileAsync(profile);
            await LoadLaunchProfilesAsync(doc);
            _dispatcher.Post(() => doc.StatusText = $"Saved launch profile \"{profile.Name}\".");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = "Couldn't save the profile: " + ex.Message);
        }
    }

    public void ApplyLaunchProfileMcpApproval(string mcpApproval)
    {
        if (!string.IsNullOrWhiteSpace(mcpApproval))
        {
            McpApproval = mcpApproval;
        }
    }

    /// <summary>
    /// Opens a new session tab from a saved profile: the point of a profile is to start something, so the
    /// settings list can do it rather than only telling you a profile exists. The profile lives on the host it
    /// was saved to, so that host is selected for you; the profile's options are applied as soon as the tab
    /// has the host's agent list, and everything stays editable before Start.
    /// </summary>
    private async Task StartFromLaunchProfileAsync(LaunchProfileRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        var doc = CreateTab();
        doc.PendingProfileId = row.Profile.Id;
        AddDocument(doc);

        // The profile is host-scoped, so there's exactly one right host to connect to — don't make the user
        // pick it again from a list.
        if (ActiveHost() is { } host
            && _knownHosts.FirstOrDefault(h => string.Equals(h.Url, host.HostUrl, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            await SelectHostAsync(doc, known);
        }
    }

    public IAsyncRelayCommand<LaunchProfileRowVm> StartFromLaunchProfileCommand { get; }

    public async Task ForkAsync(SessionDocument doc)
    {
        if (doc.Host is null || doc.Descriptor is null)
        {
            return;
        }

        // Ask the host what a fork would do: a proposed (non-existing, numeral-incremented) target folder
        // and whether the sandbox can be copy-on-write cloned. The client is remote, so only the host can
        // stat the working folder and propose a free sibling.
        ForkPlan? plan;
        try
        {
            plan = await doc.Host.ProposeForkAsync(doc.Descriptor.SessionId);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => doc.StatusText = "Couldn't prepare fork: " + ex.Message);
            return;
        }

        if (plan is null)
        {
            _dispatcher.Post(() => doc.StatusText = "This host doesn't support forking sessions.");
            return;
        }

        _dispatcher.Post(() =>
        {
            var title = doc.Title ?? "session";
            ForkPrompt = new ForkPrompt(
                title, plan,
                onConfirm: prompt => ConfirmForkAsync(doc, prompt),
                onCancel: () => ForkPrompt = null);
        });
    }

    private async Task ConfirmForkAsync(SessionDocument doc, ForkPrompt prompt)
    {
        if (doc.Host is null || doc.Descriptor is not { } descriptor)
        {
            ForkPrompt = null;
            return;
        }

        var target = prompt.TargetDirectory.Trim();
        if (target.Length == 0)
        {
            prompt.ErrorText = "Enter a target folder for the fork.";
            return;
        }

        prompt.Busy = true;
        prompt.ErrorText = null;

        var fork = new SessionDocument(this, _dispatcher)
        {
            Title = $"{doc.Title} (fork)",
            CanClose = true,
            HostName = doc.HostName,
            AgentName = doc.AgentName,
        };

        try
        {
            // Copy the working folder host-side and open a new session there (optionally CoW-cloning the
            // sandbox). This can take a while for a large tree / VM clone, so it runs before we commit the
            // tab and any error keeps the dialog open for a retry.
            var info = await doc.Host.ForkSessionAsync(descriptor.SessionId, target, prompt.CopySandbox && prompt.CanCopySandbox);
            var view = await doc.Host.SubscribeAsync(info.SessionId);
            _dispatcher.Post(() =>
            {
                ApplyTags(fork, doc.Tags.ToList());
                AddDocument(fork);
                fork.Host = doc.Host;
                fork.HostToken = doc.HostToken;
                fork.HostFingerprint = doc.HostFingerprint;
                WireStatus(fork, doc.Host);
                fork.AttachSession(CreateSession(doc.Host!, view, fork.Title!));
                fork.Descriptor = descriptor with { SessionId = info.SessionId, Title = fork.Title! };
                ForkPrompt = null;
                SaveState();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                prompt.Busy = false;
                prompt.ErrorText = ex.Message;
            });
        }
    }
}
