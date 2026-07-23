using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>Replay-fork (sessions/01): branch a conversation at a log point — the child log gets a
/// ForkedFromEvent, the parent transcript is seeded (invisibly) into the child agent's first prompt, and a
/// user-message fork returns the origin text as a draft.</summary>
public class ReplayForkTests : IDisposable
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    // Hands out a fresh scripted session per launch (fork opens a second one).
    private sealed class FreshAdapter : IAgentAdapter
    {
        public List<ScriptedAgentSession> Sessions { get; } = [];
        public AgentDescriptor Descriptor { get; } = new() { Id = "scripted", DisplayName = "S" };
        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        {
            var s = new ScriptedAgentSession();
            Sessions.Add(s);
            return Task.FromResult<IAgentSession>(s);
        }
    }

    private readonly string _src = Path.Combine(Path.GetTempPath(), "agnes-fork-src-" + Guid.NewGuid().ToString("n"));
    private readonly string _dst = Path.Combine(Path.GetTempPath(), "agnes-fork-dst-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        foreach (var d in new[] { _src, _dst })
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) { cts.Token.ThrowIfCancellationRequested(); await Task.Delay(10, cts.Token); }
    }

    private static async Task WaitForEventAsync(SessionManager m, string sessionId, Func<IReadOnlyList<SessionEvent>, bool> pred)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (pred((await m.GetSnapshotAsync(sessionId, 0)).Events)) { return; }
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task Forking_at_an_assistant_message_seeds_the_child_and_marks_its_log()
    {
        Directory.CreateDirectory(_src);
        await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "x"); // something for the workspace copy
        var adapter = new FreshAdapter();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

        var parent = await manager.OpenSessionAsync("scripted", _src, useSandbox: false);
        adapter.Sessions[0].OnPrompt = (_, s) => { s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("hi there"))); s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };
        await manager.PromptAsync(parent.SessionId, [new TextContent("hello")]);
        await WaitForEventAsync(manager, parent.SessionId, e => e.OfType<TurnEndedEvent>().Any());

        // Fork at the assistant message (seq of the assistant chunk) — seeds parent context, no draft.
        var snapshot = await manager.GetSnapshotAsync(parent.SessionId, 0);
        var assistantSeq = snapshot.Events.OfType<MessageChunkEvent>().First(m => m.Role == MessageRole.Assistant).Sequence;
        var result = await manager.ForkSessionAtAsync(parent.SessionId, _dst, assistantSeq, copySandbox: false);

        Assert.Null(result.Draft); // forked at an assistant message → no draft

        // The child's log starts with a ForkedFromEvent pointing at the parent.
        var childSnap = await manager.GetSnapshotAsync(result.Info.SessionId, 0);
        var marker = Assert.Single(childSnap.Events.OfType<ForkedFromEvent>());
        Assert.Equal(parent.SessionId, marker.ParentSessionId);
        Assert.Equal(assistantSeq, marker.ParentSequence);

        // The child agent (a fresh session) receives the parent transcript as an invisible seed ahead of the
        // user's first message.
        IReadOnlyList<ContentBlock>? childGot = null;
        adapter.Sessions[1].OnPrompt = (c, s) => { childGot = c; s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };
        await manager.PromptAsync(result.Info.SessionId, [new TextContent("continue")]);
        await WaitForAsync(() => childGot is not null);

        var seededText = string.Concat(childGot!.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("Forked conversation", seededText);
        Assert.Contains("hello", seededText);      // parent user message
        Assert.Contains("hi there", seededText);   // parent assistant message
        Assert.Contains("continue", seededText);   // the actual new prompt, appended after the seed
        // ...but the seed was NOT logged as a visible user message in the child.
        Assert.DoesNotContain(childSnap.Events.OfType<MessageChunkEvent>(), m => m.Content is TextContent t && t.Text.Contains("Forked conversation"));
    }

    [Fact]
    public async Task Forking_at_a_user_message_returns_its_text_as_a_draft()
    {
        Directory.CreateDirectory(_src);
        var adapter = new FreshAdapter();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), new NullBroadcaster(), NullLoggerFactory.Instance);

        var parent = await manager.OpenSessionAsync("scripted", _src, useSandbox: false);
        adapter.Sessions[0].OnPrompt = (_, s) => { s.Emit(new TurnEndedEvent(StopReason.EndTurn)); return Task.FromResult(StopReason.EndTurn); };
        await manager.PromptAsync(parent.SessionId, [new TextContent("try this differently")]);
        await WaitForEventAsync(manager, parent.SessionId, e => e.OfType<MessageChunkEvent>().Any(m => m.Role == MessageRole.User));

        var userSeq = (await manager.GetSnapshotAsync(parent.SessionId, 0)).Events.OfType<MessageChunkEvent>().First(m => m.Role == MessageRole.User).Sequence;
        var result = await manager.ForkSessionAtAsync(parent.SessionId, _dst, userSeq, copySandbox: false);

        Assert.Equal("try this differently", result.Draft);
    }
}
