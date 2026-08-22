using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// An inert <see cref="IAgnesHost"/>: every member answers with nothing and records nothing, so a test can
/// override the two or three calls it actually cares about instead of restating the whole wire surface.
/// Members the interface already defaults (the trailing, optional ones) are deliberately absent — the base
/// only fills in what a host is obliged to implement.
/// </summary>
public abstract class StubAgnesHost : IAgnesHost
{
    public virtual string HostUrl => "stub://host";
    public virtual AgnesConnectionState State => AgnesConnectionState.Connected;

    public event Action<AgnesConnectionState>? StateChanged { add { _ = value; } remove { _ = value; } }
    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged { add { _ = value; } remove { _ = value; } }
    public event Action<InboxRun>? InboxRunReceived { add { _ = value; } remove { _ = value; } }
    public event Action<SessionGoal>? GoalChanged { add { _ = value; } remove { _ = value; } }
    public event Action<string, long, bool>? ReadStateChanged { add { _ = value; } remove { _ = value; } }

    public virtual Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task<HostInfo> GetHostInfoAsync() => Task.FromResult(new HostInfo("stub", "Stub", "0"));

    public virtual Task<IReadOnlyList<AgentInfo>> ListAgentsAsync()
        => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    public virtual Task<AgentInfo> CheckAuthStatusAsync(string adapterId)
        => Task.FromResult(new AgentInfo(adapterId, adapterId, null, true));

    public virtual Task<SessionInfo> OpenSessionAsync(
        string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false,
        string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null)
        => throw new NotSupportedException();

    public virtual Task<SessionView> SubscribeAsync(string sessionId, long since = 0)
        => Task.FromResult(new SessionView(sessionId));

    public virtual Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content) => Task.CompletedTask;
    public virtual Task CancelAsync(string sessionId) => Task.CompletedTask;
    public virtual Task SetModeAsync(string sessionId, string modeId) => Task.CompletedTask;
    public virtual Task SwitchModelAsync(string sessionId, string? modelId) => Task.CompletedTask;

    public virtual Task<GitStatus> GetGitStatusAsync(string sessionId)
        => Task.FromResult(new GitStatus(false, null, false, []));

    public virtual Task<GitCommitResult> GitCommitAsync(string sessionId, string message)
        => Task.FromResult(new GitCommitResult(false, string.Empty));

    public virtual Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(string projectId)
        => Task.FromResult<IReadOnlyList<ReviewComment>>([]);

    public virtual Task<ReviewComment> AddReviewCommentAsync(AddReviewCommentRequest request)
        => throw new NotSupportedException();

    public virtual Task RemoveReviewCommentAsync(string id) => Task.CompletedTask;

    public virtual Task<string> UploadAttachmentAsync(string sessionId, string fileName, byte[] data)
        => Task.FromResult(fileName);

    public virtual Task MarkSessionReadAsync(string sessionId, long sequence) => Task.CompletedTask;
    public virtual Task MarkSessionUnreadAsync(string sessionId) => Task.CompletedTask;

    public virtual Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync()
        => Task.FromResult<IReadOnlyList<ScheduledTask>>([]);

    public virtual Task RemoveScheduledTaskAsync(string taskId) => Task.CompletedTask;

    public virtual Task<IReadOnlyList<InboxRun>> GetInboxAsync() => Task.FromResult<IReadOnlyList<InboxRun>>([]);

    public virtual Task RespondPermissionAsync(string sessionId, string requestId, string optionId) => Task.CompletedTask;
    public virtual Task PauseSandboxAsync(string sessionId) => Task.CompletedTask;
    public virtual Task ResumeSandboxAsync(string sessionId) => Task.CompletedTask;
    public virtual Task DeleteSandboxAsync(string sessionId) => Task.CompletedTask;
    public virtual Task StopSessionAsync(string sessionId) => Task.CompletedTask;

    public virtual Task<SandboxStatus?> GetSandboxStatusAsync(string sessionId) => Task.FromResult<SandboxStatus?>(null);

    // ---- optional surface a test may want to answer (defaulted on the interface) ----

    public virtual Task<IReadOnlyList<OpenApproval>> GetOpenApprovalsAsync()
        => Task.FromResult<IReadOnlyList<OpenApproval>>([]);

    public virtual Task AnswerAttentionRequestAsync(string requestId, string answer) => Task.CompletedTask;

    public virtual Task ResolveGatedApprovalAsync(string requestId, bool approve) => Task.CompletedTask;

    public virtual Task<IReadOnlyList<SessionSummary>> ListSessionsAsync()
        => Task.FromResult<IReadOnlyList<SessionSummary>>([]);

    // The prompt/skill library and the collaborators/grants surface. These are declared here (not left to the
    // interface defaults) so a derived fake can override them: the interface mapping is fixed at this class,
    // and a default interface member a subclass merely shadows would never be called.

    public virtual Task<IReadOnlyList<LibraryPrompt>> GetPromptsAsync()
        => Task.FromResult<IReadOnlyList<LibraryPrompt>>([]);

    public virtual Task<LibraryPrompt> SavePromptAsync(LibraryPrompt prompt)
        => throw new NotSupportedException();

    public virtual Task DeletePromptAsync(string id) => Task.CompletedTask;

    public virtual Task<IReadOnlyList<PromptTemplate>> GetPromptTemplatesAsync()
        => Task.FromResult<IReadOnlyList<PromptTemplate>>([]);

    public virtual Task<PromptTemplate> SavePromptTemplateAsync(PromptTemplate template)
        => throw new NotSupportedException();

    public virtual Task DeletePromptTemplateAsync(string token) => Task.CompletedTask;

    public virtual Task<IReadOnlyList<LibrarySkill>> GetSkillsAsync()
        => Task.FromResult<IReadOnlyList<LibrarySkill>>([]);

    public virtual Task DeleteSkillAsync(string id) => Task.CompletedTask;

    public virtual Task<IReadOnlyList<CatalogSource>> GetSkillRegistriesAsync()
        => Task.FromResult<IReadOnlyList<CatalogSource>>([]);

    public virtual Task<IReadOnlyList<RegistrySkillEntry>> GetRegistrySkillsAsync(string registryId)
        => Task.FromResult<IReadOnlyList<RegistrySkillEntry>>([]);

    public virtual Task<CatalogResults<RegistrySkillEntry>> SearchSkillsAsync(string query)
        => Task.FromResult(CatalogResults<RegistrySkillEntry>.Empty);

    public virtual Task<LibrarySkill> InstallSkillFromRegistryAsync(string registryId, string entryId)
        => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<Collaborator>> ListCollaboratorsAsync()
        => Task.FromResult<IReadOnlyList<Collaborator>>([]);

    public virtual Task<Collaborator> AddCollaboratorAsync(string gitHubLogin, string? displayName = null)
        => throw new NotSupportedException();

    public virtual Task RemoveCollaboratorAsync(string gitHubLogin) => Task.CompletedTask;

    public virtual Task<bool> CheckEligibilityAsync(string gitHubLogin) => Task.FromResult(false);

    public virtual Task<IReadOnlyList<AccessGrant>> ListGrantsAsync()
        => Task.FromResult<IReadOnlyList<AccessGrant>>([]);

    public virtual Task<AccessGrant> GrantAccessAsync(string granteeLogin, string resource, GrantScope scope)
        => throw new NotSupportedException();

    public virtual Task RevokeGrantAsync(string grantId) => Task.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
