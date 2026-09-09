using System;

namespace Agnes.Ui.Core;

/// <summary>
/// How Agnes says "when did this last happen" in a list. One vocabulary — <c>now / 4m / 2h / 3d</c>, then a
/// date — shared by every surface that shows a session's age, so the phone's session card and the desktop's
/// dashboard never disagree about what "recent" reads like.
/// </summary>
public static class RelativeTime
{
    /// <summary>Formats how long ago <paramref name="when"/> was, relative to <paramref name="now"/> (defaults
    /// to the current local time). Empty for a null timestamp, so a caller can bind it directly.</summary>
    public static string Format(DateTimeOffset? when, DateTimeOffset? now = null)
    {
        if (when is not { } stamp)
        {
            return string.Empty;
        }

        var span = (now ?? DateTimeOffset.Now) - stamp;
        return span switch
        {
            { TotalSeconds: < 45 } => "now",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes}m",
            { TotalHours: < 24 } => $"{(int)span.TotalHours}h",
            { TotalDays: < 7 } => $"{(int)span.TotalDays}d",
            _ => stamp.ToString("d MMM"),
        };
    }

    /// <summary>
    /// The same instant said in prose rather than in a list column — <c>just now / 2 min ago / 3 h ago /
    /// 2 d ago</c>. The terse <see cref="Format"/> vocabulary is right beside a title in a dense row, but a
    /// sentence the agent wrote ("fixing the config default") reads as a sentence, and "4m" hanging off the
    /// end of one looks like part of it. Empty for a null timestamp, so a caller can bind it directly.
    /// </summary>
    public static string Ago(DateTimeOffset? when, DateTimeOffset? now = null)
    {
        if (when is not { } stamp)
        {
            return string.Empty;
        }

        var span = (now ?? DateTimeOffset.Now) - stamp;
        return span.TotalSeconds < 45 ? "just now" : Elapsed(span) + " ago";
    }

    /// <summary>
    /// How long a span is, in one unit — <c>12 min</c>, <c>3 h</c>, <c>2 d</c>. Used where the sentence
    /// supplies its own preposition ("no update for 12 min"), which is why it stops short of "ago".
    /// A negative span (a clock that disagrees with the host's) reads as <c>0 min</c> rather than as a
    /// number from the future.
    /// </summary>
    public static string Elapsed(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "0 min",
        { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} min",
        { TotalHours: < 24 } => $"{(int)span.TotalHours} h",
        _ => $"{(int)span.TotalDays} d",
    };
}
