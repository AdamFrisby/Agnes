using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.Mvvm;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>An agent available on a connected host; opening it starts a session.</summary>
public sealed class AgentEntryViewModel
{
    public AgentEntryViewModel(HostConnection host, AgentInfo info, WorkspaceViewModel workspace)
    {
        Host = host;
        AdapterId = info.AdapterId;
        DisplayName = info.DisplayName;
        OpenCommand = new AsyncRelayCommand(() => workspace.OpenSessionAsync(this));
    }

    public HostConnection Host { get; }
    public string AdapterId { get; }
    public string DisplayName { get; }
    public string HostUrl => Host.HostUrl;

    public AsyncRelayCommand OpenCommand { get; }
}
