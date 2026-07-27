using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// Built-in <see cref="IMcpCatalogProvider"/>: a small set of widely-used MCP servers, offered with no network
/// call so the MCP page has something useful on it the moment it opens, even on a host with no registry plugin
/// installed and no way out to the internet. Broader discovery is a plugin (see
/// <c>Agnes.Registries.McpRegistry</c>) rather than a longer list here.
/// </summary>
public sealed class CuratedMcpCatalogProvider : IMcpCatalogProvider
{
    private static readonly IReadOnlyList<McpCatalogEntry> Entries =
    [
        Stdio("playwright", "Playwright", "Drive a real browser: navigate, click, fill forms, screenshot.",
            "npx", ["-y", "@playwright/mcp@latest"]),
        Stdio("context7", "Context7", "Up-to-date documentation for libraries, fetched on demand.",
            "npx", ["-y", "@upstash/context7-mcp"]),
        Stdio("sequential-thinking", "Sequential Thinking", "A scratchpad for multi-step reasoning.",
            "npx", ["-y", "@modelcontextprotocol/server-sequential-thinking"]),
        Stdio("github", "GitHub", "Read and write GitHub issues, pull requests and code.",
            "npx", ["-y", "@modelcontextprotocol/server-github"]),
    ];

    public string Id => "curated";

    public string DisplayName => "Curated (built in)";

    public Task<IReadOnlyList<McpCatalogEntry>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(Entries);

    private static McpCatalogEntry Stdio(string id, string name, string description, string command, IReadOnlyList<string> args)
        => new(id, name, description, Publisher: "Agnes", Homepage: null,
            Transport: McpCatalogTransport.Stdio, Command: command, Args: args);
}

/// <summary>
/// Turns a catalogued MCP server into the host's own configuration shape. Kept here, not in
/// <c>Agnes.Abstractions</c>, because <see cref="McpServerRequest"/> is a wire contract a plugin has no
/// business referencing — the catalogue speaks the domain, the host translates.
/// </summary>
public static class McpCatalogMapping
{
    /// <summary>
    /// The add-server request that installs <paramref name="entry"/>. Required environment variables are
    /// carried across as empty entries rather than dropped: the server needs them, so the user should find
    /// them waiting to be filled in rather than discover the omission when the server fails to start.
    /// Variables with a default get that default. Secrets are never given a placeholder value.
    /// </summary>
    /// <summary>The catalogued server as a not-yet-installed <see cref="McpServerInfo"/> template — what the
    /// quick-install list on the MCP page shows.</summary>
    public static McpServerInfo ToInfo(McpCatalogEntry entry)
    {
        var request = ToRequest(entry);
        return new McpServerInfo(
            entry.Id, request.Name, request.RunAt, request.Enabled, request.Transport,
            request.Command, request.Args ?? [], request.Env ?? new Dictionary<string, string>(),
            request.Url, request.BearerTokenEnv);
    }

    public static McpServerRequest ToRequest(McpCatalogEntry entry, string runAt = "Host")
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in entry.Environment)
        {
            if (variable.IsRequired || variable.Default is { Length: > 0 })
            {
                env[variable.Name] = variable.Default ?? string.Empty;
            }
        }

        var http = entry.Transport == McpCatalogTransport.Http;
        return new McpServerRequest(
            Name: entry.Name,
            RunAt: runAt,
            Enabled: true,
            Transport: http ? "http" : "stdio",
            Command: http ? null : entry.Command,
            Args: http ? null : entry.LaunchArgs,
            Env: env,
            Url: http ? entry.Url : null);
    }
}
