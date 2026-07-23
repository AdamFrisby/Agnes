using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// A source of curated, one-click-installable MCP server presets. Exposed as a plugin-point (AC13) so
/// "where do MCP presets come from" flows through the same
/// <see cref="Agnes.Abstractions.IPluginRegistry{TProvider}"/> as agents and sandboxes: the built-in
/// <see cref="CuratedMcpPresetProvider"/> ships a small well-known set, and a plugin can offer its own
/// presets (e.g. an org's internal MCP servers) without changing the host. The user-facing MCP registry
/// (<see cref="Agnes.Host.Sessions.McpRegistry"/>) stores what a user actually configures; presets are
/// just templates it can be seeded from.
/// Native-config detection — surfacing servers an agent CLI already has in its own config — is a separate,
/// adapter-level capability (<see cref="Agnes.Abstractions.IMcpDiscoveryAdapter"/>, implemented per agent
/// package); the host folds it into the effective-config preview via <see cref="McpEffectiveConfig"/>.
/// </summary>
public interface IMcpPresetProvider
{
    /// <summary>Stable id for this preset source, e.g. <c>curated</c>.</summary>
    string Id { get; }

    /// <summary>The presets this source offers, as ready-to-install <see cref="McpServerInfo"/> templates.</summary>
    IReadOnlyList<McpServerInfo> Presets { get; }
}

/// <summary>Built-in provider for a small set of widely-used MCP servers (stdio via <c>npx</c>).</summary>
public sealed class CuratedMcpPresetProvider : IMcpPresetProvider
{
    public string Id => "curated";

    public IReadOnlyList<McpServerInfo> Presets { get; } =
    [
        Stdio("playwright", "Playwright", "npx", ["-y", "@playwright/mcp@latest"]),
        Stdio("context7", "Context7", "npx", ["-y", "@upstash/context7-mcp"]),
        Stdio("sequential-thinking", "Sequential Thinking", "npx", ["-y", "@modelcontextprotocol/server-sequential-thinking"]),
        Stdio("github", "GitHub", "npx", ["-y", "@modelcontextprotocol/server-github"]),
    ];

    private static McpServerInfo Stdio(string id, string name, string command, IReadOnlyList<string> args)
        => new(id, name, RunAt: "Host", Enabled: true, Transport: "stdio", Command: command, Args: args,
            Env: new Dictionary<string, string>(), Url: null, BearerTokenEnv: null);
}
