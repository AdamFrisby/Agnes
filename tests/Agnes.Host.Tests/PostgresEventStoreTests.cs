using Agnes.Abstractions;
using Agnes.Host.Events;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Agnes.Host.Tests;

/// <summary>
/// Contract-parity + config-selection tests for the optional Postgres event-store backend (ops/03).
///
/// The same <see cref="IEventStore"/> contract assertions run against <see cref="SqliteEventStore"/> and
/// <see cref="PostgresEventStore"/>. The Postgres round-trip runs only when a server is reachable
/// (<c>POSTGRES_TEST_CONNSTRING</c> / <c>Agnes__Storage__Postgres__ConnectionString</c> env, or a local
/// Postgres on 5432); otherwise it is skipped with a clear reason — a live-verification gap, never faked with
/// SQLite. Selection and DDL/statement shape are asserted without any server.
/// </summary>
public class PostgresEventStoreTests
{
    // ---- contract parity: the same assertions, both durable backends ----

    [Fact]
    public async Task Sqlite_satisfies_the_event_store_contract()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-pg-parity-{Guid.NewGuid():n}.db");
        try
        {
            using var store = new SqliteEventStore(path);
            await AssertEventStoreContractAsync(store);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }

    // Reachability-gated: xunit v2's execution engine does not honor a runtime SkipException (that is a v3
    // feature), so the skip decision is made at discovery by the custom [PostgresReachableFact] attribute — the
    // no-extra-dependency way to conditionally skip in v2. When no usable server is present the test is reported
    // Skipped with a clear reason (a live-verification gap), never faked with SQLite.
    [PostgresReachableFact]
    public async Task Postgres_satisfies_the_event_store_contract()
    {
        using var store = new PostgresEventStore(PostgresTestServer.ConnectionString);
        await AssertEventStoreContractAsync(store);
    }

    /// <summary>The full store contract, run against any <see cref="IEventStore"/>. Uses unique session ids so
    /// it is idempotent against a persistent (shared) Postgres database.</summary>
    private static async Task AssertEventStoreContractAsync(IEventStore store)
    {
        var s1 = $"s1-{Guid.NewGuid():n}";
        var other = $"other-{Guid.NewGuid():n}";

        // append -> monotonic sequence
        var a = await store.AppendAsync(s1, new MessageChunkEvent(MessageRole.User, new TextContent("one")));
        var b = await store.AppendAsync(s1, new MessageChunkEvent(MessageRole.Assistant, new TextContent("two")));
        var c = await store.AppendAsync(s1, new TurnEndedEvent(StopReason.EndTurn));
        Assert.Equal(1, a.Sequence);
        Assert.Equal(2, b.Sequence);
        Assert.Equal(3, c.Sequence);

        // head
        Assert.Equal(3, await store.GetHeadAsync(s1));

        // read-since / snapshot+tail from a cursor, in order, round-tripped through (de)serialization
        var tail = await store.ReadSinceAsync(s1, 1);
        Assert.Equal([2L, 3L], tail.Select(e => e.Sequence));
        var assistant = Assert.IsType<MessageChunkEvent>(tail[0]);
        Assert.Equal("two", ((TextContent)assistant.Content).Text);
        Assert.IsType<TurnEndedEvent>(tail[1]);

        var snapshot = await store.ReadSinceAsync(s1, 0);
        Assert.Equal([1L, 2L, 3L], snapshot.Select(e => e.Sequence));

        // multi-session isolation
        Assert.Equal(0, await store.GetHeadAsync(other));
        Assert.Empty(await store.ReadSinceAsync(other, 0));
        var o1 = await store.AppendAsync(other, new MessageChunkEvent(MessageRole.User, new TextContent("elsewhere")));
        Assert.Equal(1, o1.Sequence);
        Assert.Equal(3, await store.GetHeadAsync(s1)); // s1 unaffected by the other session's append

        // session catalog round-trip
        var record = new SessionRecord(s1, "claude-code", "/tmp/wd", "agent-123", true, false, true, DateTimeOffset.UtcNow);
        await store.SaveSessionAsync(record);
        var listed = await store.ListSessionsAsync();
        var restored = Assert.Single(listed, r => r.SessionId == s1);
        Assert.Equal("claude-code", restored.AdapterId);
        Assert.Equal("agent-123", restored.AgentSessionId);
        Assert.True(restored.UseWorktree);
        Assert.False(restored.SkipPermissions);
        Assert.True(restored.Sandboxed);

        // upsert updates agent_session_id in place (mirrors SqliteEventStore)
        await store.SaveSessionAsync(record with { AgentSessionId = "agent-456" });
        var relisted = await store.ListSessionsAsync();
        Assert.Equal("agent-456", Assert.Single(relisted, r => r.SessionId == s1).AgentSessionId);
    }

    // ---- config selection (no server needed) ----

    [Fact]
    public void Default_and_unset_config_resolve_to_the_default_store()
    {
        // No storage config at all -> in-memory, exactly as before ops/03 (non-regression).
        var empty = Build([]);
        Assert.Equal("in-memory", EventStoreSelection.ResolveName(empty));
        var registry = RegistryFor(empty);
        Assert.IsType<InMemoryEventStore>(registry.Find("in-memory")!.Create());

        // A configured database path -> sqlite by default.
        var withDb = Build(new() { ["Agnes:Database"] = Path.Combine(Path.GetTempPath(), $"agnes-sel-{Guid.NewGuid():n}.db") });
        Assert.Equal("sqlite", EventStoreSelection.ResolveName(withDb));
        using var sqlite = Assert.IsType<SqliteEventStore>(RegistryFor(withDb).Find("sqlite")!.Create());
    }

    [Fact]
    public void Postgres_config_resolves_the_postgres_store()
    {
        var config = Build(new()
        {
            ["Agnes:Storage:EventStore"] = "postgres",
            ["Agnes:Storage:Postgres:ConnectionString"] = "Host=localhost;Database=agnes;Username=agnes;Password=secret",
        });

        Assert.Equal("postgres", EventStoreSelection.ResolveName(config));
        var provider = RegistryFor(config).Find("postgres");
        Assert.NotNull(provider);
        Assert.Equal("postgres", provider!.Name);
        // Construction must not require a reachable server (bootstrap is lazy).
        using var store = Assert.IsType<PostgresEventStore>(provider.Create());
    }

    // ---- DDL / statement shape (no server needed) ----

    [Fact]
    public void Bootstrap_ddl_is_well_formed()
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS events", PostgresEventStore.SchemaDdl);
        Assert.Contains("PRIMARY KEY (session_id, seq)", PostgresEventStore.SchemaDdl);
        Assert.Contains("seq        BIGINT NOT NULL", PostgresEventStore.SchemaDdl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS sessions", PostgresEventStore.SchemaDdl);
        Assert.Contains("session_id        TEXT PRIMARY KEY", PostgresEventStore.SchemaDdl);
        // Idempotent bootstrap: every DDL object is guarded with IF NOT EXISTS.
        Assert.Equal(3, CountOccurrences(PostgresEventStore.SchemaDdl, "IF NOT EXISTS"));
    }

    [Fact]
    public void Statements_are_parameterized()
    {
        // No bare literals for user data — every value flows through an @-parameter.
        Assert.Contains("VALUES (@sid, @seq, @ts, @json)", PostgresEventStore.InsertEventSql);
        Assert.Contains("seq > @since", PostgresEventStore.ReadSinceSql);
        Assert.Contains("session_id = @sid", PostgresEventStore.HeadSql);
        Assert.Contains("ON CONFLICT (session_id) DO UPDATE SET", PostgresEventStore.UpsertSessionSql);
        Assert.Contains("agent_session_id = EXCLUDED.agent_session_id", PostgresEventStore.UpsertSessionSql);
        Assert.StartsWith("SELECT session_id, adapter_id", PostgresEventStore.ListSessionsSql);
    }

    // ---- helpers ----

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static PluginRegistry<IEventStoreProvider> RegistryFor(IConfiguration configuration)
        => new(EventStoreSelection.BuildProviders(configuration), p => p.Name);
}

/// <summary>Resolves the Postgres server the round-trip test should use, and whether one is actually usable.
/// Never falls back to SQLite — the Postgres path is verified against a real server or explicitly skipped.</summary>
internal static class PostgresTestServer
{
    /// <summary>Env-provided connection string, else a conventional local Postgres with a short timeout.</summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNSTRING")
        ?? Environment.GetEnvironmentVariable("Agnes__Storage__Postgres__ConnectionString")
        ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres;Timeout=2;Command Timeout=5";

    /// <summary>Null when a usable server is reachable; otherwise a human-readable skip reason.</summary>
    public static string? UnavailableReason()
    {
        try
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();
            return null;
        }
        catch (Exception ex)
        {
            // Any failure (unreachable, auth, driver) means the live round-trip can't run — treat as a skip,
            // not a hard failure. Broad catch is intentional: the probe must never itself fail the suite. (PH2098)
            return "No reachable/usable Postgres server for the round-trip contract test (live-verification gap). "
                 + "Set POSTGRES_TEST_CONNSTRING or run a local Postgres on :5432 to exercise it. "
                 + $"Probe failed: {ex.Message}";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips (at discovery) when no usable Postgres server is reachable. This is
/// the no-extra-dependency way to conditionally skip under xunit v2, whose execution engine ignores a runtime
/// <c>SkipException</c>. A statically-assigned <see cref="FactAttribute.Skip"/> IS honored, so the round-trip
/// test shows as Skipped (with a reason) rather than Failed when there is no server.
/// </summary>
public sealed class PostgresReachableFactAttribute : FactAttribute
{
    public PostgresReachableFactAttribute()
    {
        var reason = PostgresTestServer.UnavailableReason();
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}
