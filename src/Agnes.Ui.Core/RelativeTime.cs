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
}
