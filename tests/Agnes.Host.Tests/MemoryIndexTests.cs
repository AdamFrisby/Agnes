using Agnes.Abstractions;
using Agnes.Host.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class MemoryIndexTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"agnes-memory-{Guid.NewGuid():n}.db");

    private static SessionEvent Message(long seq, MessageRole role, string text)
        => new MessageChunkEvent(role, new TextContent(text)) { Sequence = seq, Timestamp = DateTimeOffset.UtcNow };

    private static async Task SeedTwoSessionsAsync(IMemoryIndexProvider index)
    {
        // Session "auth" talks about the login flow; session "schema" talks about the database.
        await index.IndexAsync("auth", Message(1, MessageRole.User, "how do we handle the authentication token refresh"));
        await index.IndexAsync("auth", Message(2, MessageRole.Assistant, "the auth flow rotates the token every hour"));
        await index.IndexAsync("auth", new ToolCallEvent("t1", "Edit login.cs", ToolKind.Edit, ToolCallStatus.Completed,
            [new TextContent("adjusted the token cache")]) { Sequence = 3, Timestamp = DateTimeOffset.UtcNow });
        await index.IndexAsync("schema", Message(1, MessageRole.User, "we decided against the graph database for the schema"));
        await index.IndexAsync("schema", Message(2, MessageRole.Assistant, "the schema uses a relational store"));
    }

    [Fact]
    public async Task Search_returns_matching_session_and_sequence_with_snippet()
    {
        var index = new SqliteMemoryIndexProvider(TempDb());
        await SeedTwoSessionsAsync(index);

        var hits = await index.SearchAsync("authentication", new MemorySearchOptions());

        var hit = Assert.Single(hits);
        Assert.Equal("auth", hit.SessionId);
        Assert.Equal(1, hit.Sequence);
        // FTS5 snippet() highlights the matched term with the configured [ ] delimiters.
        Assert.Contains("authentication", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_matches_across_message_and_tool_text()
    {
        var index = new SqliteMemoryIndexProvider(TempDb());
        await SeedTwoSessionsAsync(index);

        // "token" appears in two messages and one tool call, all in the auth session.
        var hits = await index.SearchAsync("token", new MemorySearchOptions());

        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal("auth", h.SessionId));
        Assert.Contains(hits, h => h.Sequence == 3); // the ToolCallEvent's content was indexed
    }

    [Fact]
    public async Task Search_scopes_to_a_single_session_without_leakage()
    {
        var index = new SqliteMemoryIndexProvider(TempDb());
        await SeedTwoSessionsAsync(index);

        // "schema" exists only in the schema session; scoping to auth must return nothing.
        var scopedToAuth = await index.SearchAsync("schema", new MemorySearchOptions(SessionId: "auth"));
        Assert.Empty(scopedToAuth);

        var scopedToSchema = await index.SearchAsync("schema", new MemorySearchOptions(SessionId: "schema"));
        Assert.All(scopedToSchema, h => Assert.Equal("schema", h.SessionId));
        Assert.NotEmpty(scopedToSchema);
    }

    [Fact]
    public async Task Search_honors_the_result_limit()
    {
        var index = new SqliteMemoryIndexProvider(TempDb());
        await SeedTwoSessionsAsync(index);

        var limited = await index.SearchAsync("token", new MemorySearchOptions(Limit: 1));
        Assert.Single(limited);
    }

    [Fact]
    public async Task Clear_empties_the_index()
    {
        var index = new SqliteMemoryIndexProvider(TempDb());
        await SeedTwoSessionsAsync(index);
        Assert.NotEmpty(await index.SearchAsync("token", new MemorySearchOptions()));

        await index.ClearAsync();

        Assert.Empty(await index.SearchAsync("token", new MemorySearchOptions()));
    }

    [Fact]
    public async Task Non_text_events_and_blank_queries_are_no_ops()
    {
        var index = new SqliteMemoryIndexProvider(TempDb());
        // A usage event carries no searchable text — indexing it must not throw or create a row.
        await index.IndexAsync("auth", new UsageReportedEvent(new UsageMetrics(OutputTokens: 5)) { Sequence = 1 });

        Assert.Empty(await index.SearchAsync("token", new MemorySearchOptions()));
        Assert.Empty(await index.SearchAsync("   ", new MemorySearchOptions()));
    }

    [Fact]
    public async Task Reindexing_the_same_event_does_not_duplicate_rows()
    {
        var index = new SqliteMemoryIndexProvider(TempDb());
        var evt = Message(1, MessageRole.User, "idempotent token indexing");
        await index.IndexAsync("auth", evt);
        await index.IndexAsync("auth", evt);

        var hits = await index.SearchAsync("token", new MemorySearchOptions());
        Assert.Single(hits);
    }

    [Fact]
    public async Task Backfill_indexes_existing_events_from_the_shared_store()
    {
        var path = TempDb();
        // Populate the event store's own table, then build the index over the same file and backfill it.
        var store = new SqliteEventStore(path);
        await store.AppendAsync("hist", new MessageChunkEvent(MessageRole.User, new TextContent("backfilled recall term")));
        await store.AppendAsync("hist", new TurnEndedEvent(StopReason.EndTurn));

        var index = new SqliteMemoryIndexProvider(path);
        Assert.Empty(await index.SearchAsync("recall", new MemorySearchOptions())); // nothing indexed yet

        await index.BackfillAsync(MemoryBackfillScope.AllHistory);

        var hit = Assert.Single(await index.SearchAsync("recall", new MemorySearchOptions()));
        Assert.Equal("hist", hit.SessionId);
        Assert.Equal(1, hit.Sequence);
    }

    [Fact]
    public async Task IndexingEventStore_indexes_every_append_through_the_decorator()
    {
        var path = TempDb();
        var index = new SqliteMemoryIndexProvider(path);
        var store = new IndexingEventStore(new SqliteEventStore(path), index, NullLogger<IndexingEventStore>.Instance);

        // Appending through the decorated store should transparently feed the index (the host's real wiring).
        await store.AppendAsync("live", new MessageChunkEvent(MessageRole.Assistant, new TextContent("decorated append term")));

        var hit = Assert.Single(await index.SearchAsync("decorated", new MemorySearchOptions()));
        Assert.Equal("live", hit.SessionId);
        Assert.Equal(1, hit.Sequence); // sequence stamped by the inner store flows through to the index
    }

    [Fact]
    public async Task Backfill_new_only_indexes_nothing()
    {
        var path = TempDb();
        var store = new SqliteEventStore(path);
        await store.AppendAsync("hist", new MessageChunkEvent(MessageRole.User, new TextContent("recall term")));

        var index = new SqliteMemoryIndexProvider(path);
        await index.BackfillAsync(MemoryBackfillScope.NewOnly);

        Assert.Empty(await index.SearchAsync("recall", new MemorySearchOptions()));
    }
}
