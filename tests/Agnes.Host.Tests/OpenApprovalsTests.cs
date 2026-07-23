using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// The cross-session approvals aggregation (notifications/02 tier 1): open permission requests unioned
/// across live sessions, resolved ones excluded, most-recent first.
/// </summary>
public class OpenApprovalsTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private static SessionManager NewManager(ScriptedAgentAdapter adapter, IEventStore store)
        => new(TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance);

    // Drives the scripted session to emit a set of permission events for a prompt, and waits until the
    // host has persisted them all to the log.
    private static async Task EmitAsync(SessionManager manager, ScriptedAgentAdapter adapter, IEventStore store, string sessionId, params SessionEvent[] events)
    {
        adapter.Session.OnPrompt = (_, s) =>
        {
            foreach (var e in events)
            {
                s.Emit(e);
            }

            return Task.FromResult(StopReason.EndTurn);
        };

        var before = await store.GetHeadAsync(sessionId);
        await manager.PromptAsync(sessionId, [new TextContent("go")]);
        // The prompt itself is one event; then the emitted ones follow.
        var expected = before + 1 + events.Length;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await store.GetHeadAsync(sessionId) < expected)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    private static PermissionRequestedEvent Requested(string requestId, string toolCallId, string title)
        => new(requestId, toolCallId, title, [new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce)]);

    [Fact]
    public async Task Returns_only_unresolved_requests_with_correct_fields()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);

        // One request that gets resolved, one that stays open.
        await EmitAsync(manager, adapter, store, info.SessionId,
            Requested("req-resolved", "tool-1", "Write file A"),
            new PermissionResolvedEvent("req-resolved", "allow", PermissionOutcome.Allowed),
            Requested("req-open", "tool-2", "Run tests"));

        var approvals = await manager.GetOpenApprovalsAsync();

        var only = Assert.Single(approvals);
        Assert.Equal(info.SessionId, only.SessionId);
        Assert.Equal("req-open", only.RequestId);
        Assert.Equal("Run tests", only.Title);
        Assert.Equal("tool-2", only.ToolCallId);
    }

    [Fact]
    public async Task Unions_across_sessions_and_sorts_most_recent_first()
    {
        var adapterA = new ScriptedAgentAdapter("a");
        var adapterB = new ScriptedAgentAdapter("b");
        var store = new InMemoryEventStore();
        await using var manager = new SessionManager(
            TestPluginRegistries.Agents(adapterA, adapterB), store, new NullBroadcaster(), NullLoggerFactory.Instance);

        var a = await manager.OpenSessionAsync("a", "/tmp/a", useSandbox: false);
        var b = await manager.OpenSessionAsync("b", "/tmp/b", useSandbox: false);

        // Session A's request is emitted first (older), then session B's (newer). Recency ordering is by the
        // host-stamped Timestamp, so A must land strictly before B.
        await EmitAsync(manager, adapterA, store, a.SessionId, Requested("a1", "ta", "A open"));
        await Task.Delay(15);
        await EmitAsync(manager, adapterB, store, b.SessionId, Requested("b1", "tb", "B open"));

        var approvals = await manager.GetOpenApprovalsAsync();

        Assert.Equal(2, approvals.Count);
        // Newest first: B before A.
        Assert.Equal(b.SessionId, approvals[0].SessionId);
        Assert.Equal("b1", approvals[0].RequestId);
        Assert.Equal(a.SessionId, approvals[1].SessionId);
        Assert.Equal("a1", approvals[1].RequestId);
    }

    [Fact]
    public async Task Session_with_no_open_requests_contributes_nothing()
    {
        var adapter = new ScriptedAgentAdapter();
        var store = new InMemoryEventStore();
        await using var manager = NewManager(adapter, store);

        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);

        // Every request is resolved, so the session is fully answered.
        await EmitAsync(manager, adapter, store, info.SessionId,
            Requested("r1", "t1", "One"),
            new PermissionResolvedEvent("r1", "allow", PermissionOutcome.Allowed));

        var approvals = await manager.GetOpenApprovalsAsync();
        Assert.Empty(approvals);
    }
}
