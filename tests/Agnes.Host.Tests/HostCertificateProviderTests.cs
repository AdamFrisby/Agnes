using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

/// <summary>
/// The self-signed <see cref="IHostCertificateProvider"/> default: a persistent P-256 cert reused across
/// restarts so the fingerprint a client pinned at pairing keeps matching. The interface is the clean seam a
/// later real-CA (DNS-01) implementation slots into.
/// </summary>
public sealed class HostCertificateProviderTests
{
    [Fact]
    public void Generates_a_p256_cert_and_a_stable_sha256_fingerprint()
    {
        string path = Path.Combine(Path.GetTempPath(), $"agnes-cert-{Guid.NewGuid():n}.pfx");
        try
        {
            using var provider = new SelfSignedHostCertificateProvider(path);
            var cert = provider.GetCertificate();

            Assert.True(cert.HasPrivateKey);
            Assert.NotNull(cert.GetECDsaPublicKey());
            // Fingerprint is the lower-case hex SHA-256 of the DER — exactly what the client pins.
            Assert.Equal(
                Convert.ToHexStringLower(cert.GetCertHash(HashAlgorithmName.SHA256)),
                provider.Fingerprint);
            Assert.Equal(64, provider.Fingerprint.Length);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Reuses_the_persisted_cert_across_provider_instances()
    {
        string path = Path.Combine(Path.GetTempPath(), $"agnes-cert-{Guid.NewGuid():n}.pfx");
        try
        {
            string firstFingerprint;
            using (var first = new SelfSignedHostCertificateProvider(path))
            {
                firstFingerprint = first.Fingerprint;
            }

            using var second = new SelfSignedHostCertificateProvider(path);
            // A restart must keep the same fingerprint, or every paired client's pin would break.
            Assert.Equal(firstFingerprint, second.Fingerprint);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
