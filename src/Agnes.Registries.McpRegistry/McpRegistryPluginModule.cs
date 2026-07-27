using Agnes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agnes.Registries.McpRegistry;

/// <summary>
/// Plugin entry point for the official MCP registry.
///
/// Settings: <c>baseUrl</c>, for an organisation running its own registry (the API is the same shape — see the
/// registry's sub-registry/aggregator guidance).
///
/// Declared capabilities: <c>network</c>. Nothing else — the registry's read API is public, so the plugin has
/// no reason to ask for credentials.
/// </summary>
public sealed class McpRegistryPluginModule : IAgnesPluginModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IMcpCatalogProvider>(sp =>
        {
            var settings = sp.GetService<PluginSettings>() ?? PluginSettings.Empty;
            return new OfficialMcpRegistryProvider(new HttpClient(), settings.Values.GetValueOrDefault("baseUrl"));
        });
    }
}
