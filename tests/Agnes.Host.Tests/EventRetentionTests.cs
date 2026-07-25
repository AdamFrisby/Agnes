using Agnes.Abstractions;
using Agnes.Host.Events;

namespace Agnes.Host.Tests;

/// <summary>Covers Low-3 transcript retention: PruneEventsBeforeAsync removes aged events and keeps recent ones.</summary>
public class EventRetentionTests
{
    [Fact]
    public async Task Sqlite_prune_removes_events_before_cutoff_and_keeps_the_rest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-ret-{Guid.NewGuid():n}.db");
        try
        {
            using var store = new SqliteEventStore(path);
            await store.AppendAsync("s", new NoticeEvent("hello")); // stamped now on append

            // A cutoff in the past prunes nothing (the event is newer than the cutoff).
            Assert.Equal(0, await store.PruneEventsBeforeAsync(DateTimeOffset.UtcNow.AddDays(-1)));
            Assert.Single(await store.ReadSinceAsync("s", 0));

            // A cutoff in the future is after the event's timestamp, so it's pruned.
            Assert.Equal(1, await store.PruneEventsBeforeAsync(DateTimeOffset.UtcNow.AddDays(1)));
            Assert.Empty(await store.ReadSinceAsync("s", 0));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task In_memory_prune_ages_out_events_by_timestamp()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("s", new NoticeEvent("hello"));

        Assert.Equal(0, await store.PruneEventsBeforeAsync(DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(1, await store.PruneEventsBeforeAsync(DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Empty(await store.ReadSinceAsync("s", 0));
    }
}
