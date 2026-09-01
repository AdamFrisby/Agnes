using System.Text;
using Agnes.Abstractions;

namespace Agnes.Ui.Core.Transcript;

/// <summary>Renders the provider's current plan as portable Markdown for clipboard use.</summary>
public static class PlanMarkdown
{
    /// <summary>Formats every plan entry, including entries folded out of the sidebar.</summary>
    public static string Format(IReadOnlyList<PlanEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var markdown = new StringBuilder("# Plan\n");
        if (entries.Count == 0)
        {
            return markdown.ToString();
        }

        foreach (var entry in entries)
        {
            var view = PlanEntryView.Of(entry);
            var lines = entry.Content.ReplaceLineEndings("\n").Split('\n');
            markdown.Append("\n- [")
                .Append(view.IsDone ? 'x' : ' ')
                .Append("] ")
                .Append(lines[0]);

            var detail = Detail(entry, view);
            if (detail.Length > 0)
            {
                markdown.Append(" _(").Append(detail).Append(")_");
            }

            foreach (var continuation in lines.Skip(1))
            {
                markdown.Append("\n  ").Append(continuation);
            }
        }

        return markdown.Append('\n').ToString();
    }

    private static string Detail(PlanEntry entry, PlanEntryView view)
    {
        var state = view.IsDone || view.IsPending ? null : view.StatusLabel;
        var priority = string.IsNullOrWhiteSpace(entry.Priority) ? null : $"Priority: {entry.Priority}";
        return string.Join(" · ", new[] { state, priority }.Where(value => value is not null));
    }
}
