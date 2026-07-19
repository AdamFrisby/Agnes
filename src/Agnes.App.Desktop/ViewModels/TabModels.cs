using System.Windows.Input;
using Agnes.App.Desktop.Persistence;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>Stages of a tab before it holds a live session.</summary>
public enum TabStage
{
    PickHost,
    PickAgent,
    Live,
}

/// <summary>Actions a tab delegates back to the workspace (host is a per-tab choice).</summary>
public interface ITabController
{
    Task SelectHostAsync(SessionDocument doc, KnownHost host);
    Task AddHostAsync(SessionDocument doc);
    Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName, bool skipPermissions = false);
    void BackToHosts(SessionDocument doc);

    /// <summary>Persist tab metadata (rename / pin / tag) after an in-tab change.</summary>
    void PersistTabs();

    /// <summary>Archive the tab: remove it from the strip but keep it restorable.</summary>
    void ArchiveTab(SessionDocument doc);

    /// <summary>Open another tab on the same live session (a second client view).</summary>
    Task DuplicateAsync(SessionDocument doc);

    /// <summary>Open a new tab that forks a fresh session from the same host and agent.</summary>
    Task ForkAsync(SessionDocument doc);
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
    public HostChoice(string name, string url, ICommand select)
    {
        Name = name;
        Url = url;
        Select = select;
    }

    public string Name { get; }
    public string Url { get; }
    public ICommand Select { get; }
}

/// <summary>An entry in the command palette (Ctrl+K): a session to jump to or a global action.</summary>
public sealed record PaletteItem(string Label, string Hint, System.Action Invoke);

/// <summary>An agent option on the new-tab agent picker.</summary>
public sealed class AgentChoice
{
    public AgentChoice(string displayName, string adapterId, ICommand open, bool available = true)
    {
        DisplayName = displayName;
        AdapterId = adapterId;
        Open = open;
        Available = available;
    }

    public string DisplayName { get; }
    public string AdapterId { get; }
    public ICommand Open { get; }

    /// <summary>Whether the agent's CLI is installed on the host. Unavailable agents can't be opened.</summary>
    public bool Available { get; }

    public string StatusText => Available ? AdapterId : $"{AdapterId} · not installed on host";
}
