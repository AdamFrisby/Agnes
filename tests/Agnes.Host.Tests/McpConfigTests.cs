using System.Text.Json;
using Agnes.Host.Sessions;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

public class McpConfigTests
{
    private static McpServerInfo Stdio(string name) => new(
        name, name, "host", true, "stdio", "npx", ["-y", "mcp-" + name],
        new Dictionary<string, string> { ["API_KEY"] = "secret" }, null, null);

    private static McpServerInfo Http(string name) => new(
        name, name, "host", true, "http", null, [], new Dictionary<string, string>(),
        $"https://{name}.example/mcp", "TOKEN_ENV");

    [Fact]
    public void Claude_config_is_valid_json_with_stdio_and_http_servers()
    {
        var json = McpConfig.ForClaude(McpConfigEntry.From([Stdio("files"), Http("remote")]));
        using var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("mcpServers");

        var files = servers.GetProperty("files");
        Assert.Equal("npx", files.GetProperty("command").GetString());
        Assert.Equal("-y", files.GetProperty("args")[0].GetString());
        Assert.Equal("secret", files.GetProperty("env").GetProperty("API_KEY").GetString());

        var remote = servers.GetProperty("remote");
        Assert.Equal("http", remote.GetProperty("type").GetString());
        Assert.Equal("https://remote.example/mcp", remote.GetProperty("url").GetString());
    }

    [Fact]
    public void Codex_config_emits_toml_tables_per_server()
    {
        var toml = McpConfig.ForCodex(McpConfigEntry.From([Stdio("files"), Http("remote")]));

        Assert.Contains("[mcp_servers.files]", toml);
        Assert.Contains("command = \"npx\"", toml);
        Assert.Contains("args = [\"-y\", \"mcp-files\"]", toml);
        Assert.Contains("env = { API_KEY = \"secret\" }", toml);

        Assert.Contains("[mcp_servers.remote]", toml);
        Assert.Contains("url = \"https://remote.example/mcp\"", toml);
        Assert.Contains("bearer_token_env_var = \"TOKEN_ENV\"", toml);
    }

    [Fact]
    public void Empty_set_produces_empty_but_valid_config()
    {
        using var doc = JsonDocument.Parse(McpConfig.ForClaude([]));
        Assert.Empty(doc.RootElement.GetProperty("mcpServers").EnumerateObject());
        Assert.Equal(string.Empty, McpConfig.ForCodex([]));
    }

    // ---- headers: the only place a per-session bearer can go in the Claude/Copilot format ----

    [Fact]
    public void An_http_server_renders_its_headers_for_claude_and_copilot()
    {
        var entry = new McpConfigEntry
        {
            Name = "agnes",
            Transport = "http",
            Url = "http://127.0.0.1:5117/mcp-agnes",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer session-tok" },
        };

        using var doc = JsonDocument.Parse(McpConfig.ForClaude([entry]));
        var agnes = doc.RootElement.GetProperty("mcpServers").GetProperty("agnes");

        Assert.Equal("http", agnes.GetProperty("type").GetString());
        Assert.Equal("Bearer session-tok", agnes.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void A_server_with_no_headers_emits_no_headers_key()
    {
        using var doc = JsonDocument.Parse(McpConfig.ForClaude(McpConfigEntry.From([Http("remote")])));

        Assert.False(doc.RootElement.GetProperty("mcpServers").GetProperty("remote")
            .TryGetProperty("headers", out _));
    }

    [Fact]
    public void Headers_are_dropped_rather_than_half_rendered_for_codex()
    {
        // Codex's config.toml has no header map at all — only bearer_token_env_var. Emitting a "headers"
        // key it will not read would look like the token was carried when it wasn't.
        var toml = McpConfig.ForCodex(
        [
            new McpConfigEntry
            {
                Name = "agnes",
                Transport = "http",
                Url = "http://10.99.5.1:5099/mcp-agnes",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer session-tok" },
                BearerTokenEnv = "AGNES_MCP_BEARER",
            },
        ]);

        Assert.Contains("bearer_token_env_var = \"AGNES_MCP_BEARER\"", toml);
        Assert.DoesNotContain("headers", toml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session-tok", toml, StringComparison.Ordinal);
    }
}
