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
    Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName);
    void BackToHosts(SessionDocument doc);
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

/// <summary>An agent option on the new-tab agent picker.</summary>
public sealed class AgentChoice
{
    public AgentChoice(string displayName, string adapterId, ICommand open)
    {
        DisplayName = displayName;
        AdapterId = adapterId;
        Open = open;
    }

    public string DisplayName { get; }
    public string AdapterId { get; }
    public ICommand Open { get; }
}
