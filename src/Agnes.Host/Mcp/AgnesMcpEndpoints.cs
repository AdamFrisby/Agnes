using ModelContextProtocol.Server;

namespace Agnes.Host.Mcp;

/// <summary>Well-known routes for the Agnes-as-MCP-server surface.</summary>
public static class AgnesMcpEndpoints
{
    /// <summary>The Streamable-HTTP MCP endpoint path. Deliberately distinct from the client-side MCP
    /// *management* routes under <c>/mcp/*</c> (which are the reverse relationship — Agnes consuming other MCP
    /// servers), so the two never collide.</summary>
    public const string Path = "/mcp-agnes";

    /// <summary>
    /// What this MCP server says about itself at the initialize handshake. <c>ServerInstructions</c> is the
    /// one place a server may state a <i>standing</i> expectation rather than describe a tool: MCP clients
    /// (Claude Code, Copilot, Codex) put it in the model's context, so it reaches every agent that is handed
    /// the <c>agnes</c> server, whatever its CLI does about system prompts.
    /// </summary>
    /// <remarks>Factored out of <c>Program.cs</c> so a test can assert the instructions are actually set —
    /// a nudge nobody sends is indistinguishable from a feature nobody built.</remarks>
    public static void ConfigureServer(McpServerOptions options)
        => options.ServerInstructions = AgentStatusNudge.Text;
}
