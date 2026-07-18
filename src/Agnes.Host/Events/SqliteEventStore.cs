using System.Collections.Concurrent;
using Agnes.Abstractions;
using Microsoft.Data.Sqlite;

namespace Agnes.Host.Events;

/// <summary>
/// SQLite-backed event log for durable, unlimited scrollback. Sequences are assigned
/// under a per-session lock; the composite primary key (session_id, seq) enforces order.
/// </summary>
public sealed class SqliteEventStore : IEventStore, IDisposable
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SqliteEventStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS events (
                session_id TEXT NOT NULL,
                seq        INTEGER NOT NULL,
                ts         TEXT NOT NULL,
                json       TEXT NOT NULL,
                PRIMARY KEY (session_id, seq)
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<SessionEvent> AppendAsync(string sessionId, SessionEvent @event, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = Open();
            var head = await GetHeadCoreAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
            var stamped = @event with { Sequence = head + 1, Timestamp = DateTimeOffset.UtcNow };

            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO events (session_id, seq, ts, json) VALUES ($sid, $seq, $ts, $json);";
            command.Parameters.AddWithValue("$sid", sessionId);
            command.Parameters.AddWithValue("$seq", stamped.Sequence);
            command.Parameters.AddWithValue("$ts", stamped.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$json", EventJson.Serialize(stamped));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return stamped;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SessionEvent>> ReadSinceAsync(string sessionId, long sinceSequence, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM events WHERE session_id = $sid AND seq > $since ORDER BY seq ASC;";
        command.Parameters.AddWithValue("$sid", sessionId);
        command.Parameters.AddWithValue("$since", sinceSequence);

        var events = new List<SessionEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(EventJson.Deserialize(reader.GetString(0)));
        }

        return events;
    }

    public async Task<long> GetHeadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        return await GetHeadCoreAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> GetHeadCoreAsync(SqliteConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM events WHERE session_id = $sid;";
        command.Parameters.AddWithValue("$sid", sessionId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }

        SqliteConnection.ClearAllPools();
    }
}
