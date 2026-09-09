using System.Globalization;
using System.Text.RegularExpressions;

namespace Agnes.Plugins.CodeyBox;

// ---------------------------------------------------------------------------------------------------
// PARSING A PASTED PLAN
//
// The way work actually gets filed here is: an agent or a person writes a numbered plan somewhere else,
// and the operator turns it into work items. Doing that by hand is one form per step plus one dependency
// per step, so seven steps is fourteen deliberate acts and the dependencies get skipped. CodeyBox's
// external ids exist precisely to make that one act instead: siblings reference each other by a locally
// generated id and the orchestrator resolves the edges at create time, no round trips.
//
// So the parser's whole job is to find the seams a human already put in the text — headings, numbering,
// rules — and to read "depends on: 1, 3" when it is there, defaulting to a straight line when it is not.
// It is deliberately conservative about numbering: "1." inside a paragraph is a list, not a section, and
// splitting there would produce nonsense the operator has to undo.
// ---------------------------------------------------------------------------------------------------

/// <summary>The composer's pure half: parsing a pasted plan and inferring a draft from context.</summary>
public static partial class Composer
{
    /// <summary>How long a title may be before the parser trims it; the rest stays in the prompt.</summary>
    internal const int MaxTitle = 120;

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+\S")]
    private static partial Regex HeadingLine();

    /// <summary>"1." / "1)" / "Step 1" / "3/7" — a section start, but only where a section may start.</summary>
    [GeneratedRegex(@"^\s*(?:\d+[.)]\s|[Ss]tep\s+\d+|\d+\s*/\s*\d+)")]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex RuleLine();

    /// <summary>Whatever numbering opened the title, so the title reads as a title.</summary>
    [GeneratedRegex(@"^\s*(?:[Ss]tep\s+\d+|\d+\s*/\s*\d+|\d+[.)])\s*[:.\-–—]?\s*")]
    private static partial Regex TitleNumbering();

    [GeneratedRegex(@"^\s*#{1,6}\s*")]
    private static partial Regex TitleHeading();

    [GeneratedRegex(@"^\s*(?:depends\s+on|after)\s*:?\s*(?:steps?\s*)?(?<nums>\d+(?:\s*,\s*\d+)*)\s*\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DependsLine();

    /// <summary>Splits pasted text into a <see cref="Plan"/>. One section is one item.</summary>
    public static Plan Parse(string text, string projectId, string prefix)
    {
        var sections = Split(text ?? string.Empty);
        if (sections.Count == 0)
        {
            return new Plan([], [], prefix);
        }

        var problems = new List<string>();
        var single = sections.Count == 1;

        // The section number an operator writes is 1-based and the one they will read back in a problem
        // line, so it is the only numbering this method speaks in.
        var externalIds = new string[sections.Count];
        for (var i = 0; i < sections.Count; i++)
        {
            externalIds[i] = single ? string.Empty : $"{prefix}-{i + 1:00}";
        }

        if (!single && ExternalIdProblem(externalIds[0]) is { } bad)
        {
            problems.Add($"the external-id prefix \"{prefix}\" cannot be used: it {bad}");
        }

        var drafts = new List<Draft>(sections.Count);
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        for (var i = 0; i < sections.Count; i++)
        {
            var body = sections[i];
            var title = TitleOf(body);
            if (title.Length == 0)
            {
                problems.Add($"step {i + 1} has no title");
            }

            var dependsOn = new List<string>();
            if (!single)
            {
                var named = ExplicitReferences(body);
                if (named.Count > 0)
                {
                    foreach (var n in named)
                    {
                        if (n < 1 || n > sections.Count)
                        {
                            problems.Add($"step {i + 1} depends on step {n}, which does not exist");
                        }
                        else if (n == i + 1)
                        {
                            problems.Add($"step {i + 1} depends on itself");
                        }
                        else
                        {
                            dependsOn.Add(externalIds[n - 1]);
                        }
                    }
                }
                else if (i > 0)
                {
                    // The default a numbered plan means when it says nothing: each step waits on the one
                    // before it. Wrong only when the author meant a fan-out, and that is one untick.
                    dependsOn.Add(externalIds[i - 1]);
                }
            }

            drafts.Add(new Draft(
                ProjectId: projectId,
                Title: title,
                Prompt: body,
                ExternalId: single ? null : externalIds[i],
                DependsOn: dependsOn,
                Priority: null,
                Agent: null,
                BaseBranch: null,
                AuditMaxIterations: null,
                AuditorProfile: null,
                IsRefactor: false));

            edges[single ? $"step-{i + 1}" : externalIds[i]] = dependsOn;
        }

        if (BoardModel.HasCycle(edges))
        {
            problems.Add("the steps depend on each other in a cycle");
        }

        return new Plan(drafts, problems, prefix);
    }

    /// <summary>
    /// Whether an id would be rejected by the orchestrator, and why. Checked here rather than at the
    /// POST because a plan of nine drafts fails nine times at the API and once in the composer.
    /// </summary>
    internal static string? ExternalIdProblem(string id)
    {
        if (id.Length is < 1 or > 256)
        {
            return "must be 1 to 256 characters";
        }

        if (id.Any(ch => ch is < ' ' or > '~'))
        {
            return "must be printable ASCII with no whitespace";
        }

        if (id.IndexOfAny([' ', '/', '?', ';', '<', '=', '>']) >= 0)
        {
            return "must not contain a space, /, ?, ;, <, = or >";
        }

        if (id.StartsWith("wi-", StringComparison.OrdinalIgnoreCase))
        {
            return "must not start with \"wi-\", which the orchestrator reserves";
        }

        // A UUID has no culture and Guid offers no culture-aware overload, so the analyser's rule does
        // not apply here; the check itself is the orchestrator's own ("must not parse as a UUID").
#pragma warning disable PH2031 // Do not use TryParse without specifying a culture
        return Guid.TryParse(id, out _) ? "must not be shaped like a UUID" : null;
#pragma warning restore PH2031
    }

    /// <summary>The step numbers a section names for itself, empty when it names none.</summary>
    private static IReadOnlyList<int> ExplicitReferences(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var m = DependsLine().Match(line.TrimEnd('\r'));
            if (!m.Success)
            {
                continue;
            }

            return
            [
                .. m.Groups["nums"].Value
                    .Split(',')
                    .Select(n => int.Parse(n.Trim(), CultureInfo.InvariantCulture))
            ];
        }

        return [];
    }

    private static string TitleOf(string body)
    {
        var first = body.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;
        var heading = TitleHeading().Replace(first.TrimEnd('\r'), string.Empty);
        var title = TitleNumbering().Replace(heading, string.Empty);
        if (title.Trim().Length == 0)
        {
            // The line was nothing but its numbering ("## 3."). Keep what is left rather than reporting
            // no title, so "no title" means the section genuinely has none.
            title = heading;
        }

        title = title.Trim().TrimEnd(':').Trim();
        return title.Length > MaxTitle ? title[..MaxTitle].TrimEnd() : title;
    }

    /// <summary>
    /// The sections, as text. A heading or a rule always opens one; numbering only opens one where a
    /// section could start — at the top, after a blank line, or right after another boundary — which is
    /// what keeps "we tried 1. this and 2. that" from becoming two work items.
    /// </summary>
    private static IReadOnlyList<string> Split(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sections = new List<List<string>>();
        List<string>? current = null;
        var startNew = false;
        var previousBlank = true;
        var previousWasBoundary = true;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (RuleLine().IsMatch(line))
            {
                startNew = true;
                previousBlank = false;
                previousWasBoundary = true;
                continue;
            }

            var blank = string.IsNullOrWhiteSpace(line);
            var boundary = !blank
                && (HeadingLine().IsMatch(line)
                    || (NumberedLine().IsMatch(line) && (previousBlank || previousWasBoundary)));

            if (!blank && (boundary || startNew || current is null))
            {
                current = [];
                sections.Add(current);
                startNew = false;
            }

            // Blank lines before the first section are not a section; blank lines inside one are part of
            // the prompt, which is the whole section text verbatim.
            if (current is not null)
            {
                current.Add(line);
            }

            if (!blank)
            {
                previousWasBoundary = boundary;
            }

            previousBlank = blank;
        }

        return
        [
            .. sections
                .Select(s => string.Join('\n', s).Trim('\n'))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.TrimEnd())
        ];
    }
}
