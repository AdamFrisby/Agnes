using System.Collections.ObjectModel;
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
        SettingsCategories =
        [
            new SettingsCategoryVm("appearance", "Appearance", "🎨", "theme dark light system ui scale zoom accessibility reduce motion font density"),
            new SettingsCategoryVm("devices", "Devices", "🔑", "paired devices pairing token revoke auth access per-device"),
            new SettingsCategoryVm("mcp", "MCP servers", "🧩", "mcp model context protocol tools server forward sandbox host approval ask trust"),
            new SettingsCategoryVm("github", "GitHub", "⑂", "github git push credential token connect app scope repo installation secret"),
            new SettingsCategoryVm("sandbox", "Sandbox image", "📦", "sandbox image bake incus vm packages node apt npm pip agents baseline"),
            new SettingsCategoryVm("keyboard", "Keyboard", "⌨", "keyboard shortcuts keys bindings gestures"),
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
        RunTopPaletteItemCommand = new RelayCommand(() => RunPaletteItem(PaletteItems.FirstOrDefault()));
        ClosePaletteCommand = new RelayCommand(() => IsPaletteOpen = false);
        OpenUpdateCommand = new RelayCommand(OpenUpdate);
        SetScaleCommand = new RelayCommand<string>(s =>
        {
            FontScale = s switch { "small" => 0.9, "large" => 1.2, _ => 1.0 };
        });
        _factory.ActiveDockableChanged += (_, _) => UpdateWindowTitle();
    }

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
        => _factory.DocumentDock?.ActiveDockable is SessionDocument { Host: { } host } doc
           && host.HostUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? (host.HostUrl, doc.HostToken)
            : null;

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
    public IRelayCommand<string> SetSettingsCategoryCommand { get; }
    public System.Collections.ObjectModel.ObservableCollection<SettingsCategoryVm> SettingsCategories { get; }

    [ObservableProperty] private string _settingsSearch = string.Empty;
    [ObservableProperty] private string _settingsCategory = "appearance";

    public bool CatAppearance => SettingsCategory == "appearance";
    public bool CatDevices => SettingsCategory == "devices";
    public bool CatMcp => SettingsCategory == "mcp";
    public bool CatGitHub => SettingsCategory == "github";
    public bool CatSandbox => SettingsCategory == "sandbox";
    public bool CatKeyboard => SettingsCategory == "keyboard";

    partial void OnSettingsCategoryChanged(string value)
    {
        foreach (var c in SettingsCategories)
        {
            c.IsSelected = c.Id == value;
        }

        OnPropertyChanged(nameof(CatAppearance));
        OnPropertyChanged(nameof(CatDevices));
        OnPropertyChanged(nameof(CatMcp));
        OnPropertyChanged(nameof(CatGitHub));
        OnPropertyChanged(nameof(CatSandbox));
        OnPropertyChanged(nameof(CatKeyboard));
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

    public async Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName, bool skipPermissions = false, string gitCredentialMode = "Off")
    {
        if (doc.Host is null)
        {
            return;
        }

        var workingDirectory = string.IsNullOrWhiteSpace(doc.WorkingDirectory) ? DefaultWorkingDirectory : doc.WorkingDirectory.Trim();
        RememberWorkingDirectory(workingDirectory);
        var info = await doc.Host.OpenSessionAsync(adapterId, workingDirectory, skipPermissions: skipPermissions, mcpApproval: McpApproval, gitCredentialMode: gitCredentialMode);
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

            // New session on the same host/agent and project, isolated in its own git worktree so
            // the two sessions can run in parallel without colliding.
            var forkDirectory = string.IsNullOrWhiteSpace(doc.WorkingDirectory) ? DefaultWorkingDirectory : doc.WorkingDirectory.Trim();
            var info = await doc.Host.OpenSessionAsync(descriptor.AdapterId, forkDirectory, useWorktree: true);
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
