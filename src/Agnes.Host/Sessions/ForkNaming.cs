using System.Text.RegularExpressions;

namespace Agnes.Host.Sessions;

/// <summary>
/// Proposes a target directory when forking a session by copying its working folder. If the source
/// ends in a number it increments it (<c>Agnes1 → Agnes2</c>, <c>Agnes9 → Agnes10</c>,
/// <c>Agnes99 → Agnes100</c>); otherwise it appends <c>2</c> (<c>Agnes → Agnes2</c>). It never proposes
/// a path that already exists — it keeps incrementing until a free sibling is found.
/// </summary>
public static partial class ForkNaming
{
    [GeneratedRegex(@"^(?<stem>.*?)(?<num>\d+)$")]
    private static partial Regex TrailingNumber();

    /// <summary>Proposes a non-existing sibling directory for a fork of <paramref name="sourceDirectory"/>.
    /// <paramref name="exists"/> is injectable for testing (defaults to real filesystem probing).</summary>
    public static string Propose(string sourceDirectory, Func<string, bool>? exists = null)
    {
        exists ??= p => Directory.Exists(p) || File.Exists(p);

        var trimmed = sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name))
        {
            // Degenerate input (root or empty) — fall back to a suffixed sibling.
            name = string.IsNullOrEmpty(trimmed) ? "fork" : trimmed;
            parent = null;
        }

        var match = TrailingNumber().Match(name);
        string stem;
        long next;
        if (match.Success && long.TryParse(match.Groups["num"].Value, out var current))
        {
            stem = match.Groups["stem"].Value;
            next = current + 1;
        }
        else
        {
            stem = name;
            next = 2;
        }

        // Guard against runaway loops on a pathological filesystem; the ceiling is far past any real use.
        for (var i = 0; i < 100_000; i++, next++)
        {
            var candidateName = stem + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var candidate = string.IsNullOrEmpty(parent) ? candidateName : Path.Combine(parent, candidateName);
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find a free fork directory near '{sourceDirectory}'.");
    }
}
