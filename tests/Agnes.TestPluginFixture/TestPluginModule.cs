using Agnes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agnes.TestPluginFixture;

/// <summary>A trivial IAgentAdapter the test plugin registers — never actually started (tests only
/// assert on its presence/id in the registry), so StartSessionAsync intentionally throws.</summary>
public sealed class TestPluginAgentAdapter : IAgentAdapter
{
    public TestPluginAgentAdapter(string id) => Descriptor = new AgentDescriptor { Id = id, DisplayName = "Test Plugin Adapter" };

    public AgentDescriptor Descriptor { get; }

    public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Agnes.TestPluginFixture is a test double — it never actually starts a session.");
}

/// <summary>
/// The plugin package's entry point. Registers one <see cref="TestPluginAgentAdapter"/> whose id
/// encodes (a) the configured "id" setting from <see cref="PluginSettings"/>, if any, and (b) whether
/// it was able to resolve <see cref="ICredentialBroker"/> — which only succeeds if the manifest
/// declared, and the install call was granted, the "credentials" capability. Encoding both into the
/// adapter's id (rather than some other side channel) is deliberate: it lets a test assert the whole
/// capability-gating and settings-reload path purely by reading the shared <c>IPluginRegistry</c>,
/// with no cross-<see cref="System.Runtime.Loader.AssemblyLoadContext"/> communication needed.
/// </summary>
public sealed class TestPluginModule : IAgnesPluginModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentAdapter>(sp =>
        {
            var settings = sp.GetService<PluginSettings>() ?? PluginSettings.Empty;
            var idPrefix = settings.Values.GetValueOrDefault("id", "test-plugin-adapter");
            var hasCredentials = sp.GetService<ICredentialBroker>() is not null;
            return new TestPluginAgentAdapter($"{idPrefix}:{(hasCredentials ? "has-credentials" : "no-credentials")}");
        });
    }
}
