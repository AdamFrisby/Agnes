using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>The framework-agnostic file-browser VM (git-and-files/03): navigation drives the entry list,
/// opening a file populates the preview, and edit/rename/delete/new-folder call the right host op with a
/// workspace-relative path. Exercised against a recording fake host — no server.</summary>
public class FileBrowserViewModelTests
{
    private static FileEntry Dir(string name) => new(name, name, IsDirectory: true, 0, DateTimeOffset.UnixEpoch);
    private static FileEntry FileAt(string relativePath) => new(Path.GetFileName(relativePath), relativePath, IsDirectory: false, 3, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Refresh_lists_the_root_directory()
    {
        var host = new FakeBrowserHost
        {
            Listings =
            {
                [""] = [Dir("src"), FileAt("readme.md")],
            },
        };
        var vm = new FileBrowserViewModel(host, "s1");

        await vm.RefreshAsync();

        Assert.Equal(2, vm.Entries.Count);
        Assert.True(vm.IsAtRoot);
        Assert.Equal(("s1", ""), host.LastList);
    }

    [Fact]
    public async Task Navigating_into_a_directory_then_up_updates_the_entry_list()
    {
        var host = new FakeBrowserHost
        {
            Listings =
            {
                [""] = [Dir("src")],
                ["src"] = [FileAt("src/Foo.cs"), FileAt("src/Bar.cs")],
            },
        };
        var vm = new FileBrowserViewModel(host, "s1");
        await vm.RefreshAsync();

        await vm.NavigateIntoAsync(Dir("src"));

        Assert.Equal("src", vm.CurrentPath);
        Assert.False(vm.IsAtRoot);
        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal("src/Foo.cs", vm.Entries[0].RelativePath);

        await vm.NavigateUpAsync();

        Assert.True(vm.IsAtRoot);
        Assert.Equal("src", Assert.Single(vm.Entries).Name);
    }

    [Fact]
    public async Task Opening_a_file_populates_the_text_content()
    {
        var host = new FakeBrowserHost
        {
            Files = { ["readme.md"] = new FileContent("readme.md", FileContentKind.Text, "the body", null, null, 8) },
        };
        var vm = new FileBrowserViewModel(host, "s1");

        await vm.OpenEntryAsync(FileAt("readme.md"));

        Assert.True(vm.HasOpenFile);
        Assert.True(vm.IsTextOpen);
        Assert.False(vm.IsImageOpen);
        Assert.Equal("the body", vm.EditText);
    }

    [Fact]
    public async Task Opening_an_image_flags_image_preview_without_editable_text()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var host = new FakeBrowserHost
        {
            Files = { ["shot.png"] = new FileContent("shot.png", FileContentKind.Image, null, bytes, "image/png", 3) },
        };
        var vm = new FileBrowserViewModel(host, "s1");

        await vm.OpenEntryAsync(FileAt("shot.png"));

        Assert.True(vm.IsImageOpen);
        Assert.False(vm.IsTextOpen);
        Assert.Equal(string.Empty, vm.EditText);
    }

    [Fact]
    public async Task Saving_an_edit_writes_the_current_text_to_the_host()
    {
        var host = new FakeBrowserHost
        {
            Files = { ["readme.md"] = new FileContent("readme.md", FileContentKind.Text, "old", null, null, 3) },
        };
        var vm = new FileBrowserViewModel(host, "s1");
        await vm.OpenEntryAsync(FileAt("readme.md"));

        vm.EditText = "new text";
        await vm.SaveEditAsync();

        Assert.Equal(("s1", "readme.md", "new text"), host.LastWrite);
    }

    [Fact]
    public async Task Deleting_an_entry_calls_the_host_and_refreshes()
    {
        var host = new FakeBrowserHost { Listings = { [""] = [FileAt("junk.txt")] } };
        var vm = new FileBrowserViewModel(host, "s1");
        await vm.RefreshAsync();

        await vm.DeleteAsync(FileAt("junk.txt"));

        Assert.Equal(("s1", "junk.txt"), host.LastDelete);
        Assert.Equal(2, host.ListCount); // once for the initial refresh, once after the delete
    }

    [Fact]
    public async Task Renaming_keeps_the_entry_in_its_directory()
    {
        var host = new FakeBrowserHost();
        var vm = new FileBrowserViewModel(host, "s1");

        await vm.RenameAsync(FileAt("src/Old.cs"), "New.cs");

        Assert.Equal(("s1", "src/Old.cs", "src/New.cs"), host.LastRename);
    }

    [Fact]
    public async Task Creating_a_folder_targets_the_current_directory()
    {
        var host = new FakeBrowserHost
        {
            Listings =
            {
                [""] = [Dir("src")],
                ["src"] = [],
            },
        };
        var vm = new FileBrowserViewModel(host, "s1");
        await vm.RefreshAsync();
        await vm.NavigateIntoAsync(Dir("src"));

        await vm.CreateFolderAsync("nested");

        Assert.Equal(("s1", "src/nested"), host.LastCreateDir);
    }

    [Fact]
    public async Task A_failing_op_surfaces_an_error_rather_than_throwing()
    {
        var host = new FakeBrowserHost { ThrowOnRead = true };
        var vm = new FileBrowserViewModel(host, "s1");

        await vm.OpenEntryAsync(FileAt("secret.txt"));

        Assert.True(vm.HasError);
        Assert.False(vm.HasOpenFile);
        Assert.False(vm.IsBusy);
    }

    private sealed class FakeBrowserHost : IAgnesHost
    {
        public Dictionary<string, IReadOnlyList<FileEntry>> Listings { get; } = new();
        public Dictionary<string, FileContent> Files { get; } = new();
        public bool ThrowOnRead { get; init; }

        public int ListCount { get; private set; }
        public (string SessionId, string Path)? LastList { get; private set; }
        public (string SessionId, string Path, string Content)? LastWrite { get; private set; }
        public (string SessionId, string Path)? LastDelete { get; private set; }
        public (string SessionId, string From, string To)? LastRename { get; private set; }
        public (string SessionId, string Path)? LastCreateDir { get; private set; }

        public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(string sessionId, string relativePath)
        {
            ListCount++;
            LastList = (sessionId, relativePath);
            return Task.FromResult(Listings.TryGetValue(relativePath, out var e) ? e : []);
        }

        public Task<FileContent> ReadFileAsync(string sessionId, string relativePath)
            => ThrowOnRead
                ? throw new UnauthorizedAccessException("nope")
                : Task.FromResult(Files[relativePath]);

        public Task WriteFileAsync(string sessionId, string relativePath, string content)
        {
            LastWrite = (sessionId, relativePath, content);
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string sessionId, string relativePath)
        {
            LastCreateDir = (sessionId, relativePath);
            return Task.CompletedTask;
        }

        public Task RenameEntryAsync(string sessionId, string fromRelativePath, string toRelativePath)
        {
            LastRename = (sessionId, fromRelativePath, toRelativePath);
            return Task.CompletedTask;
        }

        public Task DeleteEntryAsync(string sessionId, string relativePath)
        {
            LastDelete = (sessionId, relativePath);
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadFileAsync(string sessionId, string relativePath) => Task.FromResult<byte[]>([]);

        // ---- unused IAgnesHost surface ----
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
