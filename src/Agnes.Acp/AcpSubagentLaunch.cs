using System.Text.Json;

namespace Agnes.Acp;

/// <summary>
/// A tool call that hands work to a subagent, recognized from the call's raw input.
/// </summary>
/// <param name="AgentType">Which agent was dispatched to (Copilot's <c>explore</c>, Claude's
/// <c>code-reviewer</c>, …), or null when the caller named only the work.</param>
/// <param name="Description">The short human label for the work, when the caller gave one.</param>
/// <param name="IsBackground">Whether the subagent was dispatched to run in the background, meaning its
/// result returns later and its inner work never streams.</param>
/// <remarks>
/// <para>ACP has no notion of a subagent, so an agent that spawns one reports it as an ordinary tool
/// call. Worse, the one field that would identify it — the tool's name — is not on the wire: ACP carries
/// a <em>title</em> meant for a human, and Copilot deliberately substitutes one, sending "Explore importer
/// architecture" (or "Running subtask") where the tool is called <c>task</c>. Matching titles against
/// known tool names therefore finds Copilot's subagents never, which is why they appeared nowhere in the
/// agent roster while Claude's and OpenCode's did.</para>
///
/// <para>The identity is on the wire, in <c>rawInput</c> — the tool's own arguments, which Agnes had been
/// discarding everywhere except the permission card. This reads them, and does so <b>by shape rather than
/// by adapter</b>: a call carrying a prompt plus the name of an agent to run it is a delegation whoever
/// sent it. That is not a guess about one CLI — it is the shared convention. Copilot sends
/// <c>{agent_type, description, prompt, mode}</c> and Claude's Task tool
/// <c>{subagent_type, description, prompt}</c>; recognizing the shape means a CLI that adopts it needs no
/// change here, and <c>Agnes.Acp</c> stays free of any particular agent's name.</para>
///
/// <para>The prompt itself is deliberately not kept. It is the whole brief — often thousands of words —
/// and nothing downstream shows it; the roster wants a name.</para>
/// </remarks>
public sealed record AcpSubagentLaunch(string? AgentType, string? Description, bool IsBackground)
{
    // Both spellings of "which agent", and the two fields that make it a dispatch rather than a lookup.
    private static readonly string[] AgentKeys = ["agent_type", "subagent_type", "agentType", "subagentType"];
    private static readonly string[] DescriptionKeys = ["description", "subject", "title"];

    /// <summary>
    /// Reads a delegation out of a tool call's raw input, or null when the call is an ordinary one.
    /// </summary>
    /// <remarks>
    /// Requires a prompt <em>and</em> a named agent together. Either alone is too common to act on: plenty
    /// of tools take a "prompt", and Copilot's own <c>read_agent</c> / <c>list_agents</c> name an agent
    /// without dispatching to one — treating those as launches would put a roster row on every glance at
    /// an agent definition.
    /// </remarks>
    public static AcpSubagentLaunch? TryParse(JsonElement? rawInput)
    {
        if (rawInput is not { ValueKind: JsonValueKind.Object } input)
        {
            return null;
        }

        if (Text(input, "prompt") is not { Length: > 0 })
        {
            return null;
        }

        var agentType = First(input, AgentKeys);
        if (agentType is not { Length: > 0 })
        {
            return null;
        }

        return new AcpSubagentLaunch(
            agentType,
            First(input, DescriptionKeys),
            string.Equals(Text(input, "mode"), "background", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What the roster should call this subagent: the work if the caller described it, else the
    /// agent that was asked to do it. Never the prompt — that is a brief, not a name.</summary>
    public string Name => Description is { Length: > 0 } description
        ? description
        : AgentType is { Length: > 0 } agent ? agent : "subagent";

    private static string? First(JsonElement input, string[] keys)
    {
        foreach (var key in keys)
        {
            if (Text(input, key) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private static string? Text(JsonElement input, string name)
        => input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
