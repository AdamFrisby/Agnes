using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Agents.Native;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Direct-vs-Synced sessions (.ideas/sessions/02): discovery of sessions a CLI created OUTSIDE Agnes from its
/// own on-disk logs, plus the read-only "watch" that tails such a log into the Agnes event model. Hermetic —
/// a temp Claude-home base dir is injected (never the real <c>~/.claude</c>) and temp transcripts drive attach.
/// </summary>
public class ExternalSessionsTests : IDisposable
{
    // Temp paths only (no absolute-path literals — PH2080).
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"agnes-claude-home-{Guid.NewGuid():n}");
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"agnes-ext-ws-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        GC.SuppressFinalize(this);
    }

    // A Claude-transcript conversation in its on-disk JSONL shape: a plain-string user prompt, an assistant
    // turn (text + a tool_use), and the tool's result. cwd carries the (temp) workspace so nothing is a literal.
    private string[] Transcript(string workspace) =>
    [
        """{"type":"user","cwd":"__WS__","sessionId":"S1","timestamp":"2024-01-01T00:00:00Z","message":{"role":"user","content":"hello world"}}""".Replace("__WS__", workspace, StringComparison.Ordinal),
        """{"type":"assistant","timestamp":"2024-01-01T00:00:05Z","message":{"role":"assistant","content":[{"type":"text","text":"Hi!"},{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"a.txt"}}]}}""",
        """{"type":"user","timestamp":"2024-01-01T00:00:06Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"file body"}]}}""",
    ];

    private string WriteTranscript(string sessionId, IEnumerable<string> lines)
    {
        var dir = ClaudeCodeExternalSessions.ProjectDirectory(_home, _workspace);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task Discover_lists_claude_transcripts_with_preview_timestamp_and_count()
    {
        WriteTranscript("S1", Transcript(_workspace));

        var sessions = await ClaudeCodeExternalSessions.DiscoverAsync(_home, _workspace);

        var session = Assert.Single(sessions);
        Assert.Equal(ClaudeCodeNative.AdapterId, session.AdapterId);
        Assert.Equal("hello world", session.Preview);
        Assert.Equal(3, session.MessageCount); // two user records + one assistant record
        Assert.Equal(_workspace, session.WorkspaceDirectory); // read from the transcript's cwd
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 6, TimeSpan.Zero), session.LastActivity);
    }

    [Fact]
    public async Task Discover_tolerates_missing_directory_and_malformed_lines()
    {
        // Missing projects directory => empty, no throw.
        Assert.Empty(await ClaudeCodeExternalSessions.DiscoverAsync(_home, _workspace));

        // A transcript with a malformed line among good ones => the bad line is skipped, not fatal.
        WriteTranscript("S1", ["{ not json ]", .. Transcript(_workspace)]);
        // A wholly-garbage transcript alongside it => tolerated (contributes a zero-count entry, never throws).
        WriteTranscript("S2", ["}}} broken {{{", "still not json"]);

        var sessions = await ClaudeCodeExternalSessions.DiscoverAsync(_home, _workspace);

        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.Preview == "hello world" && s.MessageCount == 3);
        Assert.Contains(sessions, s => s.MessageCount == 0);
    }

    [Fact]
    public async Task Adapter_without_the_capability_surfaces_no_external_sessions()
    {
        // A plain adapter that does NOT implement IExternalSessionSource — capability absent, graceful.
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter("plain")),
            new InMemoryEventStore(), new CollectingBroadcaster(), NullLoggerFactory.Instance);

        var discovered = await manager.DiscoverExternalSessionsAsync(_workspace);

        Assert.Empty(discovered);
    }

    [Fact]
    public async Task Attach_tails_an_external_transcript_into_a_read_only_session()
    {
        var path = WriteTranscript("S1", Transcript(_workspace));
        var adapter = ClaudeCodeNative.Create(NullLoggerFactory.Instance, claudeHome: _home);
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(), NullLoggerFactory.Instance);

        var info = await manager.AttachExternalSessionAsync(ClaudeCodeNative.AdapterId, path);
        Assert.True(info.ReadOnly); // flagged read-only so the composer/prompt is disabled

        // The transcript's lines are mapped, in order, into the Agnes session's event log.
        await WaitForAsync(() => manager.GetSnapshotAsync(info.SessionId, 0).GetAwaiter().GetResult().Events.Count >= 4);
        var snapshot = await manager.GetSnapshotAsync(info.SessionId, 0);
        Assert.True(snapshot.Session.ReadOnly);

        var user = Assert.IsType<MessageChunkEvent>(snapshot.Events[0]);
        Assert.Equal(MessageRole.User, user.Role);
        Assert.Equal("hello world", ((TextContent)user.Content).Text);
        var assistant = Assert.IsType<MessageChunkEvent>(snapshot.Events[1]);
        Assert.Equal(MessageRole.Assistant, assistant.Role);
        Assert.IsType<ToolCallEvent>(snapshot.Events[2]);
        Assert.IsType<ToolCallUpdateEvent>(snapshot.Events[3]);

        // Appending a new line to the file and re-polling surfaces the new event (live follow).
        await File.AppendAllLinesAsync(path,
        [
            """{"type":"user","timestamp":"2024-01-01T00:00:09Z","message":{"role":"user","content":"second message"}}""",
        ]);
        await WaitForAsync(() => manager.GetSnapshotAsync(info.SessionId, 0).GetAwaiter().GetResult().Events.Count >= 5);
        var grown = await manager.GetSnapshotAsync(info.SessionId, 0);
        var appended = Assert.IsType<MessageChunkEvent>(grown.Events[4]);
        Assert.Equal("second message", ((TextContent)appended.Content).Text);
    }

    [Fact]
    public async Task Prompting_a_read_only_watch_is_rejected_and_never_reaches_the_cli()
    {
        var path = WriteTranscript("S1", Transcript(_workspace));
        var adapter = ClaudeCodeNative.Create(NullLoggerFactory.Instance, claudeHome: _home);
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new CollectingBroadcaster(), NullLoggerFactory.Instance);

        var info = await manager.AttachExternalSessionAsync(ClaudeCodeNative.AdapterId, path);
        await WaitForAsync(() => manager.GetSnapshotAsync(info.SessionId, 0).GetAwaiter().GetResult().Events.Count >= 4);

        await manager.PromptAsync(info.SessionId, [new TextContent("please do a thing")]);

        var snapshot = await manager.GetSnapshotAsync(info.SessionId, 0);
        // The prompt was refused with a notice, and no user message was sent to (echoed by) the watched session.
        Assert.Contains(snapshot.Events, e => e is NoticeEvent { IsError: true });
        Assert.DoesNotContain(snapshot.Events, e => e is MessageChunkEvent { Role: MessageRole.User, Content: TextContent { Text: "please do a thing" } });
    }

    /// <summary>Records broadcast events, standing in for connected clients.</summary>
    private sealed class CollectingBroadcaster : ISessionBroadcaster
    {
        public ConcurrentQueue<(string SessionId, SessionEvent Event)> Published { get; } = new();

        public Task PublishAsync(string sessionId, SessionEvent @event)
        {
            Published.Enqueue((sessionId, @event));
            return Task.CompletedTask;
        }
    }
}
