using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agnes.Protocol;

namespace Agnes.Host.Sessions;

/// <summary>
/// One MCP server as it is about to be written into an agent's config file.
/// </summary>
/// <remarks>
/// Deliberately NOT <see cref="McpServerInfo"/>. That is the wire contract — what a client configured and
/// what the host stores — whereas this is the *materialization* of a server for one launch, which may carry
/// something the wire never should: <see cref="Headers"/> holds the per-session bearer that identifies a
/// session to Agnes's own MCP endpoint. Keeping the two types apart is what stops that secret acquiring a
/// place in a persisted record or a DTO sent to a client.
/// </remarks>
public sealed record McpConfigEntry
{
    public required string Name { get; init; }

    /// <summary><c>http</c> or <c>stdio</c>.</summary>
    public required string Transport { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    public string? Url { get; init; }

    /// <summary>Name of an environment variable holding the bearer token — the only way Codex's
    /// <c>config.toml</c> takes one, as it has no header map.</summary>
    public string? BearerTokenEnv { get; init; }

    /// <summary>Literal HTTP headers to send with every request to an <c>http</c> server. Used by the
    /// formats that accept them (Claude Code / Copilot); dropped by the ones that don't.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>A registry- or project-configured server, rendered as-is.</summary>
    public static McpConfigEntry From(McpServerInfo server) => new()
    {
        Name = server.Name,
        Transport = server.Transport,
        Command = server.Command,
        Args = server.Args,
        Env = server.Env,
        Url = server.Url,
        BearerTokenEnv = server.BearerTokenEnv,
    };

    /// <summary>Every server in a set, rendered as-is.</summary>
    public static List<McpConfigEntry> From(IEnumerable<McpServerInfo> servers)
        => servers.Select(From).ToList();
}

/// <summary>
/// Renders an Agnes MCP server set into the native config format each agent CLI reads. Pure and
/// golden-testable. Claude Code loads its JSON via <c>--mcp-config</c> and Copilot via
/// <c>--additional-mcp-config</c> (so neither touches the user's own config); Codex reads a
/// <c>~/.codex/config.toml</c> we materialize into a sandbox's home.
/// </summary>
public static class McpConfig
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Claude Code <c>--mcp-config</c> JSON: <c>{ "mcpServers": { name: {...} } }</c>. Copilot
    /// reads the same shape unchanged.</summary>
    public static string ForClaude(IReadOnlyList<McpConfigEntry> servers)
    {
        var map = new JsonObject();
        foreach (var s in servers)
        {
            JsonObject entry;
            if (IsHttp(s))
            {
                entry = new JsonObject { ["type"] = "http", ["url"] = s.Url ?? string.Empty };
                if (s.Headers.Count > 0)
                {
                    var headers = new JsonObject();
                    foreach (var (k, v) in s.Headers)
                    {
                        headers[k] = v;
                    }

                    entry["headers"] = headers;
                }
            }
            else
            {
                entry = new JsonObject
                {
                    ["command"] = s.Command ?? string.Empty,
                    ["args"] = new JsonArray(s.Args.Select(a => (JsonNode)a!).ToArray()),
                };
                if (s.Env.Count > 0)
                {
                    var env = new JsonObject();
                    foreach (var (k, v) in s.Env)
                    {
                        env[k] = v;
                    }

                    entry["env"] = env;
                }
            }

            map[s.Name] = entry;
        }

        return new JsonObject { ["mcpServers"] = map }.ToJsonString(Indented);
    }

    /// <summary>Codex <c>config.toml</c>: a <c>[mcp_servers.name]</c> table per server.</summary>
    /// <remarks>
    /// Codex takes a bearer for an http server only as <c>bearer_token_env_var</c> — the *name* of an
    /// environment variable it reads at launch — so a caller that wants one must also put that variable on
    /// the agent's process. <see cref="McpConfigEntry.Headers"/> has no representation here and is dropped
    /// rather than half-rendered.
    /// </remarks>
    public static string ForCodex(IReadOnlyList<McpConfigEntry> servers)
    {
        var sb = new StringBuilder();
        foreach (var s in servers)
        {
            sb.Append("[mcp_servers.").Append(TomlKey(s.Name)).Append("]\n");
            if (IsHttp(s))
            {
                sb.Append("url = ").Append(TomlString(s.Url ?? string.Empty)).Append('\n');
                if (!string.IsNullOrEmpty(s.BearerTokenEnv))
                {
                    sb.Append("bearer_token_env_var = ").Append(TomlString(s.BearerTokenEnv)).Append('\n');
                }
            }
            else
            {
                sb.Append("command = ").Append(TomlString(s.Command ?? string.Empty)).Append('\n');
                if (s.Args.Count > 0)
                {
                    sb.Append("args = [").Append(string.Join(", ", s.Args.Select(TomlString))).Append("]\n");
                }

                if (s.Env.Count > 0)
                {
                    sb.Append("env = { ")
                      .Append(string.Join(", ", s.Env.Select(kv => $"{TomlKey(kv.Key)} = {TomlString(kv.Value)}")))
                      .Append(" }\n");
                }
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static bool IsHttp(McpConfigEntry s)
        => string.Equals(s.Transport, "http", StringComparison.OrdinalIgnoreCase);

    // A TOML bare key if it's simple, else a quoted key.
    private static string TomlKey(string key)
        => key.All(c => char.IsLetterOrDigit(c) || c is '_' or '-') && key.Length > 0 ? key : TomlString(key);

    private static string TomlString(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
}
