namespace Agnes.Host.Sessions;

/// <summary>
/// What the host made of one status report: the line it will actually record, plus whether anything was
/// taken away getting there. The flags exist so the agent can be <i>told</i>: a status silently cut in half
/// teaches a model nothing, and it will write the same paragraph again next time.
/// </summary>
/// <param name="Status">The normalised line, ready to record.</param>
/// <param name="Clipped">The report was longer than the limit and was cut at a word boundary.</param>
/// <param name="TrimmedToFirstLine">The report had more than one line and only the first was kept.</param>
/// <param name="MaxChars">The limit that was applied, carried along so whoever words the acknowledgement
/// quotes the host's real configured number rather than guessing at the default.</param>
public sealed record StatusReportResult(string Status, bool Clipped, bool TrimmedToFirstLine, int MaxChars)
{
    /// <summary>Whether the agent's own text survived intact.</summary>
    public bool Verbatim => !Clipped && !TrimmedToFirstLine;
}

/// <summary>
/// Turning whatever a model typed into the one line Agnes shows — and into the sentence the model reads back
/// about it. Pure functions over their inputs: no state, no clock, no store, so every rule here is testable
/// on its own and the same normalisation applies whether the report arrived over MCP or from a plugin.
/// </summary>
public static class AgentStatusText
{
    /// <summary>The character that marks a clipped line. One glyph, so it costs one of the budgeted chars.</summary>
    private const char Ellipsis = '…';

    /// <summary>
    /// Normalises a raw report: the first line only, inner whitespace collapsed to single spaces, trimmed,
    /// and clipped at the last word boundary that fits <paramref name="maxChars"/> (ellipsis included in the
    /// budget). Returns null when nothing is left — an empty report is not a status.
    /// </summary>
    public static StatusReportResult? Normalize(string? raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // A status is one line by construction. Anything after the first newline is dropped rather than
        // flattened into it: a model that wrote a bulleted plan meant the first line as its headline, and
        // joining the bullets with spaces would produce a sentence it never wrote.
        var firstBreak = raw.AsSpan().IndexOfAny('\r', '\n');
        var trimmedToFirstLine = false;
        var line = raw;
        if (firstBreak >= 0)
        {
            var rest = raw[(firstBreak + 1)..];
            line = raw[..firstBreak];
            // Only report the trim when something was actually thrown away — a trailing newline is not a
            // second line, and telling the agent off for one would be noise.
            trimmedToFirstLine = !string.IsNullOrWhiteSpace(rest);
        }

        var collapsed = CollapseWhitespace(line);
        if (collapsed.Length == 0)
        {
            // The first line was blank but later lines weren't: fall back to the first line that has content,
            // otherwise a status typed after a leading newline would be refused as empty.
            foreach (var candidate in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                collapsed = CollapseWhitespace(candidate);
                if (collapsed.Length > 0)
                {
                    break;
                }
            }

            if (collapsed.Length == 0)
            {
                return null;
            }
        }

        var limit = maxChars > 0 ? maxChars : StatusOptions.DefaultMaxChars;
        return collapsed.Length <= limit
            ? new StatusReportResult(collapsed, Clipped: false, trimmedToFirstLine, limit)
            : new StatusReportResult(Clip(collapsed, limit), Clipped: true, trimmedToFirstLine, limit);
    }

    /// <summary>Every run of whitespace becomes one space; leading/trailing whitespace goes.</summary>
    private static string CollapseWhitespace(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Cuts to <paramref name="limit"/> characters <i>including</i> the ellipsis, at the last word boundary
    /// that fits. A single unbroken token longer than the limit has no boundary to find, so it is cut mid-word
    /// rather than returned as a lone ellipsis.
    /// </summary>
    private static string Clip(string value, int limit)
    {
        if (limit <= 1)
        {
            return Ellipsis.ToString();
        }

        var head = value[..(limit - 1)];
        var lastSpace = head.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            head = head[..lastSpace];
        }

        return head.TrimEnd() + Ellipsis;
    }

    /// <summary>
    /// What the tool says back to the agent. A report that survived intact gets a bare acknowledgement;
    /// anything the host took away is named, with the limit stated in the same words the tool description
    /// uses, so the next report is shorter instead of being cut again.
    /// </summary>
    public static string Acknowledge(StatusReportResult result)
    {
        if (result.Verbatim)
        {
            return "Noted.";
        }

        var limit = result.MaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parts = new List<string>(2);
        if (result.Clipped)
        {
            parts.Add(
                $"Noted the first {limit} characters: \"{result.Status}\". "
                + $"Keep future reports to one or two sentences under {limit} characters.");
        }

        if (result.TrimmedToFirstLine)
        {
            parts.Add("Kept the first line; reports are a single line.");
        }

        return string.Join(" ", parts);
    }
}
