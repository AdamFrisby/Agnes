using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public class QuotaBadgeViewModelTests
{
    [Fact]
    public async Task Refreshing_populates_the_plan_label_and_meters()
    {
        var snapshot = new QuotaSnapshot(
            "Team plan",
            [
                new QuotaMeter("Monthly messages", Used: 120, Limit: 500, Unit: "requests"),
                new QuotaMeter("Credits", Used: 5, Limit: null, Unit: "USD"),
            ],
            DateTimeOffset.UnixEpoch);
        var host = new FakeQuotaHost(_ => snapshot);
        var vm = new QuotaBadgeViewModel(() => host, ImmediateDispatcher.Instance);

        await vm.RefreshAsync("profile-1");

        Assert.True(vm.HasQuota);
        Assert.False(vm.IsUnavailable);
        Assert.Equal("Team plan", vm.PlanLabel);
        Assert.Equal("profile-1", vm.ProfileId);
        Assert.Equal(2, vm.Meters.Count);

        var messages = vm.Meters[0];
        Assert.True(messages.HasBar);
        Assert.Equal(24, messages.Percent, precision: 3);      // 120 / 500
        Assert.Equal("120 / 500 requests", messages.ValueText);

        var credits = vm.Meters[1];
        Assert.False(credits.HasBar);                           // no limit → no bar
        Assert.Equal("5 USD", credits.ValueText);
    }

    [Fact]
    public async Task An_unavailable_profile_surfaces_a_clear_state_not_a_crash()
    {
        var host = new FakeQuotaHost(_ => null); // provider without the capability / unknown profile
        var vm = new QuotaBadgeViewModel(() => host, ImmediateDispatcher.Instance);

        await vm.RefreshAsync("profile-x");

        Assert.False(vm.HasQuota);
        Assert.True(vm.IsUnavailable);
        Assert.Empty(vm.Meters);
        Assert.Equal(string.Empty, vm.PlanLabel);
    }

    [Fact]
    public async Task A_transport_error_resolves_to_unavailable()
    {
        var host = new FakeQuotaHost(_ => throw new InvalidOperationException("boom"));
        var vm = new QuotaBadgeViewModel(() => host, ImmediateDispatcher.Instance);

        await vm.RefreshAsync("profile-x"); // must not throw

        Assert.True(vm.IsUnavailable);
    }

    /// <summary>A minimal <see cref="IAgnesHost"/> that answers only the quota pull and leans on the
    /// interface defaults / throws for the rest — the badge exercises just that one call.</summary>
    private sealed class FakeQuotaHost : IAgnesHost
    {
        private readonly Func<string, QuotaSnapshot?> _quota;
        public FakeQuotaHost(Func<string, QuotaSnapshot?> quota) => _quota = quota;

        public Task<QuotaSnapshot?> GetQuotaSnapshotAsync(string profileId)
            => Task.FromResult(_quota(profileId));

        public string HostUrl => "fake://host";
        public AgnesConnectionState State => AgnesConnectionState.Connected;

#pragma warning disable CS0067 // events are part of the interface but unused here
        public event Action<AgnesConnectionState>? StateChanged;
        public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;
        public event Action<InboxRun>? InboxRunReceived;
        public event Action<string, long, bool>? ReadStateChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<HostInfo> GetHostInfoAsync() => Task.FromResult(new HostInfo("fake", "fake", "1.0"));
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
}
