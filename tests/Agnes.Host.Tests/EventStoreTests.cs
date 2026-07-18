using Agnes.Abstractions;
using Agnes.Host.Events;

namespace Agnes.Host.Tests;

public class EventStoreTests
{
    public static IEnumerable<object[]> Stores()
    {
        yield return [new InMemoryEventStore()];
        yield return [new SqliteEventStore(Path.Combine(Path.GetTempPath(), $"agnes-test-{Guid.NewGuid():n}.db"))];
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Assigns_monotonic_sequence_and_replays_from_cursor(IEventStore store)
    {
        var a = await store.AppendAsync("s1", new MessageChunkEvent(MessageRole.User, new TextContent("one")));
        var b = await store.AppendAsync("s1", new MessageChunkEvent(MessageRole.Assistant, new TextContent("two")));
        var c = await store.AppendAsync("s1", new TurnEndedEvent(StopReason.EndTurn));

        Assert.Equal(1, a.Sequence);
        Assert.Equal(2, b.Sequence);
        Assert.Equal(3, c.Sequence);
        Assert.Equal(3, await store.GetHeadAsync("s1"));

        // Tail from cursor 1 → only events 2 and 3, in order, round-tripped through (de)serialization.
        var tail = await store.ReadSinceAsync("s1", 1);
        Assert.Equal([2L, 3L], tail.Select(e => e.Sequence));
        var assistant = Assert.IsType<MessageChunkEvent>(tail[0]);
        Assert.Equal("two", ((TextContent)assistant.Content).Text);
        Assert.IsType<TurnEndedEvent>(tail[1]);

        // Sessions are isolated.
        Assert.Equal(0, await store.GetHeadAsync("other"));
    }

    [Fact]
    public async Task Sqlite_persists_across_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-persist-{Guid.NewGuid():n}.db");
        using (var store = new SqliteEventStore(path))
        {
            await store.AppendAsync("s1", new MessageChunkEvent(MessageRole.User, new TextContent("hello")));
        }

        using (var reopened = new SqliteEventStore(path))
        {
            Assert.Equal(1, await reopened.GetHeadAsync("s1"));
            var events = await reopened.ReadSinceAsync("s1", 0);
            Assert.Equal("hello", ((TextContent)((MessageChunkEvent)events[0]).Content).Text);
        }
    }
}
