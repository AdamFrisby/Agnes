using Agnes.Abstractions;
using Agnes.App.Desktop.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>The registries list one server many times — per version, per mirror. The page lists it once.</summary>
public sealed class McpCatalogShapingTests
{
    private static CatalogHit<McpCatalogEntry> Hit(string id, string name, string? url = null, string? command = null, string[]? args = null, string catalog = "official")
        => new(catalog, catalog, new McpCatalogEntry(id, name, Transport: url is null ? McpCatalogTransport.Stdio : McpCatalogTransport.Http, Command: command, Args: args, Url: url));

    [Fact]
    public void The_same_server_listed_four_times_is_shown_once()
    {
        var hits = new[]
        {
            Hit("a1", "inference.sh", url: "https://sh.inference.ac"),
            Hit("a2", "inference.sh", url: "https://sh.inference.ac/"),
            Hit("a3", "Inference.sh", url: "HTTPS://sh.inference.ac"),
            Hit("a4", "inference.sh", url: "https://api.inference.sh/mcp"),   // a different endpoint: kept
            Hit("d1", "docs-mcp", url: "https://tandem.ac/mcp"),
            Hit("d2", "docs-mcp", url: "https://tandem.ac/mcp", catalog: "mirror"),
        };

        var shown = McpCatalogShaping.Distinct(hits);

        Assert.Equal(["a1", "a4", "d1"], shown.Select(h => h.Entry.Id));
    }

    [Fact]
    public void Local_servers_are_told_apart_by_their_command_line()
    {
        var hits = new[]
        {
            Hit("p1", "playwright", command: "npx", args: ["-y", "@playwright/mcp@latest"]),
            Hit("p2", "playwright", command: "npx", args: ["-y", "@playwright/mcp@latest"]),
            Hit("p3", "playwright", command: "uvx", args: ["playwright-mcp"]),
        };

        Assert.Equal(["p1", "p3"], McpCatalogShaping.Distinct(hits).Select(h => h.Entry.Id));
    }

    [Fact]
    public void The_hosts_preset_list_is_de_duplicated_in_order()
    {
        static Agnes.Protocol.McpServerInfo Preset(string name, string? url = null, string? command = null, params string[] args)
            => new(Guid.NewGuid().ToString("n"), name, "host", true, url is null ? "stdio" : "http", command, args, new Dictionary<string, string>(), url, null);

        var presets = new[]
        {
            Preset("Playwright", command: "npx", args: ["-y", "@playwright/mcp@latest"]),
            Preset("inference.sh", url: "https://sh.inference.ac"),
            Preset("inference.sh", url: "https://sh.inference.ac/"),
            Preset("Playwright", command: "npx", args: ["-y", "@playwright/mcp@latest"]),
            Preset("docs-mcp", url: "https://tandem.ac/mcp"),
        };

        Assert.Equal(["Playwright", "inference.sh", "docs-mcp"], McpCatalogShaping.DistinctPresets(presets).Select(p => p.Name));
    }

    [Fact]
    public void An_entry_with_neither_url_nor_command_still_gets_one_row_per_name()
    {
        var hits = new[] { Hit("x1", "mystery"), Hit("x2", "mystery"), Hit("y1", "other") };
        Assert.Equal(["x1", "y1"], McpCatalogShaping.Distinct(hits).Select(h => h.Entry.Id));
    }
}
