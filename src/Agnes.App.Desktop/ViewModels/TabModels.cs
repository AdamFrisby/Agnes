using System.Windows.Input;
using Agnes.App.Desktop.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;

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

    /// <summary>Remove a saved host from the picker (and persistence), then refresh the tab's host list.</summary>
    Task ForgetHostAsync(SessionDocument doc, KnownHost host);

    /// <summary>Whether a host can be removed by the user (built-in Simulated/Recorded hosts can't).</summary>
    bool IsForgettableHost(string url);
    Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName, bool skipPermissions = false, string gitCredentialMode = "Off");
    void BackToHosts(SessionDocument doc);

    /// <summary>Persist tab metadata (rename / pin / tag) after an in-tab change.</summary>
    void PersistTabs();

    /// <summary>Archive the tab: remove it from the strip but keep it restorable.</summary>
    void ArchiveTab(SessionDocument doc);

    /// <summary>Open another tab on the same live session (a second client view).</summary>
    Task DuplicateAsync(SessionDocument doc);

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

/// <summary>An agent option on the new-tab agent picker. Selectable — picking it highlights the row;
/// the session only opens when the user presses "Start session" (no surprise auto-progress).</summary>
public sealed partial class AgentChoice : ObservableObject
{
    public AgentChoice(string displayName, string adapterId, bool available = true)
    {
        DisplayName = displayName;
        AdapterId = adapterId;
        Available = available;
    }

    public string DisplayName { get; }
    public string AdapterId { get; }

    /// <summary>Whether the agent's CLI is installed on the host. Unavailable agents can't be opened.</summary>
    public bool Available { get; }

    /// <summary>Highlighted as the chosen agent (one at a time across the list).</summary>
    [ObservableProperty]
    private bool _isSelected;

    public string StatusText => Available ? AdapterId : $"{AdapterId} · not installed on host";
}
