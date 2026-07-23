using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agnes.Client;

namespace Agnes.Integration.Tests;

/// <summary>
/// The client's trust decision over a relay address: a real-CA cert (advertised as <c>?cn=</c>) is validated by
/// chain + host name (default TLS validation); a self-signed cert (advertised as <c>?fp=</c>) is validated by
/// pinning the exact fingerprint, with no chain check. This is the additive upgrade to the pinned-only model — the
/// existing pinned relay E2E test stays green because <c>?fp=</c> still pins.
/// </summary>
public sealed class RelayClientTrustTests
{
    [Fact]
    public void A_ca_named_address_validates_by_chain_and_name()
    {
        RelayClientAddress relay = RelayClientTransport.Parse(
            "agnes-relay://relay.example.com:5100/hostid?cn=host.example.com");

        Assert.Equal("host.example.com", relay.CaName);
        Assert.Null(relay.Fingerprint);

        SslClientAuthenticationOptions ssl = RelayClientTransport.BuildSslClientOptions(relay);
        // Default chain+name validation: the target host is the CA-validated name, and there is NO pin override.
        Assert.Equal("host.example.com", ssl.TargetHost);
        Assert.Null(ssl.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void A_self_signed_address_validates_by_pinned_fingerprint()
    {
        using X509Certificate2 cert = CreateSelfSigned("agnes-host");
        using X509Certificate2 other = CreateSelfSigned("agnes-host");
        string fingerprint = Convert.ToHexStringLower(cert.GetCertHash(HashAlgorithmName.SHA256));

        RelayClientAddress relay = RelayClientTransport.Parse(
            $"agnes-relay://relay.example.com:5100/hostid?fp={fingerprint}");

        Assert.Equal(fingerprint, relay.Fingerprint);
        Assert.Null(relay.CaName);

        SslClientAuthenticationOptions ssl = RelayClientTransport.BuildSslClientOptions(relay);
        Assert.NotNull(ssl.RemoteCertificateValidationCallback);

        // The pin accepts the exact advertised cert (despite chain errors — self-signed) and rejects any other.
        Assert.True(ssl.RemoteCertificateValidationCallback!(this, cert, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(ssl.RemoteCertificateValidationCallback!(this, other, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void An_address_with_neither_trust_anchor_is_rejected()
        => Assert.Throws<FormatException>(() => RelayClientTransport.Parse("agnes-relay://relay.example.com:5100/hostid"));

    private static X509Certificate2 CreateSelfSigned(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
