using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Agents.Copilot;

/// <summary>
/// Reads the MCP servers Copilot already has configured in its OWN config
/// (<c>~/.copilot/mcp-config.json</c>, or <c>$COPILOT_HOME/mcp-config.json</c>), so Agnes can surface them
/// read-only instead of asking the user to re-enter config they already have. Same shape and contract as
/// <c>ClaudeNativeMcpConfig</c>: a pure parser over a boundary format, deserializing untyped JSON straight
/// into the typed records below, and tolerant by design — a missing or malformed file yields no servers,
/// never an exception.
/// </summary>
public static class CopilotNativeMcpConfig
{
    public const string SourceLabel = "GitHub Copilot CLI native config";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The config file Copilot reads. <c>COPILOT_HOME</c> overrides the default location, exactly
    /// as the CLI documents.</summary>
    public static string ConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("COPILOT_HOME");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "mcp-config.json")
            : Path.Combine(home, "mcp-config.json");
    }

    /// <summary>Detects the servers Copilot has natively configured. Copilot's MCP config is global — it has
    /// no per-workspace file — so <paramref name="workspaceDirectory"/> is accepted for interface symmetry
    /// and not used.</summary>
    public static Task<IReadOnlyList<NativeMcpServer>> DetectAsync(string workspaceDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ParseFile(ConfigPath()));
    }

    /// <summary>Parses one Copilot MCP config file. Missing or malformed => empty. Exposed for direct unit
    /// testing of the boundary parse.</summary>
    public static IReadOnlyList<NativeMcpServer> ParseFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return ParseContent(File.ReadAllText(path));
        }
        catch (IOException)
        {
            // Unreadable (locked, removed mid-read) — "no native servers", never an error to the user.
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Parses Copilot MCP config JSON text. Public for offline tests that don't touch the disk.</summary>
    public static IReadOnlyList<NativeMcpServer> ParseContent(string json)
    {
        CopilotMcpFile? file;
        try
        {
            file = JsonSerializer.Deserialize<CopilotMcpFile>(json, Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (file?.McpServers is null)
        {
            return [];
        }

        var servers = new List<NativeMcpServer>();
        foreach (var (name, entry) in file.McpServers)
        {
            if (string.IsNullOrWhiteSpace(name) || entry is null)
            {
                continue;
            }

            // Copilot's own discriminator is "local" | "http" | "sse"; treat anything with a URL as remote
            // and anything with a command as stdio, so an entry that omits the type still classifies.
            var isRemote = !string.IsNullOrWhiteSpace(entry.Url);
            if (!isRemote && string.IsNullOrWhiteSpace(entry.Command))
            {
                continue; // neither a command to run nor a URL to reach: nothing Agnes could show.
            }

            servers.Add(new NativeMcpServer(
                name,
                isRemote ? (entry.Type is "sse" ? "sse" : "http") : "stdio",
                isRemote ? null : entry.Command,
                entry.Args ?? [],
                entry.Env ?? new Dictionary<string, string>(StringComparer.Ordinal),
                isRemote ? entry.Url : null,
                SourceLabel));
        }

        return servers;
    }

    // ---- boundary records ----

    private sealed record CopilotMcpFile
    {
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, CopilotMcpEntry?>? McpServers { get; init; }
    }

    private sealed record CopilotMcpEntry
    {
        [JsonPropertyName("type")] public string? Type { get; init; }

        [JsonPropertyName("command")] public string? Command { get; init; }

        [JsonPropertyName("args")] public IReadOnlyList<string>? Args { get; init; }

        [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; init; }

        [JsonPropertyName("url")] public string? Url { get; init; }
    }
}
