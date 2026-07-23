using System.Text;
using Agnes.Abstractions;

namespace Agnes.Host.Events;

/// <summary>
/// Pulls the human-readable text out of a <see cref="SessionEvent"/> for full-text indexing — the same
/// corpus a person would search: message and thought prose, tool titles and their textual content, plan
/// entries, notices, titles, errors, and structured questions. Events with nothing searchable (usage
/// metrics, mode changes, permission plumbing) yield null so the caller skips them.
/// </summary>
internal static class MemoryText
{
    public static string? Extract(SessionEvent evt) => evt switch
    {
        MessageChunkEvent m => TextOf(m.Content),
        ThoughtChunkEvent t => TextOf(t.Content),
        ToolCallEvent c => Join(c.Title, JoinBlocks(c.Content)),
        ToolCallUpdateEvent u => JoinBlocks(u.Content),
        PlanEvent p => Join(p.Entries.Select(e => e.Content).ToArray()),
        NoticeEvent n => n.Message,
        SessionTitleEvent s => s.Title,
        AgentErrorEvent e => e.Message,
        QuestionAskedEvent q => Join(q.Questions.SelectMany(x => new[] { x.Header, x.Prompt }).ToArray()),
        _ => null,
    };

    private static string? JoinBlocks(IReadOnlyList<ContentBlock>? blocks)
        => blocks is null ? null : Join(blocks.Select(TextOf).ToArray());

    private static string? TextOf(ContentBlock content) => content switch
    {
        TextContent t => t.Text,
        ResourceLinkContent r => r.Name ?? r.Uri,
        DiffContent d => Join(d.Path, d.NewText),
        _ => null,
    };

    private static string? Join(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(part);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }
}
