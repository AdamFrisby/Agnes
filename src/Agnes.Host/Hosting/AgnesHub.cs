using Agnes.Abstractions;
using Agnes.Host.Attention;
using Agnes.Host.Ops;
using Agnes.Host.Plugins;
using Agnes.Host.Projects;
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
    private readonly ReviewCommentStore _reviewComments;
    private readonly Git.CheckoutManager _checkouts;
    private readonly IPluginRegistry<IMemoryIndexProvider> _memoryIndexes;
    private readonly BugReportRouter _bugReports;
    private readonly PromptLibrary _prompts;
    private readonly LaunchProfileStore _launchProfiles;
    private readonly SkillLibrary _skills;
    private readonly IPluginRegistry<IPromptRegistryProvider> _skillRegistries;
    private readonly AttentionRequestService _attention;
    private readonly QuotaService _quota;
    private readonly Notifications.PushRegistrationStore _pushRegistrations;
    private readonly Notifications.ActiveSessionViewTracker _views;
    private readonly IPluginRegistry<INotificationChannel> _channels;
    private readonly Social.FriendService _friends;
    private readonly Sharing.SessionSharingService _sharing;
    private readonly Sharing.SessionAccessAuthorizer _access;
    private readonly Sharing.PublicLinkStore _publicLinks;
    private readonly Sharing.PublicViewerTracker _publicViewers;

    public AgnesHub(SessionManager sessions, ScheduledTaskManager schedule, HostIdentity identity, DeviceRegistry tokens, PluginManagementService plugins, ClientCapabilityStore clientCaps, ReviewCommentStore reviewComments, IPluginRegistry<IMemoryIndexProvider> memoryIndexes, BugReportRouter bugReports, PromptLibrary prompts, LaunchProfileStore launchProfiles, SkillLibrary skills, IPluginRegistry<IPromptRegistryProvider> skillRegistries, AttentionRequestService attention, QuotaService quota, Notifications.PushRegistrationStore pushRegistrations, Notifications.ActiveSessionViewTracker views, IPluginRegistry<INotificationChannel> channels, Social.FriendService friends, Sharing.SessionSharingService sharing, Sharing.SessionAccessAuthorizer access, Sharing.PublicLinkStore publicLinks, Sharing.PublicViewerTracker publicViewers, Git.CheckoutManager checkouts)
    {
        _checkouts = checkouts;
        _friends = friends;
        _sharing = sharing;
        _access = access;
        _publicLinks = publicLinks;
        _publicViewers = publicViewers;
        _launchProfiles = launchProfiles;
        _pushRegistrations = pushRegistrations;
        _views = views;
        _channels = channels;
        _sessions = sessions;
        _schedule = schedule;
        _identity = identity;
        _tokens = tokens;
        _plugins = plugins;
        _clientCaps = clientCaps;
        _reviewComments = reviewComments;
        _memoryIndexes = memoryIndexes;
        _bugReports = bugReports;
        _prompts = prompts;
        _skills = skills;
        _skillRegistries = skillRegistries;
        _attention = attention;
        _quota = quota;
    }

    public override async Task OnConnectedAsync()
    {
        var query = Context.GetHttpContext()?.Request.Query;
        var token = query?[WireProtocol.TokenParameter].ToString();
        if (_tokens.IsValid(token))
        {
            await base.OnConnectedAsync();
            return;
        }

        // No valid device token: a public-link viewer may still connect if it presents a valid link token for a
        // specific session (collaboration/02). Such a connection is marked read-only and scoped to that one
        // session — every write path rejects it. Consuming the link here counts one use against its MaxUses.
        var linkToken = query?[WireProtocol.PublicLinkTokenParameter].ToString();
        var linkSession = query?[WireProtocol.PublicLinkSessionParameter].ToString();
        if (!string.IsNullOrEmpty(linkToken) && !string.IsNullOrEmpty(linkSession)
            && _publicLinks.Validate(linkSession, linkToken) == Sharing.PublicLinkValidation.Valid)
        {
            _publicViewers.Mark(Context.ConnectionId, linkSession);
            await base.OnConnectedAsync();
            return;
        }

        Context.Abort();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _clientCaps.Remove(Context.ConnectionId);
        _publicViewers.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task<HostInfo> GetHostInfo()
        => Task.FromResult(new HostInfo(_identity.HostId, _identity.DisplayName, _identity.Version, _sessions.SandboxAvailable, _sessions.SandboxRequired, _sessions.PermissionPromptsRequired));

    public Task<IReadOnlyList<AgentInfo>> ListAgents()
        => Task.FromResult(_sessions.ListAgents());

    public Task<AgentInfo> CheckAuthStatus(string adapterId)
        => _sessions.CheckAuthStatusAsync(adapterId);

    public Task<IReadOnlyList<Abstractions.ModelInfo>> ListModels(string adapterId)
        => _sessions.ListModelsAsync(adapterId);

    public Task<IReadOnlyList<HostCapability>> GetCapabilities()
        => Task.FromResult(HostCapabilityList());

    private IReadOnlyList<HostCapability> HostCapabilityList()
    {
        var caps = _sessions.GetCapabilities().ToList();
        // Plugin management is always available on a host built with the installer wired up (it is,
        // unconditionally, in Program.cs). Fail-open: a client without it just hides the Plugins screen.
        caps.Add(new HostCapability(HostCapabilityIds.PluginManagement, Available: true, FailClosed: false));
        // Transcript search is only usable when an index is configured (a durable SQLite store); without one
        // a search returns empty, so a client can simply hide the screen. Fail-open.
        caps.Add(new HostCapability(HostCapabilityIds.MemorySearch, _memoryIndexes.All.Count > 0, FailClosed: false));
        return caps;
    }

    public Task<NegotiatedCapabilities> Negotiate(ClientCapabilities client)
    {
        _clientCaps.Set(Context.ConnectionId, client);
        return Task.FromResult(CapabilityNegotiator.Reconcile(HostCapabilityList(), client));
    }

    public Task<SessionInfo> OpenSession(OpenSessionRequest request)
        => _sessions.OpenSessionAsync(request.AdapterId, request.WorkingDirectory, request.UseWorktree, request.SkipPermissions, request.McpApproval, request.GitCredentialMode, request.UseSandbox, request.ModelId);

    public Task<IReadOnlyList<LaunchProfile>> GetLaunchProfiles()
        => Task.FromResult(_launchProfiles.List());

    public Task<LaunchProfile> SaveLaunchProfile(LaunchProfile profile)
        => Task.FromResult(_launchProfiles.Save(profile));

    public Task DeleteLaunchProfile(string id)
    {
        _launchProfiles.Delete(id);
        return Task.CompletedTask;
    }

    public Task<SessionInfo> OpenSessionFromProfile(OpenSessionFromProfileRequest request)
    {
        var profile = _launchProfiles.Find(request.ProfileId)
            ?? throw new InvalidOperationException($"No launch profile with id '{request.ProfileId}'.");
        var open = profile.ToOpenSessionRequest(request.WorkingDirectoryOverride);
        return _sessions.OpenSessionAsync(open.AdapterId, open.WorkingDirectory, open.UseWorktree, open.SkipPermissions, open.McpApproval, open.GitCredentialMode, open.UseSandbox, open.ModelId);
    }
    public Task<IReadOnlyList<Abstractions.ExternalSessionInfo>> DiscoverExternalSessions(string workspaceDirectory)
        => _sessions.DiscoverExternalSessionsAsync(workspaceDirectory);

    public Task<SessionInfo> AttachExternalSession(string adapterId, string externalId)
        => _sessions.AttachExternalSessionAsync(adapterId, externalId);

    public Task<ForkPlan?> ProposeFork(string sessionId)
        => Task.FromResult(_sessions.ProposeFork(sessionId));

    public Task<SessionInfo> ForkSession(ForkSessionRequest request)
        => _sessions.ForkSessionAsync(request.SourceSessionId, request.TargetDirectory, request.CopySandbox);

    public Task<ForkAtResult> ForkSessionAt(ForkAtRequest request)
        => _sessions.ForkSessionAtAsync(request.SourceSessionId, request.TargetDirectory, request.AtSequence, request.CopySandbox);

    public async Task<SessionSnapshot> Subscribe(string sessionId, long sinceSequence)
    {
        // collaboration/02 enforcement point: only the owner, a shared recipient (any level), or a valid
        // public-link viewer scoped to this session may watch it. An unshared device is rejected here — the
        // gate is server-side, so no client can bypass it. Unshared sessions stay owner-only (additive).
        if (_publicViewers.IsPublicViewer(Context.ConnectionId))
        {
            if (!_publicViewers.CanView(Context.ConnectionId, sessionId))
            {
                throw new HubException("This public link only grants access to a single session.");
            }
        }
        else if (!_access.CanSubscribe(sessionId, CallerContext()))
        {
            throw new HubException("You do not have access to this session.");
        }

        // Join the group BEFORE snapshotting so no event is missed; the client dedupes by
        // sequence, so an event that both lands in the snapshot and is broadcast is harmless.
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        var snapshot = await _sessions.GetSnapshotAsync(sessionId, sinceSequence);

        // Seed the joining client with the session's current read state (unread indicators).
        var (readCursor, stickyUnread) = _sessions.GetReadState(sessionId);
        await Clients.Caller.OnReadState(sessionId, readCursor, stickyUnread);
        return snapshot;
    }

    public Task MarkSessionRead(string sessionId, long sequence)
        => _sessions.MarkReadAsync(sessionId, sequence);

    public Task MarkSessionUnread(string sessionId)
        => _sessions.MarkUnreadAsync(sessionId);

    public Task Unsubscribe(string sessionId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);

    public Task Prompt(PromptRequest request)
    {
        // Send/drive requires CanEdit or higher — a view-only recipient (or any public viewer) is rejected
        // server-side, not merely hidden in the UI (AC1).
        RequireWrite(request.SessionId, _access.CanPrompt, "You do not have permission to send messages to this session.");
        return _sessions.PromptAsync(request.SessionId, request.Content);
    }

    public Task<string> OpenTerminal(string sessionId, OpenTerminalRequest request)
        => _sessions.OpenTerminalAsync(sessionId, request.Command, request.Arguments, request.WorkingDirectory, request.Columns, request.Rows);

    public Task WriteTerminal(string sessionId, string terminalId, byte[] data)
        => _sessions.WriteTerminalAsync(sessionId, terminalId, data);

    public Task ResizeTerminal(string sessionId, string terminalId, int columns, int rows)
        => _sessions.ResizeTerminalAsync(sessionId, terminalId, columns, rows);

    public Task<string> BeginProviderLogin(string adapterId)
        => _sessions.BeginProviderLoginAsync(adapterId);

    public Task SetSendPolicy(string sessionId, SendPolicy policy)
        => _sessions.SetSendPolicyAsync(sessionId, policy);

    public Task EnqueuePendingMessage(string sessionId, IReadOnlyList<ContentBlock> content)
        => _sessions.EnqueuePendingMessageAsync(sessionId, content);

    public Task ReorderPendingMessage(string sessionId, string messageId, int newIndex)
        => _sessions.ReorderPendingMessageAsync(sessionId, messageId, newIndex);

    public Task SendPendingNow(string sessionId, string messageId)
        => _sessions.SendPendingNowAsync(sessionId, messageId);

    public Task RemovePendingMessage(string sessionId, string messageId)
        => _sessions.RemovePendingMessageAsync(sessionId, messageId);

    public Task<IReadOnlyList<MemorySearchResult>> SearchMemory(string query, MemorySearchOptionsDto options)
    {
        var index = _memoryIndexes.All.FirstOrDefault();
        return index is null
            ? Task.FromResult<IReadOnlyList<MemorySearchResult>>([])
            : index.SearchAsync(query, new MemorySearchOptions(options.Limit, options.SessionId));
    }

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

    public Task<GitStashInfo?> GitStash(string sessionId)
        => _sessions.GitStashAsync(sessionId);

    public Task<GitOperationResult> GitPopStash(string sessionId, string stashId)
        => _sessions.GitPopStashAsync(sessionId, stashId);

    public Task<GitSwitchResult> GitSwitchBranch(string sessionId, string branch, bool carryStash)
        => _sessions.GitSwitchBranchAsync(sessionId, branch, carryStash);

    public Task<GitPullResult> GitPull(string sessionId)
        => _sessions.GitPullAsync(sessionId);

    public Task<GitOperationResult> GitPush(string sessionId, bool publishBranch)
        => _sessions.GitPushAsync(sessionId, publishBranch);

    public Task<IReadOnlyList<Abstractions.PullRequestInfo>> ListPullRequests(string sessionId)
        => _sessions.ListPullRequestsAsync(sessionId);

    public Task<GitOperationResult> CheckoutPullRequest(string sessionId, string pullRequestId)
        => _sessions.CheckoutPullRequestAsync(sessionId, pullRequestId);

    public Task<IReadOnlyList<string>> GetChangedFiles(string sessionId, ChangedFileScope scope)
        => _sessions.GetChangedFilesAsync(sessionId, scope);

    public Task<CommitMessageSuggestion> GenerateCommitMessage(string sessionId)
        => _sessions.GenerateCommitMessageAsync(sessionId);

    public Task<IReadOnlyList<Abstractions.ReviewComment>> ListReviewComments(string projectId)
        => Task.FromResult(_reviewComments.ListForProject(projectId));

    public Task<Abstractions.ReviewComment> AddReviewComment(AddReviewCommentRequest request)
        => Task.FromResult(_reviewComments.Add(request.ProjectId, request.FilePath, request.LineNumber, request.LineHash, request.Text));

    public Task RemoveReviewComment(string id)
    {
        _reviewComments.Remove(id);
        return Task.CompletedTask;
    }

    // ---- multi-machine workspace model (connectivity/05): this host's checkout lifecycle ----

    public Task<IReadOnlyList<CheckoutDto>> ListCheckouts()
        => _checkouts.ListAsync(Context.ConnectionAborted);

    public Task<CheckoutOperationResult> CreateCheckout(CreateCheckoutRequest request)
        => _checkouts.CreateCheckoutAsync(request.RepositoryUrl, request.Path, request.Branch, request.UseWorktreeOfExisting, Context.ConnectionAborted);

    public Task<GitSwitchResult> SwitchCheckoutBranch(string checkoutId, string branch)
        => _checkouts.SwitchCheckoutBranchAsync(checkoutId, branch, Context.ConnectionAborted);

    public Task<CheckoutOperationResult> CleanUpCheckout(string checkoutId, bool force)
        => _checkouts.CleanUpCheckoutAsync(checkoutId, force, Context.ConnectionAborted);

    public Task<string> UploadAttachment(string sessionId, string fileName, byte[] data)
        => _sessions.UploadAttachmentAsync(sessionId, fileName, data);

    public Task<IReadOnlyList<FileEntry>> ListDirectory(string sessionId, string relativePath)
        => _sessions.ListDirectoryAsync(sessionId, relativePath);

    public Task<FileContent> ReadFile(string sessionId, string relativePath)
        => _sessions.ReadFileAsync(sessionId, relativePath);

    public Task WriteFile(string sessionId, string relativePath, string content)
        => _sessions.WriteFileAsync(sessionId, relativePath, content);

    public Task CreateDirectory(string sessionId, string relativePath)
        => _sessions.CreateDirectoryAsync(sessionId, relativePath);

    public Task RenameEntry(string sessionId, string fromRelativePath, string toRelativePath)
        => _sessions.RenameEntryAsync(sessionId, fromRelativePath, toRelativePath);

    public Task DeleteEntry(string sessionId, string relativePath)
        => _sessions.DeleteEntryAsync(sessionId, relativePath);

    public Task<byte[]> DownloadFile(string sessionId, string relativePath)
        => _sessions.DownloadFileAsync(sessionId, relativePath);

    public Task<ScheduledTask> ScheduleTask(ScheduleTaskRequest request)
        => _schedule.AddAsync(request);

    public Task<IReadOnlyList<ScheduledTask>> ListScheduledTasks()
        => Task.FromResult(_schedule.List());

    public Task RemoveScheduledTask(string taskId)
        => _schedule.RemoveAsync(taskId);

    public Task PauseScheduledTask(string taskId)
    {
        _schedule.SetEnabled(taskId, enabled: false);
        return Task.CompletedTask;
    }

    public Task ResumeScheduledTask(string taskId)
    {
        _schedule.SetEnabled(taskId, enabled: true);
        return Task.CompletedTask;
    }

    public Task RunScheduledTaskNow(string taskId)
    {
        _schedule.RunNow(taskId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InboxRun>> GetInbox()
        => Task.FromResult(_schedule.Inbox());

    public Task<IReadOnlyList<OpenApproval>> GetOpenApprovals()
        => _sessions.GetOpenApprovalsAsync();

    public Task RespondPermission(PermissionResponseRequest response)
    {
        // Answering a tool-permission prompt is the orthogonal, separately-granted capability — gated on the
        // share's AllowPermissionApprovals flag, independent of access level (AC2). A public viewer never has it.
        RequireWrite(response.SessionId, _access.CanApprovePermissions, "You are not permitted to approve permissions on this session.");
        return _sessions.RespondPermissionAsync(response.SessionId, response.RequestId, response.OptionId);
    }

    public async Task RegisterPushChannel(RegisterPushRequest request)
    {
        var deviceId = CallerDeviceId();
        if (deviceId is null)
        {
            return; // bootstrap/anonymous token: no device record to hang a push registration on.
        }

        _pushRegistrations.Register(deviceId, request.ChannelId, request.ChannelToken);
        _pushRegistrations.SetPreferences(
            deviceId,
            request.Prefs.Enabled,
            new Notifications.PushTriggerPrefs(request.Prefs.TurnReady, request.Prefs.PermissionRequest, request.Prefs.UserActionRequest));

        // Let the target channel record the channel-specific token (a real one would register it with FCM/APNs).
        if (_channels.Find(request.ChannelId) is { } channel)
        {
            await channel.RegisterAsync(deviceId, request.ChannelToken, Context.ConnectionAborted).ConfigureAwait(false);
        }
    }

    public Task SetSessionViewing(string sessionId, bool viewing)
    {
        if (CallerDeviceId() is { } deviceId)
        {
            if (viewing)
            {
                _views.MarkViewing(deviceId, sessionId);
            }
            else
            {
                _views.ClearViewing(deviceId, sessionId);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Resolves the connection's bearer token to its stable device id, or null for the bootstrap /
    /// anonymous token (which isn't a device record and so can't own a push registration).</summary>
    private string? CallerDeviceId()
    {
        var token = Context.GetHttpContext()?.Request.Query[WireProtocol.TokenParameter].ToString();
        var caller = _tokens.ResolveCallerId(token);
        return caller is null or "bootstrap" ? null : caller;
    }

    public Task AnswerQuestion(QuestionAnswerRequest response)
        => _sessions.AnswerQuestionAsync(response.SessionId, response.RequestId, response.Answers);

    public Task AnswerAttentionRequest(AttentionAnswerRequest response)
    {
        // Records the answer synchronously; any callback POST runs on its own task inside the service so the
        // answering client isn't held for the retry/backoff window.
        _attention.Answer(response.RequestId, response.Answer);
        return Task.CompletedTask;
    }

    public Task ResolveGatedApproval(GatedApprovalResolution resolution)
        => _sessions.ResolveGatedApprovalAsync(resolution.RequestId, resolution.Approve, Context.ConnectionAborted);

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

    // Map the wire DTO to the domain report (client never sends a payload) and route to the host's selected
    // sink. The router assembles the owner-only host-log diagnostic bundle host-side and attaches it ONLY when
    // the caller opted in for this report AND is the authorized owner; otherwise the payload stays null.
    public Task<Abstractions.BugReportResult> SubmitBugReport(BugReportDto report)
        => _bugReports.SubmitAsync(
            new Abstractions.BugReport(report.Title, report.Summary, report.CurrentBehavior, report.ExpectedBehavior, DiagnosticPayload: null),
            report.AttachDiagnostics,
            CallerId());

    // Whether the calling client may attach diagnostics — capability enabled AND this caller is the owner.
    public Task<bool> CanAttachDiagnostics()
        => Task.FromResult(_bugReports.CanAttachDiagnostics(CallerId()));

    // The authenticated caller's stable id, resolved from the connection's bearer token (null if unknown).
    private string? CallerId()
    {
        var token = Context.GetHttpContext()?.Request.Query[WireProtocol.TokenParameter].ToString();
        return _tokens.ResolveCallerId(token);
    }
    public Task<IReadOnlyList<Abstractions.LibraryPrompt>> GetPrompts()
        => Task.FromResult(_prompts.List());

    public Task<Abstractions.LibraryPrompt> SavePrompt(Abstractions.LibraryPrompt prompt)
        => Task.FromResult(_prompts.Save(prompt));

    public Task DeletePrompt(string id)
    {
        _prompts.Delete(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Abstractions.PromptTemplate>> GetPromptTemplates()
        => Task.FromResult(_prompts.ListTemplates());

    public Task<Abstractions.PromptTemplate> SavePromptTemplate(Abstractions.PromptTemplate template)
        => Task.FromResult(_prompts.SaveTemplate(template));

    public Task DeletePromptTemplate(string token)
    {
        _prompts.DeleteTemplate(token);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Abstractions.LibrarySkill>> GetSkills()
        => Task.FromResult(_skills.List());

    public Task DeleteSkill(string id)
    {
        _skills.Delete(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetSkillRegistries()
        => Task.FromResult<IReadOnlyList<string>>(_skillRegistries.All.Select(r => r.Id).ToArray());

    public async Task<IReadOnlyList<Abstractions.RegistrySkillEntry>> GetRegistrySkills(string registryId)
    {
        var registry = _skillRegistries.Find(registryId)
            ?? throw new InvalidOperationException($"Unknown skill registry '{registryId}'.");
        return await registry.ListAsync().ConfigureAwait(false);
    }

    public async Task<Abstractions.LibrarySkill> InstallSkillFromRegistry(string registryId, string entryId)
    {
        var registry = _skillRegistries.Find(registryId)
            ?? throw new InvalidOperationException($"Unknown skill registry '{registryId}'.");

        // Fetch into a scratch dir, then import (copy) into the managed library, which becomes the source of
        // truth. The scratch dir is always cleaned up regardless of outcome.
        var scratch = Path.Combine(Path.GetTempPath(), "agnes-skill-fetch", Guid.NewGuid().ToString("n"));
        try
        {
            var fetched = await registry.FetchAsync(entryId, scratch).ConfigureAwait(false);
            return _skills.Save(id: null, title: fetched.Title, sourceSkillMdPath: fetched.SkillMdPath, supportingFiles: fetched.SupportingFiles);
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    // Connected-service quota: the host serves a cached snapshot behind a staleness window (see QuotaService),
    // so redrawing a badge doesn't hammer the provider's usage endpoint. Null = "unavailable", not an error.
    public Task<Abstractions.QuotaSnapshot?> GetQuotaSnapshot(string profileId)
        => _quota.GetQuotaAsync(profileId);

    // ---- friends & social (collaboration/01) ----
    // All owner-only: managing the friend directory and minting/revoking access grants is a host-owner action.
    // A non-owner caller is refused up front (before any GitHub round-trip), so a paired-but-not-owner device
    // can neither enumerate the owner's friends nor grant itself access. The acting owner's GitHub login (from
    // their device subject) is the eligibility "actor"; it may be null for a non-GitHub-paired owner, in which
    // case only the explicit-friend eligibility path is available.

    public Task<IReadOnlyList<Abstractions.Friend>> ListFriends()
    {
        RequireOwner();
        return Task.FromResult(_friends.ListFriends());
    }

    public Task<Abstractions.Friend> AddFriend(AddFriendRequest request)
    {
        RequireOwner();
        return _friends.AddFriendAsync(request.GitHubLogin, request.DisplayName, Context.ConnectionAborted);
    }

    public Task RemoveFriend(string gitHubLogin)
    {
        RequireOwner();
        _friends.RemoveFriend(gitHubLogin);
        return Task.CompletedTask;
    }

    public Task<bool> CheckEligibility(string gitHubLogin)
    {
        RequireOwner();
        return _friends.IsEligibleAsync(OwnerGitHubLogin() ?? string.Empty, gitHubLogin, Context.ConnectionAborted);
    }

    public Task<IReadOnlyList<Abstractions.AccessGrant>> ListGrants()
    {
        RequireOwner();
        return Task.FromResult(_friends.ListGrants());
    }

    public Task<Abstractions.AccessGrant> GrantAccess(GrantAccessRequest request)
    {
        var deviceId = RequireOwner();
        return _friends.GrantAsync(OwnerGitHubLogin() ?? string.Empty, request.GranteeLogin, request.Resource, request.Scope, deviceId, Context.ConnectionAborted);
    }

    public Task RevokeGrant(string grantId)
    {
        RequireOwner();
        _friends.RevokeGrant(grantId);
        return Task.CompletedTask;
    }

    // Asserts the caller is the host owner, returning their stable device id; throws otherwise so the client
    // sees a hub error rather than a silent no-op. Owner resolution reuses DeviceRegistry.IsOwner.
    private string RequireOwner()
    {
        var caller = CallerId();
        if (!_tokens.IsOwner(caller))
        {
            throw new HubException("Only the host owner can manage friends and access grants.");
        }

        return caller!;
    }

    // The acting owner's GitHub login (from their device subject), or null if they didn't pair via GitHub.
    private string? OwnerGitHubLogin()
    {
        var token = Context.GetHttpContext()?.Request.Query[WireProtocol.TokenParameter].ToString();
        return _tokens.ResolveGitHubLogin(token);
    }

    // ---- session sharing & public links (collaboration/02) ----
    // Two deliberately-separate mechanisms. Managing either (creating/revoking a share or link) is gated to the
    // session owner or a CanManage collaborator; the read/write enforcement itself lives in Subscribe/Prompt/
    // RespondPermission above. A public viewer can never reach these — it is rejected before any of them.

    public Task<Abstractions.SessionShare> ShareSession(ShareSessionRequest request)
    {
        var caller = RequireManage(request.SessionId);
        try
        {
            return _sharing.ShareWithAsync(request.SessionId, request.RecipientId, request.Level, request.AllowPermissionApprovals, caller.DeviceId ?? string.Empty, Context.ConnectionAborted);
        }
        catch (Sharing.SharingException ex)
        {
            // A domain refusal (e.g. approvals on a view-only/inactive share) surfaces as a hub error, not a fault.
            throw new HubException(ex.Message);
        }
    }

    public Task RevokeShare(string sessionId, string recipientId)
    {
        RequireManage(sessionId);
        return _sharing.RevokeAsync(sessionId, recipientId, Context.ConnectionAborted);
    }

    public Task<IReadOnlyList<Abstractions.SessionShare>> ListShares(string sessionId)
    {
        RequireManage(sessionId);
        return Task.FromResult(_sharing.ListShares(sessionId));
    }

    public Task<Abstractions.PublicSessionLink> CreatePublicLink(CreatePublicLinkRequest request)
    {
        RequireManage(request.SessionId);
        return _sharing.CreatePublicLinkAsync(request.SessionId, request.Options, Context.ConnectionAborted);
    }

    public Task RevokePublicLink(string sessionId)
    {
        RequireManage(sessionId);
        return _sharing.RevokePublicLinkAsync(sessionId, Context.ConnectionAborted);
    }

    // Resolves the connection's identities (device id + GitHub login + owner flag) as the sharing layer sees it.
    private Sharing.SharingCaller CallerContext()
    {
        var token = Context.GetHttpContext()?.Request.Query[WireProtocol.TokenParameter].ToString();
        var deviceId = _tokens.ResolveCallerId(token);
        return new Sharing.SharingCaller(deviceId, _tokens.ResolveGitHubLogin(token), _tokens.IsOwner(deviceId));
    }

    // Rejects a write on a session unless the caller passes the given access check. A public-link viewer never
    // holds a device identity, so it fails every write check here — the read-only guarantee is structural.
    private void RequireWrite(string sessionId, Func<string, Sharing.SharingCaller, bool> check, string message)
    {
        if (_publicViewers.IsPublicViewer(Context.ConnectionId) || !check(sessionId, CallerContext()))
        {
            throw new HubException(message);
        }
    }

    // Asserts the caller may manage sharing on this session (owner or a CanManage collaborator); returns their
    // caller context. A CanEdit collaborator cannot re-share — this throws for them.
    private Sharing.SharingCaller RequireManage(string sessionId)
    {
        if (_publicViewers.IsPublicViewer(Context.ConnectionId))
        {
            throw new HubException("A public link cannot manage session sharing.");
        }

        var caller = CallerContext();
        if (!_access.CanManage(sessionId, caller))
        {
            throw new HubException("Only the session owner or a manager can change sharing on this session.");
        }

        return caller;
    }
}
