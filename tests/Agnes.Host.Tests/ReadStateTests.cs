using Agnes.Abstractions;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>Host-tracked read state: mark read/unread updates the cursor, clears/sets the sticky flag, and
/// pushes to subscribed clients (sessions/05).</summary>
public class ReadStateTests
{
    private sealed class CapturingBroadcaster : ISessionBroadcaster
    {
        public List<(string SessionId, long Seq, bool Sticky)> Pushes { get; } = [];
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
        public Task PublishReadStateAsync(string sessionId, long readSequence, bool stickyUnread)
        {
            Pushes.Add((sessionId, readSequence, stickyUnread));
            return Task.CompletedTask;
        }
    }

    private static async Task<(SessionManager Manager, string SessionId, CapturingBroadcaster Broadcaster)> OpenAsync()
    {
        var broadcaster = new CapturingBroadcaster();
        var manager = new SessionManager(
            TestPluginRegistries.Agents(new ScriptedAgentAdapter()), new InMemoryEventStore(), broadcaster, NullLoggerFactory.Instance);
        var info = await manager.OpenSessionAsync("scripted", "/tmp/work", useSandbox: false);
        return (manager, info.SessionId, broadcaster);
    }

    [Fact]
    public async Task Mark_read_advances_the_cursor_clears_sticky_and_pushes()
    {
        var (manager, sessionId, broadcaster) = await OpenAsync();
        await using var _ = manager;

        await manager.MarkUnreadAsync(sessionId);
        Assert.True(manager.GetReadState(sessionId).StickyUnread);

        await manager.MarkReadAsync(sessionId, 7);
        var state = manager.GetReadState(sessionId);
        Assert.Equal(7, state.ReadCursor);
        Assert.False(state.StickyUnread);

        // Both mutations were pushed to clients; the last reflects read-up-to-7.
        Assert.Contains(broadcaster.Pushes, p => p is { Seq: 7, Sticky: false });
    }

    [Fact]
    public async Task Mark_read_never_moves_the_cursor_backwards()
    {
        var (manager, sessionId, _) = await OpenAsync();
        await using var _ = manager;

        await manager.MarkReadAsync(sessionId, 10);
        await manager.MarkReadAsync(sessionId, 4); // stale/out-of-order
        Assert.Equal(10, manager.GetReadState(sessionId).ReadCursor);
    }
}
