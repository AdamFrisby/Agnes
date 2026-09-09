namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// One line of a unified diff, classified so it can be coloured.
///
/// <para>The diff is the one artefact in this tab a developer already knows how to read at a glance —
/// but only in colour. As undifferentiated monospace it is the least legible thing on the screen, which
/// is what it was.</para>
/// </summary>
public sealed record DiffLine(string Text, DiffLineKind Kind)
{
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsHunk => Kind == DiffLineKind.Hunk;
    public bool IsFile => Kind == DiffLineKind.File;
    public bool IsContext => Kind == DiffLineKind.Context;
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
    File,
}

public static class UnifiedDiff
{
    /// <summary>
    /// Splits a unified diff into classified lines.
    ///
    /// <para>Order matters here. <c>+++</c> and <c>---</c> are file headers, not an addition and a
    /// removal, and they begin with the same characters — testing for the header first is the whole
    /// difference between a readable diff and one where every file boundary is painted as a change.</para>
    /// </summary>
    /// <param name="maxLines">Ceiling on how much is returned. A work branch diff can be enormous, and a
    /// UI that renders every line of one stops being a UI. The caller reports what was withheld.</param>
    public static IReadOnlyList<DiffLine> Parse(string diff, int maxLines = 2000)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return [];
        }

        var lines = diff.Split('\n');
        var take = Math.Min(lines.Length, maxLines);
        var parsed = new List<DiffLine>(take);

        for (var i = 0; i < take; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var kind = line switch
            {
                _ when line.StartsWith("diff --git", StringComparison.Ordinal) => DiffLineKind.File,
                _ when line.StartsWith("+++", StringComparison.Ordinal) => DiffLineKind.File,
                _ when line.StartsWith("---", StringComparison.Ordinal) => DiffLineKind.File,
                _ when line.StartsWith("index ", StringComparison.Ordinal) => DiffLineKind.File,
                _ when line.StartsWith("@@", StringComparison.Ordinal) => DiffLineKind.Hunk,
                _ when line.StartsWith('+') => DiffLineKind.Added,
                _ when line.StartsWith('-') => DiffLineKind.Removed,
                _ => DiffLineKind.Context,
            };

            parsed.Add(new DiffLine(line, kind));
        }

        return parsed;
    }

    /// <summary>Files touched and lines changed, for the header — the summary a reviewer wants before
    /// deciding whether to read the body.</summary>
    public static string Summarise(IReadOnlyList<DiffLine> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var files = lines.Count(l => l.Text.StartsWith("diff --git", StringComparison.Ordinal));
        var added = lines.Count(l => l.IsAdded);
        var removed = lines.Count(l => l.IsRemoved);
        return $"{files} file{(files == 1 ? "" : "s")}  ·  +{added}  −{removed}";
    }
}
