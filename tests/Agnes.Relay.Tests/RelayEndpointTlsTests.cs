using Agnes.Relay;

namespace Agnes.Relay.Tests;

/// <summary>
/// The relay's own-endpoint LettuceEncrypt TLS is strictly config-gated on <c>Agnes:Relay:PublicDomain</c> and OFF
/// by default: with no public domain the relay serves plain TCP (as every other relay test relies on) and no cert
/// is ever obtained or required. These assert the gate and that the TLS wrapper cannot engage before a cert exists.
/// </summary>
public sealed class RelayEndpointTlsTests
{
    [Fact]
    public void Is_disabled_by_default_and_for_blank_domains()
    {
        Assert.False(RelayEndpointTls.IsEnabled(new RelayOptions()));
        Assert.False(RelayEndpointTls.IsEnabled(new RelayOptions { PublicDomain = "" }));
        Assert.False(RelayEndpointTls.IsEnabled(new RelayOptions { PublicDomain = "   " }));
    }

    [Fact]
    public void Is_enabled_only_when_a_public_domain_is_configured()
        => Assert.True(RelayEndpointTls.IsEnabled(new RelayOptions { PublicDomain = "relay.example.com" }));

    [Fact]
    public async Task The_certificate_store_is_empty_until_acme_succeeds()
    {
        var store = new RelayCertificateStore(pfxPath: null, NullRelayLog.Instance);
        Assert.Null(store.Current);
        Assert.Empty(await store.GetCertificatesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_tls_wrapper_refuses_to_engage_without_a_certificate()
    {
        var store = new RelayCertificateStore(pfxPath: null, NullRelayLog.Instance);
        var security = new TlsRelayConnectionSecurity(store);
        using var stream = new MemoryStream();

        // No cert has been obtained (public-domain path never ran) → the wrapper must fail loudly rather than
        // silently downgrade to plaintext.
        await Assert.ThrowsAsync<InvalidOperationException>(() => security.WrapAsync(stream));
    }
}
