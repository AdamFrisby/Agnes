using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Agents.ClaudeCode;

/// <summary>
/// Reads the MCP servers Claude Code already has configured in its OWN native config, so Agnes can surface
/// them read-only rather than making the user re-enter config they already have. Claude Code keeps MCP servers
/// in two places: a per-project <c>.mcp.json</c> at the workspace root, and the global <c>~/.claude.json</c>
/// (both a top-level <c>mcpServers</c> and a per-project <c>projects["&lt;dir&gt;"].mcpServers</c>). This is a
/// pure parser over that boundary format: untyped JSON is deserialized straight into typed records here and
/// never flows inward. Tolerant by contract — a missing or malformed file yields no servers, never throws.
/// </summary>
public static class ClaudeNativeMcpConfig
{
    public const string SourceLabel = "Claude Code native config";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Detects native MCP servers configured for <paramref name="workspaceDirectory"/>: the project
    /// <c>.mcp.json</c> plus the global <c>~/.claude.json</c> (its top-level and per-project sections). Deduped
    /// by name (project config wins over global). Never throws — unreadable config just contributes nothing.</summary>
    public static Task<IReadOnlyList<NativeMcpServer>> DetectAsync(string workspaceDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var byName = new Dictionary<string, NativeMcpServer>(StringComparer.OrdinalIgnoreCase);

        // Project-local .mcp.json takes precedence, so read it first and let later sources not overwrite it.
        if (!string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            AddAll(byName, ParseFile(Path.Combine(workspaceDirectory, ".mcp.json"), projectDirectory: null));
        }

        AddAll(byName, ParseFile(HomeConfigPath(), projectDirectory: workspaceDirectory));

        return Task.FromResult<IReadOnlyList<NativeMcpServer>>(byName.Values.ToArray());
    }

    /// <summary>Parses one Claude config file into native servers (both its top-level <c>mcpServers</c> and, when
    /// <paramref name="projectDirectory"/> is given, its <c>projects["&lt;dir&gt;"].mcpServers</c>). Missing or
    /// malformed file => empty. Exposed for direct unit testing of the boundary parse.</summary>
    public static IReadOnlyList<NativeMcpServer> ParseFile(string path, string? projectDirectory)
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            // Config unreadable (locked/removed mid-read) — treat as "no native servers", never surface an error.
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            // Same: a permissions problem on the CLI's own file must not break Agnes's preview.
            return [];
        }

        return ParseContent(text, projectDirectory);
    }

    /// <summary>Parses Claude config JSON text. Public for offline tests that don't want to touch the filesystem.</summary>
    public static IReadOnlyList<NativeMcpServer> ParseContent(string json, string? projectDirectory)
    {
        ClaudeConfigFile? config;
        try
        {
            config = JsonSerializer.Deserialize<ClaudeConfigFile>(json, Options);
        }
        catch (JsonException)
        {
            // Malformed native config — tolerate by contract and surface nothing.
            return [];
        }

        if (config is null)
        {
            return [];
        }

        var byName = new Dictionary<string, NativeMcpServer>(StringComparer.OrdinalIgnoreCase);
        AddSection(byName, config.McpServers);

        if (!string.IsNullOrWhiteSpace(projectDirectory)
            && config.Projects is { } projects
            && projects.TryGetValue(projectDirectory, out var project))
        {
            AddSection(byName, project?.McpServers);
        }

        return byName.Values.ToArray();
    }

    private static void AddSection(Dictionary<string, NativeMcpServer> into, Dictionary<string, ClaudeMcpEntry?>? section)
    {
        if (section is null)
        {
            return;
        }

        foreach (var (name, entry) in section)
        {
            if (string.IsNullOrWhiteSpace(name) || entry is null)
            {
                continue;
            }

            // First writer wins within a single file, matching Claude's own "top-level then project" precedence.
            if (!into.ContainsKey(name))
            {
                into[name] = Map(name, entry);
            }
        }
    }

    private static void AddAll(Dictionary<string, NativeMcpServer> into, IReadOnlyList<NativeMcpServer> servers)
    {
        foreach (var server in servers)
        {
            if (!into.ContainsKey(server.Name))
            {
                into[server.Name] = server;
            }
        }
    }

    private static NativeMcpServer Map(string name, ClaudeMcpEntry entry)
    {
        var isHttp = string.Equals(entry.Type, "http", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Type, "sse", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(entry.Command) && !string.IsNullOrWhiteSpace(entry.Url));

        return new NativeMcpServer(
            name,
            isHttp ? "http" : "stdio",
            entry.Command,
            entry.Args ?? [],
            entry.Env ?? new Dictionary<string, string>(),
            entry.Url,
            SourceLabel);
    }

    private static string HomeConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? "";
        }

        return Path.Combine(home, ".claude.json");
    }

    // ---- typed boundary records (Claude Code's config schema; deserialized-and-done, never flows inward) ----

    private sealed record ClaudeConfigFile
    {
        [JsonPropertyName("mcpServers")] public Dictionary<string, ClaudeMcpEntry?>? McpServers { get; init; }

        [JsonPropertyName("projects")] public Dictionary<string, ClaudeProjectEntry?>? Projects { get; init; }
    }

    private sealed record ClaudeProjectEntry
    {
        [JsonPropertyName("mcpServers")] public Dictionary<string, ClaudeMcpEntry?>? McpServers { get; init; }
    }

    private sealed record ClaudeMcpEntry
    {
        [JsonPropertyName("type")] public string? Type { get; init; }

        [JsonPropertyName("command")] public string? Command { get; init; }

        [JsonPropertyName("args")] public List<string>? Args { get; init; }

        [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; init; }

        [JsonPropertyName("url")] public string? Url { get; init; }
    }
}
