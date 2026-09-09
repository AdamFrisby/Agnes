using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Answering a request the agent has stopped waiting for. The adapter drops such a response with a log
/// line and returns success, so nothing was recorded, the card kept its buttons, and the user pressed
/// them again — one host's log held 218 discarded responses against a session advertising sixteen
/// approvals that could never be cleared. The response is recorded as withdrawn instead, so the card
/// closes on every client rather than inviting the click a nineteenth time.
/// </summary>
public class WithdrawnPermissionResponseTests
{
    private sealed class CapturingBroadcaster : ISessionBroadcaster
    {
        public List<SessionEvent> Published { get; } = [];

        public Task PublishAsync(string sessionId, SessionEvent @event)
        {
            Published.Add(@event);
            return Task.CompletedTask;
        }
    }

    private static async Task<(SessionManager Manager, ScriptedAgentAdapter Adapter, CapturingBroadcaster Bus, string SessionId)> OpenAsync()
    {
        var adapter = new ScriptedAgentAdapter();
        var broadcaster = new CapturingBroadcaster();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(adapter), new InMemoryEventStore(), broadcaster, NullLoggerFactory.Instance);
        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        return (manager, adapter, broadcaster, info.SessionId);
    }

    // Emit through the agent, the way a real one does, then wait for the manager's pump to land the last
    // of them in the store — the withdrawal check reads the log, so the log has to have caught up.
    private static async Task EmitAsync(
        ScriptedAgentAdapter adapter, CapturingBroadcaster broadcaster, params SessionEvent[] events)
    {
        foreach (var e in events)
        {
            adapter.Session.Emit(e);
        }

        for (var i = 0; i < 200 && broadcaster.Published.Count < events.Length; i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_response_to_a_withdrawn_request_is_recorded_rather_than_dropped()
    {
        var (manager, adapter, broadcaster, sessionId) = await OpenAsync();
        await using var _ = manager;

        // The shape a real Copilot session produced: it asks, then runs the tool anyway.
        await EmitAsync(
            adapter, broadcaster,
            new ToolCallEvent("tc1", "read", ToolKind.Read, ToolCallStatus.InProgress, []),
            new PermissionRequestedEvent("req1", "tc1", "Access paths outside trusted directories", []),
            new ToolCallUpdateEvent("tc1", ToolCallStatus.Completed, null));

        await manager.RespondPermissionAsync(sessionId, "req1", "allow_always");

        // Not forwarded: there is nothing on the other end to receive it.
        Assert.Null(adapter.Session.LastPermissionOptionId);

        // But recorded, so the card resolves and stops offering buttons that do nothing.
        var resolved = Assert.IsType<PermissionResolvedEvent>(broadcaster.Published[^1]);
        Assert.Equal("req1", resolved.RequestId);
        Assert.Equal(PermissionOutcome.Cancelled, resolved.Outcome);
    }

    [Fact]
    public async Task A_live_request_is_still_forwarded_to_the_agent()
    {
        var (manager, adapter, broadcaster, sessionId) = await OpenAsync();
        await using var _m = manager;

        await EmitAsync(
            adapter, broadcaster,
            new ToolCallEvent("tc1", "read", ToolKind.Read, ToolCallStatus.InProgress, []),
            new PermissionRequestedEvent("req1", "tc1", "Access paths outside trusted directories", []));

        await manager.RespondPermissionAsync(sessionId, "req1", "allow_once");

        // The diversion only ever triggers on positive evidence that nothing is waiting.
        Assert.Equal("allow_once", adapter.Session.LastPermissionOptionId);
    }

    [Fact]
    public async Task An_unknown_request_is_still_forwarded()
    {
        var (manager, adapter, _, sessionId) = await OpenAsync();
        await using var _m = manager;

        // Never seen in this log — could be a race with the append, so it goes to the agent as before
        // rather than being invented as a withdrawal.
        await manager.RespondPermissionAsync(sessionId, "mystery", "allow_once");

        Assert.Equal("allow_once", adapter.Session.LastPermissionOptionId);
    }
}
