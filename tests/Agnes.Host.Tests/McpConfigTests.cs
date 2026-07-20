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
        var json = McpConfig.ForClaude([Stdio("files"), Http("remote")]);
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
        var toml = McpConfig.ForCodex([Stdio("files"), Http("remote")]);

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
}
