using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agnes.Protocol;

namespace Agnes.Host.Sessions;

/// <summary>
/// Renders an Agnes MCP server set into the native config format each agent CLI reads. Pure and
/// golden-testable. Claude Code loads its JSON via <c>--mcp-config</c> (so it never touches the
/// user's own config); Codex reads a <c>~/.codex/config.toml</c> we materialize.
/// </summary>
public static class McpConfig
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Claude Code <c>--mcp-config</c> JSON: <c>{ "mcpServers": { name: {...} } }</c>.</summary>
    public static string ForClaude(IReadOnlyList<McpServerInfo> servers)
    {
        var map = new JsonObject();
        foreach (var s in servers)
        {
            JsonObject entry;
            if (IsHttp(s))
            {
                entry = new JsonObject { ["type"] = "http", ["url"] = s.Url ?? string.Empty };
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
    public static string ForCodex(IReadOnlyList<McpServerInfo> servers)
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

    private static bool IsHttp(McpServerInfo s)
        => string.Equals(s.Transport, "http", StringComparison.OrdinalIgnoreCase);

    // A TOML bare key if it's simple, else a quoted key.
    private static string TomlKey(string key)
        => key.All(c => char.IsLetterOrDigit(c) || c is '_' or '-') && key.Length > 0 ? key : TomlString(key);

    private static string TomlString(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
}
