using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// mTLS client-certificate validation core (AC2/AC5): a leaf signed by the configured CA is accepted, an
/// unrelated self-signed certificate is rejected, and the pinned-thumbprint path works. Certificates are
/// generated in-memory at runtime — no files, no PKI on disk.
/// </summary>
public class MtlsAuthTests
{
    private static X509Certificate2 MakeCa(string cn = "CN=Agnes Test CA")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(cn, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 MakeLeaf(X509Certificate2 ca, string cn = "CN=device-1")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(cn, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var serial = RandomNumberGenerator.GetBytes(8);
        // The returned cert is public-only, which is exactly what a server sees on the wire.
        return request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), serial);
    }

    private static X509Certificate2 MakeSelfSigned(string cn = "CN=unrelated")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(cn, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public void Leaf_signed_by_the_configured_ca_is_accepted()
    {
        using var ca = MakeCa();
        using var leaf = MakeLeaf(ca);
        var mtls = new MtlsIdentity(new MtlsOptions { Enabled = true, TrustAnchorPem = ca.ExportCertificatePem() });

        var result = mtls.Validate(leaf);
        Assert.True(result.Ok);
        Assert.Equal("device-1", result.Subject);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Unrelated_self_signed_certificate_is_rejected()
    {
        using var ca = MakeCa();
        using var unrelated = MakeSelfSigned();
        var mtls = new MtlsIdentity(new MtlsOptions { Enabled = true, TrustAnchorPem = ca.ExportCertificatePem() });

        var result = mtls.Validate(unrelated);
        Assert.False(result.Ok);
        Assert.Null(result.Subject);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Pinned_thumbprint_is_accepted_without_a_ca()
    {
        using var pinned = MakeSelfSigned("CN=pinned-device");
        var thumbprint = pinned.GetCertHashString(HashAlgorithmName.SHA256);
        var mtls = new MtlsIdentity(new MtlsOptions { Enabled = true, PinnedThumbprints = [thumbprint] });

        var result = mtls.Validate(pinned);
        Assert.True(result.Ok);
        Assert.Equal("pinned-device", result.Subject);
    }

    [Fact]
    public void Pinned_thumbprint_matches_with_separators_and_mixed_case()
    {
        using var pinned = MakeSelfSigned("CN=pinned-device");
        var raw = pinned.GetCertHashString(HashAlgorithmName.SHA256);
        var withColons = string.Join(":", Enumerable.Range(0, raw.Length / 2).Select(i => raw.Substring(i * 2, 2))).ToLowerInvariant();
        var mtls = new MtlsIdentity(new MtlsOptions { Enabled = true, PinnedThumbprints = [withColons] });

        Assert.True(mtls.Validate(pinned).Ok);
    }

    [Fact]
    public void A_certificate_not_matching_any_pin_is_rejected()
    {
        using var pinned = MakeSelfSigned("CN=pinned-device");
        using var other = MakeSelfSigned("CN=other-device");
        var mtls = new MtlsIdentity(new MtlsOptions { Enabled = true, PinnedThumbprints = [pinned.GetCertHashString(HashAlgorithmName.SHA256)] });

        Assert.False(mtls.Validate(other).Ok);
    }

    [Fact]
    public void No_certificate_is_rejected()
    {
        using var ca = MakeCa();
        var mtls = new MtlsIdentity(new MtlsOptions { Enabled = true, TrustAnchorPem = ca.ExportCertificatePem() });
        Assert.False(mtls.Validate(null).Ok);
    }

    [Fact]
    public void Disabled_or_empty_configuration_is_unusable_and_rejects()
    {
        using var ca = MakeCa();
        using var leaf = MakeLeaf(ca);

        // Enabled but neither anchor nor pins → fail-closed (unusable).
        var empty = new MtlsIdentity(new MtlsOptions { Enabled = true });
        Assert.False(empty.Options.IsUsable);
        Assert.False(empty.Validate(leaf).Ok);
        Assert.False(new MtlsAuthMethodProvider(empty).IsEnabled);

        // Configured but disabled → unusable.
        var disabled = new MtlsIdentity(new MtlsOptions { Enabled = false, TrustAnchorPem = ca.ExportCertificatePem() });
        Assert.False(disabled.Options.IsUsable);
        Assert.False(disabled.Validate(leaf).Ok);
    }

    [Fact]
    public void Provider_reports_enabled_when_configured()
    {
        using var ca = MakeCa();
        var mtls = new MtlsIdentity(new MtlsOptions { Enabled = true, TrustAnchorPem = ca.ExportCertificatePem() });
        var provider = new MtlsAuthMethodProvider(mtls);
        Assert.True(provider.IsEnabled);
        Assert.Equal("mtls", provider.MethodId);
    }
}
