using System.Collections.Concurrent;
using Agnes.Abstractions;
using Npgsql;

namespace Agnes.Host.Events;

/// <summary>
/// Postgres-backed event log — a drop-in <see cref="IEventStore"/> alternative to
/// <see cref="SqliteEventStore"/> for a scaled/shared-database deployment topology (ops/03). Semantics mirror
/// the SQLite store exactly: sequences are assigned under a per-session lock, and the composite primary key
/// <c>(session_id, seq)</c> enforces order. Only ever constructed when Postgres is explicitly selected in config,
/// so the Npgsql driver is never loaded for a default (SQLite/in-memory) deployment.
/// </summary>
/// <remarks>
/// Schema bootstrap is lazy and connection-driven (not in the constructor) so that selecting this backend — and
/// asserting its resolved type — never requires a reachable server. The DDL and statements are exposed as
/// internal constants so their shape is unit-testable without a live database.
/// </remarks>
public sealed class PostgresEventStore : IEventStore, IDisposable
{
    // Postgres supports IF NOT EXISTS on both tables and indexes, so the bootstrap is idempotent and needs no
    // separate migration table. Columns mirror the SQLite store's shape; native boolean/bigint types are used
    // where SQLite could only approximate them.
    internal const string SchemaDdl =
        """
        CREATE TABLE IF NOT EXISTS events (
            session_id TEXT   NOT NULL,
            seq        BIGINT NOT NULL,
            ts         TEXT   NOT NULL,
            json       TEXT   NOT NULL,
            PRIMARY KEY (session_id, seq)
        );
        CREATE INDEX IF NOT EXISTS ix_events_session_seq ON events (session_id, seq);
        CREATE TABLE IF NOT EXISTS sessions (
            session_id        TEXT PRIMARY KEY,
            adapter_id        TEXT    NOT NULL,
            working_directory TEXT    NOT NULL,
            agent_session_id  TEXT,
            use_worktree      BOOLEAN NOT NULL,
            skip_permissions  BOOLEAN NOT NULL,
            sandboxed         BOOLEAN NOT NULL DEFAULT FALSE,
            created_at        TEXT    NOT NULL
        );
        """;

    internal const string InsertEventSql =
        "INSERT INTO events (session_id, seq, ts, json) VALUES (@sid, @seq, @ts, @json);";

    internal const string ReadSinceSql =
        "SELECT json FROM events WHERE session_id = @sid AND seq > @since ORDER BY seq ASC;";

    internal const string HeadSql =
        "SELECT COALESCE(MAX(seq), 0) FROM events WHERE session_id = @sid;";

    internal const string UpsertSessionSql =
        """
        INSERT INTO sessions (session_id, adapter_id, working_directory, agent_session_id, use_worktree, skip_permissions, sandboxed, created_at)
        VALUES (@sid, @adapter, @wd, @agent, @wt, @skip, @sandboxed, @created)
        ON CONFLICT (session_id) DO UPDATE SET
            agent_session_id = EXCLUDED.agent_session_id;
        """;

    internal const string ListSessionsSql =
        "SELECT session_id, adapter_id, working_directory, agent_session_id, use_worktree, skip_permissions, sandboxed, created_at FROM sessions ORDER BY created_at ASC;";

    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    public PostgresEventStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<SessionEvent> AppendAsync(string sessionId, SessionEvent @event, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var gate = _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var head = await GetHeadCoreAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
            var stamped = @event with { Sequence = head + 1, Timestamp = DateTimeOffset.UtcNow };

            await using var command = new NpgsqlCommand(InsertEventSql, connection);
            command.Parameters.AddWithValue("sid", sessionId);
            command.Parameters.AddWithValue("seq", stamped.Sequence);
            command.Parameters.AddWithValue("ts", stamped.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("json", EventJson.Serialize(stamped));
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
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(ReadSinceSql, connection);
        command.Parameters.AddWithValue("sid", sessionId);
        command.Parameters.AddWithValue("since", sinceSequence);

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
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetHeadCoreAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSessionAsync(SessionRecord record, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(UpsertSessionSql, connection);
        command.Parameters.AddWithValue("sid", record.SessionId);
        command.Parameters.AddWithValue("adapter", record.AdapterId);
        command.Parameters.AddWithValue("wd", record.WorkingDirectory);
        command.Parameters.AddWithValue("agent", (object?)record.AgentSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("wt", record.UseWorktree);
        command.Parameters.AddWithValue("skip", record.SkipPermissions);
        command.Parameters.AddWithValue("sandboxed", record.Sandboxed);
        command.Parameters.AddWithValue("created", record.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(ListSessionsSql, connection);
        var records = new List<SessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new SessionRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6),
                DateTimeOffset.Parse(reader.GetString(7))));
        }

        return records;
    }

    private static async Task<long> GetHeadCoreAsync(NpgsqlConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(HeadSql, connection);
        command.Parameters.AddWithValue("sid", sessionId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(SchemaDdl, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public void Dispose()
    {
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }

        _initGate.Dispose();
    }
}
