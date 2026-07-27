using Agnes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agnes.Registries.GitHub;

/// <summary>
/// Plugin entry point. Registers one <see cref="GitHubSkillsRegistryProvider"/> over the configured repository,
/// defaulting to the official <c>anthropics/skills</c>.
///
/// Settings (from the plugin's Configure panel): <c>owner</c>, <c>repo</c>, <c>branch</c>. Point it at your own
/// repository to make an internal skills library installable everywhere Agnes is.
///
/// Declared capabilities: <c>network</c> (GitHub), and <c>credentials</c> — optional, and used for exactly one
/// thing: a github.com token raises the anonymous 60-requests-an-hour API limit. Without the grant the plugin
/// simply stays anonymous rather than failing.
/// </summary>
public sealed class GitHubSkillsPluginModule : IAgnesPluginModule
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

            return new GitHubSkillsRegistryProvider(
                bundles,
                owner: settings.Values.GetValueOrDefault("owner", "anthropics"),
                repo: settings.Values.GetValueOrDefault("repo", "skills"),
                branch: settings.Values.GetValueOrDefault("branch", "main"));
        });
    }
}
