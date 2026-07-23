using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Files;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>Attachment upload materializes to a gitignored workspace dir and returns a workspace-relative
/// path (git-and-files/03), applying the conflict policy and never escaping the workspace.</summary>
public class AttachmentUploadTests : IDisposable
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agnes-attach-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(SessionManager Manager, string SessionId)> OpenAsync()
    {
        Directory.CreateDirectory(_dir);
        var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);
        var info = await manager.OpenSessionAsync("scripted", _dir, useSandbox: false);
        return (manager, info.SessionId);
    }

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Writes_the_file_and_returns_a_workspace_relative_path()
    {
        var (manager, sessionId) = await OpenAsync();
        await using var _ = manager;

        var rel = await manager.UploadAttachmentAsync(sessionId, "shot.png", Bytes("hello"));

        Assert.Equal(".agnes/attachments/shot.png", rel);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_dir, ".agnes", "attachments", "shot.png")));
    }

    [Fact]
    public async Task Keep_both_writes_a_distinct_name_on_conflict()
    {
        var (manager, sessionId) = await OpenAsync();
        await using var _ = manager;

        await manager.UploadAttachmentAsync(sessionId, "a.png", Bytes("first"), AttachmentConflict.KeepBoth);
        var rel2 = await manager.UploadAttachmentAsync(sessionId, "a.png", Bytes("second"), AttachmentConflict.KeepBoth);

        Assert.Equal(".agnes/attachments/a (1).png", rel2);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(_dir, ".agnes", "attachments", "a.png")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(_dir, ".agnes", "attachments", "a (1).png")));
    }

    [Fact]
    public async Task Replace_overwrites_and_skip_keeps_the_original()
    {
        var (manager, sessionId) = await OpenAsync();
        await using var _ = manager;
        var path = Path.Combine(_dir, ".agnes", "attachments", "a.png");

        await manager.UploadAttachmentAsync(sessionId, "a.png", Bytes("orig"));
        await manager.UploadAttachmentAsync(sessionId, "a.png", Bytes("replaced"), AttachmentConflict.Replace);
        Assert.Equal("replaced", await File.ReadAllTextAsync(path));

        await manager.UploadAttachmentAsync(sessionId, "a.png", Bytes("ignored"), AttachmentConflict.Skip);
        Assert.Equal("replaced", await File.ReadAllTextAsync(path)); // unchanged
    }

    [Fact]
    public async Task A_traversal_filename_is_stripped_to_its_leaf_and_stays_inside_the_workspace()
    {
        var (manager, sessionId) = await OpenAsync();
        await using var _ = manager;

        var rel = await manager.UploadAttachmentAsync(sessionId, "../../../etc/evil.png", Bytes("x"));

        Assert.Equal(".agnes/attachments/evil.png", rel);
        Assert.True(File.Exists(Path.Combine(_dir, ".agnes", "attachments", "evil.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "..", "..", "..", "etc", "evil.png")));
    }
}
