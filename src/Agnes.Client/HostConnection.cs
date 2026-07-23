using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Protocol;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace Agnes.Client;

/// <summary>
/// A live connection to one Agnes host: invokes the wire contract, receives pushed events,
/// and maintains a <see cref="SessionView"/> per subscribed session (with snapshot+tail and
/// automatic reconnect). Multiple of these are pooled by <see cref="AgnesClient"/>.
/// </summary>
public sealed class HostConnection : IAgnesHost
{
    private readonly HubConnection _hub;
    private readonly ConcurrentDictionary<string, SessionView> _views = new();

    public HostConnection(string hostUrl, string token, Action<HttpConnectionOptions>? configureHttp = null)
    {
        HostUrl = hostUrl.TrimEnd('/');
        var url = $"{HostUrl}{WireProtocol.HubPath}?{WireProtocol.TokenParameter}={Uri.EscapeDataString(token)}";
        _hub = new HubConnectionBuilder()
            .WithUrl(url, options => configureHttp?.Invoke(options))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<string, SessionEvent>(nameof(IAgnesClient.OnSessionEvent), (sessionId, @event) =>
        {
            if (_views.TryGetValue(sessionId, out var view))
            {
                view.Apply(@event);
            }
        });

        _hub.On<IReadOnlyList<AgentInfo>>(nameof(IAgnesClient.OnAgentsChanged), agents =>
        {
            AgentsChanged?.Invoke(agents);
        });

        _hub.On<string, long, bool>(nameof(IAgnesClient.OnReadState), (sessionId, readSequence, stickyUnread) =>
        {
            ReadStateChanged?.Invoke(sessionId, readSequence, stickyUnread);
            return Task.CompletedTask;
        });

        _hub.On<InboxRun>(nameof(IAgnesClient.OnInboxRun), run =>
        {
            InboxRunReceived?.Invoke(run);
        });

        _hub.Reconnecting += _ => { RaiseState(AgnesConnectionState.Reconnecting); return Task.CompletedTask; };
        _hub.Closed += _ => { RaiseState(AgnesConnectionState.Disconnected); return Task.CompletedTask; };

        // On reconnect, re-subscribe each view from its last applied sequence.
        _hub.Reconnected += async _ =>
        {
            RaiseState(AgnesConnectionState.Connected);
            foreach (var view in _views.Values)
            {
                var snapshot = await _hub.InvokeAsync<SessionSnapshot>(
                    nameof(IAgnesServer.Subscribe), view.SessionId, view.LastSequence);
                view.ApplySnapshot(snapshot);
            }
        };
    }

    public string HostUrl { get; }

    public AgnesConnectionState State => _hub.State switch
    {
        HubConnectionState.Connected => AgnesConnectionState.Connected,
        HubConnectionState.Connecting => AgnesConnectionState.Connecting,
        HubConnectionState.Reconnecting => AgnesConnectionState.Reconnecting,
        _ => AgnesConnectionState.Disconnected,
    };

    public event Action<AgnesConnectionState>? StateChanged;

    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        RaiseState(AgnesConnectionState.Connecting);
        await _hub.StartAsync(cancellationToken).ConfigureAwait(false);
        RaiseState(AgnesConnectionState.Connected);
    }

    private void RaiseState(AgnesConnectionState state) => StateChanged?.Invoke(state);

    public Task<HostInfo> GetHostInfoAsync()
        => _hub.InvokeAsync<HostInfo>(nameof(IAgnesServer.GetHostInfo));

    public Task<IReadOnlyList<AgentInfo>> ListAgentsAsync()
        => _hub.InvokeAsync<IReadOnlyList<AgentInfo>>(nameof(IAgnesServer.ListAgents));

    public Task<AgentInfo> CheckAuthStatusAsync(string adapterId)
        => _hub.InvokeAsync<AgentInfo>(nameof(IAgnesServer.CheckAuthStatus), adapterId);

    public Task<IReadOnlyList<HostCapability>> GetCapabilitiesAsync()
        => _hub.InvokeAsync<IReadOnlyList<HostCapability>>(nameof(IAgnesServer.GetCapabilities));

    public Task<NegotiatedCapabilities> NegotiateAsync(ClientCapabilities client)
        => _hub.InvokeAsync<NegotiatedCapabilities>(nameof(IAgnesServer.Negotiate), client);

    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true)
        => _hub.InvokeAsync<SessionInfo>(nameof(IAgnesServer.OpenSession), new OpenSessionRequest(adapterId, workingDirectory, useWorktree, skipPermissions, mcpApproval, gitCredentialMode, useSandbox));

    public Task<ForkPlan?> ProposeForkAsync(string sessionId)
        => _hub.InvokeAsync<ForkPlan?>(nameof(IAgnesServer.ProposeFork), sessionId);

    public Task<SessionInfo> ForkSessionAsync(string sourceSessionId, string targetDirectory, bool copySandbox = true)
        => _hub.InvokeAsync<SessionInfo>(nameof(IAgnesServer.ForkSession), new ForkSessionRequest(sourceSessionId, targetDirectory, copySandbox));

    /// <summary>Subscribes to a session, returning a live view seeded from a snapshot.</summary>
    public async Task<SessionView> SubscribeAsync(string sessionId, long since = 0)
    {
        var view = _views.GetOrAdd(sessionId, id => new SessionView(id));
        var snapshot = await _hub.InvokeAsync<SessionSnapshot>(nameof(IAgnesServer.Subscribe), sessionId, since);
        view.ApplySnapshot(snapshot);
        return view;
    }

    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content)
        => _hub.InvokeAsync(nameof(IAgnesServer.Prompt), new PromptRequest(sessionId, content));

    public Task CancelAsync(string sessionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.Cancel), sessionId);

    public Task RestartAgentAsync(string sessionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RestartAgent), sessionId);

    public Task SetModeAsync(string sessionId, string modeId)
        => _hub.InvokeAsync(nameof(IAgnesServer.SetMode), sessionId, modeId);

    public Task<GitStatus> GetGitStatusAsync(string sessionId)
        => _hub.InvokeAsync<GitStatus>(nameof(IAgnesServer.GetGitStatus), sessionId);

    public Task<GitCommitResult> GitCommitAsync(string sessionId, string message)
        => _hub.InvokeAsync<GitCommitResult>(nameof(IAgnesServer.GitCommit), sessionId, message);

    public Task<IReadOnlyList<Abstractions.ReviewComment>> ListReviewCommentsAsync(string projectId)
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.ReviewComment>>(nameof(IAgnesServer.ListReviewComments), projectId);

    public Task<Abstractions.ReviewComment> AddReviewCommentAsync(AddReviewCommentRequest request)
        => _hub.InvokeAsync<Abstractions.ReviewComment>(nameof(IAgnesServer.AddReviewComment), request);

    public Task RemoveReviewCommentAsync(string id)
        => _hub.InvokeAsync(nameof(IAgnesServer.RemoveReviewComment), id);

    public Task<string> UploadAttachmentAsync(string sessionId, string fileName, byte[] data)
        => _hub.InvokeAsync<string>(nameof(IAgnesServer.UploadAttachment), sessionId, fileName, data);

    public Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request)
        => _hub.InvokeAsync<ScheduledTask>(nameof(IAgnesServer.ScheduleTask), request);

    public Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync()
        => _hub.InvokeAsync<IReadOnlyList<ScheduledTask>>(nameof(IAgnesServer.ListScheduledTasks));

    public Task RemoveScheduledTaskAsync(string taskId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RemoveScheduledTask), taskId);

    public Task<IReadOnlyList<InboxRun>> GetInboxAsync()
        => _hub.InvokeAsync<IReadOnlyList<InboxRun>>(nameof(IAgnesServer.GetInbox));

    public event Action<InboxRun>? InboxRunReceived;
    public event Action<string, long, bool>? ReadStateChanged;

    public Task MarkSessionReadAsync(string sessionId, long sequence)
        => _hub.InvokeAsync(nameof(IAgnesServer.MarkSessionRead), sessionId, sequence);

    public Task MarkSessionUnreadAsync(string sessionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.MarkSessionUnread), sessionId);

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RespondPermission), new PermissionResponseRequest(sessionId, requestId, optionId));

    public Task AnswerQuestionAsync(string sessionId, string requestId, IReadOnlyList<Agnes.Abstractions.QuestionAnswer> answers)
        => _hub.InvokeAsync(nameof(IAgnesServer.AnswerQuestion), new QuestionAnswerRequest(sessionId, requestId,
            answers.Select(a => new QuestionAnswerDto(a.QuestionId, a.SelectedLabels, a.Notes)).ToList()));

    public Task PauseSandboxAsync(string sessionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.PauseSandbox), sessionId);

    public Task ResumeSandboxAsync(string sessionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.ResumeSandbox), sessionId);

    public Task DeleteSandboxAsync(string sessionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.DeleteSandbox), sessionId);

    public Task StopSessionAsync(string sessionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.StopSession), sessionId);

    public Task<SandboxStatus?> GetSandboxStatusAsync(string sessionId)
        => _hub.InvokeAsync<SandboxStatus?>(nameof(IAgnesServer.GetSandboxStatus), sessionId);

    // ---- plugin management (see .ideas/00-plugin-architecture.md) ----
    // Forwarded over the wire so a paired client drives the host's plugin lifecycle exactly as a local
    // operator would (AC12). Overrides the "not available" defaults on IAgnesHost.

    public Task<IReadOnlyList<PluginSearchResultDto>> SearchPluginsAsync(string query)
        => _hub.InvokeAsync<IReadOnlyList<PluginSearchResultDto>>(nameof(IAgnesServer.SearchPlugins), query);

    public Task<PluginInstallOutcome> InstallPluginAsync(InstallPluginRequest request)
        => _hub.InvokeAsync<PluginInstallOutcome>(nameof(IAgnesServer.InstallPlugin), request);

    public Task<PluginInstallOutcome> UpdatePluginAsync(string pluginId, IReadOnlyList<string> grantedCapabilities)
        => _hub.InvokeAsync<PluginInstallOutcome>(nameof(IAgnesServer.UpdatePlugin), pluginId, grantedCapabilities);

    public Task SetPluginEnabledAsync(string pluginId, bool enabled)
        => _hub.InvokeAsync(nameof(IAgnesServer.SetPluginEnabled), pluginId, enabled);

    public Task ConfigurePluginAsync(string pluginId, IReadOnlyDictionary<string, string> settings)
        => _hub.InvokeAsync(nameof(IAgnesServer.ConfigurePlugin), pluginId, settings);

    public Task UninstallPluginAsync(string pluginId)
        => _hub.InvokeAsync(nameof(IAgnesServer.UninstallPlugin), pluginId);

    public Task<IReadOnlyList<InstalledPluginDto>> ListInstalledPluginsAsync()
        => _hub.InvokeAsync<IReadOnlyList<InstalledPluginDto>>(nameof(IAgnesServer.ListInstalledPlugins));

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
