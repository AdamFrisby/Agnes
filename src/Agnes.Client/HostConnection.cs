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

        // A relay address (agnes-relay://relay/hostId?fp=...) tunnels the same SignalR wire + bearer token
        // through the blind relay to the host, pinning the host's advertised cert fingerprint (AC2/AC4/AC5).
        // A direct https/http URL keeps today's behavior untouched (AC1). The transport is chosen purely from
        // the address scheme, so a caller adds a host the same way regardless of how it's reachable.
        Transport = ClientTransport.Classify(HostUrl);
        string baseUrl = HostUrl;
        Action<HttpConnectionOptions>? relayConfigure = null;
        if (RelayClientTransport.IsRelayAddress(HostUrl))
        {
            RelayClientAddress relay = RelayClientTransport.Parse(HostUrl);
            HostId = relay.HostId;
            (baseUrl, relayConfigure) = RelayClientTransport.Build(HostUrl);
        }
        else
        {
            HostId = HostUrl;
        }

        var url = $"{baseUrl}{WireProtocol.HubPath}?{WireProtocol.TokenParameter}={Uri.EscapeDataString(token)}";
        _hub = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                relayConfigure?.Invoke(options);
                configureHttp?.Invoke(options);
            })
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

    /// <summary>Stable identity of this host: the routed host-id for a relay connection, else the URL.</summary>
    public string HostId { get; }

    /// <summary>The transport this connection uses, chosen from the address scheme (Direct / Relay / Tailscale).</summary>
    public ClientTransportKind Transport { get; }

    /// <summary>The sessions this connection currently holds a live view of (one per <see cref="SubscribeAsync"/>).</summary>
    public IReadOnlyCollection<SessionView> Sessions => _views.Values.ToArray();

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

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string adapterId)
        => _hub.InvokeAsync<IReadOnlyList<ModelInfo>>(nameof(IAgnesServer.ListModels), adapterId);

    public Task<IReadOnlyList<HostCapability>> GetCapabilitiesAsync()
        => _hub.InvokeAsync<IReadOnlyList<HostCapability>>(nameof(IAgnesServer.GetCapabilities));

    public Task<NegotiatedCapabilities> NegotiateAsync(ClientCapabilities client)
        => _hub.InvokeAsync<NegotiatedCapabilities>(nameof(IAgnesServer.Negotiate), client);

    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null)
        => _hub.InvokeAsync<SessionInfo>(nameof(IAgnesServer.OpenSession), new OpenSessionRequest(adapterId, workingDirectory, useWorktree, skipPermissions, mcpApproval, gitCredentialMode, useSandbox, modelId));

    public Task<IReadOnlyList<LaunchProfile>> GetLaunchProfilesAsync()
        => _hub.InvokeAsync<IReadOnlyList<LaunchProfile>>(nameof(IAgnesServer.GetLaunchProfiles));

    public Task<LaunchProfile> SaveLaunchProfileAsync(LaunchProfile profile)
        => _hub.InvokeAsync<LaunchProfile>(nameof(IAgnesServer.SaveLaunchProfile), profile);

    public Task DeleteLaunchProfileAsync(string id)
        => _hub.InvokeAsync(nameof(IAgnesServer.DeleteLaunchProfile), id);

    public Task<SessionInfo> OpenSessionFromProfileAsync(string profileId, string? workingDirectoryOverride = null)
        => _hub.InvokeAsync<SessionInfo>(nameof(IAgnesServer.OpenSessionFromProfile), new OpenSessionFromProfileRequest(profileId, workingDirectoryOverride));
    public Task<IReadOnlyList<ExternalSessionInfo>> DiscoverExternalSessionsAsync(string workspaceDirectory)
        => _hub.InvokeAsync<IReadOnlyList<ExternalSessionInfo>>(nameof(IAgnesServer.DiscoverExternalSessions), workspaceDirectory);

    public Task<SessionInfo> AttachExternalSessionAsync(string adapterId, string externalId)
        => _hub.InvokeAsync<SessionInfo>(nameof(IAgnesServer.AttachExternalSession), adapterId, externalId);

    public Task<ForkPlan?> ProposeForkAsync(string sessionId)
        => _hub.InvokeAsync<ForkPlan?>(nameof(IAgnesServer.ProposeFork), sessionId);

    public Task<ForkAtResult> ForkSessionAtAsync(string sourceSessionId, string targetDirectory, long atSequence, bool copySandbox = true)
        => _hub.InvokeAsync<ForkAtResult>(nameof(IAgnesServer.ForkSessionAt), new ForkAtRequest(sourceSessionId, targetDirectory, atSequence, copySandbox));

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

    public Task<string> OpenTerminalAsync(string sessionId, string? command = null, IReadOnlyList<string>? arguments = null, string? workingDirectory = null, int columns = 120, int rows = 30)
        => _hub.InvokeAsync<string>(nameof(IAgnesServer.OpenTerminal), sessionId, new OpenTerminalRequest(command, arguments, workingDirectory, columns, rows));

    public Task WriteTerminalAsync(string sessionId, string terminalId, byte[] data)
        => _hub.InvokeAsync(nameof(IAgnesServer.WriteTerminal), sessionId, terminalId, data);

    public Task ResizeTerminalAsync(string sessionId, string terminalId, int columns, int rows)
        => _hub.InvokeAsync(nameof(IAgnesServer.ResizeTerminal), sessionId, terminalId, columns, rows);

    public Task<string> BeginProviderLoginAsync(string adapterId)
        => _hub.InvokeAsync<string>(nameof(IAgnesServer.BeginProviderLogin), adapterId);

    public Task SetSendPolicyAsync(string sessionId, SendPolicy policy)
        => _hub.InvokeAsync(nameof(IAgnesServer.SetSendPolicy), sessionId, policy);

    public Task EnqueuePendingMessageAsync(string sessionId, IReadOnlyList<ContentBlock> content)
        => _hub.InvokeAsync(nameof(IAgnesServer.EnqueuePendingMessage), sessionId, content);

    public Task ReorderPendingMessageAsync(string sessionId, string messageId, int newIndex)
        => _hub.InvokeAsync(nameof(IAgnesServer.ReorderPendingMessage), sessionId, messageId, newIndex);

    public Task SendPendingNowAsync(string sessionId, string messageId)
        => _hub.InvokeAsync(nameof(IAgnesServer.SendPendingNow), sessionId, messageId);

    public Task RemovePendingMessageAsync(string sessionId, string messageId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RemovePendingMessage), sessionId, messageId);

    public Task<IReadOnlyList<Agnes.Abstractions.MemorySearchResult>> SearchMemoryAsync(string query, Agnes.Abstractions.MemorySearchOptions options)
        => _hub.InvokeAsync<IReadOnlyList<Agnes.Abstractions.MemorySearchResult>>(
            nameof(IAgnesServer.SearchMemory), query, new MemorySearchOptionsDto(options.Limit, options.SessionId));

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

    public Task<GitStashInfo?> GitStashAsync(string sessionId)
        => _hub.InvokeAsync<GitStashInfo?>(nameof(IAgnesServer.GitStash), sessionId);

    public Task<GitOperationResult> GitPopStashAsync(string sessionId, string stashId)
        => _hub.InvokeAsync<GitOperationResult>(nameof(IAgnesServer.GitPopStash), sessionId, stashId);

    public Task<GitSwitchResult> GitSwitchBranchAsync(string sessionId, string branch, bool carryStash)
        => _hub.InvokeAsync<GitSwitchResult>(nameof(IAgnesServer.GitSwitchBranch), sessionId, branch, carryStash);

    public Task<GitPullResult> GitPullAsync(string sessionId)
        => _hub.InvokeAsync<GitPullResult>(nameof(IAgnesServer.GitPull), sessionId);

    public Task<GitOperationResult> GitPushAsync(string sessionId, bool publishBranch)
        => _hub.InvokeAsync<GitOperationResult>(nameof(IAgnesServer.GitPush), sessionId, publishBranch);

    public Task<IReadOnlyList<Abstractions.PullRequestInfo>> ListPullRequestsAsync(string sessionId)
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.PullRequestInfo>>(nameof(IAgnesServer.ListPullRequests), sessionId);

    public Task<GitOperationResult> CheckoutPullRequestAsync(string sessionId, string pullRequestId)
        => _hub.InvokeAsync<GitOperationResult>(nameof(IAgnesServer.CheckoutPullRequest), sessionId, pullRequestId);

    public Task<IReadOnlyList<string>> GetChangedFilesAsync(string sessionId, ChangedFileScope scope)
        => _hub.InvokeAsync<IReadOnlyList<string>>(nameof(IAgnesServer.GetChangedFiles), sessionId, scope);

    public Task<CommitMessageSuggestion> GenerateCommitMessageAsync(string sessionId)
        => _hub.InvokeAsync<CommitMessageSuggestion>(nameof(IAgnesServer.GenerateCommitMessage), sessionId);

    public Task<IReadOnlyList<Abstractions.ReviewComment>> ListReviewCommentsAsync(string projectId)
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.ReviewComment>>(nameof(IAgnesServer.ListReviewComments), projectId);

    public Task<Abstractions.ReviewComment> AddReviewCommentAsync(AddReviewCommentRequest request)
        => _hub.InvokeAsync<Abstractions.ReviewComment>(nameof(IAgnesServer.AddReviewComment), request);

    public Task RemoveReviewCommentAsync(string id)
        => _hub.InvokeAsync(nameof(IAgnesServer.RemoveReviewComment), id);

    // ---- multi-machine workspace model (connectivity/05) ----

    public Task<IReadOnlyList<CheckoutDto>> ListCheckoutsAsync()
        => _hub.InvokeAsync<IReadOnlyList<CheckoutDto>>(nameof(IAgnesServer.ListCheckouts));

    public Task<CheckoutOperationResult> CreateCheckoutAsync(CreateCheckoutRequest request)
        => _hub.InvokeAsync<CheckoutOperationResult>(nameof(IAgnesServer.CreateCheckout), request);

    public Task<GitSwitchResult> SwitchCheckoutBranchAsync(string checkoutId, string branch)
        => _hub.InvokeAsync<GitSwitchResult>(nameof(IAgnesServer.SwitchCheckoutBranch), checkoutId, branch);

    public Task<CheckoutOperationResult> CleanUpCheckoutAsync(string checkoutId, bool force)
        => _hub.InvokeAsync<CheckoutOperationResult>(nameof(IAgnesServer.CleanUpCheckout), checkoutId, force);

    public Task<string> UploadAttachmentAsync(string sessionId, string fileName, byte[] data)
        => _hub.InvokeAsync<string>(nameof(IAgnesServer.UploadAttachment), sessionId, fileName, data);

    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(string sessionId, string relativePath)
        => _hub.InvokeAsync<IReadOnlyList<FileEntry>>(nameof(IAgnesServer.ListDirectory), sessionId, relativePath);

    public Task<FileContent> ReadFileAsync(string sessionId, string relativePath)
        => _hub.InvokeAsync<FileContent>(nameof(IAgnesServer.ReadFile), sessionId, relativePath);

    public Task WriteFileAsync(string sessionId, string relativePath, string content)
        => _hub.InvokeAsync(nameof(IAgnesServer.WriteFile), sessionId, relativePath, content);

    public Task CreateDirectoryAsync(string sessionId, string relativePath)
        => _hub.InvokeAsync(nameof(IAgnesServer.CreateDirectory), sessionId, relativePath);

    public Task RenameEntryAsync(string sessionId, string fromRelativePath, string toRelativePath)
        => _hub.InvokeAsync(nameof(IAgnesServer.RenameEntry), sessionId, fromRelativePath, toRelativePath);

    public Task DeleteEntryAsync(string sessionId, string relativePath)
        => _hub.InvokeAsync(nameof(IAgnesServer.DeleteEntry), sessionId, relativePath);

    public Task<byte[]> DownloadFileAsync(string sessionId, string relativePath)
        => _hub.InvokeAsync<byte[]>(nameof(IAgnesServer.DownloadFile), sessionId, relativePath);

    public Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request)
        => _hub.InvokeAsync<ScheduledTask>(nameof(IAgnesServer.ScheduleTask), request);

    public Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync()
        => _hub.InvokeAsync<IReadOnlyList<ScheduledTask>>(nameof(IAgnesServer.ListScheduledTasks));

    public Task RemoveScheduledTaskAsync(string taskId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RemoveScheduledTask), taskId);

    public Task PauseScheduledTaskAsync(string taskId)
        => _hub.InvokeAsync(nameof(IAgnesServer.PauseScheduledTask), taskId);

    public Task ResumeScheduledTaskAsync(string taskId)
        => _hub.InvokeAsync(nameof(IAgnesServer.ResumeScheduledTask), taskId);

    public Task RunScheduledTaskNowAsync(string taskId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RunScheduledTaskNow), taskId);

    public Task<IReadOnlyList<InboxRun>> GetInboxAsync()
        => _hub.InvokeAsync<IReadOnlyList<InboxRun>>(nameof(IAgnesServer.GetInbox));

    public Task<IReadOnlyList<OpenApproval>> GetOpenApprovalsAsync()
        => _hub.InvokeAsync<IReadOnlyList<OpenApproval>>(nameof(IAgnesServer.GetOpenApprovals));

    public event Action<InboxRun>? InboxRunReceived;
    public event Action<string, long, bool>? ReadStateChanged;

    public Task MarkSessionReadAsync(string sessionId, long sequence)
        => _hub.InvokeAsync(nameof(IAgnesServer.MarkSessionRead), sessionId, sequence);

    public Task MarkSessionUnreadAsync(string sessionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.MarkSessionUnread), sessionId);

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RespondPermission), new PermissionResponseRequest(sessionId, requestId, optionId));

    public Task RegisterPushChannelAsync(string channelId, string channelToken, PushNotificationPrefs prefs)
        => _hub.InvokeAsync(nameof(IAgnesServer.RegisterPushChannel), new RegisterPushRequest(channelId, channelToken, prefs));

    public Task SetSessionViewingAsync(string sessionId, bool viewing)
        => _hub.InvokeAsync(nameof(IAgnesServer.SetSessionViewing), sessionId, viewing);

    public Task AnswerAttentionRequestAsync(string requestId, string answer)
        => _hub.InvokeAsync(nameof(IAgnesServer.AnswerAttentionRequest), new AttentionAnswerRequest(requestId, answer));

    public Task ResolveGatedApprovalAsync(string requestId, bool approve)
        => _hub.InvokeAsync(nameof(IAgnesServer.ResolveGatedApproval), new GatedApprovalResolution(requestId, approve));

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

    public Task<Abstractions.BugReportResult> SubmitBugReportAsync(BugReportDto report)
        => _hub.InvokeAsync<Abstractions.BugReportResult>(nameof(IAgnesServer.SubmitBugReport), report);

    public Task<bool> CanAttachDiagnosticsAsync()
        => _hub.InvokeAsync<bool>(nameof(IAgnesServer.CanAttachDiagnostics));
    // ---- prompt library (see .ideas/extensibility/02-prompts-skills-library.md) ----

    public Task<IReadOnlyList<Abstractions.LibraryPrompt>> GetPromptsAsync()
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.LibraryPrompt>>(nameof(IAgnesServer.GetPrompts));

    public Task<Abstractions.LibraryPrompt> SavePromptAsync(Abstractions.LibraryPrompt prompt)
        => _hub.InvokeAsync<Abstractions.LibraryPrompt>(nameof(IAgnesServer.SavePrompt), prompt);

    public Task DeletePromptAsync(string id)
        => _hub.InvokeAsync(nameof(IAgnesServer.DeletePrompt), id);

    public Task<IReadOnlyList<Abstractions.PromptTemplate>> GetPromptTemplatesAsync()
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.PromptTemplate>>(nameof(IAgnesServer.GetPromptTemplates));

    public Task<Abstractions.PromptTemplate> SavePromptTemplateAsync(Abstractions.PromptTemplate template)
        => _hub.InvokeAsync<Abstractions.PromptTemplate>(nameof(IAgnesServer.SavePromptTemplate), template);

    public Task DeletePromptTemplateAsync(string token)
        => _hub.InvokeAsync(nameof(IAgnesServer.DeletePromptTemplate), token);

    public Task<IReadOnlyList<Abstractions.LibrarySkill>> GetSkillsAsync()
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.LibrarySkill>>(nameof(IAgnesServer.GetSkills));

    public Task DeleteSkillAsync(string id)
        => _hub.InvokeAsync(nameof(IAgnesServer.DeleteSkill), id);

    public Task<IReadOnlyList<string>> GetSkillRegistriesAsync()
        => _hub.InvokeAsync<IReadOnlyList<string>>(nameof(IAgnesServer.GetSkillRegistries));

    public Task<IReadOnlyList<Abstractions.RegistrySkillEntry>> GetRegistrySkillsAsync(string registryId)
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.RegistrySkillEntry>>(nameof(IAgnesServer.GetRegistrySkills), registryId);

    public Task<Abstractions.LibrarySkill> InstallSkillFromRegistryAsync(string registryId, string entryId)
        => _hub.InvokeAsync<Abstractions.LibrarySkill>(nameof(IAgnesServer.InstallSkillFromRegistry), registryId, entryId);

    public Task<Abstractions.QuotaSnapshot?> GetQuotaSnapshotAsync(string profileId)
        => _hub.InvokeAsync<Abstractions.QuotaSnapshot?>(nameof(IAgnesServer.GetQuotaSnapshot), profileId);

    // ---- friends & social (collaboration/01) ----

    public Task<IReadOnlyList<Abstractions.Friend>> ListFriendsAsync()
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.Friend>>(nameof(IAgnesServer.ListFriends));

    public Task<Abstractions.Friend> AddFriendAsync(string gitHubLogin, string? displayName = null)
        => _hub.InvokeAsync<Abstractions.Friend>(nameof(IAgnesServer.AddFriend), new AddFriendRequest(gitHubLogin, displayName));

    public Task RemoveFriendAsync(string gitHubLogin)
        => _hub.InvokeAsync(nameof(IAgnesServer.RemoveFriend), gitHubLogin);

    public Task<bool> CheckEligibilityAsync(string gitHubLogin)
        => _hub.InvokeAsync<bool>(nameof(IAgnesServer.CheckEligibility), gitHubLogin);

    public Task<IReadOnlyList<Abstractions.AccessGrant>> ListGrantsAsync()
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.AccessGrant>>(nameof(IAgnesServer.ListGrants));

    public Task<Abstractions.AccessGrant> GrantAccessAsync(string granteeLogin, string resource, Abstractions.GrantScope scope)
        => _hub.InvokeAsync<Abstractions.AccessGrant>(nameof(IAgnesServer.GrantAccess), new GrantAccessRequest(granteeLogin, resource, scope));

    public Task RevokeGrantAsync(string grantId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RevokeGrant), grantId);

    // ---- session sharing & public links (collaboration/02) ----

    public Task<Abstractions.SessionShare> ShareSessionAsync(string sessionId, string recipientId, Abstractions.SessionAccessLevel level, bool allowPermissionApprovals = false)
        => _hub.InvokeAsync<Abstractions.SessionShare>(nameof(IAgnesServer.ShareSession), new ShareSessionRequest(sessionId, recipientId, level, allowPermissionApprovals));

    public Task RevokeShareAsync(string sessionId, string recipientId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RevokeShare), sessionId, recipientId);

    public Task<IReadOnlyList<Abstractions.SessionShare>> ListSharesAsync(string sessionId)
        => _hub.InvokeAsync<IReadOnlyList<Abstractions.SessionShare>>(nameof(IAgnesServer.ListShares), sessionId);

    public Task<Abstractions.PublicSessionLink> CreatePublicLinkAsync(string sessionId, Abstractions.PublicLinkOptions options)
        => _hub.InvokeAsync<Abstractions.PublicSessionLink>(nameof(IAgnesServer.CreatePublicLink), new CreatePublicLinkRequest(sessionId, options));

    public Task RevokePublicLinkAsync(string sessionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RevokePublicLink), sessionId);

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
