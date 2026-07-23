using Agnes.Abstractions;

namespace Agnes.Host.Sessions;

/// <summary>
/// Pure query logic that scopes a session's changed files to a turn or the whole session, over the
/// event-sourced log (see <c>git-and-files/01-deep-git-integration.md</c>). No new tracking state: the files a
/// tool call touched are already recorded in its normalized <see cref="ToolCallEvent"/> / <see
/// cref="ToolCallUpdateEvent"/> content, and turns are already delimited by <see cref="TurnEndedEvent"/> — so
/// scoping is a filter over history by file path and sequence range, computed on demand.
/// </summary>
internal static class ChangedFileScoping
{
    /// <summary>The file paths a single tool-call event touched — the <see cref="DiffContent.Path"/> of every
    /// diff it carries (the concrete file-modifying signal). A call's files may arrive on its initial event or
    /// on a later update, so both event kinds are considered.</summary>
    public static IEnumerable<string> TouchedPaths(SessionEvent @event) => @event switch
    {
        ToolCallEvent tc => PathsIn(tc.Content),
        ToolCallUpdateEvent { Content: { } content } => PathsIn(content),
        _ => [],
    };

    private static IEnumerable<string> PathsIn(IReadOnlyList<ContentBlock> content)
    {
        foreach (var block in content)
        {
            if (block is DiffContent { Path.Length: > 0 } diff)
            {
                yield return diff.Path;
            }
        }
    }

    /// <summary>
    /// The <c>(LowerExclusive, UpperInclusive]</c> sequence window of the session's most recent turn. A turn is
    /// the run of events ending at a <see cref="TurnEndedEvent"/>; if the log holds events after the last
    /// <see cref="TurnEndedEvent"/>, those form an in-progress turn and become the current one — otherwise the
    /// current turn is the one that just ended.
    /// </summary>
    public static (long LowerExclusive, long UpperInclusive) CurrentTurnWindow(IReadOnlyList<SessionEvent> events)
    {
        var turnEnds = new List<long>();
        long maxSeq = 0;
        foreach (var @event in events)
        {
            if (@event.Sequence > maxSeq)
            {
                maxSeq = @event.Sequence;
            }

            if (@event is TurnEndedEvent)
            {
                turnEnds.Add(@event.Sequence);
            }
        }

        if (turnEnds.Count == 0)
        {
            return (0, long.MaxValue); // a single open turn spanning the whole log
        }

        var lastEnd = turnEnds[^1];
        if (maxSeq > lastEnd)
        {
            return (lastEnd, long.MaxValue); // an in-progress turn after the last completed one
        }

        var priorEnd = turnEnds.Count >= 2 ? turnEnds[^2] : 0;
        return (priorEnd, lastEnd);
    }

    /// <summary>Every file touched by a tool call anywhere in the session log.</summary>
    public static IReadOnlyList<string> ThisSession(IReadOnlyList<SessionEvent> events, string workingDirectory)
        => Normalize(events.SelectMany(TouchedPaths), workingDirectory);

    /// <summary>Only the files touched by tool calls in the session's current turn.</summary>
    public static IReadOnlyList<string> ThisTurn(IReadOnlyList<SessionEvent> events, string workingDirectory)
    {
        var (lower, upper) = CurrentTurnWindow(events);
        var paths = events
            .Where(@event => @event.Sequence > lower && @event.Sequence <= upper)
            .SelectMany(TouchedPaths);
        return Normalize(paths, workingDirectory);
    }

    // Distinct, normalized (relative-to-working-dir, POSIX-separated), sorted for a deterministic result set
    // comparable to the git status paths used by the whole-repo scope.
    private static IReadOnlyList<string> Normalize(IEnumerable<string> paths, string workingDirectory)
        => paths.Select(path => NormalizeOne(path, workingDirectory))
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

    private static string NormalizeOne(string path, string workingDirectory)
    {
        var value = path;
        if (Path.IsPathRooted(value) && !string.IsNullOrEmpty(workingDirectory))
        {
            var relative = Path.GetRelativePath(workingDirectory, value);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                value = relative;
            }
        }

        return value.Replace('\\', '/');
    }
}
