namespace Agnes.Host.Mcp;

/// <summary>Well-known routes for the Agnes-as-MCP-server surface.</summary>
public static class AgnesMcpEndpoints
{
    /// <summary>The Streamable-HTTP MCP endpoint path. Deliberately distinct from the client-side MCP
    /// *management* routes under <c>/mcp/*</c> (which are the reverse relationship — Agnes consuming other MCP
    /// servers), so the two never collide.</summary>
    public const string Path = "/mcp-agnes";
}
