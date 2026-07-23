using System.Text;
using Agnes.Abstractions;

namespace Agnes.Host.Mcp;

/// <summary>
/// The host-side enforcement point for Agnes's conservative voice/MCP privacy default (mirrors the
/// client-side <c>VoiceContextSummarizer</c>): everything an MCP client — potentially an external speech API
/// like OpenAI Realtime — can ever learn about a session's transcript passes through here. With
/// <paramref name="forwardRawContext"/> false (the default) the filtering is structural: raw tool-call
/// arguments and file contents/paths are excluded when the transcript is built, so a caller is unable to
/// receive them at all. Only an explicit per-caller opt-in includes that raw material. Pure and deterministic;
/// holds no state. The failure mode this guards against — source code or file paths silently leaving the host
/// to a third-party API — is exactly the kind of privacy regression that's hard to notice after the fact.
/// </summary>
public sealed class TranscriptPrivacyFilter
{
    /// <summary>Cap on how many recent events shape the transcript, so it can't grow unbounded.</summary>
    private const int MaxLines = 40;

    public McpTranscript Build(IReadOnlyList<SessionEvent> events, bool forwardRawContext)
    {
        var lines = new List<string>();
        foreach (var evt in events)
        {
            if (Describe(evt, forwardRawContext) is { } line)
            {
                lines.Add(line);
            }
        }

        if (lines.Count > MaxLines)
        {
            lines = lines.GetRange(lines.Count - MaxLines, MaxLines);
        }

        return new McpTranscript(lines, forwardRawContext);
    }

    private static string? Describe(SessionEvent evt, bool forwardRaw) => evt switch
    {
        // The user's own words and the agent's natural-language prose are neither tool-call arguments nor file
        // paths/contents, so they are always safe to include.
        MessageChunkEvent { Role: MessageRole.User, Content: TextContent user } => Prefix("You", user.Text),
        MessageChunkEvent { Role: MessageRole.Assistant, Content: TextContent asst } => Prefix("Agent", asst.Text),

        // Tool calls are the sensitive surface. Off by default we emit ONLY the taxonomy + status — never the
        // Title (which routinely embeds a file path) and never the Content blocks (command args, diffs, file
        // text, resource URIs). Raw detail is included solely when the caller opted in.
        ToolCallEvent tc => forwardRaw
            ? $"Agent ran {tc.Kind} tool \"{tc.Title}\"{DescribeContent(tc.Content)}"
            : $"Agent used a {tc.Kind} tool ({tc.Status})",

        // A permission Title likewise can contain a path; keep it generic unless raw context is allowed.
        PermissionRequestedEvent pr => forwardRaw
            ? $"Agent asked permission: {pr.Title}"
            : "Agent requested a permission",

        TurnEndedEvent turn => $"Turn ended ({turn.Reason})",
        AgentErrorEvent err => forwardRaw ? $"Error: {err.Message}" : "The agent reported an error",
        _ => null,
    };

    private static string? Prefix(string who, string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : $"{who}: {trimmed}";
    }

    private static string DescribeContent(IReadOnlyList<ContentBlock> content)
    {
        if (content.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent t:
                    sb.Append(' ').Append(t.Text);
                    break;
                case DiffContent d:
                    sb.Append(" [").Append(d.Path).Append("] ").Append(d.NewText);
                    break;
                case ResourceLinkContent r:
                    sb.Append(' ').Append(r.Uri);
                    break;
                default:
                    break;
            }
        }

        return sb.ToString();
    }
}
