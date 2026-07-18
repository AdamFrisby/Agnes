using System.Text.Json;
using Agnes.Abstractions;

namespace Agnes.Agents.Native;

/// <summary>
/// Maps a coding CLI's native stream-json (JSONL) protocol to/from Agnes's domain model. One
/// implementation per CLI (e.g. Claude Code). Pure and golden-JSON testable, like the ACP mapper.
/// </summary>
public interface INativeStreamMapper
{
    /// <summary>Translates one stdout JSONL line into zero or more <see cref="SessionEvent"/>s.</summary>
    IEnumerable<SessionEvent> ToEvents(JsonElement line);

    /// <summary>Builds the JSON line written to the agent's stdin to send a user prompt.</summary>
    string BuildUserTurn(IReadOnlyList<ContentBlock> content);
}
