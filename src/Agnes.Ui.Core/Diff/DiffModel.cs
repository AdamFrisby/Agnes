namespace Agnes.Ui.Core.Diff;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
    FileHeader,
}

/// <summary>One rendered line of a unified diff, with old/new line numbers.</summary>
public sealed record DiffLine(int? OldLine, int? NewLine, DiffLineKind Kind, string Text)
{
    public string OldLineText => OldLine?.ToString() ?? string.Empty;
    public string NewLineText => NewLine?.ToString() ?? string.Empty;

    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };

    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsHunk => Kind is DiffLineKind.Hunk or DiffLineKind.FileHeader;
}

/// <summary>Parses a unified diff into <see cref="DiffLine"/>s with running line numbers.</summary>
public static class DiffParser
{
    public static bool LooksLikeDiff(string text)
        => text.Contains("@@ ", StringComparison.Ordinal)
           || text.StartsWith("--- ", StringComparison.Ordinal)
           || text.StartsWith("diff ", StringComparison.Ordinal);

    public static IReadOnlyList<DiffLine> Parse(string diff)
    {
        var lines = new List<DiffLine>();
        int oldNo = 0, newNo = 0;

        foreach (var raw in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldNo, newNo) = ParseHunkHeader(raw, oldNo, newNo);
                lines.Add(new DiffLine(null, null, DiffLineKind.Hunk, raw));
            }
            else if (raw.StartsWith("+++", StringComparison.Ordinal) || raw.StartsWith("---", StringComparison.Ordinal)
                     || raw.StartsWith("diff ", StringComparison.Ordinal) || raw.StartsWith("index ", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(null, null, DiffLineKind.FileHeader, raw));
            }
            else if (raw.StartsWith('+'))
            {
                lines.Add(new DiffLine(null, newNo, DiffLineKind.Added, raw[1..]));
                newNo++;
            }
            else if (raw.StartsWith('-'))
            {
                lines.Add(new DiffLine(oldNo, null, DiffLineKind.Removed, raw[1..]));
                oldNo++;
            }
            else
            {
                var text = raw.StartsWith(' ') ? raw[1..] : raw;
                lines.Add(new DiffLine(oldNo, newNo, DiffLineKind.Context, text));
                oldNo++;
                newNo++;
            }
        }

        // Drop a trailing blank line produced by a final newline.
        if (lines.Count > 0 && lines[^1] is { Kind: DiffLineKind.Context, Text: "" })
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static (int Old, int New) ParseHunkHeader(string header, int oldNo, int newNo)
    {
        // @@ -oldStart,oldCount +newStart,newCount @@
        try
        {
            var body = header.Trim('@', ' ');
            var parts = body.Split(' ');
            var old = parts.FirstOrDefault(p => p.StartsWith('-'));
            var neu = parts.FirstOrDefault(p => p.StartsWith('+'));
            var o = old is not null ? int.Parse(old.TrimStart('-').Split(',')[0]) : oldNo;
            var n = neu is not null ? int.Parse(neu.TrimStart('+').Split(',')[0]) : newNo;
            return (o, n);
        }
        catch
        {
            return (oldNo, newNo);
        }
    }
}
