namespace Agnes.Cli;

/// <summary>How a caller-supplied id/prefix matched a set of candidates.</summary>
public enum PrefixMatchKind
{
    /// <summary>No candidate started with the prefix.</summary>
    None,

    /// <summary>Exactly one candidate matched — <see cref="PrefixResult{T}.Value"/> is set.</summary>
    Unique,

    /// <summary>More than one candidate matched — <see cref="PrefixResult{T}.Candidates"/> lists them.</summary>
    Ambiguous,
}

/// <summary>The outcome of resolving a prefix against a candidate set.</summary>
public sealed record PrefixResult<T>(PrefixMatchKind Kind, T? Value, IReadOnlyList<string> Candidates)
{
    public static PrefixResult<T> NoMatch() => new(PrefixMatchKind.None, default, []);

    public static PrefixResult<T> Unique(T value) => new(PrefixMatchKind.Unique, value, []);

    public static PrefixResult<T> Ambiguous(IReadOnlyList<string> candidates)
        => new(PrefixMatchKind.Ambiguous, default, candidates);
}

/// <summary>
/// Resolves an unambiguous id <em>prefix</em> to a single item, the ergonomics win every session/machine
/// id argument gets (scripts and humans skip pasting a full GUID). A pure function over its inputs so it
/// is trivially testable: unique prefix resolves, an ambiguous prefix reports the candidates, no match
/// reports none. An exact, full-id match always wins even when it is also a prefix of longer ids.
/// </summary>
public static class PrefixResolver
{
    public static PrefixResult<T> Resolve<T>(IEnumerable<T> items, Func<T, string> idSelector, string prefix)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(idSelector);
        var needle = (prefix ?? string.Empty).Trim();

        var all = items.ToArray();

        // An exact match is unambiguous by definition — a full id resolves to itself even if it also
        // prefixes longer ids (e.g. "sim-1" when "sim-10" exists).
        var exact = all.Where(i => string.Equals(idSelector(i), needle, StringComparison.Ordinal)).ToArray();
        if (exact.Length == 1)
        {
            return PrefixResult<T>.Unique(exact[0]);
        }

        if (needle.Length == 0)
        {
            return PrefixResult<T>.NoMatch();
        }

        var matches = all.Where(i => idSelector(i).StartsWith(needle, StringComparison.Ordinal)).ToArray();
        return matches.Length switch
        {
            0 => PrefixResult<T>.NoMatch(),
            1 => PrefixResult<T>.Unique(matches[0]),
            _ => PrefixResult<T>.Ambiguous(matches.Select(idSelector).ToArray()),
        };
    }
}
