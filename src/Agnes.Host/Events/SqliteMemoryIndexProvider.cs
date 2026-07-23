using Agnes.Abstractions;
using Microsoft.Data.Sqlite;

namespace Agnes.Host.Events;

/// <summary>
/// Text-only <see cref="IMemoryIndexProvider"/> built on SQLite's FTS5 extension. It lives as a sibling
/// FTS5 virtual table in the event store's own database file, so it reuses the host's existing SQLite
/// lifecycle (location, backup) and — crucially — <see cref="BackfillAsync"/> can read the durable
/// <c>events</c> table directly to index history without a second copy of the corpus. Kept fully separate
/// from <see cref="SqliteEventStore"/>: it only ever <i>observes</i> appends (via
/// <see cref="IMemoryIndexProvider.IndexAsync"/> off the host's append path), never gates them.
/// </summary>
public sealed class SqliteMemoryIndexProvider : IMemoryIndexProvider
{
    // FTS5 column layout. body is the only indexed column; the rest are UNINDEXED metadata we read back.
    private const int BodyColumn = 3;

    private readonly string _connectionString;

    public SqliteMemoryIndexProvider(string databasePath)
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
            CREATE VIRTUAL TABLE IF NOT EXISTS event_fts USING fts5(
                session_id UNINDEXED,
                seq        UNINDEXED,
                ts         UNINDEXED,
                body,
                tokenize = 'porter unicode61'
            );
            """;
        command.ExecuteNonQuery();
    }

    public string Id => "text-only";

    public async Task IndexAsync(string sessionId, SessionEvent evt, CancellationToken cancellationToken = default)
    {
        var body = MemoryText.Extract(evt);
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        await using var connection = Open();
        await IndexRowAsync(connection, sessionId, evt.Sequence, evt.Timestamp.ToString("O"), body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, MemorySearchOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT session_id, seq, ts, snippet(event_fts, {BodyColumn}, '[', ']', '…', 12) AS snip
            FROM event_fts
            WHERE event_fts MATCH $q
              AND ($sid IS NULL OR session_id = $sid)
            ORDER BY rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$q", ToMatchExpression(query));
        command.Parameters.AddWithValue("$sid", (object?)options.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", options.Limit);

        var results = new List<MemorySearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new MemorySearchResult(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(2))));
        }

        return results;
    }

    public async Task BackfillAsync(MemoryBackfillScope scope, CancellationToken cancellationToken = default)
    {
        // New-only means "index nothing historical" — live appends do the rest.
        if (scope == MemoryBackfillScope.NewOnly)
        {
            return;
        }

        await using var connection = Open();
        if (!await EventsTableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT session_id, seq, ts, json FROM events;";
        if (scope == MemoryBackfillScope.Last30Days)
        {
            // ts is ISO-8601 round-trip ("O"), so lexical comparison is chronological.
            select.CommandText = "SELECT session_id, seq, ts, json FROM events WHERE ts >= $cutoff;";
            select.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        }

        var rows = new List<(string SessionId, long Seq, string Ts, string Json)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = MemoryText.Extract(EventJson.Deserialize(row.Json));
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            await IndexRowAsync(connection, row.SessionId, row.Seq, row.Ts, body, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM event_fts;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Delete-then-insert keeps a (session_id, seq) row single even if the same event is both backfilled
    // and observed live — FTS5 has no unique constraint of its own to lean on.
    private static async Task IndexRowAsync(SqliteConnection connection, string sessionId, long sequence, string timestamp, string body, CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM event_fts WHERE session_id = $sid AND seq = $seq;";
        delete.Parameters.AddWithValue("$sid", sessionId);
        delete.Parameters.AddWithValue("$seq", sequence);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO event_fts (session_id, seq, ts, body) VALUES ($sid, $seq, $ts, $body);";
        insert.Parameters.AddWithValue("$sid", sessionId);
        insert.Parameters.AddWithValue("$seq", sequence);
        insert.Parameters.AddWithValue("$ts", timestamp);
        insert.Parameters.AddWithValue("$body", body);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> EventsTableExistsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'events';";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    // Treat the user's query as a bag of terms matched as prefixes, quoting each so FTS5 syntax characters
    // in ordinary search text can't throw a "malformed MATCH expression". An empty result short-circuits.
    private static string ToMatchExpression(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .ToArray();
        return terms.Length == 0 ? "\"\"" : string.Join(' ', terms);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
