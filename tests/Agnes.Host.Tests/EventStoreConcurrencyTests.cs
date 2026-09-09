using Agnes.Abstractions;
using Agnes.Host.Events;
using Microsoft.Data.Sqlite;

namespace Agnes.Host.Tests;

/// <summary>
/// The event log under simultaneous read and write. These pin the storage settings rather than any logic,
/// because the settings are what failed: a live host ran the log and the full-text index over the same
/// database file in shared-cache mode, which locks at <i>table</i> granularity, so an ordinary read of
/// <c>events</c> collided with the append the agent's event pump was making. SQLite reports that as
/// <c>SQLITE_LOCKED</c> — and never calls the busy handler for it, so no timeout could absorb it. Ten pumps
/// died that way in one session, each one faulting the agent into a restart and a full replay.
/// </summary>
public class EventStoreConcurrencyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"agnes-concurrency-{Guid.NewGuid():n}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Database_uses_write_ahead_logging()
    {
        using var store = new SqliteEventStore(_path);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        // WAL is what lets a reader hold a consistent snapshot without blocking the writer. The rollback
        // journal the store used before ("delete") makes the two mutually exclusive by design.
        Assert.Equal("wal", Convert.ToString(command.ExecuteScalar())?.ToLowerInvariant());
    }

    [Fact]
    public async Task Appends_survive_concurrent_readers_on_the_same_session()
    {
        using var store = new SqliteEventStore(_path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Seed something for the readers to scan, so they hold a real read over `events`.
        for (var i = 0; i < 50; i++)
        {
            await store.AppendAsync("s1", new MessageChunkEvent(MessageRole.Assistant, new TextContent($"seed {i}")), cts.Token);
        }

        var stop = false;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                await store.ReadSinceAsync("s1", 0, cts.Token);
                await store.GetHeadAsync("s1", cts.Token);
            }
        }, cts.Token)).ToArray();

        try
        {
            // The append path is the one that must not fail: it is the agent's event pump, and an exception
            // here is what used to cost the session its agent.
            for (var i = 0; i < 300; i++)
            {
                await store.AppendAsync("s1", new MessageChunkEvent(MessageRole.Assistant, new TextContent($"live {i}")), cts.Token);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            await Task.WhenAll(readers);
        }

        Assert.Equal(350, await store.GetHeadAsync("s1", cts.Token));
    }

    [Fact]
    public async Task Appends_survive_the_full_text_index_working_the_same_file()
    {
        // The exact pairing from the live host: the FTS5 index is a sibling table in the event store's own
        // database file, so the two components write the same file through separate connections.
        using var store = new SqliteEventStore(_path);
        var index = new SqliteMemoryIndexProvider(_path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var stop = false;
        var searcher = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                await index.SearchAsync("live", new MemorySearchOptions(Limit: 20), cts.Token);
            }
        }, cts.Token);

        try
        {
            for (var i = 0; i < 300; i++)
            {
                var stored = await store.AppendAsync(
                    "s1", new MessageChunkEvent(MessageRole.Assistant, new TextContent($"live event {i}")), cts.Token);
                await index.IndexAsync("s1", stored, cts.Token);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            await searcher;
        }

        Assert.Equal(300, await store.GetHeadAsync("s1", cts.Token));
    }
}
