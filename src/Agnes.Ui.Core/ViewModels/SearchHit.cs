namespace Agnes.Ui.Core.ViewModels;

/// <summary>A single search match inside a session's transcript, deep-linkable by anchor.</summary>
public sealed class SearchHit
{
    public SearchHit(string anchorId, string kind, string snippet, string? sessionTitle = null)
    {
        AnchorId = anchorId;
        Kind = kind;
        Snippet = snippet;
        SessionTitle = sessionTitle;
    }

    /// <summary>Anchor of the transcript item to scroll to.</summary>
    public string AnchorId { get; }

    /// <summary>Short label of what matched (e.g. "You", "Agent", "Read").</summary>
    public string Kind { get; }

    /// <summary>A one-line excerpt around the match.</summary>
    public string Snippet { get; }

    /// <summary>For cross-session results: which session this hit belongs to.</summary>
    public string? SessionTitle { get; }

    /// <summary>Builds a one-line excerpt centred on the first match of <paramref name="query"/>.</summary>
    public static string Excerpt(string text, string query, int radius = 48)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        var i = flat.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return flat.Length > radius * 2 ? flat[..(radius * 2)].TrimEnd() + "…" : flat;
        }

        var start = Math.Max(0, i - radius);
        var end = Math.Min(flat.Length, i + query.Length + radius);
        var slice = flat[start..end];
        return (start > 0 ? "…" : string.Empty) + slice + (end < flat.Length ? "…" : string.Empty);
    }
}
