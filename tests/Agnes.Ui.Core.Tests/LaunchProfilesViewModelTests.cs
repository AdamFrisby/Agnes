using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public class LaunchProfilesViewModelTests
{
    [Fact]
    public async Task Refreshing_populates_the_saved_profiles()
    {
        var host = new FakeProfileHost();
        host.Seed(new LaunchProfile("a", "Scratch", "opencode"));
        host.Seed(new LaunchProfile("b", "Prod", "claude-code"));
        var vm = new LaunchProfilesViewModel(() => host, ImmediateDispatcher.Instance);

        await vm.RefreshAsync();

        Assert.True(vm.HasProfiles);
        Assert.Equal(2, vm.Profiles.Count);
    }

    [Fact]
    public async Task Deleting_a_profile_removes_it_over_the_wire_and_from_the_list()
    {
        var host = new FakeProfileHost();
        host.Seed(new LaunchProfile("a", "Scratch", "opencode"));
        var vm = new LaunchProfilesViewModel(() => host, ImmediateDispatcher.Instance);
        await vm.RefreshAsync();

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.DeleteCommand).ExecuteAsync(vm.Profiles[0]);

        Assert.False(vm.HasProfiles);
        Assert.Empty(host.All);
    }

    [Fact]
    public async Task With_no_host_the_surface_degrades_cleanly()
    {
        var vm = new LaunchProfilesViewModel(() => null, ImmediateDispatcher.Instance);
        await vm.RefreshAsync(); // must not throw
        Assert.False(vm.HasProfiles);
    }

    [Fact]
    public async Task A_row_describes_what_picking_the_profile_would_do()
    {
        var host = new FakeProfileHost();
        host.Seed(new LaunchProfile(
            "a", "Prod fixes", "claude-code", "/srv/app", UseWorktree: true, SkipPermissions: true,
            McpApproval: "Trust", GitCredentialMode: "Ask", UseSandbox: true, ModelId: "claude-opus-5"));
        var vm = new LaunchProfilesViewModel(() => host, ImmediateDispatcher.Instance);

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Profiles);
        Assert.Contains("claude-code", row.Where, StringComparison.Ordinal);
        Assert.Contains("/srv/app", row.Where, StringComparison.Ordinal);
        Assert.Contains("in a sandbox VM", row.Where, StringComparison.Ordinal);
        Assert.Contains("own git worktree", row.Where, StringComparison.Ordinal);
        Assert.Contains("autonomous", row.Posture, StringComparison.Ordinal);
        Assert.Contains("MCP tools: Trust", row.Posture, StringComparison.Ordinal);
        Assert.Contains("git credentials: Ask", row.Posture, StringComparison.Ordinal);
        Assert.Contains("claude-opus-5", row.Posture, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_less_profile_says_it_will_ask_at_launch()
    {
        var host = new FakeProfileHost();
        host.Seed(new LaunchProfile("a", "Scratch", "opencode", UseSandbox: false));
        var vm = new LaunchProfilesViewModel(() => host, ImmediateDispatcher.Instance);

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Profiles);
        Assert.Contains("asked at launch", row.Where, StringComparison.Ordinal);
        Assert.Contains("on the host", row.Where, StringComparison.Ordinal);
        Assert.Contains("asks before each tool", row.Posture, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renaming_keeps_every_other_captured_option()
    {
        var host = new FakeProfileHost();
        host.Seed(new LaunchProfile("a", "Scratch", "opencode", "/srv/app", SkipPermissions: true, ModelId: "gpt-5"));
        var vm = new LaunchProfilesViewModel(() => host, ImmediateDispatcher.Instance);
        await vm.RefreshAsync();

        vm.BeginRenameCommand.Execute(vm.Profiles[0]);
        Assert.True(vm.Profiles[0].IsRenaming);
        Assert.Equal("Scratch", vm.Profiles[0].DraftName);

        vm.Profiles[0].DraftName = "Nightly";
        await vm.CommitRenameCommand.ExecuteAsync(vm.Profiles[0]);

        var stored = Assert.Single(host.All);
        Assert.Equal("Nightly", stored.Name);
        Assert.Equal("a", stored.Id);
        Assert.Equal("/srv/app", stored.WorkingDirectory);
        Assert.True(stored.SkipPermissions);
        Assert.Equal("gpt-5", stored.ModelId);
        Assert.False(vm.Profiles[0].IsRenaming);
    }

    [Fact]
    public async Task An_empty_rename_is_ignored_rather_than_wiping_the_name()
    {
        var host = new FakeProfileHost();
        host.Seed(new LaunchProfile("a", "Scratch", "opencode"));
        var vm = new LaunchProfilesViewModel(() => host, ImmediateDispatcher.Instance);
        await vm.RefreshAsync();
        vm.BeginRenameCommand.Execute(vm.Profiles[0]);

        vm.Profiles[0].DraftName = "   ";
        await vm.CommitRenameCommand.ExecuteAsync(vm.Profiles[0]);

        Assert.Equal("Scratch", Assert.Single(host.All).Name);
    }

    /// <summary>A minimal <see cref="IAgnesHost"/> with an in-memory launch-profile store; everything else
    /// leans on the interface defaults / throws.</summary>
    private sealed class FakeProfileHost : IAgnesHost
    {
        private readonly Dictionary<string, LaunchProfile> _byId = new(StringComparer.Ordinal);

        public void Seed(LaunchProfile p) => _byId[p.Id] = p;
        public IReadOnlyCollection<LaunchProfile> All => _byId.Values.ToArray();

        public Task<IReadOnlyList<LaunchProfile>> GetLaunchProfilesAsync()
            => Task.FromResult<IReadOnlyList<LaunchProfile>>(_byId.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray());

        public Task<LaunchProfile> SaveLaunchProfileAsync(LaunchProfile profile)
        {
            var stored = string.IsNullOrWhiteSpace(profile.Id) ? profile with { Id = Guid.NewGuid().ToString("n") } : profile;
            _byId[stored.Id] = stored;
            return Task.FromResult(stored);
        }

        public Task DeleteLaunchProfileAsync(string id)
        {
            _byId.Remove(id);
            return Task.CompletedTask;
        }

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
    public Task SwitchModelAsync(string sessionId, string? modelId) => Task.CompletedTask;
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
