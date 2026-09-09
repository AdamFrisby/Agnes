using Microsoft.Data.Sqlite;

namespace Agnes.Host.Events;

/// <summary>
/// The single place the host's SQLite connections are shaped. <see cref="SqliteEventStore"/> and
/// <see cref="SqliteMemoryIndexProvider"/> open the <b>same</b> database file — the FTS5 index lives as a
/// sibling table beside <c>events</c> on purpose — so their connection settings have to agree. They are
/// centralized here rather than spelled out twice precisely because a silent disagreement between them is
/// what this type exists to prevent.
/// </summary>
/// <remarks>
/// <para>Two settings carry the weight, and both were learned from a live host rather than from theory.</para>
///
/// <para><b>Private cache, not shared.</b> Shared-cache mode makes connections in one process share a page
/// cache and, with it, <i>table-level</i> locks — so an ordinary read of <c>events</c> (a client fetching a
/// snapshot, the indexer backfilling) locks the table against the append that the agent's event pump is
/// trying to make. The failure that produces is <c>SQLITE_LOCKED</c>, and the critical detail is that
/// <b>SQLite never invokes the busy handler for <c>SQLITE_LOCKED</c></b>: a timeout cannot rescue it and a
/// retry loop is the only recourse. One live session lost its event pump ten times this way, each loss
/// faulting the agent into a restart-and-replay. Shared cache buys nothing here — the host is one process
/// with a handful of connections, not a memory-constrained embedded device — so it is simply off, which is
/// also SQLite's own long-standing recommendation.</para>
///
/// <para><b>WAL, not the rollback journal.</b> WAL gives readers a consistent snapshot without blocking the
/// writer, which removes the reader/writer collision above at the source rather than making it survivable.
/// It is a persistent property of the file, so it is set once when the file is opened for setup;
/// <c>busy_timeout</c> by contrast is per-connection state and must be re-applied on every open.</para>
/// </remarks>
internal static class SqliteDatabase
{
    /// <summary>
    /// How long a connection waits for a competing write to finish before failing. WAL leaves only
    /// writer-versus-writer contention, which is brief; this is generous enough that a checkpoint or a
    /// large append can never surface as an error to a caller.
    /// </summary>
    private const int BusyTimeoutMilliseconds = 15_000;

    /// <summary>
    /// The connection string both stores use. Note the absence of <c>Cache=Shared</c>: private cache is the
    /// default and, per the remarks above, deliberately kept.
    /// </summary>
    public static string BuildConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

    /// <summary>
    /// Opens a connection and applies the per-connection settings. Every open goes through here: a pooled
    /// connection can hand back state that predates these pragmas, so they are cheap to re-assert and unsafe
    /// to assume.
    /// </summary>
    public static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();

        return connection;
    }

    /// <summary>
    /// Applies the settings stored in the database file itself. Idempotent, and safe to run from whichever
    /// store happens to construct first — the second call reads back the mode the first one set.
    /// </summary>
    public static void ConfigureFile(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // journal_mode returns the resulting mode as a row, so it is a scalar rather than a non-query.
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteScalar();

        // NORMAL is the standard companion to WAL: it drops the per-commit fsync that DELETE-mode needs,
        // which is what the append path was paying on every streamed token. Under WAL it stays crash-safe —
        // the exposure is losing the most recent commits to a power cut, never a corrupt file — and a
        // transcript that ends a few events early is a far better outcome than a stalled thread pool.
        using var synchronous = connection.CreateCommand();
        synchronous.CommandText = "PRAGMA synchronous = NORMAL;";
        synchronous.ExecuteNonQuery();
    }
}
