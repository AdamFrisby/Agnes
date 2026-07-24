using Agnes.Abstractions;
using Agnes.Host.Events;

namespace Agnes.Host.Tests;

/// <summary>Direct unit coverage of the pure vector math and rank fusion behind the semantic memory tier.</summary>
public class VectorMathTests
{
    [Fact]
    public void Cosine_of_identical_vectors_is_one()
        => Assert.Equal(1d, VectorMath.Cosine([1f, 2f, 3f], [1f, 2f, 3f]), 6);

    [Fact]
    public void Cosine_of_a_scaled_vector_is_one()
        => Assert.Equal(1d, VectorMath.Cosine([1f, 2f, 3f], [2f, 4f, 6f]), 6);

    [Fact]
    public void Cosine_of_orthogonal_vectors_is_zero()
        => Assert.Equal(0d, VectorMath.Cosine([1f, 0f], [0f, 1f]), 6);

    [Fact]
    public void Cosine_of_opposite_vectors_is_minus_one()
        => Assert.Equal(-1d, VectorMath.Cosine([1f, 0f], [-1f, 0f]), 6);

    [Fact]
    public void Cosine_orders_a_near_vector_above_a_far_one()
    {
        ReadOnlySpan<float> query = [1f, 0f, 0f];
        var near = VectorMath.Cosine(query, [0.9f, 0.1f, 0f]);
        var far = VectorMath.Cosine(query, [0f, 1f, 0f]);
        Assert.True(near > far);
    }

    [Fact]
    public void Cosine_returns_zero_for_a_length_mismatch_or_zero_vector()
    {
        Assert.Equal(0d, VectorMath.Cosine([1f, 2f], [1f, 2f, 3f]));
        Assert.Equal(0d, VectorMath.Cosine([0f, 0f], [1f, 1f]));
    }

    [Fact]
    public void Blob_round_trips_a_vector_exactly()
    {
        float[] vector = [-1.5f, 0f, 3.25f, 42f];
        var restored = VectorMath.FromBlob(VectorMath.ToBlob(vector));
        Assert.Equal(vector, restored);
    }

    [Fact]
    public void Fusion_ranks_a_doc_both_tiers_agree_on_above_single_tier_hits()
    {
        var both = Result("both", 1);
        var keywordOnly = Result("kw", 2);
        var semanticOnly = Result("sem", 3);

        var fused = MemoryRankFusion.Fuse(
            keyword: [both, keywordOnly],
            semantic: [both, semanticOnly],
            limit: 10);

        // "both" is ranked #1 by each tier, so RRF sums put it first ahead of the single-tier hits.
        Assert.Equal("both", fused[0].SessionId);
        Assert.Contains(fused, r => r.SessionId == "kw");
        Assert.Contains(fused, r => r.SessionId == "sem");
    }

    [Fact]
    public void Fusion_prefers_the_keyword_snippet_when_both_tiers_return_a_row()
    {
        var keyword = new MemorySearchResult("s", 1, "[highlighted] keyword snippet", DateTimeOffset.UnixEpoch);
        var semantic = new MemorySearchResult("s", 1, "plain semantic excerpt", DateTimeOffset.UnixEpoch);

        var fused = MemoryRankFusion.Fuse([keyword], [semantic], limit: 10);

        var only = Assert.Single(fused);
        Assert.Equal("[highlighted] keyword snippet", only.Snippet);
    }

    [Fact]
    public void Fusion_honors_the_limit()
    {
        var fused = MemoryRankFusion.Fuse(
            keyword: [Result("a", 1), Result("b", 2), Result("c", 3)],
            semantic: [],
            limit: 2);
        Assert.Equal(2, fused.Count);
    }

    private static MemorySearchResult Result(string sessionId, long sequence)
        => new(sessionId, sequence, $"snippet-{sessionId}", DateTimeOffset.UnixEpoch);
}
