using Agnes.Abstractions;

namespace Agnes.Host.Events;

/// <summary>
/// Reciprocal-rank fusion (RRF) of the two memory tiers — keyword (FTS5) and semantic (cosine) — so a hit
/// that either tier surfaces reaches the caller, and a hit both tiers agree on floats to the top. RRF is
/// used rather than blending raw scores because FTS5's <c>rank</c> and cosine similarity live on
/// incomparable scales; ranks are comparable. Pure and deterministic so the fusion is unit-testable.
/// See .ideas/ops/02-memory-search.md (the "blend vs replace" open question — this blends).
/// </summary>
internal static class MemoryRankFusion
{
    /// <summary>
    /// Dampening constant from the original RRF paper; keeps any single list from dominating and softens the
    /// contribution of long tails. 60 is the widely-used default.
    /// </summary>
    private const double K = 60d;

    /// <summary>
    /// Fuses two best-first result lists into one, ranked by summed reciprocal rank. When a key appears in
    /// both lists the keyword snippet is kept (FTS5's is highlighted; the semantic one is a plain excerpt).
    /// </summary>
    public static IReadOnlyList<MemorySearchResult> Fuse(
        IReadOnlyList<MemorySearchResult> keyword,
        IReadOnlyList<MemorySearchResult> semantic,
        int limit)
    {
        var scores = new Dictionary<(string SessionId, long Sequence), double>();
        // Keyword results win the snippet tie-break, so seed the chosen-result map from them first.
        var chosen = new Dictionary<(string SessionId, long Sequence), MemorySearchResult>();

        Accumulate(keyword, scores, chosen, keepExisting: false);
        Accumulate(semantic, scores, chosen, keepExisting: true);

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => kv.Key.Sequence)
            .Take(limit)
            .Select(kv => chosen[kv.Key])
            .ToArray();
    }

    private static void Accumulate(
        IReadOnlyList<MemorySearchResult> list,
        Dictionary<(string SessionId, long Sequence), double> scores,
        Dictionary<(string SessionId, long Sequence), MemorySearchResult> chosen,
        bool keepExisting)
    {
        for (var rank = 0; rank < list.Count; rank++)
        {
            var result = list[rank];
            var key = (result.SessionId, result.Sequence);
            scores[key] = (scores.TryGetValue(key, out var existing) ? existing : 0d) + (1d / (K + rank + 1));
            if (!keepExisting || !chosen.ContainsKey(key))
            {
                chosen[key] = result;
            }
        }
    }
}
