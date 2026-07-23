using Agnes.Cli;

namespace Agnes.Cli.Tests;

public sealed class PrefixResolverTests
{
    private static readonly string[] Ids = ["sim-0001", "sim-0002", "abcdef12"];

    [Fact]
    public void Unique_prefix_resolves_to_the_single_match()
    {
        var result = PrefixResolver.Resolve(Ids, x => x, "abc");

        Assert.Equal(PrefixMatchKind.Unique, result.Kind);
        Assert.Equal("abcdef12", result.Value);
    }

    [Fact]
    public void Ambiguous_prefix_returns_all_candidates()
    {
        var result = PrefixResolver.Resolve(Ids, x => x, "sim-000");

        Assert.Equal(PrefixMatchKind.Ambiguous, result.Kind);
        Assert.Equal(["sim-0001", "sim-0002"], result.Candidates.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void No_match_returns_none()
    {
        var result = PrefixResolver.Resolve(Ids, x => x, "zzz");

        Assert.Equal(PrefixMatchKind.None, result.Kind);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Empty_prefix_matches_nothing()
    {
        var result = PrefixResolver.Resolve(Ids, x => x, "");

        Assert.Equal(PrefixMatchKind.None, result.Kind);
    }

    [Fact]
    public void Exact_full_id_wins_even_when_it_prefixes_a_longer_id()
    {
        string[] ids = ["sim-1", "sim-10", "sim-100"];

        var result = PrefixResolver.Resolve(ids, x => x, "sim-1");

        Assert.Equal(PrefixMatchKind.Unique, result.Kind);
        Assert.Equal("sim-1", result.Value);
    }
}
