using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>The advertised auth bootstrap methods are built-in <see cref="IAuthMethodProvider"/> plugins in
/// a registry (AC13); <c>/auth/methods</c> reads their enabled state and client metadata from it.</summary>
public class AuthMethodProviderTests
{
    private static DeviceRegistry Devices(bool pairingEnabled)
        => new(null, Path.Combine(Path.GetTempPath(), "agnes-authtest-" + Guid.NewGuid().ToString("n") + ".json"), pairingEnabled: pairingEnabled);

    [Fact]
    public void Pairing_provider_reflects_the_device_registry_state()
    {
        Assert.True(new PairingAuthMethodProvider(Devices(pairingEnabled: true)).IsEnabled);
        Assert.False(new PairingAuthMethodProvider(Devices(pairingEnabled: false)).IsEnabled);
        Assert.Equal("pairing", new PairingAuthMethodProvider(Devices(pairingEnabled: true)).MethodId);
    }

    [Fact]
    public void GitHub_provider_exposes_the_client_id_only_when_usable()
    {
        var usable = new GitHubIdentity(
            new GitHubUserLookup(new HttpClient()),
            new GitHubAuthOptions { Enabled = true, ClientId = "cid-123", AllowedUsers = ["octocat"] },
            NullLogger<GitHubIdentity>.Instance);
        var provider = new GitHubAuthMethodProvider(usable);

        Assert.True(provider.IsEnabled);
        Assert.Equal("cid-123", provider.ClientMetadata["clientId"]);

        var off = new GitHubAuthMethodProvider(new GitHubIdentity(
            new GitHubUserLookup(new HttpClient()),
            new GitHubAuthOptions { Enabled = false },
            NullLogger<GitHubIdentity>.Instance));
        Assert.False(off.IsEnabled);
        Assert.Empty(off.ClientMetadata);
    }

    [Fact]
    public void Registry_finds_each_built_in_method_by_id()
    {
        var devices = Devices(pairingEnabled: true);
        var github = new GitHubIdentity(new GitHubUserLookup(new HttpClient()), new GitHubAuthOptions { Enabled = false }, NullLogger<GitHubIdentity>.Instance);
        var keypair = new KeypairAuth(new KeypairAuthOptions { Enabled = false }, NullLogger<KeypairAuth>.Instance);

        IReadOnlyList<IAuthMethodProvider> providers =
            [new PairingAuthMethodProvider(devices), new GitHubAuthMethodProvider(github), new KeypairAuthMethodProvider(keypair)];
        var registry = new PluginRegistry<IAuthMethodProvider>(providers, m => m.MethodId);

        Assert.NotNull(registry.Find("pairing"));
        Assert.NotNull(registry.Find("github"));
        Assert.NotNull(registry.Find("keypair"));
        Assert.Null(registry.Find("oidc"));
    }
}
