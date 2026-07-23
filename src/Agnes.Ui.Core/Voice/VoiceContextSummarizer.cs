using System.Text;
using Agnes.Abstractions;

namespace Agnes.Ui.Core.Voice;

/// <summary>
/// The privacy-filtered context the voice controller builds from session activity. Everything a provider
/// (potentially an external speech API) can ever see about the session passes through here, so the filtering
/// is structural: with <see cref="VoiceOptions.ForwardRawContext"/> off, this record is built from only the
/// safe fields of the events and never carries a raw tool-call argument or file path/content at all.
/// </summary>
public sealed record VoiceContext(IReadOnlyList<string> Lines, bool IncludedRawContext)
{
    /// <summary>A single spoken sentence for turn-completion read-back.</summary>
    public string SpokenSummary =>
        Lines.Count == 0 ? "The agent finished its turn." : string.Join(". ", Lines) + ".";

    /// <summary>The full context string a provider would receive (also privacy-filtered).</summary>
    public string ToPromptContext() => string.Join('\n', Lines);
}

/// <summary>
/// Builds the voice controller's context from a session's event log while enforcing Agnes's conservative
/// privacy default. This is where the guarantee lives — not in any provider: when
/// <see cref="VoiceOptions.ForwardRawContext"/> is false, raw tool-call arguments and file contents/paths
/// are excluded here, so a provider is structurally unable to receive them. Pure and deterministic; holds no
/// state.
/// </summary>
public sealed class VoiceContextSummarizer
{
    /// <summary>Cap on how many recent events shape the summary, so context can't grow unbounded.</summary>
    private const int MaxLines = 12;

    public VoiceContext Summarize(IReadOnlyList<SessionEvent> events, VoiceOptions options)
    {
        var forwardRaw = options.ForwardRawContext;
        var lines = new List<string>();

        foreach (var evt in events)
        {
            var line = Describe(evt, forwardRaw);
            if (line is not null)
            {
                lines.Add(line);
            }
        }

        if (lines.Count > MaxLines)
        {
            lines = lines.GetRange(lines.Count - MaxLines, MaxLines);
        }

        return new VoiceContext(lines, forwardRaw);
    }

    private static string? Describe(SessionEvent evt, bool forwardRaw) => evt switch
    {
        // Assistant natural-language prose is the agent's own response — the thing voice read-back exists to
        // speak. It is neither a tool-call argument nor a file path/content, so it is always included.
        MessageChunkEvent { Role: MessageRole.Assistant, Content: TextContent text } => Trim(text.Text),

        // A user's own spoken/typed prompt is safe to echo back into context.
        MessageChunkEvent { Role: MessageRole.User, Content: TextContent } => null,

        // Tool calls are the sensitive surface. Off by default we emit ONLY the taxonomy + status — never the
        // Title (which routinely embeds a file path) and never the Content blocks (command args, diffs, file
        // text, resource URIs). Raw detail is included solely when the user opted in per provider.
        ToolCallEvent tc => forwardRaw
            ? $"Ran {tc.Kind} tool \"{tc.Title}\"{DescribeContent(tc.Content)}"
            : $"Used a {tc.Kind} tool ({tc.Status})",

        // A permission Title likewise can contain a path; keep it generic unless raw context is allowed.
        PermissionRequestedEvent pr => forwardRaw
            ? $"Asked permission: {pr.Title}"
            : "Requested a permission",

        TurnEndedEvent turn => $"Turn ended ({turn.Reason})",
        AgentErrorEvent err => forwardRaw ? $"Error: {err.Message}" : "The agent reported an error",
        _ => null,
    };

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

    private static string? Trim(string text)
    {
        var t = text.Trim();
        return t.Length == 0 ? null : t;
    }
}
