using System.Collections.ObjectModel;
using Agnes.Abstractions;
using Agnes.Protocol;
using Agnes.App.Mobile.Services;
using Agnes.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>One agent offered on the chosen host.</summary>
public sealed partial class AgentOption : ObservableObject
{
    public AgentOption(AgentInfo info)
    {
        Info = info;
        Name = info.DisplayName;
        AdapterId = info.AdapterId;
        Available = info.Available;
        Auth = info.Auth;
    }

    public AgentInfo Info { get; }

    public string Name { get; }

    public string AdapterId { get; }

    /// <summary>Whether the CLI is installed on the host. Unavailable agents are shown, greyed — knowing
    /// an agent exists but isn't installed is more useful than it silently missing.</summary>
    public bool Available { get; }

    public ProviderAuthStatus? Auth { get; }

    public bool HasAuth => Auth is not null;

    public bool IsSignedIn => Auth is { IsLoggedIn: true };

    public string AuthText => Auth is null ? string.Empty : Auth.IsLoggedIn ? "Signed in" : "Not signed in";

    public string Detail => Available ? AdapterId : $"{AdapterId} · not installed";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Starting a session: pick where it runs, what runs it, and how much rope it gets.
///
/// The desktop puts this in a tab that walks host → agent → options. On a phone it's one scrolling
/// form with the Start button pinned to the bottom, because scrolling back up to commit is the kind of
/// thing that makes an app feel like a website.
/// </summary>
public sealed partial class NewSessionPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;
    private readonly HostBook _hosts;
    private readonly SessionsViewModel _sessions;

    public NewSessionPageViewModel(IAppShell shell, HostBook hosts, SessionsViewModel sessions)
    {
        _shell = shell;
        _hosts = hosts;
        _sessions = sessions;
        _workingDirectory = shell.Settings.LastWorkingDirectory;

        Hosts = new ObservableCollection<HostLink>(hosts.Links);
        _selectedHost = hosts.Links.FirstOrDefault(h => h.IsOnline && !h.IsBuiltIn)
            ?? hosts.Links.FirstOrDefault(h => !h.IsBuiltIn)
            ?? hosts.Links.FirstOrDefault();

        SelectHostCommand = new AsyncRelayCommand<HostLink>(SelectHostAsync);
        SelectAgentCommand = new RelayCommand<AgentOption>(SelectAgent);
        StartCommand = new AsyncRelayCommand(StartAsync, () => SelectedAgent is { Available: true } && !IsStarting);
        AddHostCommand = new RelayCommand(() =>
        {
            _shell.Pop();
            _shell.Push(new ConnectPageViewModel(_shell, _hosts, _sessions));
        });
        ApplyProfileCommand = new RelayCommand<LaunchProfile>(ApplyProfile);
        SetPermissionCommand = new RelayCommand<string>(v =>
        {
            if (!PermissionsLocked)
            {
                SkipPermissions = v == "auto";
            }
        });
        SetGitCredentialCommand = new RelayCommand<string>(v => { if (v is not null) { GitCredentialMode = v; } });

        if (_selectedHost is not null)
        {
            _ = SelectHostAsync(_selectedHost);
        }
    }

    public override string Title => "New session";

    public ObservableCollection<HostLink> Hosts { get; }

    public bool HasChoiceOfHosts => Hosts.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostName))]
    private HostLink? _selectedHost;

    public string HostName => SelectedHost?.Name ?? "No host";

    public ObservableCollection<AgentOption> Agents { get; } = [];

    public ObservableCollection<LaunchProfile> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    [ObservableProperty]
    private AgentOption? _selectedAgent;

    partial void OnSelectedAgentChanged(AgentOption? value) => StartCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    private string _workingDirectory;

    [ObservableProperty]
    private bool _isLoadingAgents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    public bool HasStatus => Status.Length > 0;

    [ObservableProperty]
    private bool _isStarting;

    partial void OnIsStartingChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    // ---- how much rope ----

    /// <summary>Run tool calls without asking. Off by default, and forced off on a host that requires
    /// prompts — this is the setting most worth being conservative about from a phone, where you may not
    /// be watching.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionAsk))]
    [NotifyPropertyChangedFor(nameof(PermissionAuto))]
    private bool _skipPermissions;

    public bool PermissionAsk => !SkipPermissions;
    public bool PermissionAuto => SkipPermissions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionsLocked))]
    private bool _permissionPromptsRequired;

    public bool PermissionsLocked => PermissionPromptsRequired;

    [ObservableProperty]
    private bool _useSandbox = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SandboxLocked))]
    private bool _sandboxAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SandboxLocked))]
    private bool _sandboxRequired;

    public bool SandboxLocked => !SandboxAvailable || SandboxRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitOff))]
    [NotifyPropertyChangedFor(nameof(GitAsk))]
    [NotifyPropertyChangedFor(nameof(GitTrust))]
    private string _gitCredentialMode = "Ask";

    public bool GitOff => GitCredentialMode == "Off";
    public bool GitAsk => GitCredentialMode == "Ask";
    public bool GitTrust => GitCredentialMode == "Trust";

    public IAsyncRelayCommand<HostLink> SelectHostCommand { get; }
    public IRelayCommand<AgentOption> SelectAgentCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand AddHostCommand { get; }
    public IRelayCommand<LaunchProfile> ApplyProfileCommand { get; }
    public IRelayCommand<string> SetPermissionCommand { get; }
    public IRelayCommand<string> SetGitCredentialCommand { get; }

    private async Task SelectHostAsync(HostLink? link)
    {
        if (link is null)
        {
            return;
        }

        SelectedHost = link;
        _shell.Dispatcher.Post(() =>
        {
            IsLoadingAgents = true;
            Status = string.Empty;
            Agents.Clear();
            Profiles.Clear();
            SelectedAgent = null;
        });

        var host = await link.ConnectAsync().ConfigureAwait(false);
        if (host is null)
        {
            _shell.Dispatcher.Post(() =>
            {
                IsLoadingAgents = false;
                Status = link.Error is { Length: > 0 } e ? $"Can't reach {link.Name} — {e}" : $"Can't reach {link.Name}.";
            });
            return;
        }

        try
        {
            var agents = await host.ListAgentsAsync().ConfigureAwait(false);
            var info = await host.GetHostInfoAsync().ConfigureAwait(false);
            var profiles = await SafeProfilesAsync(host).ConfigureAwait(false);

            _shell.Dispatcher.Post(() =>
            {
                SandboxAvailable = info.SandboxAvailable;
                SandboxRequired = info.RequireSandbox;
                UseSandbox = info.SandboxAvailable;
                PermissionPromptsRequired = info.RequirePermissionPrompts;
                if (PermissionPromptsRequired)
                {
                    SkipPermissions = false;
                }

                foreach (var agent in agents)
                {
                    Agents.Add(new AgentOption(agent));
                }

                foreach (var profile in profiles)
                {
                    Profiles.Add(profile);
                }

                OnPropertyChanged(nameof(HasProfiles));
                SelectAgent(Agents.FirstOrDefault(a => a.Available));
                IsLoadingAgents = false;
            });
        }
        catch (Exception ex)
        {
            _shell.Dispatcher.Post(() =>
            {
                IsLoadingAgents = false;
                Status = "Couldn't list agents: " + ex.Message;
            });
        }
    }

    private static async Task<IReadOnlyList<LaunchProfile>> SafeProfilesAsync(IAgnesHost host)
    {
        try
        {
            return await host.GetLaunchProfilesAsync().ConfigureAwait(false);
        }
        catch
        {
            return []; // a host without the feature simply offers none
        }
    }

    private void SelectAgent(AgentOption? option)
    {
        if (option is null || !option.Available)
        {
            return;
        }

        foreach (var agent in Agents)
        {
            agent.IsSelected = ReferenceEquals(agent, option);
        }

        SelectedAgent = option;
    }

    private void ApplyProfile(LaunchProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory))
        {
            WorkingDirectory = profile.WorkingDirectory;
        }

        SkipPermissions = profile.SkipPermissions && !PermissionPromptsRequired;
        GitCredentialMode = profile.GitCredentialMode;
        UseSandbox = profile.UseSandbox && SandboxAvailable;
        SelectAgent(Agents.FirstOrDefault(a => a.AdapterId == profile.AdapterId && a.Available));
        _shell.Haptics.Tick();
        Status = $"Applied \"{profile.Name}\" — adjust anything, then start.";
    }

    private async Task StartAsync()
    {
        if (SelectedHost is not { } link || SelectedAgent is not { Available: true } agent)
        {
            return;
        }

        var directory = WorkingDirectory.Trim();
        if (directory.Length == 0)
        {
            _shell.Dispatcher.Post(() => Status = "Which folder should it run in?");
            return;
        }

        _shell.Dispatcher.Post(() =>
        {
            IsStarting = true;
            Status = $"Starting {agent.Name}…";
        });

        try
        {
            var host = await link.ConnectAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("host unreachable");

            var info = await host.OpenSessionAsync(
                agent.AdapterId,
                directory,
                skipPermissions: SkipPermissions,
                gitCredentialMode: GitCredentialMode,
                useSandbox: SandboxAvailable && UseSandbox).ConfigureAwait(false);

            var view = await host.SubscribeAsync(info.SessionId).ConfigureAwait(false);

            _shell.Dispatcher.Post(() =>
            {
                var title = info.WorkingDirectory;
                var session = _sessions.Build(host, view, title);
                var saved = new SavedSession(link.Name, link.Url, link.Saved.Token, info.SessionId,
                    agent.AdapterId, title, info.WorkingDirectory);

                ((ShellViewModel)_shell).UpdateSettings(s => s with { LastWorkingDirectory = directory });
                IsStarting = false;
                _shell.Haptics.Success();
                _shell.Pop();
                _sessions.Adopt(link, session, saved);
            });
        }
        catch (Exception ex)
        {
            _shell.Dispatcher.Post(() =>
            {
                IsStarting = false;
                Status = "Couldn't start: " + ex.Message;
            });
        }
    }
}
