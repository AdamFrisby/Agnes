using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Client;
using Microsoft.Data.Sqlite;

namespace Agnes.Client.Cache;

/// <summary>
/// The <see cref="ISessionEventCache"/> over one SQLite file: an <c>events</c> table keyed by host,
/// session and sequence holding each event as JSON, and a <c>ranges</c> table saying which stretch of each
/// session's log those rows are a complete copy of.
/// </summary>
/// <remarks>
/// <para>Every operation is queued to a single writer task and run in order, and consecutive queued
/// operations share one transaction. That is what keeps the live path cheap: an agent streaming message
/// chunks produces dozens of one-event writes a second, and each becomes an insert inside a transaction
/// that commits once per batch rather than once per event. A read queued behind writes sees them, because
/// the queue is the order.</para>
/// <para>The file is opened in WAL mode with <c>synchronous=NORMAL</c>: a power cut can lose the last
/// batch, never corrupt the file — and a lost batch is merely a few events to fetch again. The WAL is
/// capped at 64 MB after a checkpoint and folded back on close, because a first open of a long session
/// writes its whole log in one transaction.</para>
/// </remarks>
public sealed class SqliteSessionEventCache : ISessionEventCache, IAsyncDisposable
{
    private const int BatchLimit = 512;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _db;
    private readonly Channel<Op> _ops = Channel.CreateUnbounded<Op>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    private sealed record Op(Func<SqliteConnection, SqliteTransaction, object?> Work, TaskCompletionSource<object?> Done);

    private SqliteSessionEventCache(SqliteConnection db, string path)
    {
        _db = db;
        Path = path;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Where the cache lives on disk.</summary>
    public string Path { get; }

    /// <summary>Opens (creating if needed) the cache file at <paramref name="path"/>.</summary>
    public static SqliteSessionEventCache Open(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        db.Open();
        using (var setup = db.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA journal_size_limit=67108864;
                CREATE TABLE IF NOT EXISTS ranges(
                    host TEXT NOT NULL, session TEXT NOT NULL,
                    floor INTEGER NOT NULL, head INTEGER NOT NULL,
                    head_kind TEXT NOT NULL, head_at TEXT NOT NULL,
                    PRIMARY KEY(host, session));
                CREATE TABLE IF NOT EXISTS events(
                    host TEXT NOT NULL, session TEXT NOT NULL, seq INTEGER NOT NULL,
                    body TEXT NOT NULL,
                    PRIMARY KEY(host, session, seq)) WITHOUT ROWID;
                """;
            setup.ExecuteNonQuery();
        }

        return new SqliteSessionEventCache(db, path);
    }

    public Task<CachedRange?> RangeAsync(string hostId, string sessionId, CancellationToken cancellationToken = default)
        => RunAsync((db, tx) => ReadRange(db, tx, hostId, sessionId), cancellationToken);

    public Task<IReadOnlyList<SessionEvent>> ReadAsync(string hostId, string sessionId, long since, CancellationToken cancellationToken = default)
        => RunAsync<IReadOnlyList<SessionEvent>>((db, tx) =>
        {
            using var cmd = Command(db, tx, "SELECT body FROM events WHERE host=@h AND session=@s AND seq>@since ORDER BY seq");
            cmd.Parameters.AddWithValue("@h", hostId);
            cmd.Parameters.AddWithValue("@s", sessionId);
            cmd.Parameters.AddWithValue("@since", since);
            var events = new List<SessionEvent>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (JsonSerializer.Deserialize<SessionEvent>(reader.GetString(0), Json) is { } @event)
                {
                    events.Add(@event);
                }
            }
            return events;
        }, cancellationToken);

    public Task WriteAsync(string hostId, string sessionId, long since, IReadOnlyList<SessionEvent> events, CancellationToken cancellationToken = default)
        => RunAsync<object?>((db, tx) =>
        {
            if (events.Count == 0)
            {
                return null;
            }

            var newHead = events.MaxBy(e => e.Sequence)!;
            var existing = ReadRange(db, tx, hostId, sessionId);
            if (existing is not null)
            {
                var touches = since <= existing.Head && newHead.Sequence >= existing.Floor;
                if (!touches)
                {
                    // Two islands; keep the bigger one. A single live event that somehow lost touch with a
                    // long history must not evict it.
                    if (newHead.Sequence - since <= existing.Head - existing.Floor)
                    {
                        return null;
                    }
                    Forget(db, tx, hostId, sessionId);
                    existing = null;
                }
            }

            using (var insert = Command(db, tx, "INSERT OR IGNORE INTO events(host, session, seq, body) VALUES(@h, @s, @seq, @body)"))
            {
                insert.Parameters.AddWithValue("@h", hostId);
                insert.Parameters.AddWithValue("@s", sessionId);
                var seq = insert.Parameters.Add("@seq", SqliteType.Integer);
                var body = insert.Parameters.Add("@body", SqliteType.Text);
                foreach (var @event in events)
                {
                    seq.Value = @event.Sequence;
                    body.Value = JsonSerializer.Serialize(@event, Json);
                    insert.ExecuteNonQuery();
                }
            }

            var floor = existing is null ? since : Math.Min(existing.Floor, since);
            var head = existing is null || newHead.Sequence >= existing.Head
                ? new CachedRange(floor, newHead.Sequence, CachedReplay.KindOf(newHead), newHead.Timestamp)
                : existing with { Floor = floor };
            using (var upsert = Command(db, tx, """
                INSERT INTO ranges(host, session, floor, head, head_kind, head_at) VALUES(@h, @s, @floor, @head, @kind, @at)
                ON CONFLICT(host, session) DO UPDATE SET floor=excluded.floor, head=excluded.head, head_kind=excluded.head_kind, head_at=excluded.head_at
                """))
            {
                upsert.Parameters.AddWithValue("@h", hostId);
                upsert.Parameters.AddWithValue("@s", sessionId);
                upsert.Parameters.AddWithValue("@floor", head.Floor);
                upsert.Parameters.AddWithValue("@head", head.Head);
                upsert.Parameters.AddWithValue("@kind", head.HeadKind);
                upsert.Parameters.AddWithValue("@at", head.HeadTimestamp.ToString("o", CultureInfo.InvariantCulture));
                upsert.ExecuteNonQuery();
            }
            return null;
        }, cancellationToken);

    public Task ForgetAsync(string hostId, string sessionId, CancellationToken cancellationToken = default)
        => RunAsync<object?>((db, tx) =>
        {
            Forget(db, tx, hostId, sessionId);
            return null;
        }, cancellationToken);

    /// <summary>One session's entry in the cache: whose it is, what stretch is held, and how much it weighs.</summary>
    public sealed record Entry(string HostId, string SessionId, CachedRange Range, long Bytes);

    /// <summary>Every session held, with its range — for a settings page that shows what the cache holds.</summary>
    public Task<IReadOnlyList<Entry>> ListAsync(CancellationToken cancellationToken = default)
        => RunAsync<IReadOnlyList<Entry>>((db, tx) =>
        {
            using var cmd = Command(db, tx, """
                SELECT r.host, r.session, r.floor, r.head, r.head_kind, r.head_at,
                       (SELECT COALESCE(SUM(LENGTH(body)), 0) FROM events e WHERE e.host=r.host AND e.session=r.session)
                FROM ranges r ORDER BY r.host, r.session
                """);
            var rows = new List<Entry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Entry(reader.GetString(0), reader.GetString(1), RangeFrom(reader, 2), reader.GetInt64(6)));
            }
            return rows;
        }, cancellationToken);

    private static CachedRange? ReadRange(SqliteConnection db, SqliteTransaction tx, string hostId, string sessionId)
    {
        using var cmd = Command(db, tx, "SELECT floor, head, head_kind, head_at FROM ranges WHERE host=@h AND session=@s");
        cmd.Parameters.AddWithValue("@h", hostId);
        cmd.Parameters.AddWithValue("@s", sessionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? RangeFrom(reader, 0) : null;
    }

    private static CachedRange RangeFrom(SqliteDataReader reader, int at)
        => new(
            reader.GetInt64(at),
            reader.GetInt64(at + 1),
            reader.GetString(at + 2),
            DateTimeOffset.Parse(reader.GetString(at + 3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static void Forget(SqliteConnection db, SqliteTransaction tx, string hostId, string sessionId)
    {
        using var cmd = Command(db, tx, "DELETE FROM events WHERE host=@h AND session=@s; DELETE FROM ranges WHERE host=@h AND session=@s");
        cmd.Parameters.AddWithValue("@h", hostId);
        cmd.Parameters.AddWithValue("@s", sessionId);
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private async Task<T> RunAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ops.Writer.TryWrite(new Op((db, tx) => work(db, tx), done)))
        {
            throw new ObjectDisposedException(nameof(SqliteSessionEventCache));
        }
        var result = await done.Task.ConfigureAwait(false);
        return (T)result!;
    }

    private async Task PumpAsync()
    {
        var reader = _ops.Reader;
        var batch = new List<Op>(BatchLimit);
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < BatchLimit && reader.TryRead(out var op))
            {
                batch.Add(op);
            }

            SqliteTransaction tx;
            try
            {
                tx = _db.BeginTransaction();
            }
            catch (Exception ex)
            {
                foreach (var op in batch)
                {
                    op.Done.TrySetException(ex);
                }
                continue;
            }

            using (tx)
            {
                foreach (var op in batch)
                {
                    try
                    {
                        op.Done.TrySetResult(op.Work(_db, tx));
                    }
                    catch (Exception ex)
                    {
                        op.Done.TrySetException(ex);
                    }
                }

                try
                {
                    tx.Commit();
                }
                catch (Exception)
                {
                    // The batch's callers were already answered; a failed commit means those rows are
                    // simply not there next time, which the replay treats as a cache miss.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ops.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
        try
        {
            // A first open of a long session writes its whole log in one transaction, which the automatic
            // checkpoint cannot fold back until the next commit; fold it now so the WAL does not sit beside
            // the file at the size of the largest session until the next run.
            using var checkpoint = _db.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Purely housekeeping; the next open checkpoints on its own.
        }
        await _db.DisposeAsync().ConfigureAwait(false);
    }
}
