using Agnes.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Agnes.Host.Events;

/// <summary>
/// SQLite-backed <see cref="IMemoryIndexProvider"/>. Its baseline is a full-text tier on SQLite's FTS5
/// extension, living as a sibling FTS5 virtual table in the event store's own database file so it reuses the
/// host's existing SQLite lifecycle (location, backup) and <see cref="BackfillAsync"/> can read the durable
/// <c>events</c> table directly to index history without a second copy of the corpus.
/// </summary>
/// <remarks>
/// When an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> is supplied (config-selected in Program.cs —
/// see .ideas/ops/02-memory-search.md), a second <b>semantic</b> tier switches on: each indexed chunk's
/// embedding vector is stored in a sibling table in the same file (no external vector DB), and a search
/// embeds the query, ranks candidates by cosine similarity, and fuses that ranking with the FTS5 ranking via
/// reciprocal-rank fusion so keyword and semantic hits both surface. With no generator (the default) the
/// provider behaves <b>exactly</b> as FTS5-only, computing and storing no vectors. Kept fully separate from
/// <see cref="SqliteEventStore"/>: it only ever <i>observes</i> appends via
/// <see cref="IMemoryIndexProvider.IndexAsync"/> off the host's append path, never gates them.
/// </remarks>
public sealed class SqliteMemoryIndexProvider : IMemoryIndexProvider
{
    // FTS5 column layout. body is the only indexed column; the rest are UNINDEXED metadata we read back.
    private const int BodyColumn = 3;

    // How many hits to pull from each tier before fusing. A little headroom past the caller's limit lets a
    // hit ranked modestly by both tiers overtake one ranked highly by only one.
    private const int TierCandidateHeadroom = 20;

    private readonly string _connectionString;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;

    public SqliteMemoryIndexProvider(string databasePath, IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        _embeddings = embeddingGenerator;
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

        if (_embeddings is not null)
        {
            // Vectors live beside the FTS5 table in the same file. A real PRIMARY KEY (unlike FTS5) makes the
            // upsert idempotent for an event that's both backfilled and observed live.
            using var vectors = connection.CreateCommand();
            vectors.CommandText =
                """
                CREATE TABLE IF NOT EXISTS event_vectors (
                    session_id TEXT    NOT NULL,
                    seq        INTEGER NOT NULL,
                    ts         TEXT    NOT NULL,
                    body       TEXT    NOT NULL,
                    vec        BLOB    NOT NULL,
                    PRIMARY KEY (session_id, seq)
                );
                """;
            vectors.ExecuteNonQuery();
        }
    }

    /// <summary><c>text-only</c> for the FTS5-only default, <c>embeddings</c> once a semantic tier is on.</summary>
    public string Id => _embeddings is null ? "text-only" : "embeddings";

    public async Task IndexAsync(string sessionId, SessionEvent evt, CancellationToken cancellationToken = default)
    {
        var body = MemoryText.Extract(evt);
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        var timestamp = evt.Timestamp.ToString("O");
        await using var connection = Open();
        await IndexRowAsync(connection, sessionId, evt.Sequence, timestamp, body, cancellationToken).ConfigureAwait(false);
        await IndexVectorAsync(connection, sessionId, evt.Sequence, timestamp, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, MemorySearchOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await using var connection = Open();

        // FTS5-only default: return the keyword ranking unchanged — zero new behavior when embeddings are off.
        if (_embeddings is null)
        {
            return await KeywordSearchAsync(connection, query, options, options.Limit, cancellationToken).ConfigureAwait(false);
        }

        // Hybrid: pull a candidate slice from each tier and fuse. Headroom lets a modest-in-both hit win.
        var candidates = options.Limit + TierCandidateHeadroom;
        var keyword = await KeywordSearchAsync(connection, query, options, candidates, cancellationToken).ConfigureAwait(false);
        var semantic = await SemanticSearchAsync(connection, query, options, candidates, cancellationToken).ConfigureAwait(false);
        return MemoryRankFusion.Fuse(keyword, semantic, options.Limit);
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
            await IndexVectorAsync(connection, row.SessionId, row.Seq, row.Ts, body, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM event_fts;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (_embeddings is not null)
        {
            await using var vectors = connection.CreateCommand();
            vectors.CommandText = "DELETE FROM event_vectors;";
            await vectors.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<MemorySearchResult>> KeywordSearchAsync(
        SqliteConnection connection, string query, MemorySearchOptions options, int limit, CancellationToken cancellationToken)
    {
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
        command.Parameters.AddWithValue("$limit", limit);

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

    // Brute-force cosine scan over the locally-stored vectors — plausible at a single host's scale (thousands
    // to low tens of thousands of chunks) per the design; a dedicated vector DB would solve a scale Agnes
    // doesn't have. Ranks purely on the embedding signal; fusion with FTS5 happens one level up.
    private async Task<IReadOnlyList<MemorySearchResult>> SemanticSearchAsync(
        SqliteConnection connection, string query, MemorySearchOptions options, int limit, CancellationToken cancellationToken)
    {
        var queryVector = await EmbedAsync(query, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, seq, ts, body, vec
            FROM event_vectors
            WHERE ($sid IS NULL OR session_id = $sid);
            """;
        command.Parameters.AddWithValue("$sid", (object?)options.SessionId ?? DBNull.Value);

        var scored = new List<(MemorySearchResult Result, double Score)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var blob = (byte[])reader[4];
            var score = VectorMath.Cosine(queryVector, VectorMath.FromBlob(blob));
            var result = new MemorySearchResult(
                reader.GetString(0),
                reader.GetInt64(1),
                Snippet(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(2)));
            scored.Add((result, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Result.Sequence)
            .Take(limit)
            .Select(s => s.Result)
            .ToArray();
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

    // No-op unless the semantic tier is enabled; otherwise embed the chunk and upsert its vector (a real
    // PRIMARY KEY makes INSERT OR REPLACE idempotent across backfill + live observation of the same event).
    private async Task IndexVectorAsync(SqliteConnection connection, string sessionId, long sequence, string timestamp, string body, CancellationToken cancellationToken)
    {
        if (_embeddings is null)
        {
            return;
        }

        var vector = await EmbedAsync(body, cancellationToken).ConfigureAwait(false);

        await using var upsert = connection.CreateCommand();
        upsert.CommandText =
            """
            INSERT OR REPLACE INTO event_vectors (session_id, seq, ts, body, vec)
            VALUES ($sid, $seq, $ts, $body, $vec);
            """;
        upsert.Parameters.AddWithValue("$sid", sessionId);
        upsert.Parameters.AddWithValue("$seq", sequence);
        upsert.Parameters.AddWithValue("$ts", timestamp);
        upsert.Parameters.AddWithValue("$body", body);
        upsert.Parameters.AddWithValue("$vec", VectorMath.ToBlob(vector));
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        // _embeddings is non-null on every path that calls this.
        var embeddings = await _embeddings!.GenerateAsync([text], cancellationToken: cancellationToken).ConfigureAwait(false);
        return embeddings[0].Vector.ToArray();
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

    // A semantic hit has no matched span to highlight, so give the caller a plain leading excerpt of the
    // chunk (single-line, bounded) — enough to recognize the result and jump to it.
    private static string Snippet(string body)
    {
        const int MaxLength = 160;
        var flattened = body.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= MaxLength ? flattened : flattened[..MaxLength] + "…";
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
