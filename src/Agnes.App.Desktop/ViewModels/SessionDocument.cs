using System.Collections.ObjectModel;
using Agnes.App.Desktop.Persistence;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>One browser-style tab: an agent picker until connected, then a live session.</summary>
public sealed partial class SessionDocument : Document
{
    [ObservableProperty]
    private SessionViewModel? _session;

    [ObservableProperty]
    private bool _isPicking = true;

    [ObservableProperty]
    private string _statusText = "Connecting…";

    [ObservableProperty]
    private ObservableCollection<AgentChoice>? _agents;

    /// <summary>Set once connected; used to persist/restore this tab.</summary>
    public SessionDescriptor? Descriptor { get; set; }

    public void ShowAgents(IEnumerable<AgentChoice> choices)
    {
        Agents = new ObservableCollection<AgentChoice>(choices);
        IsPicking = true;
        StatusText = "Pick an agent to start";
    }

    public void AttachSession(SessionViewModel session)
    {
        Session = session;
        IsPicking = false;
        StatusText = "Connected";
    }
}

/// <summary>An agent option shown on the new-tab picker.</summary>
public sealed class AgentChoice
{
    public AgentChoice(string displayName, string adapterId, IAsyncRelayCommand open)
    {
        DisplayName = displayName;
        AdapterId = adapterId;
        Open = open;
    }

    public string DisplayName { get; }
    public string AdapterId { get; }
    public IAsyncRelayCommand Open { get; }
}
