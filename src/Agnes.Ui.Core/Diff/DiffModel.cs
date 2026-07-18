using System.Text;

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

/// <summary>One row of a side-by-side (split) diff: old on the left, new on the right.</summary>
public sealed record DiffSplitRow(
    string LeftNo, string LeftText, bool LeftRemoved, bool HasLeft,
    string RightNo, string RightText, bool RightAdded, bool HasRight,
    bool IsHunk, string HeaderText);

/// <summary>Builds unified-diff text from full old/new file contents (for ACP structured diffs).</summary>
public static class UnifiedDiff
{
    public static string Format(string path, string? oldText, string newText)
    {
        var a = SplitLines(oldText ?? string.Empty);
        var b = SplitLines(newText);
        var ops = Diff(a, b);

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');
        sb.Append("@@ -1,").Append(a.Length).Append(" +1,").Append(b.Length).Append(" @@\n");
        foreach (var (marker, text) in ops)
        {
            sb.Append(marker).Append(text).Append('\n');
        }

        return sb.ToString();
    }

    private static string[] SplitLines(string s)
        => s.Length == 0 ? [] : s.Replace("\r\n", "\n").Split('\n');

    // Longest-common-subsequence line diff → ' ' context, '-' removed, '+' added.
    private static List<(char Marker, string Text)> Diff(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                result.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                result.Add(('-', a[x]));
                x++;
            }
            else
            {
                result.Add(('+', b[y]));
                y++;
            }
        }

        while (x < n) { result.Add(('-', a[x])); x++; }
        while (y < m) { result.Add(('+', b[y])); y++; }
        return result;
    }
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

    /// <summary>Projects a unified diff into paired left/old ⇄ right/new rows for a split view.</summary>
    public static IReadOnlyList<DiffSplitRow> ToSplit(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<DiffSplitRow>(lines.Count);
        foreach (var l in lines)
        {
            if (l.IsHunk)
            {
                rows.Add(new DiffSplitRow(string.Empty, string.Empty, false, false,
                    string.Empty, string.Empty, false, false, true, l.Text));
            }
            else if (l.IsAdded)
            {
                rows.Add(new DiffSplitRow(string.Empty, string.Empty, false, false,
                    l.NewLineText, l.Text, true, true, false, string.Empty));
            }
            else if (l.IsRemoved)
            {
                rows.Add(new DiffSplitRow(l.OldLineText, l.Text, true, true,
                    string.Empty, string.Empty, false, false, false, string.Empty));
            }
            else
            {
                rows.Add(new DiffSplitRow(l.OldLineText, l.Text, false, true,
                    l.NewLineText, l.Text, false, true, false, string.Empty));
            }
        }

        return rows;
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
