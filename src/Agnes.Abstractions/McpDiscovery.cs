namespace Agnes.Abstractions;

/// <summary>
/// An MCP server an agent CLI already has configured in its OWN native config (e.g. Claude Code's
/// <c>~/.claude.json</c> / a project <c>.mcp.json</c>), surfaced read-only so Agnes can show "what does this
/// tool already know about, that Agnes doesn't manage." Deliberately a small, adapter-neutral shape rather
/// than <c>McpServerInfo</c> (which lives in <c>Agnes.Protocol</c>, downstream of this package): the host maps
/// it into the wire type, tagging it as native-origin. <see cref="SourceLabel"/> is a human-readable origin
/// for the UI (e.g. "Claude Code native config").
/// </summary>
public sealed record NativeMcpServer(
    string Name,
    string Transport,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Url,
    string SourceLabel);

/// <summary>
/// Optional capability an <see cref="IAgentAdapter"/> may implement (checked via <c>is IMcpDiscoveryAdapter</c>,
/// like <see cref="IModelListingAdapter"/>) when it can read the MCP servers its CLI has configured natively.
/// Detection is adapter-level, not central, because every CLI's own MCP config format differs — a detector
/// lives with the agent it's specific to. An adapter that can't read its CLI's config simply doesn't implement
/// this interface, so no native servers are surfaced (graceful). Always non-throwing: a missing or malformed
/// config yields an empty list, never an exception.
/// </summary>
public interface IMcpDiscoveryAdapter
{
    /// <summary>The MCP servers this CLI already has configured natively for <paramref name="workspaceDirectory"/>
    /// (both global and per-project config, where the CLI has both). Empty when none / config unreadable.</summary>
    Task<IReadOnlyList<NativeMcpServer>> DetectNativeConfigAsync(string workspaceDirectory, CancellationToken ct = default);
}
