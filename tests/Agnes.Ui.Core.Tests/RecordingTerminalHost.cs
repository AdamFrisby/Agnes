using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;

namespace Agnes.Ui.Core.Tests;

/// <summary>A minimal <see cref="IAgnesHost"/> that records terminal open/write/resize calls and leans on
/// the interface's defaults for everything else. Only the terminal surface is exercised by
/// <see cref="TerminalPanelViewModelTests"/>; the rest throws to keep the fake honest.</summary>
internal sealed class RecordingTerminalHost : IAgnesHost
{
    private int _opened;

    public List<(string SessionId, string TerminalId, byte[] Data)> Writes { get; } = [];
    public List<(string SessionId, string TerminalId, int Columns, int Rows)> Resizes { get; } = [];
    public List<(string SessionId, string? Command, int Columns, int Rows)> Opens { get; } = [];

    public Task<string> OpenTerminalAsync(string sessionId, string? command = null, IReadOnlyList<string>? arguments = null, string? workingDirectory = null, int columns = 120, int rows = 30)
    {
        Opens.Add((sessionId, command, columns, rows));
        return Task.FromResult($"term-{++_opened}");
    }

    public Task WriteTerminalAsync(string sessionId, string terminalId, byte[] data)
    {
        Writes.Add((sessionId, terminalId, data));
        return Task.CompletedTask;
    }

    public Task ResizeTerminalAsync(string sessionId, string terminalId, int columns, int rows)
    {
        Resizes.Add((sessionId, terminalId, columns, rows));
        return Task.CompletedTask;
    }

    // ---- everything below is unused by the terminal tests ----
    public string HostUrl => "recording://host";
    public AgnesConnectionState State => AgnesConnectionState.Connected;

#pragma warning disable CS0067 // events are part of the interface but unused here
    public event Action<AgnesConnectionState>? StateChanged;
    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;
    public event Action<InboxRun>? InboxRunReceived;
    public event Action<string, long, bool>? ReadStateChanged;
#pragma warning restore CS0067

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<HostInfo> GetHostInfoAsync() => Task.FromResult(new HostInfo("recording", "recording", "1.0"));
    public Task<IReadOnlyList<AgentInfo>> ListAgentsAsync() => Task.FromResult<IReadOnlyList<AgentInfo>>([]);
    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory, bool useWorktree = false, bool skipPermissions = false, string mcpApproval = "Ask", string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null)
        => throw new NotSupportedException();
    public Task<SessionView> SubscribeAsync(string sessionId, long since = 0) => throw new NotSupportedException();
    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content) => Task.CompletedTask;
    public Task CancelAsync(string sessionId) => Task.CompletedTask;
    public Task SetModeAsync(string sessionId, string modeId) => Task.CompletedTask;
    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId) => Task.CompletedTask;
    public Task<GitStatus> GetGitStatusAsync(string sessionId) => Task.FromResult(new GitStatus(false, null, false, []));
    public Task<GitCommitResult> GitCommitAsync(string sessionId, string message) => Task.FromResult(new GitCommitResult(true, "ok"));
    public Task<string> UploadAttachmentAsync(string sessionId, string fileName, byte[] data) => Task.FromResult(fileName);
    public Task<ScheduledTask> ScheduleTaskAsync(ScheduleTaskRequest request) => throw new NotSupportedException();
    public Task<IReadOnlyList<ScheduledTask>> ListScheduledTasksAsync() => Task.FromResult<IReadOnlyList<ScheduledTask>>([]);
    public Task RemoveScheduledTaskAsync(string taskId) => Task.CompletedTask;
    public Task<IReadOnlyList<InboxRun>> GetInboxAsync() => Task.FromResult<IReadOnlyList<InboxRun>>([]);
    public Task MarkSessionReadAsync(string sessionId, long sequence) => Task.CompletedTask;
    public Task MarkSessionUnreadAsync(string sessionId) => Task.CompletedTask;
    public Task PauseSandboxAsync(string sessionId) => Task.CompletedTask;
    public Task ResumeSandboxAsync(string sessionId) => Task.CompletedTask;
    public Task DeleteSandboxAsync(string sessionId) => Task.CompletedTask;
    public Task StopSessionAsync(string sessionId) => Task.CompletedTask;
    public Task<SandboxStatus?> GetSandboxStatusAsync(string sessionId) => Task.FromResult<SandboxStatus?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
