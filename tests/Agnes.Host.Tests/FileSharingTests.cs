using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Files;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// An agent sending the user a file: the copy lands somewhere stable inside the workspace, the appended
/// <see cref="FileSharedEvent"/> reaches subscribed clients, and every way of getting a file out of the
/// workspace that shouldn't work is refused before anything is read or written.
/// </summary>
public sealed class FileSharingTests : IDisposable
{
    /// <summary>Stands in for a subscribed client: what the host broadcast, in order.</summary>
    private sealed class CollectingBroadcaster : ISessionBroadcaster
    {
        public List<(string SessionId, SessionEvent Event)> Published { get; } = [];

        public Task PublishAsync(string sessionId, SessionEvent @event)
        {
            lock (Published)
            {
                Published.Add((sessionId, @event));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class VetoShare : IEventInterceptor<BeforeFileSharedEvent>
    {
        public ValueTask InterceptAsync(BeforeFileSharedEvent evt, CancellationToken ct = default)
        {
            evt.Cancel("it carries a credential");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RewriteCaption : IEventInterceptor<BeforeFileSharedEvent>
    {
        public ValueTask InterceptAsync(BeforeFileSharedEvent evt, CancellationToken ct = default)
        {
            evt.Caption = "reviewed: " + evt.Caption;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A plugin watching the spine for the observe-only fact.</summary>
    private sealed class WatchShares : IEventObserver<FileSharedEvent>
    {
        public List<FileSharedEvent> Seen { get; } = [];

        public ValueTask ObserveAsync(FileSharedEvent evt, CancellationToken ct = default)
        {
            Seen.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agnes-share-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed record Harness(SessionManager Manager, string SessionId, CollectingBroadcaster Broadcaster, EventBus Bus);

    private async Task<Harness> OpenAsync(SharingOptions? sharing = null, params object[] interceptors)
    {
        Directory.CreateDirectory(_dir);
        var bus = new EventBus();
        foreach (var interceptor in interceptors)
        {
            switch (interceptor)
            {
                case IEventInterceptor<BeforeFileSharedEvent> i:
                    bus.Intercept(i);
                    break;
                default:
                    throw new ArgumentException("Unsupported interceptor type.", nameof(interceptors));
            }
        }

        var broadcaster = new CollectingBroadcaster();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(), broadcaster,
            NullLoggerFactory.Instance, eventBus: bus, sharing: sharing);
        var info = await manager.OpenSessionAsync("scripted", _dir, useSandbox: false);
        return new Harness(manager, info.SessionId, broadcaster, bus);
    }

    private string WriteWorkspaceFile(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public async Task Copies_the_file_to_a_stable_path_and_describes_it()
    {
        var h = await OpenAsync();
        await using var _ = h.Manager;
        WriteWorkspaceFile("out/report.md", "the numbers");

        var shared = await h.Manager.ShareFileAsync(h.SessionId, "out/report.md", "before vs after");

        Assert.Equal("report.md", shared.FileName);
        Assert.Equal(16, shared.FileId.Length);
        Assert.Equal($".agnes/shared/{shared.FileId}/report.md", shared.RelativePath);
        Assert.Equal("the numbers".Length, shared.Size);
        Assert.Equal("text/markdown", shared.MimeType);
        Assert.Equal("before vs after", shared.Caption);

        // The copy is real and independent of the original: editing the source afterwards must not change
        // what the person receives.
        var stored = Path.Combine(_dir, ".agnes", "shared", shared.FileId, "report.md");
        Assert.Equal("the numbers", await File.ReadAllTextAsync(stored));
        WriteWorkspaceFile("out/report.md", "rewritten");
        Assert.Equal("the numbers", await File.ReadAllTextAsync(stored));

        // And the original is still where the agent left it — this sends a copy, it doesn't take the file away.
        Assert.True(File.Exists(Path.Combine(_dir, "out", "report.md")));
    }

    [Fact]
    public async Task The_shared_directory_ignores_itself_so_the_copy_never_lands_in_a_commit()
    {
        var h = await OpenAsync();
        await using var _ = h.Manager;
        WriteWorkspaceFile("shot.png", "png");

        await h.Manager.ShareFileAsync(h.SessionId, "shot.png", null);

        Assert.Equal("*\n", await File.ReadAllTextAsync(Path.Combine(_dir, ".agnes", ".gitignore")));
    }

    [Fact]
    public async Task An_absolute_path_and_a_sandbox_work_path_resolve_to_the_same_file()
    {
        var h = await OpenAsync();
        await using var _ = h.Manager;
        var absolute = WriteWorkspaceFile("build/app.zip", "zip");

        var viaAbsolute = await h.Manager.ShareFileAsync(h.SessionId, absolute, null);
        // Inside a sandbox the very same directory is mounted at /work, so this is the path the agent sees.
        var viaSandbox = await h.Manager.ShareFileAsync(h.SessionId, "/work/build/app.zip", null);

        Assert.Equal("app.zip", viaAbsolute.FileName);
        Assert.Equal("app.zip", viaSandbox.FileName);
        Assert.Equal("application/zip", viaSandbox.MimeType);
        // Distinct ids: each send is its own artifact, so one can't be clobbered by the next.
        Assert.NotEqual(viaAbsolute.FileId, viaSandbox.FileId);
    }

    [Fact]
    public async Task An_unrecognised_extension_shares_with_no_mime_type()
    {
        var h = await OpenAsync();
        await using var _ = h.Manager;
        WriteWorkspaceFile("thing.qqq", "x");

        var shared = await h.Manager.ShareFileAsync(h.SessionId, "thing.qqq", null);

        Assert.Null(shared.MimeType);
    }

    [Fact]
    public async Task A_path_outside_the_workspace_is_refused_without_touching_it()
    {
        var h = await OpenAsync();
        await using var _ = h.Manager;
        var outside = Path.Combine(Path.GetTempPath(), "agnes-outside-" + Guid.NewGuid().ToString("n") + ".txt");
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            foreach (var attempt in new[] { outside, "../" + Path.GetFileName(outside), "/work/../" + Path.GetFileName(outside) })
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => h.Manager.ShareFileAsync(h.SessionId, attempt, null));
                Assert.Contains("outside this session's workspace", error.Message, StringComparison.Ordinal);
            }

            // Nothing was copied, and nothing was logged as sent.
            Assert.False(Directory.Exists(Path.Combine(_dir, ".agnes", "shared")));
            Assert.DoesNotContain(h.Broadcaster.Published, p => p.Event is FileSharedEvent);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task A_missing_file_and_a_directory_are_both_refused()
    {
        var h = await OpenAsync();
        await using var _ = h.Manager;
        Directory.CreateDirectory(Path.Combine(_dir, "docs"));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Manager.ShareFileAsync(h.SessionId, "nope.md", null));
        Assert.Contains("no file at", missing.Message, StringComparison.Ordinal);

        var directory = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Manager.ShareFileAsync(h.SessionId, "docs", null));
        Assert.Contains("is a directory", directory.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_over_the_cap_is_refused_and_the_message_names_the_limit()
    {
        var h = await OpenAsync(new SharingOptions { MaxBytes = 1024 });
        await using var _ = h.Manager;
        WriteWorkspaceFile("big.bin", new string('x', 4096));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Manager.ShareFileAsync(h.SessionId, "big.bin", null));

        Assert.Contains("limit for a sent file", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, ".agnes", "shared")));
    }

    [Fact]
    public async Task An_interceptor_can_veto_the_send_and_its_reason_reaches_the_caller()
    {
        var h = await OpenAsync(sharing: null, new VetoShare());
        await using var _ = h.Manager;
        WriteWorkspaceFile("secrets.env", "TOKEN=1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Manager.ShareFileAsync(h.SessionId, "secrets.env", null));

        Assert.Contains("it carries a credential", error.Message, StringComparison.Ordinal);
        // Vetoed before the copy: a blocked file leaves nothing behind for anyone to fetch.
        Assert.False(Directory.Exists(Path.Combine(_dir, ".agnes", "shared")));
        Assert.DoesNotContain(h.Broadcaster.Published, p => p.Event is FileSharedEvent);
    }

    [Fact]
    public async Task An_interceptor_can_rewrite_the_caption()
    {
        var h = await OpenAsync(sharing: null, new RewriteCaption());
        await using var _ = h.Manager;
        WriteWorkspaceFile("chart.png", "png");

        var shared = await h.Manager.ShareFileAsync(h.SessionId, "chart.png", "q3");

        Assert.Equal("reviewed: q3", shared.Caption);
    }

    [Fact]
    public async Task The_event_is_persisted_sequenced_and_broadcast_to_subscribed_clients()
    {
        var h = await OpenAsync();
        await using var _ = h.Manager;
        WriteWorkspaceFile("report.md", "hello");

        var shared = await h.Manager.ShareFileAsync(h.SessionId, "report.md", null);

        // Broadcast (what a connected client sees live)…
        var published = Assert.Single(h.Broadcaster.Published, p => p.Event is FileSharedEvent);
        Assert.Equal(h.SessionId, published.SessionId);
        Assert.Equal(shared.FileId, ((FileSharedEvent)published.Event).FileId);

        // …and persisted in the log with a sequence, so a client joining later replays it identically.
        var snapshot = await h.Manager.GetSnapshotAsync(h.SessionId, 0);
        var replayed = Assert.Single(snapshot.Events.OfType<FileSharedEvent>());
        Assert.Equal(shared.FileId, replayed.FileId);
        Assert.True(replayed.Sequence > 0);
        Assert.Equal(replayed.Sequence, shared.Sequence);
    }

    [Fact]
    public async Task The_appended_event_rides_the_spine_so_plugins_see_it()
    {
        var h = await OpenAsync();
        await using var _ = h.Manager;
        WriteWorkspaceFile("chart.png", "png");
        var watcher = new WatchShares();
        h.Bus.Observe(watcher);

        await h.Manager.ShareFileAsync(h.SessionId, "chart.png", null);

        Assert.Equal("chart.png", Assert.Single(watcher.Seen).FileName);
    }

    [Fact]
    public void The_mime_map_is_extension_only_and_honest_about_what_it_does_not_know()
    {
        Assert.Equal("image/png", SharedFileTypes.MimeTypeFor("SHOT.PNG"));
        Assert.Equal("application/pdf", SharedFileTypes.MimeTypeFor("a/b/spec.pdf"));
        Assert.Equal("application/yaml", SharedFileTypes.MimeTypeFor("ci.yml"));
        Assert.Null(SharedFileTypes.MimeTypeFor("Makefile"));
        Assert.Null(SharedFileTypes.MimeTypeFor("weird.qqq"));
        Assert.Null(SharedFileTypes.MimeTypeFor(null));
    }
}
