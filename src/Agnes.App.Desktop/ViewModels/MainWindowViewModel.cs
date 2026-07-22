using System.Collections.ObjectModel;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.Plugins;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Plugins;
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
            _clientPlugins ??= DesktopClientPlugins.Build(Notifier,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Agnes", "client-plugins"));
            var caps = DesktopClientPlugins.Capabilities(Environment.MachineName, _clientPlugins);
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
        SetThemeCommand = new RelayCommand<string>(t => { if (t is not null) { Theme = t; } });
        LoadDevicesCommand = new AsyncRelayCommand(LoadDevicesAsync);
        RevokeDeviceCommand = new AsyncRelayCommand<string>(RevokeDeviceAsync);
        LoadMcpServersCommand = new AsyncRelayCommand(LoadMcpServersAsync);
        AddMcpServerCommand = new AsyncRelayCommand(AddMcpServerAsync);
        RemoveMcpServerCommand = new AsyncRelayCommand<string>(RemoveMcpServerAsync);
        ToggleMcpServerCommand = new AsyncRelayCommand<McpServerInfo>(ToggleMcpServerAsync);
        SetMcpApprovalCommand = new RelayCommand<string>(v => { if (v is not null) { McpApproval = v; } });
        LoadCredentialStatusCommand = new AsyncRelayCommand(LoadCredentialStatusAsync);
        ConnectGitHubCommand = new AsyncRelayCommand(ConnectGitHubAsync);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        SetSettingsCategoryCommand = new RelayCommand<string>(v => { if (v is not null) { SettingsCategory = v; } });
        LinkGitHubNowCommand = new RelayCommand(LinkGitHubNow);
        DismissGitHubLinkPromptCommand = new RelayCommand(() => ShowGitHubLinkPrompt = false);
        LoadSandboxesCommand = new AsyncRelayCommand(LoadSandboxesAsync);
        DeleteSandboxRecordCommand = new AsyncRelayCommand<SandboxRecordDto>(DeleteSandboxRecordAsync);
        ResumeSandboxRecordCommand = new AsyncRelayCommand<SandboxRecordDto>(ResumeSandboxRecordAsync);
        FindOrphansCommand = new AsyncRelayCommand(FindOrphansAsync);
        ReapOrphansCommand = new AsyncRelayCommand(ReapOrphansAsync);
        LoadProjectsCommand = new AsyncRelayCommand(LoadProjectsAsync);
        SelectProjectCommand = new RelayCommand<ProjectDto>(SelectProject);
        SaveProjectCommand = new AsyncRelayCommand(SaveProjectAsync);
        AddProjectMcpCommand = new RelayCommand(AddProjectMcp);
        RemoveProjectMcpCommand = new RelayCommand<McpServerInfo>(m => { if (m is not null) { ProjectMcp.Remove(m); } });
        SettingsCategories =
        [
            // This device (client-global)
            new SettingsCategoryVm("appearance", "Appearance", "🎨", "theme dark light system ui scale zoom accessibility reduce motion font density"),
            new SettingsCategoryVm("keyboard", "Keyboard", "⌨", "keyboard shortcuts keys bindings gestures"),
            // The connected host
            new SettingsCategoryVm("github", "GitHub accounts", "⑂", "github git push credential token connect app scope repo installation secret account"),
            new SettingsCategoryVm("devices", "Devices", "🔑", "paired devices pairing token revoke auth access per-device"),
            new SettingsCategoryVm("sandboxes", "Sandboxes", "📦", "sandbox vm incus running stopped resume restart delete reap orphan cleanup lifecycle"),
            // Per-project
            new SettingsCategoryVm("projects", "Projects", "📁", "project repo sandbox image mcp servers packages node apt npm pip agents credentials defaults per-repo"),
            new SettingsCategoryVm("plugins", "Plugins", "🧩", "plugin plugins extension nuget install uninstall browse marketplace capability consent provider adapter transport voice notification enable disable configure"),
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
        _factory.ActiveDockableChanged += (_, _) => UpdateWindowTitle();

        Plugins = new PluginManagementViewModel(ActiveHost, _dispatcher);
    }

    /// <summary>The plugin-management surface for the active host (Browse / install / configure / enable).</summary>
    public PluginManagementViewModel Plugins { get; }

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

    /// <summary>Theme: "System" (follow OS), "Light" or "Dark". Applies immediately and persists.</summary>
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
                OnPropertyChanged(nameof(IsSystemTheme));
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDarkTheme));
            }
        }
    }

    public bool IsSystemTheme => Theme is not "Light" and not "Dark";
    public bool IsLightTheme => Theme == "Light";
    public bool IsDarkTheme => Theme == "Dark";

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


    /// <summary>Applies a theme string to the running application (no-op off the UI/host).</summary>
    public static void ApplyTheme(string theme)
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                "Light" => Avalonia.Styling.ThemeVariant.Light,
                "Dark" => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }
    }

    // ---- device management (for the active session's host) ----

    public ObservableCollection<DeviceInfo> Devices { get; } = [];

    [ObservableProperty]
    private string _devicesStatus = "Open a session on a host to manage its paired devices.";

    public IAsyncRelayCommand LoadDevicesCommand { get; }
    public IAsyncRelayCommand<string> RevokeDeviceCommand { get; }

    private (string Url, string Token)? ActiveHttpHost()
    {
        static bool IsHttp(SessionDocument d) =>
            d.Host is { } h && h.HostUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        // Prefer the active session's host; but the Settings tab isn't a session, so fall back to ANY
        // connected http host among the open tabs — otherwise opening Settings dead-ends the GitHub/device/
        // sandbox management that needs a host.
        if (_factory.DocumentDock?.ActiveDockable is SessionDocument active && IsHttp(active))
        {
            return (active.Host!.HostUrl, active.HostToken);
        }

        var any = AllDocuments().FirstOrDefault(IsHttp);
        return any is not null ? (any.Host!.HostUrl, any.HostToken) : null;
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
            var list = await DeviceManagement.ListAsync(target.Value.Url, target.Value.Token);
            _dispatcher.Post(() =>
            {
                Devices.Clear();
                foreach (var d in list) { Devices.Add(d); }
                DevicesStatus = list.Count == 0 ? "No paired devices." : $"{list.Count} paired device(s).";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => DevicesStatus = "Couldn't load devices: " + ex.Message);
        }
    }

    private async Task RevokeDeviceAsync(string? deviceId)
    {
        var target = ActiveHttpHost();
        if (target is null || string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        try
        {
            await DeviceManagement.RevokeAsync(target.Value.Url, target.Value.Token, deviceId);
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => DevicesStatus = "Couldn't revoke: " + ex.Message);
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
            var status = await CredentialManagement.GetStatusAsync(host, doc.HostToken);
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
    public bool CatKeyboard => SettingsCategory == "keyboard";
    public bool CatGitHub => SettingsCategory == "github";
    public bool CatDevices => SettingsCategory == "devices";
    public bool CatSandboxes => SettingsCategory == "sandboxes";
    public bool CatProjects => SettingsCategory == "projects";
    public bool CatPlugins => SettingsCategory == "plugins";

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
        OnPropertyChanged(nameof(CatKeyboard));
        OnPropertyChanged(nameof(CatGitHub));
        OnPropertyChanged(nameof(CatDevices));
        OnPropertyChanged(nameof(CatSandboxes));
        OnPropertyChanged(nameof(CatProjects));
        OnPropertyChanged(nameof(CatPlugins));
        OnPropertyChanged(nameof(ActiveHostName));
        if (value == "projects" && SelectedProject is null)
        {
            _ = LoadProjectsAsync();
        }
        else if (value == "sandboxes")
        {
            _ = LoadSandboxesAsync();
        }
        else if (value == "plugins")
        {
            _ = Plugins.RefreshInstalledAsync();
        }
    }

    // ---- Sandboxes: the host's managed VMs (stop-on-close · resume · delete) ----
    public IAsyncRelayCommand LoadSandboxesCommand { get; }
    public IAsyncRelayCommand<SandboxRecordDto> DeleteSandboxRecordCommand { get; }
    public IAsyncRelayCommand<SandboxRecordDto> ResumeSandboxRecordCommand { get; }
    public IAsyncRelayCommand FindOrphansCommand { get; }
    public IAsyncRelayCommand ReapOrphansCommand { get; }

    public System.Collections.ObjectModel.ObservableCollection<SandboxRecordDto> Sandboxes { get; } = [];
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
            var orphans = await SandboxManagement.OrphansAsync(target.Value.Url, target.Value.Token);
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
            _dispatcher.Post(() => SandboxesStatus = "Couldn't scan for orphans: " + ex.Message);
        }
    }

    private async Task ReapOrphansAsync()
    {
        var target = ActiveHttpHost();
        if (target is null) { return; }

        try
        {
            var reaped = await SandboxManagement.ReapAsync(target.Value.Url, target.Value.Token);
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
            _dispatcher.Post(() => SandboxesStatus = "Couldn't reap: " + ex.Message);
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
            var list = await SandboxManagement.ListAsync(target.Value.Url, target.Value.Token);
            _dispatcher.Post(() =>
            {
                Sandboxes.Clear();
                foreach (var s in list) { Sandboxes.Add(s); }
                OnPropertyChanged(nameof(HasSandboxes));
                SandboxesStatus = list.Count == 0
                    ? "No sandboxes yet — sandboxed sessions appear here (stopped ones stay until you delete them)."
                    : $"{list.Count} sandbox(es) on {ActiveHostName}.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxesStatus = "Couldn't load sandboxes: " + ex.Message);
        }
    }

    private async Task ResumeSandboxRecordAsync(SandboxRecordDto? sandbox)
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
            var info = await SandboxManagement.ResumeAsync(target.Value.Url, target.Value.Token, sandbox.SessionId);
            if (info is null)
            {
                _dispatcher.Post(() => SandboxesStatus = "Resume failed — the host returned no session.");
                return;
            }

            _dispatcher.Post(() =>
            {
                // Open a tab attached to the resumed session (reuses the reconnect flow).
                var descriptor = new SessionDescriptor(ActiveHostName, target.Value.Url, target.Value.Token, info.SessionId, info.AdapterId, sandbox.Title);
                var doc = new SessionDocument(this)
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
            _dispatcher.Post(() => SandboxesStatus = "Couldn't resume: " + ex.Message);
        }
    }

    private async Task DeleteSandboxRecordAsync(SandboxRecordDto? sandbox)
    {
        var target = ActiveHttpHost();
        if (target is null || sandbox is null) { return; }

        try
        {
            var list = await SandboxManagement.DeleteAsync(target.Value.Url, target.Value.Token, sandbox.SessionId);
            _dispatcher.Post(() =>
            {
                Sandboxes.Clear();
                foreach (var s in list) { Sandboxes.Add(s); }
                OnPropertyChanged(nameof(HasSandboxes));
                SandboxesStatus = $"Deleted the sandbox for '{sandbox.Title}'.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxesStatus = "Couldn't delete: " + ex.Message);
        }
    }

    partial void OnSettingsSearchChanged(string value)
    {
        var query = (value ?? string.Empty).Trim();
        SettingsCategoryVm? firstMatch = null;
        foreach (var c in SettingsCategories)
        {
            c.IsVisible = c.Matches(query);
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
            var list = await ProjectManagement.ListAsync(target.Value.Url, target.Value.Token);
            var credentials = await CredentialManagement.GetStatusAsync(target.Value.Url, target.Value.Token);
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
            _dispatcher.Post(() => ProjectsStatus = "Couldn't load projects: " + ex.Message);
        }
    }

    private void SelectProject(ProjectDto? project)
    {
        if (project is null) { return; }
        SelectedProject = project;
        ProjName = project.Name;
        ProjNode = project.Sandbox.Node;
        ProjApt = string.Join(' ', project.Sandbox.AptPackages);
        ProjNpm = string.Join(' ', project.Sandbox.NpmGlobals);
        ProjPip = string.Join(' ', project.Sandbox.PipPackages);
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
        var sandbox = SelectedProject.Sandbox with { Node = ProjNode, AptPackages = Split(ProjApt), NpmGlobals = Split(ProjNpm), PipPackages = Split(ProjPip) };
        var dto = SelectedProject with
        {
            Name = ProjName,
            Sandbox = sandbox,
            McpServers = ProjectMcp.ToArray(),
            CredentialAccount = string.IsNullOrWhiteSpace(ProjAccount) ? null : ProjAccount,
            Repo = string.IsNullOrWhiteSpace(ProjRepo) ? null : ProjRepo.Trim(),
            Defaults = new ProjectDefaultsDto(ProjSkipPermissions, ProjGitMode, ProjMcpApproval),
        };

        try
        {
            IsSavingProject = true;
            ProjectsStatus = $"Saving '{dto.Name}' — rebuilding its sandbox image, this can take a minute…";
            await ProjectManagement.SaveAsync(target.Value.Url, target.Value.Token, dto);
            _dispatcher.Post(() => ProjectsStatus = $"Saved '{dto.Name}' — its sandbox image is rebuilding.");
            await LoadProjectsAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => ProjectsStatus = "Couldn't save: " + ex.Message);
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
            _dispatcher.Post(() => CredentialStatus = "Open a session on a host to link GitHub.");
            return;
        }

        try
        {
            var status = await CredentialManagement.GetStatusAsync(target.Value.Url, target.Value.Token);
            _dispatcher.Post(() => CredentialStatus = status switch
            {
                { Installed: true } => $"GitHub connected ({status.Slug}). Sandboxed pushes mint scoped tokens.",
                { State: "app-created" } => "GitHub app created — finish installing it to enable pushes.",
                _ => "GitHub not linked. Sandboxed git push needs a linked account.",
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => CredentialStatus = "Couldn't load credential status: " + ex.Message);
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
            var url = await CredentialManagement.ConnectGitHubAsync(target.Value.Url, target.Value.Token);
            if (url is { Length: > 0 })
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                _dispatcher.Post(() => CredentialStatus = "Continue in your browser: create the app, then choose repositories.");
            }
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => CredentialStatus = "Couldn't start GitHub connect: " + ex.Message);
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
            var list = await McpManagement.ListAsync(target.Value.Url, target.Value.Token);
            _dispatcher.Post(() =>
            {
                McpServers.Clear();
                foreach (var s in list) { McpServers.Add(s); }
                McpStatus = list.Count == 0 ? "No MCP servers configured." : $"{list.Count} MCP server(s).";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't load MCP servers: " + ex.Message);
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
            await McpManagement.AddAsync(target.Value.Url, target.Value.Token, request);
            _dispatcher.Post(() =>
            {
                NewMcpName = NewMcpCommand = NewMcpArgs = NewMcpUrl = string.Empty;
            });
            await LoadMcpServersAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't add server: " + ex.Message);
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
            await McpManagement.RemoveAsync(target.Value.Url, target.Value.Token, id);
            await LoadMcpServersAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't remove server: " + ex.Message);
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
            server.Command, server.Args, server.Env, server.Url, server.BearerTokenEnv);

        try
        {
            await McpManagement.UpdateAsync(target.Value.Url, target.Value.Token, server.Id, request);
            await LoadMcpServersAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => McpStatus = "Couldn't update server: " + ex.Message);
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
            var view = await SandboxImageManagement.GetAsync(target.Value.Url, target.Value.Token);
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
            _dispatcher.Post(() => SandboxImageStatus = "Couldn't load: " + ex.Message);
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
            var status = await SandboxImageManagement.SaveAsync(target.Value.Url, target.Value.Token, BuildImageDto());
            _dispatcher.Post(() => SetImageStatus(status));
            await PollImageStatusAsync(target.Value);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxImageStatus = "Couldn't save: " + ex.Message);
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
            var status = await SandboxImageManagement.RebuildAsync(target.Value.Url, target.Value.Token);
            _dispatcher.Post(() => SetImageStatus(status));
            await PollImageStatusAsync(target.Value);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SandboxImageStatus = "Couldn't rebuild: " + ex.Message);
        }
    }

    // Refresh status while a bake is in progress (baking can take minutes).
    private async Task PollImageStatusAsync((string Url, string Token) target)
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(3000);
            SandboxImageStatusDto? status;
            try
            {
                status = await SandboxImageManagement.GetStatusAsync(target.Url, target.Token);
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

    public IRelayCommand NextTabCommand { get; private set; } = null!;
    public IRelayCommand PrevTabCommand { get; private set; } = null!;
    public IRelayCommand<string> ActivateTabByIndexCommand { get; private set; } = null!;

    // ---- command palette (Ctrl+K): jump to a session or run a global action ----

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
            new("New tab", "Ctrl+T", () => NewTabCommand.Execute(null)),
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
        if (int.TryParse(oneBased, out var n))
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

    public async Task<bool> SelectHostAsync(SessionDocument doc, KnownHost host)
    {
        try
        {
            _dispatcher.Post(() =>
            {
                doc.HostName = host.Name;
                doc.HostToken = host.Token;
                doc.IsConnectingHost = true;
                doc.StatusText = $"Connecting to {host.Name}…";
            });

            var agnesHost = await _connector.ConnectAsync(host.Url, host.Token);
            doc.Host = agnesHost;
            _ = NegotiateCapabilitiesAsync(agnesHost);
            WireStatus(doc, agnesHost);

            var agents = await agnesHost.ListAgentsAsync();
            // Learn whether this host can sandbox, so the new-session screen can default the toggle on.
            var hostInfo = await agnesHost.GetHostInfoAsync();
            _dispatcher.Post(() =>
            {
                doc.SandboxAvailable = hostInfo.SandboxAvailable;
                doc.UseSandbox = hostInfo.SandboxAvailable; // default on when available
                doc.ShowAgents(agents);
            });
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

    public async Task AddHostAsync(SessionDocument doc)
    {
        var url = doc.NewHostUrl.Trim();
        if (!IsValidHostUrl(url))
        {
            _dispatcher.Post(() => doc.StatusText = "Enter a host address like https://your-host:5099");
            return;
        }

        // The field takes a pairing code: exchange it for a durable per-device token. If pairing
        // doesn't apply (e.g. a pre-issued bootstrap token was pasted), fall back to using it directly —
        // but remember the pairing failure so a mistyped/expired code produces a clear message rather than
        // silently saving a broken host.
        var codeOrToken = doc.NewHostToken.Trim();
        var token = codeOrToken;
        var pairingFailed = false;
        if (!string.IsNullOrEmpty(codeOrToken))
        {
            try
            {
                _dispatcher.Post(() => doc.StatusText = "Pairing…");
                var deviceName = $"{Environment.MachineName} (desktop)";
                var paired = await Agnes.Client.DevicePairing.PairAsync(url, codeOrToken, deviceName);
                token = paired.Token;
            }
            catch
            {
                pairingFailed = true; // fall back to trying the entry as a direct token below.
            }
        }

        // Persist ONLY after a successful connection, so a wrong URL / expired code never gets saved.
        var host = new KnownHost(string.IsNullOrWhiteSpace(doc.NewHostName) ? url : doc.NewHostName.Trim(), url, token);
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
        var url = doc.NewHostUrl.Trim();
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

        var methods = await Agnes.Client.AuthDiscovery.GetMethodsAsync(url).ConfigureAwait(false);
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
        var url = doc.NewHostUrl.Trim();
        if (!IsValidHostUrl(url))
        {
            _dispatcher.Post(() => doc.StatusText = "Enter a host address like https://your-host:5099 first.");
            return;
        }

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
            var paired = await Agnes.Client.KeypairEnrollment.AuthenticateAsync(url, deviceName).ConfigureAwait(false);

            var host = new KnownHost(string.IsNullOrWhiteSpace(doc.NewHostName) ? url : doc.NewHostName.Trim(), url, paired.Token);
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
        var url = doc.NewHostUrl.Trim();
        if (!IsValidHostUrl(url))
        {
            _dispatcher.Post(() => doc.StatusText = "Enter a host address like https://your-host:5099 first.");
            return;
        }

        try
        {
            var methods = await Agnes.Client.AuthDiscovery.GetMethodsAsync(url).ConfigureAwait(false);
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
                .CompleteAsync(url, methods.GitHubClientId, code, deviceName).ConfigureAwait(false);

            // Same persist-on-successful-connect flow as AddHostAsync — never save a host we couldn't reach.
            var host = new KnownHost(string.IsNullOrWhiteSpace(doc.NewHostName) ? url : doc.NewHostName.Trim(), url, paired.Token);
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

    public async Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName, bool skipPermissions = false, string gitCredentialMode = "Off", bool useSandbox = true)
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
            var info = await doc.Host.OpenSessionAsync(adapterId, workingDirectory, skipPermissions: skipPermissions, mcpApproval: McpApproval, gitCredentialMode: gitCredentialMode, useSandbox: useSandbox);
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
                var status = await SandboxImageManagement.GetStatusAsync(url, doc.HostToken, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            _ = NegotiateCapabilitiesAsync(host);
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
        _dispatcher.Post(() => doc.ConnectionState = host.State);
        host.StateChanged += state => _dispatcher.Post(() => doc.ConnectionState = state);
        // Usage is per-session and flows through the session event stream (SessionDocument mirrors
        // its SessionViewModel.Usage) — not a host-level property.

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

        var fork = new SessionDocument(this)
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
