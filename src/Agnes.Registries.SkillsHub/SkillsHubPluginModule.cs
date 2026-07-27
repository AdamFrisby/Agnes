using Agnes.Abstractions;
using Agnes.Registries.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace Agnes.Registries.SkillsHub;

/// <summary>
/// Plugin entry point for the skillshub.wtf registry.
///
/// Settings: <c>baseUrl</c>, for pointing at a self-hosted or staging instance. No key is needed — the read
/// endpoints are public.
///
/// Declared capabilities: <c>network</c> (skillshub.wtf and GitHub), and <c>credentials</c> — optional, used
/// only to raise GitHub's anonymous API rate limit while downloading a bundle.
/// </summary>
public sealed class SkillsHubPluginModule : IAgnesPluginModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPromptRegistryProvider>(sp =>
        {
            var settings = sp.GetService<PluginSettings>() ?? PluginSettings.Empty;
            var broker = sp.GetService<ICredentialBroker>();
            var bundles = new GitHubSkillBundles(
                new HttpClient(),
                broker is null ? null : ct => broker.ResolveAsync("github.com", ct));

            return new SkillsHubRegistryProvider(
                new HttpClient(),
                bundles,
                settings.Values.GetValueOrDefault("baseUrl"));
        });
    }
}
