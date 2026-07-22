using Agnes.Host.Plugins;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace Agnes.Host.Hosting;

/// <summary>SignalR endpoint implementing the Agnes wire contract (<see cref="IAgnesServer"/>).</summary>
public sealed class AgnesHub : Hub<IAgnesClient>, IAgnesServer
{
    private readonly SessionManager _sessions;
    private readonly ScheduledTaskManager _schedule;
    private readonly HostIdentity _identity;
    private readonly DeviceRegistry _tokens;
    private readonly PluginManagementService _plugins;
    private readonly ClientCapabilityStore _clientCaps;

    public AgnesHub(SessionManager sessions, ScheduledTaskManager schedule, HostIdentity identity, DeviceRegistry tokens, PluginManagementService plugins, ClientCapabilityStore clientCaps)
    {
        _sessions = sessions;
        _schedule = schedule;
        _identity = identity;
        _tokens = tokens;
        _plugins = plugins;
        _clientCaps = clientCaps;
    }

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query[WireProtocol.TokenParameter].ToString();
        if (!_tokens.IsValid(token))
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _clientCaps.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task<HostInfo> GetHostInfo()
        => Task.FromResult(new HostInfo(_identity.HostId, _identity.DisplayName, _identity.Version, _sessions.SandboxAvailable));

    public Task<IReadOnlyList<AgentInfo>> ListAgents()
        => Task.FromResult(_sessions.ListAgents());

    public Task<IReadOnlyList<HostCapability>> GetCapabilities()
        => Task.FromResult(HostCapabilityList());

    private IReadOnlyList<HostCapability> HostCapabilityList()
    {
        var caps = _sessions.GetCapabilities().ToList();
        // Plugin management is always available on a host built with the installer wired up (it is,
        // unconditionally, in Program.cs). Fail-open: a client without it just hides the Plugins screen.
        caps.Add(new HostCapability(HostCapabilityIds.PluginManagement, Available: true, FailClosed: false));
        return caps;
    }

    public Task<NegotiatedCapabilities> Negotiate(ClientCapabilities client)
    {
        _clientCaps.Set(Context.ConnectionId, client);
        return Task.FromResult(CapabilityNegotiator.Reconcile(HostCapabilityList(), client));
    }

    public Task<SessionInfo> OpenSession(OpenSessionRequest request)
        => _sessions.OpenSessionAsync(request.AdapterId, request.WorkingDirectory, request.UseWorktree, request.SkipPermissions, request.McpApproval, request.GitCredentialMode, request.UseSandbox);

    public Task<ForkPlan?> ProposeFork(string sessionId)
        => Task.FromResult(_sessions.ProposeFork(sessionId));

    public Task<SessionInfo> ForkSession(ForkSessionRequest request)
        => _sessions.ForkSessionAsync(request.SourceSessionId, request.TargetDirectory, request.CopySandbox);

    public async Task<SessionSnapshot> Subscribe(string sessionId, long sinceSequence)
    {
        // Join the group BEFORE snapshotting so no event is missed; the client dedupes by
        // sequence, so an event that both lands in the snapshot and is broadcast is harmless.
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        return await _sessions.GetSnapshotAsync(sessionId, sinceSequence);
    }

    public Task Unsubscribe(string sessionId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);

    public Task Prompt(PromptRequest request)
        => _sessions.PromptAsync(request.SessionId, request.Content);

    public Task Cancel(string sessionId)
        => _sessions.CancelAsync(sessionId);

    public Task RestartAgent(string sessionId)
        => _sessions.RestartAgentAsync(sessionId);

    public Task SetMode(string sessionId, string modeId)
        => _sessions.SetModeAsync(sessionId, modeId);

    public Task<GitStatus> GetGitStatus(string sessionId)
        => _sessions.GetGitStatusAsync(sessionId);

    public Task<GitCommitResult> GitCommit(string sessionId, string message)
        => _sessions.GitCommitAsync(sessionId, message);

    public Task<ScheduledTask> ScheduleTask(ScheduleTaskRequest request)
        => _schedule.AddAsync(request);

    public Task<IReadOnlyList<ScheduledTask>> ListScheduledTasks()
        => Task.FromResult(_schedule.List());

    public Task RemoveScheduledTask(string taskId)
        => _schedule.RemoveAsync(taskId);

    public Task<IReadOnlyList<InboxRun>> GetInbox()
        => Task.FromResult(_schedule.Inbox());

    public Task RespondPermission(PermissionResponseRequest response)
        => _sessions.RespondPermissionAsync(response.SessionId, response.RequestId, response.OptionId);

    public Task AnswerQuestion(QuestionAnswerRequest response)
        => _sessions.AnswerQuestionAsync(response.SessionId, response.RequestId, response.Answers);

    public Task PauseSandbox(string sessionId) => _sessions.PauseSandboxAsync(sessionId);

    public Task ResumeSandbox(string sessionId) => _sessions.ResumeSandboxAsync(sessionId);

    public Task DeleteSandbox(string sessionId) => _sessions.DeleteSandboxAsync(sessionId);

    public Task StopSession(string sessionId) => _sessions.StopSessionAsync(sessionId);

    public Task<SandboxStatus?> GetSandboxStatus(string sessionId)
        => Task.FromResult(_sessions.GetSandboxStatus(sessionId));

    public Task<IReadOnlyList<PluginSearchResultDto>> SearchPlugins(string query)
        => _plugins.SearchAsync(query);

    public Task<PluginInstallOutcome> InstallPlugin(InstallPluginRequest request)
        => _plugins.InstallAsync(request);

    public Task<PluginInstallOutcome> UpdatePlugin(string pluginId, IReadOnlyList<string> grantedCapabilities)
        => _plugins.UpdateAsync(pluginId, grantedCapabilities);

    public Task SetPluginEnabled(string pluginId, bool enabled)
        => _plugins.SetEnabledAsync(pluginId, enabled);

    public Task ConfigurePlugin(string pluginId, IReadOnlyDictionary<string, string> settings)
        => _plugins.ConfigureAsync(pluginId, settings);

    public Task UninstallPlugin(string pluginId)
        => _plugins.UninstallAsync(pluginId);

    public Task<IReadOnlyList<InstalledPluginDto>> ListInstalledPlugins()
        => _plugins.ListInstalledAsync();
}
