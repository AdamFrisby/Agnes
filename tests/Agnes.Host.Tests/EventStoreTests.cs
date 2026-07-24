using Agnes.Abstractions;
using Agnes.Host.Events;
using Microsoft.Data.Sqlite;

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

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Session_record_round_trips_the_model_id(IEventStore store)
    {
        var record = new SessionRecord("s1", "claude-code-native", "/work", "agent-1",
            UseWorktree: false, SkipPermissions: false, Sandboxed: true, DateTimeOffset.UtcNow, ModelId: "opus");
        await store.SaveSessionAsync(record);

        var loaded = Assert.Single(await store.ListSessionsAsync());
        Assert.Equal("opus", loaded.ModelId);

        // A model switch re-saves the same session with a new model — the upsert must update it.
        await store.SaveSessionAsync(record with { ModelId = "sonnet" });
        Assert.Equal("sonnet", (await store.ListSessionsAsync()).Single().ModelId);
    }

    [Fact]
    public async Task Sqlite_migrates_a_legacy_catalogue_without_the_model_id_column()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-legacy-{Guid.NewGuid():n}.db");

        // Seed a pre-model_id catalogue (the column simply doesn't exist yet), as an older host would leave it.
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        await using (var seed = new SqliteConnection(cs))
        {
            await seed.OpenAsync();
            await using var cmd = seed.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE sessions (
                    session_id TEXT PRIMARY KEY, adapter_id TEXT NOT NULL, working_directory TEXT NOT NULL,
                    agent_session_id TEXT, use_worktree INTEGER NOT NULL, skip_permissions INTEGER NOT NULL,
                    sandboxed INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
                INSERT INTO sessions VALUES ('old', 'claude-code', '/w', 'a', 0, 0, 0, '2020-01-01T00:00:00.0000000+00:00');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools(); // release the file so the store opens it cleanly

        // Opening the store must add the column (not throw) and the legacy row must load with a null model.
        using var store = new SqliteEventStore(path);
        var loaded = Assert.Single(await store.ListSessionsAsync());
        Assert.Equal("old", loaded.SessionId);
        Assert.Null(loaded.ModelId);
    }
}
