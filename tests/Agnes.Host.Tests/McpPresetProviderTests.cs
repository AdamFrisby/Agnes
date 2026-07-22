using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>MCP presets come from built-in <see cref="IMcpPresetProvider"/> plugins in a registry (AC13),
/// aggregated for the <c>/mcp/presets</c> endpoint.</summary>
public class McpPresetProviderTests
{
    [Fact]
    public void Curated_provider_offers_well_known_stdio_presets()
    {
        var provider = new CuratedMcpPresetProvider();
        Assert.Equal("curated", provider.Id);
        Assert.Contains(provider.Presets, p => p.Id == "playwright" && p.Transport == "stdio" && p.Command == "npx");
        Assert.Contains(provider.Presets, p => p.Id == "github");
        Assert.All(provider.Presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void Registry_aggregates_presets_across_providers()
    {
        IReadOnlyList<IMcpPresetProvider> providers = [new CuratedMcpPresetProvider()];
        var registry = new PluginRegistry<IMcpPresetProvider>(providers, p => p.Id);

        var all = registry.All.SelectMany(p => p.Presets).ToArray();
        Assert.NotEmpty(all);
        Assert.Equal(all.Length, all.Select(p => p.Id).Distinct().Count()); // no duplicate ids in the built-in set
    }
}
