using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.App.Desktop.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>Stages of a tab before it holds a live session.</summary>
public enum TabStage
{
    PickHost,
    PickAgent,

    /// <summary>Opening the session (may take a while — e.g. baking the sandbox image).</summary>
    Starting,
    Live,
}

/// <summary>Actions a tab delegates back to the workspace (host is a per-tab choice).</summary>
public interface ITabController
{
    Task<bool> SelectHostAsync(SessionDocument doc, KnownHost host);
    Task AddHostAsync(SessionDocument doc);

    /// <summary>Query which sign-in methods the entered host offers (GET /auth/methods) and update the tab.</summary>
    Task DiscoverAuthMethodsAsync(SessionDocument doc);

    /// <summary>Run the GitHub device-flow sign-in for the entered host, then connect + persist on success.</summary>
    Task SignInWithGitHubAsync(SessionDocument doc);

    /// <summary>Sign in with the client's keypair (shows the public line to authorize), then connect on success.</summary>
    Task SignInWithKeyAsync(SessionDocument doc);

    /// <summary>Remove a saved host from the picker (and persistence), then refresh the tab's host list.</summary>
    Task ForgetHostAsync(SessionDocument doc, KnownHost host);

    /// <summary>Whether a host can be removed by the user (built-in Simulated/Recorded hosts can't).</summary>
    bool IsForgettableHost(string url);
    Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName, bool skipPermissions = false, string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null);

    /// <summary>Loads the models offered for an agent (live or static) and reconciles them against the user's
    /// favorites, populating the tab's model picker. No-op when the host reports no models.</summary>
    Task LoadModelsAsync(SessionDocument doc, string adapterId);

    /// <summary>Toggles a model as a favorite for the tab's selected agent (pure client-side) and persists it.</summary>
    void ToggleModelFavorite(SessionDocument doc, ModelChoice model);

    /// <summary>Force a fresh (cache-bypassing) provider login-state check for one agent on the tab's host,
    /// returning its refreshed status (or null when the agent has no reliable signal).</summary>
    Task<ProviderAuthStatus?> CheckAgentAuthAsync(SessionDocument doc, string adapterId);
    void BackToHosts(SessionDocument doc);

    /// <summary>Persist tab metadata (rename / pin / tag) after an in-tab change.</summary>
    void PersistTabs();

    /// <summary>Archive the tab: remove it from the strip but keep it restorable.</summary>
    void ArchiveTab(SessionDocument doc);

    /// <summary>Open another tab on the same live session (a second client view).</summary>
    Task DuplicateAsync(SessionDocument doc);

    /// <summary>Open a fresh, empty session on the same host/agent carrying this one's launch config.</summary>
    Task NewSessionSameSetupAsync(SessionDocument source);

    /// <summary>Open a new tab that forks a fresh session from the same host and agent.</summary>
    Task ForkAsync(SessionDocument doc);

    /// <summary>Detach the tab into its own floating window (drag it back to re-dock).</summary>
    void FloatTab(SessionDocument doc);

    /// <summary>The working directory to prefill for a new session (last used, or the user's home).</summary>
    string DefaultWorkingDirectory { get; }

    /// <summary>Remembers the working directory a session was opened in, as the next default.</summary>
    void RememberWorkingDirectory(string path);
}

/// <summary>A cross-session search result: a transcript hit plus the tab it lives in.</summary>
public sealed class GlobalHit
{
    public GlobalHit(SessionDocument tab, Agnes.Ui.Core.ViewModels.SearchHit hit)
    {
        Tab = tab;
        Hit = hit;
    }

    public SessionDocument Tab { get; }
    public Agnes.Ui.Core.ViewModels.SearchHit Hit { get; }

    public string SessionTitle => Hit.SessionTitle ?? Tab.Title ?? "session";
    public string Kind => Hit.Kind;
    public string Snippet => Hit.Snippet;
}

/// <summary>A host option on the new-tab host picker.</summary>
public sealed class HostChoice
{
    public HostChoice(string name, string url, ICommand select, ICommand? forget = null)
    {
        Name = name;
        Url = url;
        Select = select;
        Forget = forget;
    }

    public string Name { get; }
    public string Url { get; }
    public ICommand Select { get; }

    /// <summary>Removes this host from the picker; null for built-in hosts that can't be removed.</summary>
    public ICommand? Forget { get; }

    public bool CanForget => Forget is not null;
}

/// <summary>An entry in the command palette (Ctrl+K): a session to jump to or a global action.</summary>
public sealed record PaletteItem(string Label, string Hint, System.Action Invoke);

/// <summary>A model option on the new-tab model picker (per the selected agent). Built from a reconciled
/// <see cref="Agnes.Ui.Core.ViewModels.ModelOption"/>: an unavailable one is a stale favorite the provider
/// dropped (shown, not selectable); the star toggles the favorite via the tab controller.</summary>
public sealed partial class ModelChoice : ObservableObject
{
    public ModelChoice(Agnes.Ui.Core.ViewModels.ModelOption option, System.Action<ModelChoice>? toggleFavorite = null)
    {
        Id = option.Id;
        DisplayName = option.DisplayName;
        IsCustomEntryAllowed = option.IsCustomEntryAllowed;
        IsAvailable = option.IsAvailable;
        _isFavorite = option.IsFavorite;
        ToggleFavoriteCommand = new RelayCommand(() => toggleFavorite?.Invoke(this), () => IsAvailable);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public bool IsCustomEntryAllowed { get; }

    /// <summary>Whether the model is in the current catalog. A favorited-but-removed model is shown as a
    /// visible "no longer available" row rather than silently offered as working.</summary>
    public bool IsAvailable { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteGlyph))]
    private bool _isFavorite;

    /// <summary>Highlighted as the chosen model (one at a time across the list).</summary>
    [ObservableProperty]
    private bool _isSelected;

    public IRelayCommand ToggleFavoriteCommand { get; }

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
}

/// <summary>An agent option on the new-tab agent picker. Selectable — picking it highlights the row;
/// the session only opens when the user presses "Start session" (no surprise auto-progress).</summary>
public sealed partial class AgentChoice : ObservableObject
{
    // Forces a fresh (cache-bypassing) auth check on the host and returns the refreshed status, or null when
    // the agent has no reliable login signal. Null delegate = this host doesn't support auth checks.
    private readonly Func<Task<ProviderAuthStatus?>>? _checkAuth;

    public AgentChoice(string displayName, string adapterId, bool available = true,
        ProviderAuthStatus? auth = null, Func<Task<ProviderAuthStatus?>>? checkAuth = null)
    {
        DisplayName = displayName;
        AdapterId = adapterId;
        Available = available;
        _auth = auth;
        _checkAuth = checkAuth;
        CheckAuthCommand = new AsyncRelayCommand(CheckAuthAsync, () => _checkAuth is not null && !IsChecking);
    }

    public string DisplayName { get; }
    public string AdapterId { get; }

    /// <summary>Whether the agent's CLI is installed on the host. Unavailable agents can't be opened.</summary>
    public bool Available { get; }

    /// <summary>Highlighted as the chosen agent (one at a time across the list).</summary>
    [ObservableProperty]
    private bool _isSelected;

    public string StatusText => Available ? AdapterId : $"{AdapterId} · not installed on host";

    // ---- provider login state (only shown when the adapter reports a reliable signal) ----

    /// <summary>The CLI's machine-local login state, or null when the adapter has no reliable signal (no badge).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuth))]
    [NotifyPropertyChangedFor(nameof(IsLoggedIn))]
    [NotifyPropertyChangedFor(nameof(IsLoggedOut))]
    [NotifyPropertyChangedFor(nameof(AuthBadgeText))]
    [NotifyPropertyChangedFor(nameof(AuthTooltip))]
    private ProviderAuthStatus? _auth;

    /// <summary>True while a "Check now" probe is in flight (disables the button, shows progress).</summary>
    [ObservableProperty]
    private bool _isChecking;

    partial void OnIsCheckingChanged(bool value) => CheckAuthCommand.NotifyCanExecuteChanged();

    /// <summary>Forces a fresh login-state check (bypassing any cached status).</summary>
    public IAsyncRelayCommand CheckAuthCommand { get; }

    /// <summary>Whether this host can check login state at all (drives the "Check now" button's visibility).</summary>
    public bool CanCheckAuth => _checkAuth is not null;

    /// <summary>Whether there's a login badge to show — false means "no reliable signal", so show nothing.</summary>
    public bool HasAuth => Auth is not null;

    public bool IsLoggedIn => Auth is { IsLoggedIn: true };
    public bool IsLoggedOut => Auth is { IsLoggedIn: false };

    public string AuthBadgeText => Auth is null ? string.Empty : Auth.IsLoggedIn ? "signed in" : "not signed in";

    public string? AuthTooltip => Auth switch
    {
        null => null,
        { IsLoggedIn: true } a => a.Method is { Length: > 0 } m ? $"Signed in ({m})" : "Signed in",
        { Issue: { Length: > 0 } issue } => issue,
        _ => "Not signed in",
    };

    private async Task CheckAuthAsync()
    {
        if (_checkAuth is null)
        {
            return;
        }

        IsChecking = true;
        try
        {
            Auth = await _checkAuth().ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
        }
    }
}
