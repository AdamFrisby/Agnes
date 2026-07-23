using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Non-regression for the additive enterprise methods (AC4): when OIDC/mTLS are not configured they are
/// registered but disabled, the registry still resolves the original three methods, and the
/// <see cref="AuthMethods"/> wire shape defaults the new fields off — so an existing deployment is
/// unaffected. Also verifies the new methods are discoverable by id once configured.
/// </summary>
public class EnterpriseAuthMethodsTests
{
    private static DeviceRegistry Devices()
        => new(null, Path.Combine(Path.GetTempPath(), "agnes-entauth-" + Guid.NewGuid().ToString("n") + ".json"), pairingEnabled: true);

    private static PluginRegistry<IAuthMethodProvider> Registry(OidcIdentity oidc, MtlsIdentity mtls)
    {
        var github = new GitHubIdentity(new GitHubUserLookup(new HttpClient()), new GitHubAuthOptions { Enabled = false }, NullLogger<GitHubIdentity>.Instance);
        var keypair = new KeypairAuth(new KeypairAuthOptions { Enabled = false }, NullLogger<KeypairAuth>.Instance);
        IReadOnlyList<IAuthMethodProvider> providers =
        [
            new PairingAuthMethodProvider(Devices()),
            new GitHubAuthMethodProvider(github),
            new KeypairAuthMethodProvider(keypair),
            new OidcAuthMethodProvider(oidc),
            new MtlsAuthMethodProvider(mtls),
        ];
        return new PluginRegistry<IAuthMethodProvider>(providers, m => m.MethodId);
    }

    [Fact]
    public void With_nothing_configured_the_new_methods_are_registered_but_disabled()
    {
        var registry = Registry(
            new OidcIdentity(new OidcOptions()),
            new MtlsIdentity(new MtlsOptions()));

        Assert.NotNull(registry.Find("pairing"));
        Assert.NotNull(registry.Find("github"));
        Assert.NotNull(registry.Find("keypair"));
        Assert.NotNull(registry.Find("oidc"));
        Assert.NotNull(registry.Find("mtls"));

        Assert.False(registry.Find("oidc")!.IsEnabled);
        Assert.False(registry.Find("mtls")!.IsEnabled);
    }

    [Fact]
    public void AuthMethods_wire_shape_defaults_the_enterprise_fields_off()
    {
        // The original four-arg constructor still binds — trailing-optional additions keep it back-compatible.
        var methods = new AuthMethods(Pairing: true, GitHub: false, GitHubClientId: null, Keypair: false);
        Assert.False(methods.Oidc);
        Assert.Null(methods.OidcIssuer);
        Assert.False(methods.Mtls);
    }

    [Fact]
    public void Configured_enterprise_methods_are_discoverable_and_advertise_metadata()
    {
        var oidc = new OidcIdentity(new OidcOptions
        {
            Enabled = true,
            Issuer = "https://issuer.test/",
            Audience = "agnes",
            JwksJson = "{\"keys\":[]}",
        });
        var registry = Registry(oidc, new MtlsIdentity(new MtlsOptions()));

        var oidcMethod = registry.Find("oidc");
        Assert.True(oidcMethod!.IsEnabled);
        Assert.Equal("https://issuer.test/", oidcMethod.ClientMetadata["issuer"]);
    }
}
