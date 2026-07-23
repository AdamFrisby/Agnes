using System.Security.Cryptography;
using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// Each auth method carries an <see cref="AuthFlowKind"/> so the client buckets it into the right UX group
/// (add a device / restore access / authorize a headless process), and <c>GET /auth/methods</c> reports it
/// via <see cref="AuthMethodsFactory"/> (connectivity/04 AC1).
/// </summary>
public class AuthFlowKindTests
{
    private static DeviceRegistry Devices(bool pairingEnabled = true)
        => new(null, Path.Combine(Path.GetTempPath(), "agnes-flowkind-" + Guid.NewGuid().ToString("n") + ".json"), pairingEnabled: pairingEnabled);

    private static GitHubIdentity GitHub(bool enabled)
        => new(new GitHubUserLookup(new HttpClient()),
            enabled ? new GitHubAuthOptions { Enabled = true, ClientId = "cid", AllowedUsers = ["octocat"] } : new GitHubAuthOptions { Enabled = false },
            NullLogger<GitHubIdentity>.Instance);

    private static KeypairAuth Keypair(bool enabled)
    {
        if (!enabled)
        {
            return new KeypairAuth(new KeypairAuthOptions { Enabled = false }, NullLogger<KeypairAuth>.Instance);
        }

        // A real P-256 SPKI line so IsUsable turns on (it requires ≥1 parseable authorized key).
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var file = Path.Combine(Path.GetTempPath(), "agnes-flowkind-keys-" + Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllText(file, Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()) + " test-key\n");
        return new KeypairAuth(new KeypairAuthOptions { Enabled = true, AuthorizedKeysFile = file }, NullLogger<KeypairAuth>.Instance);
    }

    [Fact]
    public void The_three_built_ins_map_to_the_expected_flow_kinds()
    {
        Assert.Equal(AuthFlowKind.NewDevice, new PairingAuthMethodProvider(Devices()).Kind);
        Assert.Equal(AuthFlowKind.NewDevice, new GitHubAuthMethodProvider(GitHub(enabled: true)).Kind);
        Assert.Equal(AuthFlowKind.ConnectTerminal, new KeypairAuthMethodProvider(Keypair(enabled: true)).Kind);
    }

    [Fact]
    public void Default_kind_is_new_device_so_untagged_providers_compile_and_bucket_sanely()
    {
        // The interface default keeps the enterprise providers (which don't tag themselves) at NewDevice.
        IAuthMethodProvider oidc = new OidcAuthMethodProvider(new OidcIdentity(new OidcOptions()));
        IAuthMethodProvider mtls = new MtlsAuthMethodProvider(new MtlsIdentity(new MtlsOptions()));
        Assert.Equal(AuthFlowKind.NewDevice, oidc.Kind);
        Assert.Equal(AuthFlowKind.NewDevice, mtls.Kind);
    }

    [Fact]
    public void AuthMethodsFactory_reports_a_flow_descriptor_per_enabled_method_with_its_kind()
    {
        IReadOnlyList<IAuthMethodProvider> providers =
        [
            new PairingAuthMethodProvider(Devices()),
            new GitHubAuthMethodProvider(GitHub(enabled: true)),
            new KeypairAuthMethodProvider(Keypair(enabled: true)),
        ];
        var registry = new PluginRegistry<IAuthMethodProvider>(providers, m => m.MethodId);

        var wire = AuthMethodsFactory.Build(registry);

        Assert.NotNull(wire.Flows);
        Assert.Equal(AuthFlowKind.NewDevice, wire.Flows!.Single(f => f.MethodId == "pairing").Kind);
        Assert.Equal(AuthFlowKind.NewDevice, wire.Flows.Single(f => f.MethodId == "github").Kind);
        Assert.Equal(AuthFlowKind.ConnectTerminal, wire.Flows.Single(f => f.MethodId == "keypair").Kind);
    }

    [Fact]
    public void AuthMethodsFactory_omits_disabled_methods_from_the_flow_descriptors()
    {
        IReadOnlyList<IAuthMethodProvider> providers =
        [
            new PairingAuthMethodProvider(Devices(pairingEnabled: true)),
            new GitHubAuthMethodProvider(GitHub(enabled: false)),
            new KeypairAuthMethodProvider(Keypair(enabled: false)),
        ];
        var registry = new PluginRegistry<IAuthMethodProvider>(providers, m => m.MethodId);

        var wire = AuthMethodsFactory.Build(registry);

        Assert.NotNull(wire.Flows);
        Assert.Equal("pairing", Assert.Single(wire.Flows!).MethodId);
        Assert.True(wire.Pairing);
        Assert.False(wire.GitHub);
        Assert.False(wire.Keypair);
    }
}
