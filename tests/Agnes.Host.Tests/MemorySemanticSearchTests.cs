using Agnes.Abstractions;
using Agnes.Host.Events;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using TextContent = Agnes.Abstractions.TextContent;

namespace Agnes.Host.Tests;

/// <summary>
/// Offline coverage for the embedding-backed semantic tier of memory search (ops/02). Every test drives a
/// deterministic <see cref="FakeEmbeddingGenerator"/> — no real OpenAI/Ollama call — so ranking behavior is
/// asserted purely from known text→vector mappings. The live provider round-trip is a documented gap in
/// docs/live-verification-gaps.md.
/// </summary>
public class MemorySemanticSearchTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"agnes-semantic-{Guid.NewGuid():n}.db");

    private static SessionEvent Message(long seq, string text)
        => new MessageChunkEvent(MessageRole.User, new TextContent(text)) { Sequence = seq, Timestamp = DateTimeOffset.UtcNow };

    /// <summary>An <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> that maps exact strings to fixed
    /// vectors, so a test controls similarity precisely and can assert whether it was ever invoked.</summary>
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly IReadOnlyDictionary<string, float[]> _vectors;

        public FakeEmbeddingGenerator(IReadOnlyDictionary<string, float[]> vectors) => _vectors = vectors;

        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var embeddings = new List<Embedding<float>>();
            foreach (var value in values)
            {
                Calls++;
                // Unknown text embeds to the zero vector (cosine 0 against everything) — harmless filler.
                var vector = _vectors.TryGetValue(value, out var known) ? known : new float[] { 0f, 0f, 0f };
                embeddings.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to dispose — the fake holds no unmanaged or connection state.
        }
    }

    [Fact]
    public async Task Semantic_search_ranks_by_cosine_even_without_keyword_overlap()
    {
        // A and the query share NO words, but their vectors are near; B is unrelated in both senses.
        const string textA = "quantum physics lecture notes";
        const string textB = "chocolate cake recipe";
        const string query = "wave particle duality";
        var generator = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            [textA] = [1f, 0f, 0f],
            [textB] = [0f, 1f, 0f],
            [query] = [0.9f, 0.1f, 0f],
        });
        var index = new SqliteMemoryIndexProvider(TempDb(), generator);
        await index.IndexAsync("a", Message(1, textA));
        await index.IndexAsync("b", Message(1, textB));

        var hits = (await index.SearchAsync(query, new MemorySearchOptions())).ToList();

        // Both are returned (fusion surfaces every semantic candidate), but A — cosine-nearer — ranks first,
        // despite zero keyword overlap with the query.
        Assert.Equal("a", hits[0].SessionId);
        Assert.Contains(hits, h => h.SessionId == "b");
        Assert.True(hits.FindIndex(h => h.SessionId == "a") < hits.FindIndex(h => h.SessionId == "b"));
    }

    [Fact]
    public async Task Hybrid_returns_both_a_keyword_hit_and_a_semantic_only_hit()
    {
        // "token" is a literal keyword hit; the semantic-only doc shares no query word but a near vector.
        const string keywordDoc = "the auth token rotates hourly";
        const string semanticDoc = "credential secret rotation policy";
        const string unrelated = "sourdough bread baking";
        const string query = "token security";
        var generator = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            [keywordDoc] = [0f, 0f, 1f],   // far from the query vector — it wins on keyword, not semantics
            [semanticDoc] = [1f, 1f, 0f],  // near the query vector — it wins on semantics, not keyword
            [unrelated] = [0f, 0f, 0f],
            [query] = [1f, 1f, 0f],
        });
        var index = new SqliteMemoryIndexProvider(TempDb(), generator);
        await index.IndexAsync("kw", Message(1, keywordDoc));
        await index.IndexAsync("sem", Message(1, semanticDoc));
        await index.IndexAsync("none", Message(1, unrelated));

        var hits = (await index.SearchAsync(query, new MemorySearchOptions())).ToList();

        // Fusion must surface BOTH tiers' hits — the keyword-only match and the semantic-only match.
        Assert.Contains(hits, h => h.SessionId == "kw");
        Assert.Contains(hits, h => h.SessionId == "sem");
        // The doc that is neither a keyword nor a semantic match should not outrank the two real hits; it may
        // appear (semantic tier scans all vectors) but must sit below them.
        var worst = Math.Max(
            hits.FindIndex(h => h.SessionId == "kw"),
            hits.FindIndex(h => h.SessionId == "sem"));
        var noneIndex = hits.FindIndex(h => h.SessionId == "none");
        Assert.True(noneIndex == -1 || noneIndex > worst);
    }

    [Fact]
    public async Task Default_provider_computes_no_embeddings_and_creates_no_vector_table()
    {
        var path = TempDb();
        // No generator => FTS5-only, exactly as before. Id reflects the tier.
        var index = new SqliteMemoryIndexProvider(path);
        Assert.Equal("text-only", index.Id);

        await index.IndexAsync("s", Message(1, "authentication token refresh"));
        var hits = await index.SearchAsync("token", new MemorySearchOptions());
        Assert.Single(hits);

        // The vector table must never be created when embeddings are off (non-regression / no dead weight).
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'event_vectors';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public void Enabled_provider_reports_the_embeddings_tier()
    {
        var generator = new FakeEmbeddingGenerator(new Dictionary<string, float[]>());
        var index = new SqliteMemoryIndexProvider(TempDb(), generator);
        Assert.Equal("embeddings", index.Id);
    }

    [Fact]
    public async Task Clear_empties_both_the_keyword_and_vector_tiers()
    {
        const string doc = "vector rotation term";
        var generator = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            [doc] = [1f, 0f, 0f],
            ["rotation"] = [1f, 0f, 0f],
        });
        var index = new SqliteMemoryIndexProvider(TempDb(), generator);
        await index.IndexAsync("s", Message(1, doc));
        Assert.NotEmpty(await index.SearchAsync("rotation", new MemorySearchOptions()));

        await index.ClearAsync();

        Assert.Empty(await index.SearchAsync("rotation", new MemorySearchOptions()));
    }

    [Fact]
    public async Task Backfill_embeds_historical_events_for_semantic_recall()
    {
        var path = TempDb();
        const string doc = "distributed consensus protocol";
        const string query = "raft paxos leader election";
        var store = new SqliteEventStore(path);
        await store.AppendAsync("hist", new MessageChunkEvent(MessageRole.User, new TextContent(doc)));

        var generator = new FakeEmbeddingGenerator(new Dictionary<string, float[]>
        {
            [doc] = [1f, 0f, 0f],
            [query] = [1f, 0f, 0f], // identical direction — perfect cosine, zero keyword overlap
        });
        var index = new SqliteMemoryIndexProvider(path, generator);
        await index.BackfillAsync(MemoryBackfillScope.AllHistory);

        var hit = Assert.Single(await index.SearchAsync(query, new MemorySearchOptions()));
        Assert.Equal("hist", hit.SessionId);
    }

    [Fact]
    public async Task Config_default_and_none_build_no_generator()
    {
        // Unset provider => null (FTS5-only).
        Assert.Null(EmbeddingSelection.Build(new ConfigurationBuilder().Build()));

        // Explicit none => null.
        var none = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Agnes:Search:Embeddings:Provider"] = "none" })
            .Build();
        Assert.Null(EmbeddingSelection.Build(none));

        await Task.CompletedTask;
    }

    [Fact]
    public void Config_openai_requires_a_key_and_local_requires_a_base_url()
    {
        var openAiNoKey = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Agnes:Search:Embeddings:Provider"] = "openai" })
            .Build();
        Assert.Throws<InvalidOperationException>(() => EmbeddingSelection.Build(openAiNoKey));

        var localNoUrl = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agnes:Search:Embeddings:Provider"] = "local",
                ["Agnes:Search:Embeddings:Local:Model"] = "nomic-embed-text",
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() => EmbeddingSelection.Build(localNoUrl));

        var unknown = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Agnes:Search:Embeddings:Provider"] = "banana" })
            .Build();
        Assert.Throws<InvalidOperationException>(() => EmbeddingSelection.Build(unknown));
    }

    [Fact]
    public void Config_openai_and_local_build_a_usable_generator()
    {
        // These construct the OpenAI connector but never call the network (no GenerateAsync here).
        var openAi = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agnes:Search:Embeddings:Provider"] = "openai",
                ["Agnes:Search:Embeddings:OpenAI:ApiKey"] = "sk-test-not-real",
            })
            .Build();
        using var openAiGen = EmbeddingSelection.Build(openAi);
        Assert.NotNull(openAiGen);

        var local = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agnes:Search:Embeddings:Provider"] = "local",
                ["Agnes:Search:Embeddings:Local:BaseUrl"] = "http://localhost:11434/v1",
                ["Agnes:Search:Embeddings:Local:Model"] = "nomic-embed-text",
            })
            .Build();
        using var localGen = EmbeddingSelection.Build(local);
        Assert.NotNull(localGen);
    }
}
